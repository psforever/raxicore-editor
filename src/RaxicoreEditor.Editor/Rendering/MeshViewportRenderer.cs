using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RaxicoreEditor.Editor.Documents;
using Silk.NET.Vulkan;

namespace RaxicoreEditor.Editor.Rendering
{
    /// <summary>
    /// Renders a textured mesh (per-material submeshes, each with its own DDS texture) to an offscreen
    /// color+depth target and reads the color image back as tightly-packed BGRA bytes for an Avalonia
    /// WriteableBitmap. No swapchain/surface. Untextured submeshes bind a shared 1×1 white texture.
    /// </summary>
    public sealed unsafe class MeshViewportRenderer : IDisposable
    {
        private const Format ColorFormat = Format.B8G8R8A8Srgb; // matches Avalonia Bgra8888
        private const Format DepthFormat = Format.D32Sfloat;
        private const Format TextureFormat = Format.B8G8R8A8Unorm; // DDS decode is BGRA

        private readonly VulkanContext _ctx;
        private readonly Vk _vk;
        private readonly Device _dev;

        private RenderPass _renderPass;
        private DescriptorSetLayout _descLayout;
        private PipelineLayout _pipelineLayout;
        private Pipeline _pipeline;
        // Translucent overlay pass (shield domes, energy beams, shoreline foam — "mask" materials):
        // same descriptor layout/push constants/vertex format as the opaque pipeline, so it reuses
        // _pipelineLayout; only the fragment shader and blend/depth-write state differ.
        private Pipeline _blendPipeline;
        private Sampler _sampler;
        private CommandBuffer _cmd;
        private Fence _fence;

        // Size-dependent targets.
        private int _width, _height;
        private Image _colorImage;
        private DeviceMemory _colorMem;
        private ImageView _colorView;
        private Image _depthImage;
        private DeviceMemory _depthMem;
        private ImageView _depthView;
        private Framebuffer _framebuffer;
        private Silk.NET.Vulkan.Buffer _readback;
        private DeviceMemory _readbackMem;

        // Mesh — per-submesh GPU buffers + shared textures/descriptors.
        private struct GpuSubmesh
        {
            public Silk.NET.Vulkan.Buffer Vbuf;
            public DeviceMemory Vmem;
            public Silk.NET.Vulkan.Buffer Ibuf;
            public DeviceMemory Imem;
            public uint IndexCount;
            public DescriptorSet DescSet;
            public bool Translucent;
        }
        private struct GpuTexture
        {
            public Image Image;
            public DeviceMemory Mem;
            public ImageView View;
        }
        private readonly List<GpuSubmesh> _submeshes = new();
        private readonly List<GpuTexture> _textures = new();
        private DescriptorPool _descPool;

        // Optional skeleton-overlay line list (position+color per vertex; drawn after the mesh).
        private PipelineLayout _bonePipelineLayout;
        private Pipeline _bonePipeline;
        private Silk.NET.Vulkan.Buffer _boneVbuf;
        private DeviceMemory _boneVmem;
        private uint _boneVertexCount;

        public MeshViewportRenderer(VulkanContext ctx)
        {
            _ctx = ctx;
            _vk = ctx.Vk;
            _dev = ctx.Device;
            CreateRenderPass();
            CreateDescriptorLayoutAndSampler();
            CreatePipeline();
            CreateBlendPipeline();
            CreateBonePipeline();
            AllocateCommandBuffer();
        }

        // ---- mesh upload -----------------------------------------------------------------------

        public void SetMesh(IReadOnlyList<MeshSubmesh> submeshes)
        {
            _vk.DeviceWaitIdle(_dev);
            DestroyMesh();
            ClearSkeletonLines(); // a newly loaded model's skeleton (if any) is rebuilt separately
            if (submeshes.Count == 0)
            {
                return;
            }

            // Collect unique textures (dedupe by BGRA array reference); index 0 is always a 1×1 white.
            var texIndex = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
            var texSources = new List<(byte[] bgra, int w, int h)>();
            byte[] white = new byte[] { 255, 255, 255, 255 };
            texSources.Add((white, 1, 1)); // white = index 0
            foreach (MeshSubmesh s in submeshes)
            {
                if (s.HasTexture && s.TextureBgra != null && !texIndex.ContainsKey(s.TextureBgra))
                {
                    texIndex[s.TextureBgra] = texSources.Count;
                    texSources.Add((s.TextureBgra, s.TextureWidth, s.TextureHeight));
                }
            }

            CreateDescriptorPool((uint)texSources.Count);
            var descSets = new DescriptorSet[texSources.Count];
            for (int i = 0; i < texSources.Count; i++)
            {
                GpuTexture t = CreateTexture(texSources[i].bgra, texSources[i].w, texSources[i].h);
                _textures.Add(t);
                descSets[i] = AllocateTextureDescriptor(t.View);
            }

            foreach (MeshSubmesh s in submeshes)
            {
                if (s.Indices.Length == 0)
                {
                    continue;
                }
                (Silk.NET.Vulkan.Buffer vbuf, DeviceMemory vmem) =
                    CreateHostBuffer<float>(s.Vertices, BufferUsageFlags.VertexBufferBit);
                (Silk.NET.Vulkan.Buffer ibuf, DeviceMemory imem) =
                    CreateHostBuffer<uint>(s.Indices, BufferUsageFlags.IndexBufferBit);
                int ti = s.HasTexture && s.TextureBgra != null && texIndex.TryGetValue(s.TextureBgra, out int idx) ? idx : 0;
                _submeshes.Add(new GpuSubmesh
                {
                    Vbuf = vbuf, Vmem = vmem, Ibuf = ibuf, Imem = imem,
                    IndexCount = (uint)s.Indices.Length, DescSet = descSets[ti],
                    Translucent = s.IsTranslucent,
                });
            }
        }

        /// <summary>Number of GPU submeshes (1:1 with the list passed to <see cref="SetMesh"/>).</summary>
        public int SubmeshCount => _submeshes.Count;

        /// <summary>
        /// Overwrite a submesh's vertex buffer in place (host-visible, no recreation) — used for per-frame
        /// CPU skinning. The vertex count must be unchanged. Safe because Render() is synchronous (waits
        /// its fence), so the GPU is idle between frames.
        /// </summary>
        public void UpdateSubmeshVertices(int index, float[] verts)
        {
            if (index < 0 || index >= _submeshes.Count)
            {
                return;
            }
            GpuSubmesh s = _submeshes[index];
            if (s.Vbuf.Handle == 0)
            {
                return;
            }
            ulong size = (ulong)((long)verts.Length * sizeof(float));
            void* mapped = null;
            _vk.MapMemory(_dev, s.Vmem, 0, size, 0, ref mapped);
            fixed (float* src = verts)
            {
                System.Buffer.MemoryCopy(src, mapped, size, size);
            }
            _vk.UnmapMemory(_dev, s.Vmem);
        }

        // ---- skeleton overlay ------------------------------------------------------------------

        /// <summary>
        /// Replace the skeleton-overlay line list. <paramref name="posColor"/> is interleaved
        /// (px,py,pz, r,g,b) per vertex, 2 vertices per bone-to-parent segment (already in view-space —
        /// positions are drawn with just the camera MVP, no per-vertex skinning/model transform).
        /// </summary>
        public void SetSkeletonLines(float[] posColor)
        {
            ClearSkeletonLines();
            uint vertexCount = (uint)(posColor.Length / 6);
            if (vertexCount == 0)
            {
                return;
            }
            (_boneVbuf, _boneVmem) = CreateHostBuffer<float>(posColor, BufferUsageFlags.VertexBufferBit);
            _boneVertexCount = vertexCount;
        }

        public void ClearSkeletonLines()
        {
            if (_boneVbuf.Handle != 0) { _vk.DestroyBuffer(_dev, _boneVbuf, null); _vk.FreeMemory(_dev, _boneVmem, null); }
            _boneVbuf = default;
            _boneVmem = default;
            _boneVertexCount = 0;
        }

        // ---- offscreen sizing ------------------------------------------------------------------

        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == _width && height == _height && _framebuffer.Handle != 0)
            {
                return;
            }
            _vk.DeviceWaitIdle(_dev);
            DestroyTargets();
            _width = width;
            _height = height;

            (_colorImage, _colorMem) = CreateImage(width, height, ColorFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit);
            _colorView = CreateImageView(_colorImage, ColorFormat, ImageAspectFlags.ColorBit);
            (_depthImage, _depthMem) = CreateImage(width, height, DepthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit);
            _depthView = CreateImageView(_depthImage, DepthFormat, ImageAspectFlags.DepthBit);

            var attachments = stackalloc ImageView[2] { _colorView, _depthView };
            var fci = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = (uint)width,
                Height = (uint)height,
                Layers = 1,
            };
            Framebuffer fb;
            VulkanContext.Check(_vk.CreateFramebuffer(_dev, &fci, null, &fb), "CreateFramebuffer");
            _framebuffer = fb;

            ulong size = (ulong)width * (ulong)height * 4UL;
            (_readback, _readbackMem) = CreateBuffer(size, BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        }

        // ---- render ----------------------------------------------------------------------------

        public int Width => _width;
        public int Height => _height;

        public bool Render(Matrix4x4 mvp, Matrix4x4 model, byte[] dst)
        {
            if (_framebuffer.Handle == 0 || dst.Length < _width * _height * 4)
            {
                return false;
            }

            _vk.ResetCommandBuffer(_cmd, 0);
            var bi = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _vk.BeginCommandBuffer(_cmd, &bi);

            var clears = stackalloc ClearValue[2];
            clears[0] = new ClearValue(new ClearColorValue(0.04f, 0.04f, 0.05f, 1f));
            clears[1] = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0));

            var rp = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _renderPass,
                Framebuffer = _framebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_width, (uint)_height)),
                ClearValueCount = 2,
                PClearValues = clears,
            };
            _vk.CmdBeginRenderPass(_cmd, &rp, SubpassContents.Inline);

            var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_width, (uint)_height));
            _vk.CmdSetViewport(_cmd, 0, 1, &viewport);
            _vk.CmdSetScissor(_cmd, 0, 1, &scissor);

            if (_submeshes.Count > 0)
            {
                var push = stackalloc float[32];
                CopyMatrix(mvp, push);
                CopyMatrix(model, push + 16);
                _vk.CmdPushConstants(_cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, 128, push);
                ulong offset = 0;

                // Opaque/cutout pass first (depth write on), then translucent overlays (depth write
                // off) so masks/shields/beams correctly composite over whatever is already drawn.
                _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, _pipeline);
                foreach (GpuSubmesh s in _submeshes)
                {
                    if (s.Translucent) continue;
                    DescriptorSet ds = s.DescSet;
                    _vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);
                    var vbuf = s.Vbuf;
                    _vk.CmdBindVertexBuffers(_cmd, 0, 1, &vbuf, &offset);
                    _vk.CmdBindIndexBuffer(_cmd, s.Ibuf, 0, IndexType.Uint32);
                    _vk.CmdDrawIndexed(_cmd, s.IndexCount, 1, 0, 0, 0);
                }

                _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, _blendPipeline);
                foreach (GpuSubmesh s in _submeshes)
                {
                    if (!s.Translucent) continue;
                    DescriptorSet ds = s.DescSet;
                    _vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);
                    var vbuf = s.Vbuf;
                    _vk.CmdBindVertexBuffers(_cmd, 0, 1, &vbuf, &offset);
                    _vk.CmdBindIndexBuffer(_cmd, s.Ibuf, 0, IndexType.Uint32);
                    _vk.CmdDrawIndexed(_cmd, s.IndexCount, 1, 0, 0, 0);
                }
            }

            if (_boneVertexCount > 0)
            {
                _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, _bonePipeline);
                var bonePush = stackalloc float[16];
                CopyMatrix(mvp, bonePush);
                _vk.CmdPushConstants(_cmd, _bonePipelineLayout, ShaderStageFlags.VertexBit, 0, 64, bonePush);
                ulong boneOffset = 0;
                var boneVbuf = _boneVbuf;
                _vk.CmdBindVertexBuffers(_cmd, 0, 1, &boneVbuf, &boneOffset);
                _vk.CmdDraw(_cmd, _boneVertexCount, 1, 0, 0);
            }

            _vk.CmdEndRenderPass(_cmd);

            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)_width, (uint)_height, 1),
            };
            _vk.CmdCopyImageToBuffer(_cmd, _colorImage, ImageLayout.TransferSrcOptimal, _readback, 1, &region);

            _vk.EndCommandBuffer(_cmd);

            var cmd = _cmd;
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
            };
            _vk.ResetFences(_dev, 1, in _fence);
            VulkanContext.Check(_vk.QueueSubmit(_ctx.GraphicsQueue, 1, &submit, _fence), "QueueSubmit");
            _vk.WaitForFences(_dev, 1, in _fence, true, ulong.MaxValue);

            ulong size = (ulong)_width * (ulong)_height * 4UL;
            void* mapped = null;
            _vk.MapMemory(_dev, _readbackMem, 0, size, 0, ref mapped);
            new ReadOnlySpan<byte>(mapped, (int)size).CopyTo(dst);
            _vk.UnmapMemory(_dev, _readbackMem);
            return true;
        }

        // ---- vulkan object creation ------------------------------------------------------------

        private void CreateRenderPass()
        {
            var color = new AttachmentDescription
            {
                Format = ColorFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.TransferSrcOptimal,
            };
            var depth = new AttachmentDescription
            {
                Format = DepthFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.DontCare,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            };

            var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
            var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
            var sub = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorRef,
                PDepthStencilAttachment = &depthRef,
            };

            var dep = new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.TransferBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
            };

            var attachments = stackalloc AttachmentDescription[2] { color, depth };
            var rp = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 2,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &sub,
                DependencyCount = 1,
                PDependencies = &dep,
            };
            RenderPass pass;
            VulkanContext.Check(_vk.CreateRenderPass(_dev, &rp, null, &pass), "CreateRenderPass");
            _renderPass = pass;
        }

        private void CreateDescriptorLayoutAndSampler()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
            var dlci = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding,
            };
            DescriptorSetLayout layout;
            VulkanContext.Check(_vk.CreateDescriptorSetLayout(_dev, &dlci, null, &layout), "DescriptorSetLayout");
            _descLayout = layout;

            var sci = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                AddressModeU = SamplerAddressMode.Repeat,
                AddressModeV = SamplerAddressMode.Repeat,
                AddressModeW = SamplerAddressMode.Repeat,
                MipmapMode = SamplerMipmapMode.Linear,
                MaxLod = 1f,
                BorderColor = BorderColor.IntOpaqueBlack,
            };
            Sampler sampler;
            VulkanContext.Check(_vk.CreateSampler(_dev, &sci, null, &sampler), "CreateSampler");
            _sampler = sampler;
        }

        private void CreateDescriptorPool(uint maxSets)
        {
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = maxSets,
            };
            var pci = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = maxSets,
            };
            DescriptorPool pool;
            VulkanContext.Check(_vk.CreateDescriptorPool(_dev, &pci, null, &pool), "DescriptorPool");
            _descPool = pool;
        }

        private DescriptorSet AllocateTextureDescriptor(ImageView view)
        {
            DescriptorSetLayout layout = _descLayout;
            var ai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            DescriptorSet set;
            VulkanContext.Check(_vk.AllocateDescriptorSets(_dev, &ai, &set), "AllocateDescriptorSets");

            var imageInfo = new DescriptorImageInfo
            {
                Sampler = _sampler,
                ImageView = view,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imageInfo,
            };
            _vk.UpdateDescriptorSets(_dev, 1, &write, 0, null);
            return set;
        }

        private GpuTexture CreateTexture(byte[] bgra, int w, int h)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            ulong size = (ulong)w * (ulong)h * 4UL;

            (Silk.NET.Vulkan.Buffer staging, DeviceMemory stagingMem) = CreateBuffer(size,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* mapped = null;
            _vk.MapMemory(_dev, stagingMem, 0, size, 0, ref mapped);
            int copyLen = (int)Math.Min(size, (ulong)bgra.Length);
            fixed (byte* src = bgra)
            {
                System.Buffer.MemoryCopy(src, mapped, size, (ulong)copyLen);
            }
            _vk.UnmapMemory(_dev, stagingMem);

            (Image image, DeviceMemory mem) = CreateImage(w, h, TextureFormat,
                ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit);

            OneTimeSubmit(cmd =>
            {
                TransitionLayout(cmd, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageExtent = new Extent3D((uint)w, (uint)h, 1),
                };
                _vk.CmdCopyBufferToImage(cmd, staging, image, ImageLayout.TransferDstOptimal, 1, &region);
                TransitionLayout(cmd, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            });

            _vk.DestroyBuffer(_dev, staging, null);
            _vk.FreeMemory(_dev, stagingMem, null);

            ImageView view = CreateImageView(image, TextureFormat, ImageAspectFlags.ColorBit);
            return new GpuTexture { Image = image, Mem = mem, View = view };
        }

        private void TransitionLayout(CommandBuffer cmd, Image image, ImageLayout from, ImageLayout to)
        {
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            PipelineStageFlags srcStage, dstStage;
            if (from == ImageLayout.Undefined && to == ImageLayout.TransferDstOptimal)
            {
                barrier.SrcAccessMask = 0;
                barrier.DstAccessMask = AccessFlags.TransferWriteBit;
                srcStage = PipelineStageFlags.TopOfPipeBit;
                dstStage = PipelineStageFlags.TransferBit;
            }
            else
            {
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                srcStage = PipelineStageFlags.TransferBit;
                dstStage = PipelineStageFlags.FragmentShaderBit;
            }
            _vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
        }

        private void OneTimeSubmit(Action<CommandBuffer> record)
        {
            var ai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _ctx.CommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer cmd;
            VulkanContext.Check(_vk.AllocateCommandBuffers(_dev, &ai, &cmd), "AllocateCommandBuffers(upload)");
            var bi = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _vk.BeginCommandBuffer(cmd, &bi);
            record(cmd);
            _vk.EndCommandBuffer(cmd);

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
            };
            _vk.ResetFences(_dev, 1, in _fence);
            VulkanContext.Check(_vk.QueueSubmit(_ctx.GraphicsQueue, 1, &submit, _fence), "QueueSubmit(upload)");
            _vk.WaitForFences(_dev, 1, in _fence, true, ulong.MaxValue);
            _vk.FreeCommandBuffers(_dev, _ctx.CommandPool, 1, &cmd);
        }

        private void CreatePipeline()
        {
            ShaderModule vs = CreateShader(LoadEmbedded("mesh.vert.spv"));
            ShaderModule fs = CreateShader(LoadEmbedded("mesh.frag.spv"));
            byte* entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");

            var pcRange = new PushConstantRange(ShaderStageFlags.VertexBit, 0, 128);
            DescriptorSetLayout layout = _descLayout;
            var plci = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &layout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pcRange,
            };
            PipelineLayout pipelineLayout;
            VulkanContext.Check(_vk.CreatePipelineLayout(_dev, &plci, null, &pipelineLayout), "PipelineLayout");
            _pipelineLayout = pipelineLayout;

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vs,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fs,
                PName = entry,
            };

            var binding = new VertexInputBindingDescription(0, 32, VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[3];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);   // pos
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);  // normal
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 24);     // uv
            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attrs,
            };
            var ia = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var vp = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var rs = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var ms = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var ds = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Less,
            };
            var cba = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var cb = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &cba,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dyn = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynStates,
            };

            var gp = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vi,
                PInputAssemblyState = &ia,
                PViewportState = &vp,
                PRasterizationState = &rs,
                PMultisampleState = &ms,
                PDepthStencilState = &ds,
                PColorBlendState = &cb,
                PDynamicState = &dyn,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            Pipeline pipeline;
            VulkanContext.Check(_vk.CreateGraphicsPipelines(_dev, default, 1, &gp, null, &pipeline),
                "CreateGraphicsPipelines");
            _pipeline = pipeline;

            _vk.DestroyShaderModule(_dev, vs, null);
            _vk.DestroyShaderModule(_dev, fs, null);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        }

        // Translucent overlay pipeline for "mask" materials (shield domes, energy beams, shoreline
        // foam): identical vertex format, descriptor layout, and push-constant range as the opaque
        // pipeline (reuses _pipelineLayout — only mesh_blend.frag, standard alpha blending, and
        // depth-write-off differ), so these overlays composite over the opaque pass instead of either
        // fully replacing it (as a hard alpha-test discard would) or corrupting the depth buffer for
        // whatever's drawn after them.
        private void CreateBlendPipeline()
        {
            ShaderModule vs = CreateShader(LoadEmbedded("mesh.vert.spv"));
            ShaderModule fs = CreateShader(LoadEmbedded("mesh_blend.frag.spv"));
            byte* entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vs,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fs,
                PName = entry,
            };

            var binding = new VertexInputBindingDescription(0, 32, VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[3];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);   // pos
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);  // normal
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 24);     // uv
            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attrs,
            };
            var ia = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var vp = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var rs = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var ms = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var ds = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.Less,
            };
            var cba = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
            };
            var cb = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &cba,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dyn = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynStates,
            };

            var gp = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vi,
                PInputAssemblyState = &ia,
                PViewportState = &vp,
                PRasterizationState = &rs,
                PMultisampleState = &ms,
                PDepthStencilState = &ds,
                PColorBlendState = &cb,
                PDynamicState = &dyn,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            Pipeline pipeline;
            VulkanContext.Check(_vk.CreateGraphicsPipelines(_dev, default, 1, &gp, null, &pipeline),
                "CreateGraphicsPipelines(blend)");
            _blendPipeline = pipeline;

            _vk.DestroyShaderModule(_dev, vs, null);
            _vk.DestroyShaderModule(_dev, fs, null);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        }

        // Minimal line-list pipeline for the optional skeleton overlay: position+color vertices, a
        // single mat4 push constant (camera MVP only — bone positions are already fully composed in
        // view-space by SkeletalAnimator), no descriptor sets/textures. Depth-tested against the mesh
        // (so bones are correctly hidden behind opaque geometry) but not depth-written, so overlapping
        // bone segments don't fight each other and nothing after them is affected.
        private void CreateBonePipeline()
        {
            ShaderModule vs = CreateShader(LoadEmbedded("bone.vert.spv"));
            ShaderModule fs = CreateShader(LoadEmbedded("bone.frag.spv"));
            byte* entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");

            var pcRange = new PushConstantRange(ShaderStageFlags.VertexBit, 0, 64);
            var plci = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 0,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pcRange,
            };
            PipelineLayout pipelineLayout;
            VulkanContext.Check(_vk.CreatePipelineLayout(_dev, &plci, null, &pipelineLayout), "BonePipelineLayout");
            _bonePipelineLayout = pipelineLayout;

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vs,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fs,
                PName = entry,
            };

            var binding = new VertexInputBindingDescription(0, 24, VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[2];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);   // pos
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);  // color
            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 2,
                PVertexAttributeDescriptions = attrs,
            };
            var ia = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.LineList,
            };
            var vp = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var rs = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var ms = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var ds = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.Less,
            };
            var cba = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var cb = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &cba,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dyn = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynStates,
            };

            var gp = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vi,
                PInputAssemblyState = &ia,
                PViewportState = &vp,
                PRasterizationState = &rs,
                PMultisampleState = &ms,
                PDepthStencilState = &ds,
                PColorBlendState = &cb,
                PDynamicState = &dyn,
                Layout = _bonePipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            Pipeline pipeline;
            VulkanContext.Check(_vk.CreateGraphicsPipelines(_dev, default, 1, &gp, null, &pipeline),
                "CreateGraphicsPipelines(bone)");
            _bonePipeline = pipeline;

            _vk.DestroyShaderModule(_dev, vs, null);
            _vk.DestroyShaderModule(_dev, fs, null);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
        }

        private void AllocateCommandBuffer()
        {
            var ai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _ctx.CommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer cb;
            VulkanContext.Check(_vk.AllocateCommandBuffers(_dev, &ai, &cb), "AllocateCommandBuffers");
            _cmd = cb;

            var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Fence fence;
            VulkanContext.Check(_vk.CreateFence(_dev, &fci, null, &fence), "CreateFence");
            _fence = fence;
        }

        private ShaderModule CreateShader(byte[] code)
        {
            fixed (byte* p = code)
            {
                var ci = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)p,
                };
                ShaderModule m;
                VulkanContext.Check(_vk.CreateShaderModule(_dev, &ci, null, &m), "CreateShaderModule");
                return m;
            }
        }

        private (Silk.NET.Vulkan.Buffer, DeviceMemory) CreateBuffer(ulong size, BufferUsageFlags usage,
            MemoryPropertyFlags props)
        {
            var bci = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Silk.NET.Vulkan.Buffer buffer;
            VulkanContext.Check(_vk.CreateBuffer(_dev, &bci, null, &buffer), "CreateBuffer");

            _vk.GetBufferMemoryRequirements(_dev, buffer, out MemoryRequirements req);
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = _ctx.FindMemoryType(req.MemoryTypeBits, props),
            };
            DeviceMemory mem;
            VulkanContext.Check(_vk.AllocateMemory(_dev, &ai, null, &mem), "AllocateMemory(buffer)");
            _vk.BindBufferMemory(_dev, buffer, mem, 0);
            return (buffer, mem);
        }

        private (Silk.NET.Vulkan.Buffer, DeviceMemory) CreateHostBuffer<T>(T[] data, BufferUsageFlags usage)
            where T : unmanaged
        {
            ulong size = (ulong)((long)data.Length * sizeof(T));
            (Silk.NET.Vulkan.Buffer buffer, DeviceMemory mem) = CreateBuffer(size, usage,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* mapped = null;
            _vk.MapMemory(_dev, mem, 0, size, 0, ref mapped);
            fixed (T* src = data)
            {
                System.Buffer.MemoryCopy(src, mapped, size, size);
            }
            _vk.UnmapMemory(_dev, mem);
            return (buffer, mem);
        }

        private (Image, DeviceMemory) CreateImage(int w, int h, Format format, ImageUsageFlags usage)
        {
            var ici = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D((uint)w, (uint)h, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                InitialLayout = ImageLayout.Undefined,
                SharingMode = SharingMode.Exclusive,
            };
            Image image;
            VulkanContext.Check(_vk.CreateImage(_dev, &ici, null, &image), "CreateImage");

            _vk.GetImageMemoryRequirements(_dev, image, out MemoryRequirements req);
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = _ctx.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory mem;
            VulkanContext.Check(_vk.AllocateMemory(_dev, &ai, null, &mem), "AllocateMemory(image)");
            _vk.BindImageMemory(_dev, image, mem, 0);
            return (image, mem);
        }

        private ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspect)
        {
            var vci = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
            };
            ImageView view;
            VulkanContext.Check(_vk.CreateImageView(_dev, &vci, null, &view), "CreateImageView");
            return view;
        }

        private static byte[] LoadEmbedded(string name)
        {
            var asm = typeof(MeshViewportRenderer).Assembly;
            using Stream? s = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Missing embedded shader: " + name);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private static void CopyMatrix(Matrix4x4 m, float* dst)
        {
            dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
            dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
            dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
            dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
        }

        // ---- teardown --------------------------------------------------------------------------

        private void DestroyMesh()
        {
            foreach (GpuSubmesh s in _submeshes)
            {
                if (s.Vbuf.Handle != 0) { _vk.DestroyBuffer(_dev, s.Vbuf, null); _vk.FreeMemory(_dev, s.Vmem, null); }
                if (s.Ibuf.Handle != 0) { _vk.DestroyBuffer(_dev, s.Ibuf, null); _vk.FreeMemory(_dev, s.Imem, null); }
            }
            _submeshes.Clear();
            foreach (GpuTexture t in _textures)
            {
                if (t.View.Handle != 0) _vk.DestroyImageView(_dev, t.View, null);
                if (t.Image.Handle != 0) { _vk.DestroyImage(_dev, t.Image, null); _vk.FreeMemory(_dev, t.Mem, null); }
            }
            _textures.Clear();
            if (_descPool.Handle != 0) { _vk.DestroyDescriptorPool(_dev, _descPool, null); _descPool = default; }
        }

        private void DestroyTargets()
        {
            if (_framebuffer.Handle != 0) { _vk.DestroyFramebuffer(_dev, _framebuffer, null); _framebuffer = default; }
            if (_colorView.Handle != 0) { _vk.DestroyImageView(_dev, _colorView, null); _colorView = default; }
            if (_depthView.Handle != 0) { _vk.DestroyImageView(_dev, _depthView, null); _depthView = default; }
            if (_colorImage.Handle != 0) { _vk.DestroyImage(_dev, _colorImage, null); _vk.FreeMemory(_dev, _colorMem, null); _colorImage = default; }
            if (_depthImage.Handle != 0) { _vk.DestroyImage(_dev, _depthImage, null); _vk.FreeMemory(_dev, _depthMem, null); _depthImage = default; }
            if (_readback.Handle != 0) { _vk.DestroyBuffer(_dev, _readback, null); _vk.FreeMemory(_dev, _readbackMem, null); _readback = default; }
        }

        public void Dispose()
        {
            if (_dev.Handle == 0)
            {
                return;
            }
            _vk.DeviceWaitIdle(_dev);
            DestroyMesh();
            ClearSkeletonLines();
            DestroyTargets();
            if (_sampler.Handle != 0) _vk.DestroySampler(_dev, _sampler, null);
            if (_descLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_dev, _descLayout, null);
            if (_fence.Handle != 0) _vk.DestroyFence(_dev, _fence, null);
            if (_pipeline.Handle != 0) _vk.DestroyPipeline(_dev, _pipeline, null);
            if (_blendPipeline.Handle != 0) _vk.DestroyPipeline(_dev, _blendPipeline, null);
            if (_pipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _pipelineLayout, null);
            if (_bonePipeline.Handle != 0) _vk.DestroyPipeline(_dev, _bonePipeline, null);
            if (_bonePipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _bonePipelineLayout, null);
            if (_renderPass.Handle != 0) _vk.DestroyRenderPass(_dev, _renderPass, null);
        }
    }
}
