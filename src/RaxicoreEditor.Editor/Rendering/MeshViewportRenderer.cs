using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RaxicoreEditor.Editor.Documents;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

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
        // DDS decode is BGRA, and the source art is sRGB-encoded like any 2003-era diffuse texture. This
        // has to be the _Srgb variant, not Unorm: sampling it then gives the shader properly linear
        // albedo, which the sRGB colour attachment correctly re-encodes exactly once on write. Sampling as
        // Unorm (the raw encoded bytes, unconverted) applies that same sRGB curve a second time on top of
        // the one already baked into the texture, which is what was producing the washed-out, low-contrast
        // look -- the same fix already applied to the sky panorama in sky.frag, just via the hardware path
        // (a format on the image view) instead of a manual `pow(color, 2.2)` in the shader.
        private const Format TextureFormat = Format.B8G8R8A8Srgb;

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
        // Engine-derived shading variants (optional, toggled by RenderSettings.EngineShading): same
        // pipeline layout / vertex format as the opaque + blend pipelines above, only the shaders differ.
        private Pipeline _enginePipeline;
        private Pipeline _engineBlendPipeline;
        // Procedural sky background (optional): a fullscreen pass drawn before the mesh, with its own layout
        // (no descriptor set; a 128-byte fragment push constant) — no vertex buffer, depth off.
        private Pipeline _skyPipeline;
        private PipelineLayout _skyPipelineLayout;
        private bool _skyEnabled;
        private Matrix4x4 _skyRayVp = Matrix4x4.Identity;
        private Vector4 _skyTint = new(1, 1, 1, 1);
        private Vector4 _skyHorizon; // zone atmosphere/fog colour for the below-horizon falloff
        // The sky panorama texture + its own persistent descriptor (independent of the per-mesh pool, which
        // is rebuilt on every SetMesh). _skyHasTex gates the sky pass until a texture is uploaded.
        private GpuTexture _skyTex;
        private DescriptorPool _skyDescPool;
        private DescriptorSet _skyDescSet;
        private bool _skyHasTex;
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
        private bool _readbackCoherent = true; // false when readback uses HOST_CACHED (needs invalidation)

        // Mesh — one GPU draw unit ("batch"). Static geometry is merged by (texture, translucency) into a
        // handful of large device-local batches (few draw calls, VRAM-resident); each vertex-skinned submesh
        // stays its own host-visible batch so the viewport can rewrite its vertices per animation frame.
        private struct GpuBatch
        {
            public Silk.NET.Vulkan.Buffer Vbuf;
            public DeviceMemory Vmem;
            public Silk.NET.Vulkan.Buffer Ibuf;
            public DeviceMemory Imem;
            public uint IndexCount;
            public uint VertexCount;
            public DescriptorSet DescSet;
            public bool Translucent;
            public bool HostVisible; // true only for skinned batches (per-frame CPU vertex updates)
        }

        // ---- ray tracing (only touched when _ctx.SupportsRayTracing) --------------------------------
        // V1 scope: one BLAS per opaque, non-skinned batch (skinned meshes would need a per-frame BLAS
        // rebuild; translucent surfaces would need any-hit alpha testing) -- both are fast-follow work, not
        // this pass. Shading uses the same baked per-vertex colour the rasterizer falls back to for
        // untextured geometry (no bindless texture array in the hit shader yet, also fast-follow).
        private struct RtBlas
        {
            public Silk.NET.Vulkan.Buffer Buffer;
            public DeviceMemory Mem;
            public AccelerationStructureKHR Handle;
        }
        // Mirrors the shader's `struct InstanceDesc { uint64_t vertexAddr; uint64_t indexAddr; }` (std430:
        // two 8-byte fields, no padding) -- indexed by gl_InstanceCustomIndexEXT in mesh.rchit.
        private struct RtInstanceDesc
        {
            public ulong VertexAddr;
            public ulong IndexAddr;
        }

        private bool _rtPipelineReady;
        private DescriptorSetLayout _rtDescLayout;
        private PipelineLayout _rtPipelineLayout;
        private Pipeline _rtPipeline;
        private DescriptorPool _rtDescPool;
        private DescriptorSet _rtDescSet;
        private Silk.NET.Vulkan.Buffer _sbtBuffer;
        private DeviceMemory _sbtMem;
        private StridedDeviceAddressRegionKHR _sbtRaygen, _sbtMiss, _sbtHit, _sbtCallable;

        private readonly List<RtBlas> _rtBlas = new();
        private Silk.NET.Vulkan.Buffer _rtTlasBuf;
        private DeviceMemory _rtTlasMem;
        private AccelerationStructureKHR _rtTlas;
        private Silk.NET.Vulkan.Buffer _rtInstDescBuf;
        private DeviceMemory _rtInstDescMem;
        private bool _rtSceneReady;

        // Ray tracing writes into its own plain UNORM storage image rather than _colorImage: many drivers
        // do not support STORAGE_IMAGE usage on sRGB formats (imageStore has no sRGB-encoding image format
        // at all in GLSL), so _colorImage's Srgb format is never touched by the RT path -- the shader
        // gamma-encodes by hand (see mesh.rgen) and this image's bytes are copied to the readback buffer
        // directly, bypassing _colorImage entirely for RT frames.
        private const Format RtColorFormat = Format.B8G8R8A8Unorm;
        private Image _rtColorImage;
        private DeviceMemory _rtColorMem;
        private ImageView _rtColorView;
        private struct GpuTexture
        {
            public Image Image;
            public DeviceMemory Mem;
            public ImageView View;
        }
        private readonly List<GpuBatch> _batches = new();
        // Maps an original (skinned) submesh index → its entry in _batches, for UpdateSubmeshVertices.
        private readonly Dictionary<int, int> _skinnedBatch = new();
        private readonly List<GpuTexture> _textures = new();
        private DescriptorPool _descPool;

        // Optional skeleton-overlay line list (position+color per vertex; drawn after the mesh).
        private PipelineLayout _bonePipelineLayout;
        private Pipeline _bonePipeline;      // depth-tested: bones are hidden behind opaque geometry
        private Pipeline _bonePipelineXray;  // depth test off: bones drawn through the model (x-ray)
        private Silk.NET.Vulkan.Buffer _boneVbuf;
        private DeviceMemory _boneVmem;
        private uint _boneVertexCount;

        /// <summary>When true, the skeleton overlay is drawn through the mesh (no depth test) instead of
        /// being occluded by it. Toggled from the viewport; only affects the bone lines, not the trajectory.</summary>
        public bool SkeletonXray { get; set; }
        // Optional trajectory overlay (a bone's path over the clip) — same line pipeline/format as the bones.
        private Silk.NET.Vulkan.Buffer _trajVbuf;
        private DeviceMemory _trajVmem;
        private uint _trajVertexCount;

        public MeshViewportRenderer(VulkanContext ctx)
        {
            _ctx = ctx;
            _vk = ctx.Vk;
            _dev = ctx.Device;
            CreateRenderPass();
            CreateDescriptorLayoutAndSampler();
            CreatePipelineLayout();
            _pipeline = CreateOpaquePipeline("mesh.vert.spv", "mesh.frag.spv");
            _blendPipeline = CreateBlendPipeline("mesh.vert.spv", "mesh_blend.frag.spv");
            _enginePipeline = CreateOpaquePipeline("engine.vert.spv", "engine.frag.spv");
            _engineBlendPipeline = CreateBlendPipeline("engine.vert.spv", "engine_blend.frag.spv");
            CreateSkyPipeline();
            CreateBonePipeline();
            AllocateCommandBuffer();
            CreateRayTracingPipeline();
        }

        // ---- mesh upload -----------------------------------------------------------------------

        public void SetMesh(IReadOnlyList<MeshSubmesh> submeshes)
        {
            _vk.DeviceWaitIdle(_dev);
            DestroyMesh();
            ClearSkeletonLines(); // a newly loaded model's skeleton (if any) is rebuilt separately
            ClearTrajectoryLines();
            if (submeshes.Count == 0)
            {
                return;
            }

            // Collect unique BASE textures (dedupe by BGRA array reference); index 0 is always a 1×1 white.
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

            // Collect unique DETAIL textures (materials.adb's mat_detail) the same way; index 0 is a
            // neutral 50%-grey 1×1. The fragment shader blends base*detail*2 (era-correct D3D8 modulate2x
            // detail-texture op), and grey*2 = 1.0, so a submesh with no detail texture is unaffected —
            // no shader branch needed to distinguish "has detail" from "doesn't".
            var detailIndex = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
            var detailSources = new List<(byte[] bgra, int w, int h)>();
            byte[] neutralDetail = new byte[] { 128, 128, 128, 255 };
            detailSources.Add((neutralDetail, 1, 1)); // no detail = index 0
            foreach (MeshSubmesh s in submeshes)
            {
                if (s.HasDetailTexture && s.DetailTextureBgra != null && !detailIndex.ContainsKey(s.DetailTextureBgra))
                {
                    detailIndex[s.DetailTextureBgra] = detailSources.Count;
                    detailSources.Add((s.DetailTextureBgra, s.DetailTextureWidth, s.DetailTextureHeight));
                }
            }

            int TexOf(MeshSubmesh s) =>
                s.HasTexture && s.TextureBgra != null && texIndex.TryGetValue(s.TextureBgra, out int idx) ? idx : 0;
            int DetailOf(MeshSubmesh s) =>
                s.HasDetailTexture && s.DetailTextureBgra != null && detailIndex.TryGetValue(s.DetailTextureBgra, out int idx) ? idx : 0;

            // One combined (base, detail) descriptor set per DISTINCT PAIR actually used — not per base
            // texture alone, since two materials can share a base texture but differ in detail texture (or
            // vice versa). Enumerated up front so the descriptor pool can be sized exactly.
            var pairIndex = new Dictionary<(int baseIdx, int detailIdx), int>();
            var pairList = new List<(int baseIdx, int detailIdx)>();
            foreach (MeshSubmesh s in submeshes)
            {
                var pair = (TexOf(s), DetailOf(s));
                if (!pairIndex.ContainsKey(pair))
                {
                    pairIndex[pair] = pairList.Count;
                    pairList.Add(pair);
                }
            }

            CreateDescriptorPool((uint)pairList.Count);
            var baseGpuByIndex = new Dictionary<int, GpuTexture>();
            var detailGpuByIndex = new Dictionary<int, GpuTexture>();
            GpuTexture GetBaseGpu(int idx)
            {
                if (!baseGpuByIndex.TryGetValue(idx, out GpuTexture t))
                {
                    t = CreateTexture(texSources[idx].bgra, texSources[idx].w, texSources[idx].h);
                    _textures.Add(t);
                    baseGpuByIndex[idx] = t;
                }
                return t;
            }
            GpuTexture GetDetailGpu(int idx)
            {
                if (!detailGpuByIndex.TryGetValue(idx, out GpuTexture t))
                {
                    t = CreateTexture(detailSources[idx].bgra, detailSources[idx].w, detailSources[idx].h);
                    _textures.Add(t);
                    detailGpuByIndex[idx] = t;
                }
                return t;
            }
            var pairDescSets = new DescriptorSet[pairList.Count];
            for (int p = 0; p < pairList.Count; p++)
            {
                GpuTexture b = GetBaseGpu(pairList[p].baseIdx);
                GpuTexture d = GetDetailGpu(pairList[p].detailIdx);
                pairDescSets[p] = AllocateTextureDescriptor(b.View, d.View);
            }
            int PairOf(MeshSubmesh s) => pairIndex[(TexOf(s), DetailOf(s))];

            // Pass 1: tally the merged size of each static (non-skinned) group, keyed by (texture pair,
            // translucency). Skinned submeshes are handled individually below and excluded from merging.
            var groupTotals = new Dictionary<(int pair, bool trans), (long verts, long indices)>();
            for (int i = 0; i < submeshes.Count; i++)
            {
                MeshSubmesh s = submeshes[i];
                if (s.Indices.Length == 0 || s.IsSkinned)
                {
                    continue;
                }
                var key = (PairOf(s), s.IsTranslucent);
                groupTotals.TryGetValue(key, out (long verts, long indices) t);
                t.verts += s.Vertices.Length / 8;
                t.indices += s.Indices.Length;
                groupTotals[key] = t;
            }

            // Allocate one merged (stride-14 vertex, uint index) CPU array per group, then fill them in a
            // second pass — offsetting each submesh's indices by the group's running vertex count. This is
            // one big device-local buffer pair per group instead of a pair per submesh (58k → a few hundred),
            // which is the bulk of the load-time and per-frame win.
            var groupData = new Dictionary<(int, bool), (float[] v, uint[] ix, int vOff, int iOff, int vBase)>();
            foreach (KeyValuePair<(int pair, bool trans), (long verts, long indices)> g in groupTotals)
            {
                groupData[g.Key] = (new float[g.Value.verts * 14], new uint[g.Value.indices], 0, 0, 0);
            }
            for (int i = 0; i < submeshes.Count; i++)
            {
                MeshSubmesh s = submeshes[i];
                if (s.Indices.Length == 0 || s.IsSkinned)
                {
                    continue;
                }
                var key = (PairOf(s), s.IsTranslucent);
                (float[] v, uint[] ix, int vOff, int iOff, int vBase) d = groupData[key];
                int vc = s.Vertices.Length / 8;
                AppendVertices(d.v, d.vOff, s.Vertices, s.Colors, s.DetailTileRate);
                for (int k = 0; k < s.Indices.Length; k++)
                {
                    d.ix[d.iOff + k] = s.Indices[k] + (uint)d.vBase;
                }
                d.vOff += vc * 14;
                d.iOff += s.Indices.Length;
                d.vBase += vc;
                groupData[key] = d;
            }
            foreach (KeyValuePair<(int pair, bool trans), (float[] v, uint[] ix, int vOff, int iOff, int vBase)> g in groupData)
            {
                if (g.Value.ix.Length == 0)
                {
                    continue;
                }
                (Silk.NET.Vulkan.Buffer vbuf, DeviceMemory vmem) =
                    CreateDeviceLocalBuffer<float>(g.Value.v, BufferUsageFlags.VertexBufferBit);
                (Silk.NET.Vulkan.Buffer ibuf, DeviceMemory imem) =
                    CreateDeviceLocalBuffer<uint>(g.Value.ix, BufferUsageFlags.IndexBufferBit);
                _batches.Add(new GpuBatch
                {
                    Vbuf = vbuf, Vmem = vmem, Ibuf = ibuf, Imem = imem,
                    IndexCount = (uint)g.Value.ix.Length, VertexCount = (uint)(g.Value.v.Length / 14),
                    DescSet = pairDescSets[g.Key.pair], Translucent = g.Key.trans, HostVisible = false,
                });
            }

            // Skinned submeshes: each keeps its own host-visible vertex buffer (rewritten per animation
            // frame by UpdateSubmeshVertices) and a static device-local index buffer.
            for (int i = 0; i < submeshes.Count; i++)
            {
                MeshSubmesh s = submeshes[i];
                if (s.Indices.Length == 0 || !s.IsSkinned)
                {
                    continue;
                }
                float[] vbData = BuildVertexBuffer(s.Vertices, s.Colors, s.DetailTileRate);
                (Silk.NET.Vulkan.Buffer vbuf, DeviceMemory vmem) =
                    CreateHostBuffer<float>(vbData, BufferUsageFlags.VertexBufferBit);
                (Silk.NET.Vulkan.Buffer ibuf, DeviceMemory imem) =
                    CreateDeviceLocalBuffer<uint>(s.Indices, BufferUsageFlags.IndexBufferBit);
                _skinnedBatch[i] = _batches.Count;
                _batches.Add(new GpuBatch
                {
                    Vbuf = vbuf, Vmem = vmem, Ibuf = ibuf, Imem = imem,
                    IndexCount = (uint)s.Indices.Length, VertexCount = (uint)(vbData.Length / 14),
                    DescSet = pairDescSets[PairOf(s)], Translucent = s.IsTranslucent, HostVisible = true,
                });
            }

            BuildRayTracingScene();
        }

        /// <summary>Number of GPU draw batches (merged static groups + individual skinned submeshes).</summary>
        public int SubmeshCount => _batches.Count;

        // Interleave one submesh's stride-8 geometry + optional stride-4 colour directly into the merged
        // group array at float offset <paramref name="dstOff"/> (stride-14), avoiding a per-submesh temp.
        // The trailing 2 floats are the detail-texture UV (base UV * tileRate), baked once here at build
        // time — cheaper than a per-frame shader uniform and correct since tileRate is a material constant.
        private static void AppendVertices(float[] dst, int dstOff, float[] v8, float[]? c4, float tileRate)
        {
            int vc = v8.Length / 8;
            for (int i = 0; i < vc; i++)
            {
                int si = i * 8, di = dstOff + i * 14;
                dst[di + 0] = v8[si + 0]; dst[di + 1] = v8[si + 1]; dst[di + 2] = v8[si + 2];
                dst[di + 3] = v8[si + 3]; dst[di + 4] = v8[si + 4]; dst[di + 5] = v8[si + 5];
                dst[di + 6] = v8[si + 6]; dst[di + 7] = v8[si + 7];
                if (c4 != null)
                {
                    dst[di + 8] = c4[i * 4 + 0]; dst[di + 9] = c4[i * 4 + 1];
                    dst[di + 10] = c4[i * 4 + 2]; dst[di + 11] = c4[i * 4 + 3];
                }
                else
                {
                    dst[di + 8] = 1f; dst[di + 9] = 1f; dst[di + 10] = 1f; dst[di + 11] = 1f;
                }
                dst[di + 12] = v8[si + 6] * tileRate; dst[di + 13] = v8[si + 7] * tileRate;
            }
        }

        // Interleave stride-8 geometry (pos3, normal3, uv2) with stride-4 baked colour (rgba) and a
        // detail UV (base uv * tileRate) into the stride-14 vertex all four mesh pipelines read. Colour
        // defaults to white when none is supplied.
        private static float[] BuildVertexBuffer(float[] v8, float[]? c4, float tileRate)
        {
            int vc = v8.Length / 8;
            var outp = new float[vc * 14];
            for (int i = 0; i < vc; i++)
            {
                int si = i * 8, di = i * 14;
                outp[di + 0] = v8[si + 0]; outp[di + 1] = v8[si + 1]; outp[di + 2] = v8[si + 2];
                outp[di + 3] = v8[si + 3]; outp[di + 4] = v8[si + 4]; outp[di + 5] = v8[si + 5];
                outp[di + 6] = v8[si + 6]; outp[di + 7] = v8[si + 7];
                if (c4 != null)
                {
                    outp[di + 8] = c4[i * 4 + 0]; outp[di + 9] = c4[i * 4 + 1];
                    outp[di + 10] = c4[i * 4 + 2]; outp[di + 11] = c4[i * 4 + 3];
                }
                else
                {
                    outp[di + 8] = 1f; outp[di + 9] = 1f; outp[di + 10] = 1f; outp[di + 11] = 1f;
                }
                outp[di + 12] = v8[si + 6] * tileRate; outp[di + 13] = v8[si + 7] * tileRate;
            }
            return outp;
        }

        /// <summary>
        /// Overwrite a submesh's vertex buffer in place (host-visible, no recreation) — used for per-frame
        /// CPU skinning. The vertex count must be unchanged. Safe because Render() is synchronous (waits
        /// its fence), so the GPU is idle between frames.
        /// </summary>
        public void UpdateSubmeshVertices(int index, float[] verts)
        {
            // `index` is an original (skinned) submesh index; only skinned submeshes have host-visible
            // vertex buffers. Static merged batches are device-local and never updated here.
            if (!_skinnedBatch.TryGetValue(index, out int batch))
            {
                return;
            }
            GpuBatch s = _batches[batch];
            if (s.Vbuf.Handle == 0 || !s.HostVisible)
            {
                return;
            }
            // The GPU buffer is stride-14 (geometry + baked colour + detail UV); the incoming skinned data
            // is stride-8 geometry only. Write the 8 geometry floats of each vertex and leave the 4 colour
            // floats and 2 detail-UV floats (set at upload) untouched, so re-skinning a pre-lit part keeps
            // its baked colour and detail tiling.
            int vc = verts.Length / 8;
            ulong size = (ulong)((long)vc * 14 * sizeof(float));
            void* mapped = null;
            _vk.MapMemory(_dev, s.Vmem, 0, size, 0, ref mapped);
            var dst = (float*)mapped;
            fixed (float* src = verts)
            {
                for (int i = 0; i < vc; i++)
                {
                    float* d = dst + i * 14;
                    float* sp = src + i * 8;
                    for (int k = 0; k < 8; k++) d[k] = sp[k];
                }
            }
            _vk.UnmapMemory(_dev, s.Vmem);
        }

        /// <summary>
        /// Set the sky for the next frame. <paramref name="rayVp"/> is the inverse of the translation-stripped
        /// view-projection (maps NDC to a world ray direction); <paramref name="tint"/> multiplies the sampled
        /// panorama (0-1 RGB, from the zone's ambient mood). The sky only draws when enabled AND a panorama has
        /// been uploaded via <see cref="SetSkyTexture"/>.
        /// </summary>
        public void SetSky(bool enabled, Matrix4x4 rayVp, Vector3 tint, Vector3 horizon)
        {
            _skyEnabled = enabled;
            _skyRayVp = rayVp;
            _skyTint = new Vector4(tint, 1f);
            _skyHorizon = new Vector4(horizon, 0f);
        }

        /// <summary>Upload the zone's equirectangular sky panorama (BGRA). Replaces any previous one; safe to
        /// call between frames (Render is synchronous). Pass a null/empty buffer to clear it.</summary>
        public void SetSkyTexture(byte[]? bgra, int w, int h)
        {
            _vk.DeviceWaitIdle(_dev);
            if (_skyTex.Image.Handle != 0)
            {
                _vk.DestroyImageView(_dev, _skyTex.View, null);
                _vk.DestroyImage(_dev, _skyTex.Image, null);
                _vk.FreeMemory(_dev, _skyTex.Mem, null);
                _skyTex = default;
            }
            _skyHasTex = false;
            if (bgra == null || bgra.Length < w * h * 4 || w <= 0 || h <= 0)
            {
                return;
            }
            _skyTex = CreateTexture(bgra, w, h);
            var imageInfo = new DescriptorImageInfo { Sampler = _sampler, ImageView = _skyTex.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _skyDescSet, DstBinding = 0, DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &imageInfo,
            };
            _vk.UpdateDescriptorSets(_dev, 1, &write, 0, null);
            _skyHasTex = true;
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

        /// <summary>Replace the trajectory line list — a bone's path over the clip, as a view-space line list
        /// (px,py,pz, r,g,b), drawn with the same overlay pipeline as the skeleton.</summary>
        public void SetTrajectoryLines(float[] posColor)
        {
            ClearTrajectoryLines();
            uint vertexCount = (uint)(posColor.Length / 6);
            if (vertexCount == 0)
            {
                return;
            }
            (_trajVbuf, _trajVmem) = CreateHostBuffer<float>(posColor, BufferUsageFlags.VertexBufferBit);
            _trajVertexCount = vertexCount;
        }

        public void ClearTrajectoryLines()
        {
            if (_trajVbuf.Handle != 0) { _vk.DestroyBuffer(_dev, _trajVbuf, null); _vk.FreeMemory(_dev, _trajVmem, null); }
            _trajVbuf = default;
            _trajVmem = default;
            _trajVertexCount = 0;
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

            if (_ctx.SupportsRayTracing)
            {
                (_rtColorImage, _rtColorMem) = CreateImage(width, height, RtColorFormat,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit);
                _rtColorView = CreateImageView(_rtColorImage, RtColorFormat, ImageAspectFlags.ColorBit);
                WriteRtImageDescriptor();
            }

            ulong size = (ulong)width * (ulong)height * 4UL;
            CreateReadbackBuffer(size);
        }

        // The per-frame framebuffer readback is CPU-read-bound: the shader/GPU write the color image, then
        // the CPU copies it out to the WriteableBitmap. HOST_COHERENT memory is typically write-combined
        // (uncached), and reading uncached memory is *catastrophically* slow (~150 MB/s → tens of ms for a
        // 1080p frame). HOST_CACHED memory makes CPU reads fast; it may be non-coherent, so the read path
        // invalidates the mapped range first. Falls back to coherent if the device has no cached type.
        private void CreateReadbackBuffer(ulong size)
        {
            var bci = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };
            Silk.NET.Vulkan.Buffer buffer;
            VulkanContext.Check(_vk.CreateBuffer(_dev, &bci, null, &buffer), "CreateBuffer(readback)");
            _vk.GetBufferMemoryRequirements(_dev, buffer, out MemoryRequirements req);

            uint typeIndex;
            if (_ctx.TryFindMemoryType(req.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit, out typeIndex))
            {
                _readbackCoherent = _ctx.MemoryTypeIsCoherent(typeIndex);
            }
            else
            {
                typeIndex = _ctx.FindMemoryType(req.MemoryTypeBits,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                _readbackCoherent = true;
            }

            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = typeIndex,
            };
            DeviceMemory mem;
            VulkanContext.Check(_vk.AllocateMemory(_dev, &ai, null, &mem), "AllocateMemory(readback)");
            _vk.BindBufferMemory(_dev, buffer, mem, 0);
            _readback = buffer;
            _readbackMem = mem;
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

            bool rt = RenderSettings.RayTracing && _ctx.SupportsRayTracing && _rtPipelineReady &&
                      _rtSceneReady && _rtColorImage.Handle != 0;

            _vk.ResetCommandBuffer(_cmd, 0);
            var bi = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _vk.BeginCommandBuffer(_cmd, &bi);

            if (rt)
            {
                RecordRayTrace(mvp);
            }
            else
            {
                RecordRasterize(mvp, model);
            }

            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)_width, (uint)_height, 1),
            };
            Image srcImage = rt ? _rtColorImage : _colorImage;
            _vk.CmdCopyImageToBuffer(_cmd, srcImage, ImageLayout.TransferSrcOptimal, _readback, 1, &region);

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
            // HOST_CACHED readback memory may be non-coherent: make the GPU's writes visible to the CPU
            // cache before reading. (Coherent memory needs no invalidation.)
            if (!_readbackCoherent)
            {
                var range = new MappedMemoryRange
                {
                    SType = StructureType.MappedMemoryRange,
                    Memory = _readbackMem,
                    Offset = 0,
                    Size = Vk.WholeSize,
                };
                _vk.InvalidateMappedMemoryRanges(_dev, 1, &range);
            }
            new ReadOnlySpan<byte>(mapped, (int)size).CopyTo(dst);
            _vk.UnmapMemory(_dev, _readbackMem);
            return true;
        }

        private void RecordRasterize(Matrix4x4 mvp, Matrix4x4 model)
        {
            var clears = stackalloc ClearValue[2];
            clears[0] = new ClearValue(new ClearColorValue(0.04f, 0.04f, 0.05f, 1f));
            clears[1] = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0)); // reversed-Z: far = 0

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

            // Textured sky first (fills the background), then the mesh draws over it with its own depth.
            if (_skyEnabled && _skyHasTex)
            {
                var skyPush = stackalloc float[24]; // mat4 (16) + vec4 tint (4) + vec4 horizon (4)
                CopyMatrix(_skyRayVp, skyPush);
                skyPush[16] = _skyTint.X; skyPush[17] = _skyTint.Y; skyPush[18] = _skyTint.Z; skyPush[19] = _skyTint.W;
                skyPush[20] = _skyHorizon.X; skyPush[21] = _skyHorizon.Y; skyPush[22] = _skyHorizon.Z; skyPush[23] = _skyHorizon.W;
                DescriptorSet sset = _skyDescSet;
                _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, _skyPipeline);
                _vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, _skyPipelineLayout, 0, 1, &sset, 0, null);
                _vk.CmdPushConstants(_cmd, _skyPipelineLayout, ShaderStageFlags.FragmentBit, 0, 96, skyPush);
                _vk.CmdDraw(_cmd, 3, 1, 0, 0);
            }

            if (_batches.Count > 0)
            {
                var push = stackalloc float[32];
                CopyMatrix(mvp, push);
                CopyMatrix(model, push + 16);
                _vk.CmdPushConstants(_cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, 128, push);
                ulong offset = 0;

                // Engine-derived shading toggle: same layout/vertex format, so it's a pure pipeline swap.
                bool engine = RenderSettings.EngineShading;

                // Opaque/cutout pass first (depth write on), then translucent overlays (depth write
                // off) so masks/shields/beams correctly composite over whatever is already drawn.
                _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, engine ? _enginePipeline : _pipeline);
                foreach (GpuBatch s in _batches)
                {
                    if (s.Translucent) continue;
                    DescriptorSet ds = s.DescSet;
                    _vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &ds, 0, null);
                    var vbuf = s.Vbuf;
                    _vk.CmdBindVertexBuffers(_cmd, 0, 1, &vbuf, &offset);
                    _vk.CmdBindIndexBuffer(_cmd, s.Ibuf, 0, IndexType.Uint32);
                    _vk.CmdDrawIndexed(_cmd, s.IndexCount, 1, 0, 0, 0);
                }

                _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, engine ? _engineBlendPipeline : _blendPipeline);
                foreach (GpuBatch s in _batches)
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
                Pipeline bonePipe = SkeletonXray && _bonePipelineXray.Handle != 0 ? _bonePipelineXray : _bonePipeline;
                _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, bonePipe);
                var bonePush = stackalloc float[16];
                CopyMatrix(mvp, bonePush);
                _vk.CmdPushConstants(_cmd, _bonePipelineLayout, ShaderStageFlags.VertexBit, 0, 64, bonePush);
                ulong boneOffset = 0;
                var boneVbuf = _boneVbuf;
                _vk.CmdBindVertexBuffers(_cmd, 0, 1, &boneVbuf, &boneOffset);
                _vk.CmdDraw(_cmd, _boneVertexCount, 1, 0, 0);
            }

            if (_trajVertexCount > 0)
            {
                _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, _bonePipeline);
                var trajPush = stackalloc float[16];
                CopyMatrix(mvp, trajPush);
                _vk.CmdPushConstants(_cmd, _bonePipelineLayout, ShaderStageFlags.VertexBit, 0, 64, trajPush);
                ulong trajOffset = 0;
                var trajVbuf = _trajVbuf;
                _vk.CmdBindVertexBuffers(_cmd, 0, 1, &trajVbuf, &trajOffset);
                _vk.CmdDraw(_cmd, _trajVertexCount, 1, 0, 0);
            }

            _vk.CmdEndRenderPass(_cmd);
        }

        // Ray-traced equivalent of RecordRasterize: no render pass, dispatches vkCmdTraceRaysKHR into the
        // dedicated RT output image (see RtColorFormat's doc comment for why it isn't _colorImage), with
        // the layout transitions the render pass would otherwise have handled for us via its attachment
        // description/subpass dependency.
        private void RecordRayTrace(Matrix4x4 mvp)
        {
            var toGeneral = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                // Contents are irrelevant going in (every pixel is about to be overwritten by imageStore),
                // so OldLayout = Undefined is correct regardless of the image's actual previous layout.
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _rtColorImage,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                DstAccessMask = AccessFlags.ShaderWriteBit,
            };
            _vk.CmdPipelineBarrier(_cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.RayTracingShaderBitKhr,
                0, 0, null, 0, null, 1, &toGeneral);

            _vk.CmdBindPipeline(_cmd, PipelineBindPoint.RayTracingKhr, _rtPipeline);
            var ds = _rtDescSet;
            _vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.RayTracingKhr, _rtPipelineLayout, 0, 1, &ds, 0, null);

            // model is always identity in this viewer (see the caller of Render), so mvp == viewProj and
            // its inverse is all the raygen shader needs to reconstruct world-space camera rays.
            Matrix4x4.Invert(mvp, out Matrix4x4 invViewProj);
            var push = stackalloc float[16];
            CopyMatrix(invViewProj, push);
            _vk.CmdPushConstants(_cmd, _rtPipelineLayout, ShaderStageFlags.RaygenBitKhr, 0, 64, push);

            var raygen = _sbtRaygen;
            var miss = _sbtMiss;
            var hit = _sbtHit;
            var callable = _sbtCallable;
            _ctx.KhrRayTracingPipeline!.CmdTraceRays(_cmd, &raygen, &miss, &hit, &callable, (uint)_width, (uint)_height, 1);

            var toTransferSrc = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.General,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _rtColorImage,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
            };
            _vk.CmdPipelineBarrier(_cmd, PipelineStageFlags.RayTracingShaderBitKhr, PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferSrc);
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
            // binding0 = base albedo, binding1 = the tiled detail texture (materials.adb's mat_detail) --
            // a neutral 50%-grey 1×1 when a material has none (see SetMesh), so the shader can always
            // sample+blend both with no branch.
            var bindings = stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
            bindings[1] = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
            var dlci = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = bindings,
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
            // 2 combined-image-sampler descriptors per set (base + detail texture).
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = maxSets * 2,
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

        private DescriptorSet AllocateTextureDescriptor(ImageView baseView, ImageView detailView)
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

            var baseInfo = new DescriptorImageInfo
            {
                Sampler = _sampler,
                ImageView = baseView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var detailInfo = new DescriptorImageInfo
            {
                Sampler = _sampler,
                ImageView = detailView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &baseInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 1,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &detailInfo,
            };
            _vk.UpdateDescriptorSets(_dev, 2, writes, 0, null);
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

        // Shared pipeline layout for all four mesh pipelines (opaque/blend × generic/engine): base +
        // detail texture samplers (set 0) + a 128-byte vertex push constant (mvp, model). The engine
        // pipelines only ever sample binding0 — declaring more bindings than a given shader reads is legal.
        private void CreatePipelineLayout()
        {
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
        }

        // Opaque/cutout mesh pipeline (depth write on, no blend), parameterised by shader so the generic
        // (mesh.*) and engine-derived (engine.*) variants share one body.
        private Pipeline CreateOpaquePipeline(string vertSpv, string fragSpv)
        {
            ShaderModule vs = CreateShader(LoadEmbedded(vertSpv));
            ShaderModule fs = CreateShader(LoadEmbedded(fragSpv));
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

            var binding = new VertexInputBindingDescription(0, 56, VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[5];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);   // pos
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);  // normal
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 24);     // uv
            attrs[3] = new VertexInputAttributeDescription(3, 0, Format.R32G32B32A32Sfloat, 32); // baked colour (rgba)
            attrs[4] = new VertexInputAttributeDescription(4, 0, Format.R32G32Sfloat, 48);     // detail uv (= uv * tileRate)
            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 5,
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
                DepthCompareOp = CompareOp.Greater, // reversed-Z
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

            _vk.DestroyShaderModule(_dev, vs, null);
            _vk.DestroyShaderModule(_dev, fs, null);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
            return pipeline;
        }

        // Translucent overlay pipeline for "mask" materials (shield domes, energy beams, shoreline
        // foam): identical vertex format, descriptor layout, and push-constant range as the opaque
        // pipeline (reuses _pipelineLayout — only mesh_blend.frag, standard alpha blending, and
        // depth-write-off differ), so these overlays composite over the opaque pass instead of either
        // fully replacing it (as a hard alpha-test discard would) or corrupting the depth buffer for
        // whatever's drawn after them.
        private Pipeline CreateBlendPipeline(string vertSpv, string fragSpv)
        {
            ShaderModule vs = CreateShader(LoadEmbedded(vertSpv));
            ShaderModule fs = CreateShader(LoadEmbedded(fragSpv));
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

            var binding = new VertexInputBindingDescription(0, 56, VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[5];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);   // pos
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);  // normal
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 24);     // uv
            attrs[3] = new VertexInputAttributeDescription(3, 0, Format.R32G32B32A32Sfloat, 32); // baked colour (rgba)
            attrs[4] = new VertexInputAttributeDescription(4, 0, Format.R32G32Sfloat, 48);     // detail uv (= uv * tileRate)
            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 5,
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
                DepthCompareOp = CompareOp.Greater, // reversed-Z
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

            _vk.DestroyShaderModule(_dev, vs, null);
            _vk.DestroyShaderModule(_dev, fs, null);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);
            return pipeline;
        }

        // Procedural sky pipeline: a fullscreen triangle (no vertex buffer — corners from gl_VertexIndex),
        // a 128-byte fragment push constant (inverse ray view-proj + colours), no descriptor sets, depth
        // test/write off (it fills the background before the mesh draws over it).
        private void CreateSkyPipeline()
        {
            ShaderModule vs = CreateShader(LoadEmbedded("sky.vert.spv"));
            ShaderModule fs = CreateShader(LoadEmbedded("sky.frag.spv"));
            byte* entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");

            var pcRange = new PushConstantRange(ShaderStageFlags.FragmentBit, 0, 96); // mat4 invRayVp + vec4 tint + vec4 horizon
            DescriptorSetLayout descLayout = _descLayout;                            // sky.frag only samples binding0
            var plci = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &descLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pcRange,
            };
            PipelineLayout layout;
            VulkanContext.Check(_vk.CreatePipelineLayout(_dev, &plci, null, &layout), "SkyPipelineLayout");
            _skyPipelineLayout = layout;

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vs, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fs, PName = entry };

            var vi = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo }; // no inputs
            var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var rs = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise, LineWidth = 1f };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = false, DepthWriteEnable = false, DepthCompareOp = CompareOp.Always };
            var cba = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, BlendEnable = false };
            var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &cba };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dyn = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates };

            var gp = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2, PStages = stages,
                PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &vp,
                PRasterizationState = &rs, PMultisampleState = &ms, PDepthStencilState = &ds,
                PColorBlendState = &cb, PDynamicState = &dyn,
                Layout = _skyPipelineLayout, RenderPass = _renderPass, Subpass = 0,
            };
            Pipeline pipeline;
            VulkanContext.Check(_vk.CreateGraphicsPipelines(_dev, default, 1, &gp, null, &pipeline), "CreateGraphicsPipelines(sky)");
            _skyPipeline = pipeline;

            _vk.DestroyShaderModule(_dev, vs, null);
            _vk.DestroyShaderModule(_dev, fs, null);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);

            // Dedicated descriptor pool + set for the sky panorama, persistent across mesh reloads. Sized
            // for 2 combined-image-samplers per set (matching _descLayout's binding0+binding1) even though
            // sky.frag only ever reads binding0 -- allocating a set from a layout reserves capacity for
            // every binding in that layout, regardless of which ones the pipeline using it actually samples.
            var skyPoolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 2 };
            var skyPci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &skyPoolSize, MaxSets = 1 };
            DescriptorPool skyPool;
            VulkanContext.Check(_vk.CreateDescriptorPool(_dev, &skyPci, null, &skyPool), "SkyDescriptorPool");
            _skyDescPool = skyPool;
            DescriptorSetLayout dl = _descLayout;
            var sai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _skyDescPool, DescriptorSetCount = 1, PSetLayouts = &dl };
            DescriptorSet skySet;
            VulkanContext.Check(_vk.AllocateDescriptorSets(_dev, &sai, &skySet), "SkyDescriptorSet");
            _skyDescSet = skySet;
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
                DepthCompareOp = CompareOp.Greater, // reversed-Z
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

            // Second variant for x-ray mode: identical, but with the depth test off so the bones draw
            // through the model. gp.PDepthStencilState still points at `ds`, so mutating it and rebuilding
            // yields the no-depth-test pipeline.
            ds.DepthTestEnable = false;
            ds.DepthCompareOp = CompareOp.Always;
            Pipeline pipelineXray;
            VulkanContext.Check(_vk.CreateGraphicsPipelines(_dev, default, 1, &gp, null, &pipelineXray),
                "CreateGraphicsPipelines(bone-xray)");
            _bonePipelineXray = pipelineXray;

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

        // ---- ray tracing pipeline (built once; independent of any loaded mesh) -----------------------

        // Descriptor set layout: binding 0 = TLAS (raygen), binding 1 = output storage image (raygen),
        // binding 2 = per-instance vertex/index buffer-address SSBO (closest-hit). Push constant carries
        // the inverse view-projection the raygen shader unprojects screen pixels through.
        private void CreateRayTracingPipeline()
        {
            if (!_ctx.SupportsRayTracing)
            {
                return;
            }
            KhrRayTracingPipeline khrRtp = _ctx.KhrRayTracingPipeline!;

            var dslBindings = stackalloc DescriptorSetLayoutBinding[3];
            dslBindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0, DescriptorType = DescriptorType.AccelerationStructureKhr,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.RaygenBitKhr,
            };
            dslBindings[1] = new DescriptorSetLayoutBinding
            {
                Binding = 1, DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.RaygenBitKhr,
            };
            dslBindings[2] = new DescriptorSetLayoutBinding
            {
                Binding = 2, DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ClosestHitBitKhr,
            };
            var dslCi = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = dslBindings,
            };
            DescriptorSetLayout dsl;
            VulkanContext.Check(_vk.CreateDescriptorSetLayout(_dev, &dslCi, null, &dsl), "DescriptorSetLayout(rt)");
            _rtDescLayout = dsl;

            var pcRange = new PushConstantRange(ShaderStageFlags.RaygenBitKhr, 0, 64); // mat4 invViewProj
            var plCi = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1, PSetLayouts = &dsl,
                PushConstantRangeCount = 1, PPushConstantRanges = &pcRange,
            };
            PipelineLayout pipeLayout;
            VulkanContext.Check(_vk.CreatePipelineLayout(_dev, &plCi, null, &pipeLayout), "PipelineLayout(rt)");
            _rtPipelineLayout = pipeLayout;

            ShaderModule rgen = CreateShader(LoadEmbedded("mesh.rgen.spv"));
            ShaderModule rmiss = CreateShader(LoadEmbedded("mesh.rmiss.spv"));
            ShaderModule rchit = CreateShader(LoadEmbedded("mesh.rchit.spv"));
            byte* entry = (byte*)Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");

            var stages = stackalloc PipelineShaderStageCreateInfo[3];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.RaygenBitKhr, Module = rgen, PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.MissBitKhr, Module = rmiss, PName = entry,
            };
            stages[2] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ClosestHitBitKhr, Module = rchit, PName = entry,
            };

            var groups = stackalloc RayTracingShaderGroupCreateInfoKHR[3];
            groups[0] = new RayTracingShaderGroupCreateInfoKHR
            {
                SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
                GeneralShader = 0, ClosestHitShader = Vk.ShaderUnusedKhr, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr,
            };
            groups[1] = new RayTracingShaderGroupCreateInfoKHR
            {
                SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
                GeneralShader = 1, ClosestHitShader = Vk.ShaderUnusedKhr, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr,
            };
            groups[2] = new RayTracingShaderGroupCreateInfoKHR
            {
                SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.TrianglesHitGroupKhr,
                GeneralShader = Vk.ShaderUnusedKhr, ClosestHitShader = 2, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr,
            };

            var rtpCi = new RayTracingPipelineCreateInfoKHR
            {
                SType = StructureType.RayTracingPipelineCreateInfoKhr,
                StageCount = 3, PStages = stages, GroupCount = 3, PGroups = groups,
                MaxPipelineRayRecursionDepth = 1, Layout = pipeLayout,
            };
            Pipeline pipeline;
            VulkanContext.Check(
                khrRtp.CreateRayTracingPipelines(_dev, new DeferredOperationKHR(), default, 1, &rtpCi, null, &pipeline),
                "CreateRayTracingPipelines");
            _rtPipeline = pipeline;

            _vk.DestroyShaderModule(_dev, rgen, null);
            _vk.DestroyShaderModule(_dev, rmiss, null);
            _vk.DestroyShaderModule(_dev, rchit, null);
            Silk.NET.Core.Native.SilkMarshal.Free((nint)entry);

            BuildShaderBindingTable(khrRtp);

            var poolSizes = stackalloc DescriptorPoolSize[3];
            poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.AccelerationStructureKhr, DescriptorCount = 1 };
            poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 1 };
            poolSizes[2] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 1 };
            var dpCi = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 3, PPoolSizes = poolSizes,
            };
            DescriptorPool pool;
            VulkanContext.Check(_vk.CreateDescriptorPool(_dev, &dpCi, null, &pool), "DescriptorPool(rt)");
            _rtDescPool = pool;

            var dsAlloc = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _rtDescPool,
                DescriptorSetCount = 1, PSetLayouts = &dsl,
            };
            DescriptorSet set;
            VulkanContext.Check(_vk.AllocateDescriptorSets(_dev, &dsAlloc, &set), "AllocateDescriptorSets(rt)");
            _rtDescSet = set;

            _rtPipelineReady = true;
        }

        private void BuildShaderBindingTable(KhrRayTracingPipeline khrRtp)
        {
            PhysicalDeviceRayTracingPipelinePropertiesKHR props = _ctx.RayTracingProperties;
            uint handleSize = props.ShaderGroupHandleSize;
            uint handleSizeAligned = AlignUp(handleSize, props.ShaderGroupHandleAlignment);

            const uint groupCount = 3;
            uint handlesTotalSize = groupCount * handleSize;
            byte[] handles = new byte[handlesTotalSize];
            fixed (byte* pHandles = handles)
            {
                VulkanContext.Check(
                    khrRtp.GetRayTracingShaderGroupHandles(_dev, _rtPipeline, 0, groupCount, (nuint)handlesTotalSize, pHandles),
                    "GetRayTracingShaderGroupHandles");
            }

            // One region per group (raygen, miss, hit), each padded to the base alignment; one handle each.
            ulong regionSize = AlignUp((ulong)handleSizeAligned, props.ShaderGroupBaseAlignment);
            ulong sbtSize = regionSize * groupCount;
            (_sbtBuffer, _sbtMem) = CreateBuffer(sbtSize,
                BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped = null;
            _vk.MapMemory(_dev, _sbtMem, 0, sbtSize, 0, ref mapped);
            byte* sbtBytes = (byte*)mapped;
            fixed (byte* pHandles = handles)
            {
                for (uint g = 0; g < groupCount; g++)
                {
                    System.Buffer.MemoryCopy(pHandles + g * handleSize, sbtBytes + (ulong)g * regionSize, handleSize, handleSize);
                }
            }
            _vk.UnmapMemory(_dev, _sbtMem);

            ulong sbtAddr = GetBufferAddress(_sbtBuffer);
            _sbtRaygen = new StridedDeviceAddressRegionKHR { DeviceAddress = sbtAddr + 0 * regionSize, Stride = regionSize, Size = regionSize };
            _sbtMiss = new StridedDeviceAddressRegionKHR { DeviceAddress = sbtAddr + 1 * regionSize, Stride = regionSize, Size = regionSize };
            _sbtHit = new StridedDeviceAddressRegionKHR { DeviceAddress = sbtAddr + 2 * regionSize, Stride = regionSize, Size = regionSize };
            _sbtCallable = default;
        }

        // Builds one BLAS per opaque/non-skinned batch + one TLAS (identity-transform instances) + the
        // per-instance vertex/index device-address SSBO the closest-hit shader indexes with
        // gl_InstanceCustomIndexEXT. Called once per SetMesh (after _batches is populated) and tears down
        // whatever scene existed before.
        private void BuildRayTracingScene()
        {
            DestroyRayTracingScene();
            if (!_ctx.SupportsRayTracing)
            {
                return;
            }
            KhrAccelerationStructure khrAs = _ctx.KhrAccelerationStructure!;

            var eligible = new List<int>();
            for (int i = 0; i < _batches.Count; i++)
            {
                GpuBatch b = _batches[i];
                if (!b.Translucent && !b.HostVisible && b.IndexCount > 0)
                {
                    eligible.Add(i);
                }
            }
            if (eligible.Count == 0)
            {
                return;
            }

            var instDescs = new RtInstanceDesc[eligible.Count];
            var instances = new AccelerationStructureInstanceKHR[eligible.Count];

            for (int idx = 0; idx < eligible.Count; idx++)
            {
                GpuBatch b = _batches[eligible[idx]];
                ulong vAddr = GetBufferAddress(b.Vbuf);
                ulong iAddr = GetBufferAddress(b.Ibuf);
                instDescs[idx] = new RtInstanceDesc { VertexAddr = vAddr, IndexAddr = iAddr };
                uint maxVertex = b.VertexCount > 0 ? b.VertexCount - 1 : 0;
                uint primCount = b.IndexCount / 3;

                AccelerationStructureGeometryKHR sizingGeom = MakeBlasGeometry(vAddr, iAddr, maxVertex);
                var sizingInfo = new AccelerationStructureBuildGeometryInfoKHR
                {
                    SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                    Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                    Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                    Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                    GeometryCount = 1, PGeometries = &sizingGeom,
                };
                var sizeInfo = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
                khrAs.GetAccelerationStructureBuildSizes(_dev, AccelerationStructureBuildTypeKHR.DeviceKhr, &sizingInfo, &primCount, &sizeInfo);

                (Silk.NET.Vulkan.Buffer asBuf, DeviceMemory asMem) = CreateBuffer(sizeInfo.AccelerationStructureSize,
                    BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                    MemoryPropertyFlags.DeviceLocalBit);
                var asCi = new AccelerationStructureCreateInfoKHR
                {
                    SType = StructureType.AccelerationStructureCreateInfoKhr,
                    Buffer = asBuf, Size = sizeInfo.AccelerationStructureSize, Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                };
                AccelerationStructureKHR blas;
                VulkanContext.Check(khrAs.CreateAccelerationStructure(_dev, &asCi, null, &blas), "CreateAccelerationStructure(BLAS)");

                (Silk.NET.Vulkan.Buffer scratchBuf, DeviceMemory scratchMem) = CreateBuffer(sizeInfo.BuildScratchSize,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                    MemoryPropertyFlags.DeviceLocalBit);
                ulong scratchAddr = GetBufferAddress(scratchBuf);

                // buildGeometry/buildInfo/range are declared fresh INSIDE the lambda (not captured from the
                // outer scope) so taking their address here doesn't hit CS1686 ("cannot take address of a
                // closure-captured local") -- only genuinely local-to-the-closure variables get their
                // address taken; everything from the outer scope (vAddr, iAddr, maxVertex, blas,
                // scratchAddr, primCount, khrAs) is captured by value/reference and merely read.
                AccelerationStructureKHR blasCaptured = blas;
                uint primCountCaptured = primCount;
                OneTimeSubmit(cmd =>
                {
                    AccelerationStructureGeometryKHR buildGeometry = MakeBlasGeometry(vAddr, iAddr, maxVertex);
                    var buildInfo = new AccelerationStructureBuildGeometryInfoKHR
                    {
                        SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                        Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                        Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                        Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                        GeometryCount = 1, PGeometries = &buildGeometry,
                        DstAccelerationStructure = blasCaptured,
                        ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratchAddr },
                    };
                    var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primCountCaptured };
                    var pRange = &range;
                    khrAs.CmdBuildAccelerationStructures(cmd, 1, &buildInfo, &pRange);
                });

                _vk.DestroyBuffer(_dev, scratchBuf, null);
                _vk.FreeMemory(_dev, scratchMem, null);

                var addrInfo = new AccelerationStructureDeviceAddressInfoKHR
                {
                    SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = blas,
                };
                ulong blasAddress = khrAs.GetAccelerationStructureDeviceAddress(_dev, &addrInfo);
                _rtBlas.Add(new RtBlas { Buffer = asBuf, Mem = asMem, Handle = blas });

                var inst = new AccelerationStructureInstanceKHR
                {
                    Mask = 0xFF,
                    InstanceCustomIndex = (uint)idx,
                    InstanceShaderBindingTableRecordOffset = 0,
                    Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
                    AccelerationStructureReference = blasAddress,
                };
                // Identity 3x4 row-major transform (the viewer's model matrix is always identity -- see
                // MeshViewportRenderer.Render's caller -- so instance-space == world-space already).
                inst.Transform.Matrix[0] = 1; inst.Transform.Matrix[5] = 1; inst.Transform.Matrix[10] = 1;
                instances[idx] = inst;
            }

            ulong instSize = (ulong)(instances.Length * sizeof(AccelerationStructureInstanceKHR));
            (Silk.NET.Vulkan.Buffer instBuf, DeviceMemory instMem) = CreateBuffer(instSize,
                BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* instMapped = null;
            _vk.MapMemory(_dev, instMem, 0, instSize, 0, ref instMapped);
            fixed (AccelerationStructureInstanceKHR* src = instances)
            {
                System.Buffer.MemoryCopy(src, instMapped, instSize, instSize);
            }
            _vk.UnmapMemory(_dev, instMem);
            ulong instAddr = GetBufferAddress(instBuf);

            uint tlasPrimCount = (uint)instances.Length;
            AccelerationStructureGeometryKHR sizingTlasGeom = MakeTlasGeometry(instAddr);
            var sizingTlasInfo = new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.TopLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = 1, PGeometries = &sizingTlasGeom,
            };
            var tlasSizeInfo = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
            khrAs.GetAccelerationStructureBuildSizes(_dev, AccelerationStructureBuildTypeKHR.DeviceKhr, &sizingTlasInfo, &tlasPrimCount, &tlasSizeInfo);

            (Silk.NET.Vulkan.Buffer tlasBuf, DeviceMemory tlasMem) = CreateBuffer(tlasSizeInfo.AccelerationStructureSize,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit);
            var tlasCi = new AccelerationStructureCreateInfoKHR
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = tlasBuf, Size = tlasSizeInfo.AccelerationStructureSize, Type = AccelerationStructureTypeKHR.TopLevelKhr,
            };
            AccelerationStructureKHR tlas;
            VulkanContext.Check(khrAs.CreateAccelerationStructure(_dev, &tlasCi, null, &tlas), "CreateAccelerationStructure(TLAS)");

            (Silk.NET.Vulkan.Buffer tlasScratch, DeviceMemory tlasScratchMem) = CreateBuffer(tlasSizeInfo.BuildScratchSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.DeviceLocalBit);
            ulong tlasScratchAddr = GetBufferAddress(tlasScratch);

            AccelerationStructureKHR tlasCaptured = tlas;
            uint tlasPrimCountCaptured = tlasPrimCount;
            OneTimeSubmit(cmd =>
            {
                AccelerationStructureGeometryKHR geom = MakeTlasGeometry(instAddr);
                var buildInfo = new AccelerationStructureBuildGeometryInfoKHR
                {
                    SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                    Type = AccelerationStructureTypeKHR.TopLevelKhr,
                    Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                    Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                    GeometryCount = 1, PGeometries = &geom,
                    DstAccelerationStructure = tlasCaptured,
                    ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = tlasScratchAddr },
                };
                var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = tlasPrimCountCaptured };
                var pRange = &range;
                // The instance buffer was just written from the CPU; make sure that write is visible to
                // the acceleration-structure build before it reads the buffer.
                var barrier = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.HostWriteBit, DstAccessMask = AccessFlags.AccelerationStructureWriteBitKhr,
                };
                _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.HostBit, PipelineStageFlags.AccelerationStructureBuildBitKhr,
                    0, 1, &barrier, 0, null, 0, null);
                khrAs.CmdBuildAccelerationStructures(cmd, 1, &buildInfo, &pRange);
            });

            _vk.DestroyBuffer(_dev, tlasScratch, null);
            _vk.FreeMemory(_dev, tlasScratchMem, null);
            _vk.DestroyBuffer(_dev, instBuf, null);
            _vk.FreeMemory(_dev, instMem, null);

            _rtTlasBuf = tlasBuf;
            _rtTlasMem = tlasMem;
            _rtTlas = tlas;
            (_rtInstDescBuf, _rtInstDescMem) = CreateHostBuffer<RtInstanceDesc>(instDescs, BufferUsageFlags.StorageBufferBit);
            _rtSceneReady = true;

            WriteRtAccelerationAndInstanceDescriptors();
        }

        private static AccelerationStructureGeometryKHR MakeBlasGeometry(ulong vAddr, ulong iAddr, uint maxVertex)
        {
            var triData = new AccelerationStructureGeometryTrianglesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                VertexFormat = Format.R32G32B32Sfloat,
                VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vAddr },
                VertexStride = 56, // stride-14-float vertex (see AppendVertices/BuildVertexBuffer) = 56 bytes
                MaxVertex = maxVertex,
                IndexType = IndexType.Uint32,
                IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = iAddr },
            };
            return new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Triangles = triData },
                Flags = GeometryFlagsKHR.OpaqueBitKhr,
            };
        }

        private static AccelerationStructureGeometryKHR MakeTlasGeometry(ulong instAddr)
        {
            return new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.InstancesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR
                {
                    Instances = new AccelerationStructureGeometryInstancesDataKHR
                    {
                        SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                        ArrayOfPointers = false,
                        Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instAddr },
                    },
                },
            };
        }

        private void DestroyRayTracingScene()
        {
            if (!_ctx.SupportsRayTracing)
            {
                return;
            }
            KhrAccelerationStructure? khrAs = _ctx.KhrAccelerationStructure;
            foreach (RtBlas b in _rtBlas)
            {
                if (b.Handle.Handle != 0) khrAs!.DestroyAccelerationStructure(_dev, b.Handle, null);
                if (b.Buffer.Handle != 0) { _vk.DestroyBuffer(_dev, b.Buffer, null); _vk.FreeMemory(_dev, b.Mem, null); }
            }
            _rtBlas.Clear();
            if (_rtTlas.Handle != 0) { khrAs!.DestroyAccelerationStructure(_dev, _rtTlas, null); _rtTlas = default; }
            if (_rtTlasBuf.Handle != 0) { _vk.DestroyBuffer(_dev, _rtTlasBuf, null); _vk.FreeMemory(_dev, _rtTlasMem, null); _rtTlasBuf = default; }
            if (_rtInstDescBuf.Handle != 0) { _vk.DestroyBuffer(_dev, _rtInstDescBuf, null); _vk.FreeMemory(_dev, _rtInstDescMem, null); _rtInstDescBuf = default; }
            _rtSceneReady = false;
        }

        private void WriteRtAccelerationAndInstanceDescriptors()
        {
            if (_rtDescSet.Handle == 0)
            {
                return;
            }
            var asHandle = _rtTlas;
            var asWrite = new WriteDescriptorSetAccelerationStructureKHR
            {
                SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1, PAccelerationStructures = &asHandle,
            };
            var instInfo = new DescriptorBufferInfo { Buffer = _rtInstDescBuf, Offset = 0, Range = Vk.WholeSize };
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstSet = _rtDescSet, DstBinding = 0,
                DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = _rtDescSet, DstBinding = 2,
                DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &instInfo,
            };
            _vk.UpdateDescriptorSets(_dev, 2, writes, 0, null);
        }

        private void WriteRtImageDescriptor()
        {
            if (_rtDescSet.Handle == 0 || _rtColorView.Handle == 0)
            {
                return;
            }
            var imgInfo = new DescriptorImageInfo { ImageLayout = ImageLayout.General, ImageView = _rtColorView };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = _rtDescSet, DstBinding = 1,
                DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &imgInfo,
            };
            _vk.UpdateDescriptorSets(_dev, 1, &write, 0, null);
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
            // A buffer created with ShaderDeviceAddressBit usage must be allocated with the matching
            // DeviceAddressBit memory-allocate flag, regardless of whether this call site ever queries the
            // address itself (VUID-vkBindBufferMemory-bufferDeviceAddress-03339) -- an acceleration
            // structure's storage/scratch buffer needs the usage bit but is addressed via
            // vkGetAccelerationStructureDeviceAddressKHR, not vkGetBufferDeviceAddress, so deriving this
            // from the usage bits (not a separate "do you want the address back" parameter) is what the
            // spec actually requires.
            bool needsAddressFlag = (usage & BufferUsageFlags.ShaderDeviceAddressBit) != 0;
            var allocFlags = new MemoryAllocateFlagsInfo
            {
                SType = StructureType.MemoryAllocateFlagsInfo,
                Flags = MemoryAllocateFlags.DeviceAddressBit,
            };
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = needsAddressFlag ? &allocFlags : null,
                AllocationSize = req.Size,
                MemoryTypeIndex = _ctx.FindMemoryType(req.MemoryTypeBits, props),
            };
            DeviceMemory mem;
            VulkanContext.Check(_vk.AllocateMemory(_dev, &ai, null, &mem), "AllocateMemory(buffer)");
            _vk.BindBufferMemory(_dev, buffer, mem, 0);
            return (buffer, mem);
        }

        private ulong GetBufferAddress(Silk.NET.Vulkan.Buffer buf)
        {
            var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = buf };
            return _vk.GetBufferDeviceAddress(_dev, &info);
        }

        private static ulong AlignUp(ulong value, ulong align) => (value + align - 1) / align * align;
        private static uint AlignUp(uint value, uint align) => (uint)AlignUp((ulong)value, align);

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

        // Upload static data to a DEVICE_LOCAL (VRAM) buffer via a temporary host-visible staging buffer.
        // Device-local memory means the GPU fetches vertices/indices from VRAM instead of across PCIe on
        // every draw — a large win for big static meshes. Used for merged geometry + static index buffers.
        private (Silk.NET.Vulkan.Buffer, DeviceMemory) CreateDeviceLocalBuffer<T>(T[] data, BufferUsageFlags usage)
            where T : unmanaged
        {
            ulong size = (ulong)((long)data.Length * sizeof(T));
            (Silk.NET.Vulkan.Buffer staging, DeviceMemory stagingMem) = CreateBuffer(size,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            void* mapped = null;
            _vk.MapMemory(_dev, stagingMem, 0, size, 0, ref mapped);
            fixed (T* src = data)
            {
                System.Buffer.MemoryCopy(src, mapped, size, size);
            }
            _vk.UnmapMemory(_dev, stagingMem);

            // When the GPU supports ray tracing, static geometry buffers also carry the flags an
            // acceleration structure build needs (device address + AS build input) so
            // BuildRayTracingScene can reference them directly with no separate RT-only copy. A no-op on
            // buffers that never end up in a BLAS (translucent groups, skinned index buffers) -- an unused
            // usage bit costs nothing.
            BufferUsageFlags rtFlags = _ctx.SupportsRayTracing
                ? BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr
                : 0;
            (Silk.NET.Vulkan.Buffer buffer, DeviceMemory mem) = CreateBuffer(size,
                usage | BufferUsageFlags.TransferDstBit | rtFlags, MemoryPropertyFlags.DeviceLocalBit);

            OneTimeSubmit(cmd =>
            {
                var region = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = size };
                _vk.CmdCopyBuffer(cmd, staging, buffer, 1, &region);
            });

            _vk.DestroyBuffer(_dev, staging, null);
            _vk.FreeMemory(_dev, stagingMem, null);
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
            foreach (GpuBatch s in _batches)
            {
                if (s.Vbuf.Handle != 0) { _vk.DestroyBuffer(_dev, s.Vbuf, null); _vk.FreeMemory(_dev, s.Vmem, null); }
                if (s.Ibuf.Handle != 0) { _vk.DestroyBuffer(_dev, s.Ibuf, null); _vk.FreeMemory(_dev, s.Imem, null); }
            }
            _batches.Clear();
            _skinnedBatch.Clear();
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
            if (_rtColorView.Handle != 0) { _vk.DestroyImageView(_dev, _rtColorView, null); _rtColorView = default; }
            if (_rtColorImage.Handle != 0) { _vk.DestroyImage(_dev, _rtColorImage, null); _vk.FreeMemory(_dev, _rtColorMem, null); _rtColorImage = default; }
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
            ClearTrajectoryLines();
            DestroyTargets();
            DestroyRayTracingScene();
            if (_ctx.SupportsRayTracing)
            {
                if (_sbtBuffer.Handle != 0) { _vk.DestroyBuffer(_dev, _sbtBuffer, null); _vk.FreeMemory(_dev, _sbtMem, null); }
                if (_rtDescPool.Handle != 0) _vk.DestroyDescriptorPool(_dev, _rtDescPool, null);
                if (_rtPipeline.Handle != 0) _vk.DestroyPipeline(_dev, _rtPipeline, null);
                if (_rtPipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _rtPipelineLayout, null);
                if (_rtDescLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_dev, _rtDescLayout, null);
            }
            if (_sampler.Handle != 0) _vk.DestroySampler(_dev, _sampler, null);
            if (_descLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_dev, _descLayout, null);
            if (_fence.Handle != 0) _vk.DestroyFence(_dev, _fence, null);
            if (_pipeline.Handle != 0) _vk.DestroyPipeline(_dev, _pipeline, null);
            if (_blendPipeline.Handle != 0) _vk.DestroyPipeline(_dev, _blendPipeline, null);
            if (_enginePipeline.Handle != 0) _vk.DestroyPipeline(_dev, _enginePipeline, null);
            if (_engineBlendPipeline.Handle != 0) _vk.DestroyPipeline(_dev, _engineBlendPipeline, null);
            if (_skyTex.Image.Handle != 0)
            {
                _vk.DestroyImageView(_dev, _skyTex.View, null);
                _vk.DestroyImage(_dev, _skyTex.Image, null);
                _vk.FreeMemory(_dev, _skyTex.Mem, null);
            }
            if (_skyDescPool.Handle != 0) _vk.DestroyDescriptorPool(_dev, _skyDescPool, null);
            if (_skyPipeline.Handle != 0) _vk.DestroyPipeline(_dev, _skyPipeline, null);
            if (_skyPipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _skyPipelineLayout, null);
            if (_pipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _pipelineLayout, null);
            if (_bonePipeline.Handle != 0) _vk.DestroyPipeline(_dev, _bonePipeline, null);
            if (_bonePipelineXray.Handle != 0) _vk.DestroyPipeline(_dev, _bonePipelineXray, null);
            if (_bonePipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _bonePipelineLayout, null);
            if (_renderPass.Handle != 0) _vk.DestroyRenderPass(_dev, _renderPass, null);
        }
    }
}
