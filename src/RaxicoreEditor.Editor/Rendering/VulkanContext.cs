using System;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace RaxicoreEditor.Editor.Rendering
{
    /// <summary>
    /// A headless Vulkan device shared across viewports. No instance/device surface or swapchain
    /// extensions are enabled — rendering is entirely offscreen, so there is no on-screen Vulkan
    /// presentation (and none of the swapchain/fullscreen-exclusive/device-loss failure modes).
    /// </summary>
    public sealed unsafe class VulkanContext : IDisposable
    {
        public Vk Vk { get; }
        public Instance Instance { get; private set; }
        public PhysicalDevice PhysicalDevice { get; private set; }
        public Device Device { get; private set; }
        public Queue GraphicsQueue { get; private set; }
        public uint GraphicsFamily { get; private set; }
        public CommandPool CommandPool { get; private set; }

        /// <summary>
        /// Whether the selected physical device can do hardware-accelerated ray tracing: both
        /// <c>VK_KHR_acceleration_structure</c> and <c>VK_KHR_ray_tracing_pipeline</c> (plus its required
        /// dependency <c>VK_KHR_deferred_host_operations</c>) are advertised, AND the device actually
        /// reports the corresponding feature bits set -- a device can list an extension as present while
        /// still reporting its features as unsupported (e.g. some software/CPU Vulkan implementations),
        /// so the extension check alone is not sufficient.
        ///
        /// This is a read-only capability probe: none of these extensions are enabled on the logical
        /// device, so detecting support here has no effect on anything that renders today.
        /// </summary>
        public bool SupportsRayTracing { get; private set; }

        /// <summary>Loaded <c>VK_KHR_acceleration_structure</c> function table, or null if unsupported.</summary>
        public KhrAccelerationStructure? KhrAccelerationStructure { get; private set; }

        /// <summary>Loaded <c>VK_KHR_ray_tracing_pipeline</c> function table, or null if unsupported.</summary>
        public KhrRayTracingPipeline? KhrRayTracingPipeline { get; private set; }

        /// <summary>
        /// Device limits for ray tracing pipelines (shader group handle size/alignment, SBT base
        /// alignment, max recursion depth, ...), queried once alongside device creation. Default/zeroed
        /// when <see cref="SupportsRayTracing"/> is false -- nothing should read it in that case.
        /// </summary>
        public PhysicalDeviceRayTracingPipelinePropertiesKHR RayTracingProperties { get; private set; }

        private static VulkanContext? _shared;
        private static bool _failed;

        /// <summary>The shared context, or null if Vulkan is unavailable on this machine.</summary>
        public static VulkanContext? TryGetShared()
        {
            if (_shared != null)
            {
                return _shared;
            }
            if (_failed)
            {
                return null;
            }
            try
            {
                _shared = new VulkanContext();
                return _shared;
            }
            catch (Exception ex)
            {
                // Previously silent -- a broken driver/instance/device produced no diagnostic trail at
                // all, anywhere. stderr is always safe to write to (redirected or not: a WinExe with
                // nothing attached simply discards it), so this costs nothing and gives the one signal
                // available for "why did every 3D viewport just silently stop working".
                Console.Error.WriteLine($"VulkanContext initialisation failed: {ex}");
                _failed = true;
                return null;
            }
        }

        private VulkanContext()
        {
            Vk = Vk.GetApi();
            CreateInstance();
            PickPhysicalDevice();
            SupportsRayTracing = ProbeRayTracingSupport();
            CreateDevice();
            CreateCommandPool();
        }

        private void CreateInstance()
        {
            var appName = (byte*)SilkMarshal.StringToPtr("RaxicoreEditor");
            var engineName = (byte*)SilkMarshal.StringToPtr("RaxicoreEditor");
            try
            {
                var app = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = appName,
                    ApplicationVersion = new Version32(0, 1, 0),
                    PEngineName = engineName,
                    EngineVersion = new Version32(0, 1, 0),
                    // 1.2 so PhysicalDeviceVulkan12Features (buffer device address, descriptor indexing,
                    // ...) is available as a single feature-query/enable struct -- both are hard
                    // prerequisites of VK_KHR_acceleration_structure. Purely additive over 1.1: it does
                    // not require the device itself to support 1.2 (CreateDevice below still checks that
                    // per-device via SupportsRayTracing before touching any of it), and every existing
                    // 1.0/1.1 call in this file keeps working unchanged.
                    ApiVersion = Vk.Version12,
                };
                // macOS renders Vulkan only through MoltenVK, a non-conformant "portability" driver that the
                // loader hides unless VK_KHR_portability_enumeration is enabled and the enumeration flag is
                // set. This block is entered only on macOS, so Windows/Linux instance creation is unchanged.
                nint extPtr = 0;
                uint extCount = 0;
                InstanceCreateFlags flags = 0;
                if (OperatingSystem.IsMacOS() && InstanceExtensionAvailable("VK_KHR_portability_enumeration"))
                {
                    extPtr = SilkMarshal.StringArrayToPtr(new[] { "VK_KHR_portability_enumeration" });
                    extCount = 1;
                    flags = InstanceCreateFlags.EnumeratePortabilityBitKhr;
                }

                var ci = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &app,
                    Flags = flags,
                    EnabledExtensionCount = extCount,
                    PpEnabledExtensionNames = (byte**)extPtr,
                };
                Instance instance;
                Result r = Vk.CreateInstance(&ci, null, &instance);
                if (extPtr != 0)
                {
                    SilkMarshal.Free(extPtr);
                }
                Check(r, "CreateInstance");
                Instance = instance;
            }
            finally
            {
                SilkMarshal.Free((nint)appName);
                SilkMarshal.Free((nint)engineName);
            }
        }

        // Whether a Vulkan instance-level extension is advertised by the loader.
        private bool InstanceExtensionAvailable(string name)
        {
            uint n = 0;
            Vk.EnumerateInstanceExtensionProperties((byte*)null, ref n, null);
            if (n == 0)
            {
                return false;
            }
            var props = new ExtensionProperties[n];
            fixed (ExtensionProperties* p = props)
            {
                Vk.EnumerateInstanceExtensionProperties((byte*)null, ref n, p);
                for (uint i = 0; i < n; i++)
                {
                    if (ExtensionName(&p[i]) == name)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // Whether a device-level extension is supported by a physical device.
        private bool DeviceExtensionAvailable(PhysicalDevice dev, string name)
        {
            uint n = 0;
            Vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, ref n, null);
            if (n == 0)
            {
                return false;
            }
            var props = new ExtensionProperties[n];
            fixed (ExtensionProperties* p = props)
            {
                Vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, ref n, p);
                for (uint i = 0; i < n; i++)
                {
                    if (ExtensionName(&p[i]) == name)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string ExtensionName(ExtensionProperties* e) =>
            System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)e->ExtensionName) ?? "";

        private void PickPhysicalDevice()
        {
            uint count = 0;
            Vk.EnumeratePhysicalDevices(Instance, ref count, null);
            if (count == 0)
            {
                throw new InvalidOperationException("No Vulkan physical devices");
            }
            Span<PhysicalDevice> devices = stackalloc PhysicalDevice[(int)count];
            fixed (PhysicalDevice* p = devices)
            {
                Vk.EnumeratePhysicalDevices(Instance, ref count, p);
            }

            PhysicalDevice = devices[0];
            for (int i = 0; i < (int)count; i++)
            {
                Vk.GetPhysicalDeviceProperties(devices[i], out PhysicalDeviceProperties props);
                if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
                {
                    PhysicalDevice = devices[i];
                    break;
                }
            }

            uint qcount = 0;
            Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref qcount, null);
            Span<QueueFamilyProperties> qprops = stackalloc QueueFamilyProperties[(int)qcount];
            fixed (QueueFamilyProperties* q = qprops)
            {
                Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref qcount, q);
            }

            GraphicsFamily = uint.MaxValue;
            for (uint i = 0; i < qcount; i++)
            {
                if ((qprops[(int)i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    GraphicsFamily = i;
                    break;
                }
            }
            if (GraphicsFamily == uint.MaxValue)
            {
                throw new InvalidOperationException("No graphics queue family");
            }
        }

        /// <summary>
        /// True if <see cref="PhysicalDevice"/> both advertises the ray tracing extensions and reports
        /// their feature bits as actually supported.
        /// </summary>
        private bool ProbeRayTracingSupport()
        {
            // PhysicalDeviceVulkan12Features (queried/enabled below) is only meaningful if the device
            // itself reports 1.2 -- the instance's ApiVersion above is a ceiling on what the APP can ask
            // for, not a floor on what any given DEVICE actually implements.
            Vk.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties devProps);
            if (devProps.ApiVersion < Vk.Version12)
            {
                return false;
            }

            if (!DeviceExtensionAvailable(PhysicalDevice, "VK_KHR_acceleration_structure") ||
                !DeviceExtensionAvailable(PhysicalDevice, "VK_KHR_ray_tracing_pipeline") ||
                !DeviceExtensionAvailable(PhysicalDevice, "VK_KHR_deferred_host_operations"))
            {
                return false;
            }

            var rtPipelineFeatures = new PhysicalDeviceRayTracingPipelineFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr,
            };
            var asFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
                PNext = &rtPipelineFeatures,
            };
            var features2 = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &asFeatures,
            };
            Vk.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);

            return asFeatures.AccelerationStructure && rtPipelineFeatures.RayTracingPipeline;
        }

        private void CreateDevice()
        {
            float priority = 1f;
            var qci = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = GraphicsFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            var features = new PhysicalDeviceFeatures { FillModeNonSolid = true };

            // A MoltenVK physical device is a portability-subset device; the Vulkan spec REQUIRES enabling
            // VK_KHR_portability_subset whenever a device advertises it. Only reached on macOS.
            var deviceExts = new System.Collections.Generic.List<string>();
            if (OperatingSystem.IsMacOS() && DeviceExtensionAvailable(PhysicalDevice, "VK_KHR_portability_subset"))
            {
                deviceExts.Add("VK_KHR_portability_subset");
            }

            // Provisioning is independent of the user's Render > Ray Tracing menu toggle: the capability
            // is enabled here, once, whenever the hardware/driver supports it, exactly like FillModeNonSolid
            // above. Whether any given frame actually USES it is a separate, per-frame, RenderSettings.RayTracing
            // check made by the renderer -- this only has to happen once, at device creation.
            if (SupportsRayTracing)
            {
                deviceExts.Add("VK_KHR_deferred_host_operations");
                deviceExts.Add("VK_KHR_acceleration_structure");
                deviceExts.Add("VK_KHR_ray_tracing_pipeline");
            }

            nint devExtPtr = deviceExts.Count > 0 ? SilkMarshal.StringArrayToPtr(deviceExts.ToArray()) : 0;

            var rtPipeFeatures = new PhysicalDeviceRayTracingPipelineFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr,
                RayTracingPipeline = SupportsRayTracing,
            };
            var asFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
                AccelerationStructure = SupportsRayTracing,
                PNext = &rtPipeFeatures,
            };
            var vk12Features = new PhysicalDeviceVulkan12Features
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                BufferDeviceAddress = SupportsRayTracing,
                PNext = &asFeatures,
            };

            // Chaining PhysicalDeviceVulkan12Features (etc.) requires PEnabledFeatures to be left null --
            // VkPhysicalDeviceFeatures2-style structs and the legacy PEnabledFeatures pointer are mutually
            // exclusive per the spec. Base features (FillModeNonSolid) move into the chain's own
            // PhysicalDeviceFeatures2 wrapper instead.
            var features2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, Features = features, PNext = &vk12Features };
            var dci = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features2,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &qci,
                EnabledExtensionCount = (uint)deviceExts.Count,
                PpEnabledExtensionNames = (byte**)devExtPtr,
            };

            Device device;
            Result res = Vk.CreateDevice(PhysicalDevice, &dci, null, &device);
            if (devExtPtr != 0)
            {
                SilkMarshal.Free(devExtPtr);
            }
            Check(res, "CreateDevice");
            Device = device;
            Vk.GetDeviceQueue(Device, GraphicsFamily, 0, out Queue queue);
            GraphicsQueue = queue;

            if (SupportsRayTracing)
            {
                if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrAccelerationStructure khrAs) ||
                    !Vk.TryGetDeviceExtension(Instance, Device, out KhrRayTracingPipeline khrRtp))
                {
                    // Genuinely shouldn't happen given the extensions were just enabled above, but if the
                    // loader can't resolve the function pointers for some reason, fail closed rather than
                    // leave SupportsRayTracing true with null function tables behind it.
                    SupportsRayTracing = false;
                    return;
                }
                KhrAccelerationStructure = khrAs;
                KhrRayTracingPipeline = khrRtp;

                var rtProps = new PhysicalDeviceRayTracingPipelinePropertiesKHR { SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr };
                var props2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &rtProps };
                Vk.GetPhysicalDeviceProperties2(PhysicalDevice, &props2);
                RayTracingProperties = rtProps;
            }
        }

        private void CreateCommandPool()
        {
            var pci = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = GraphicsFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            CommandPool pool;
            Check(Vk.CreateCommandPool(Device, &pci, null, &pool), "CreateCommandPool");
            CommandPool = pool;
        }

        public uint FindMemoryType(uint typeBits, MemoryPropertyFlags props)
        {
            if (TryFindMemoryType(typeBits, props, out uint i))
            {
                return i;
            }
            throw new InvalidOperationException("No suitable Vulkan memory type");
        }

        /// <summary>Find a memory type index with all of <paramref name="props"/>, without throwing.</summary>
        public bool TryFindMemoryType(uint typeBits, MemoryPropertyFlags props, out uint index)
        {
            Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties mp);
            for (uint i = 0; i < mp.MemoryTypeCount; i++)
            {
                if ((typeBits & (1u << (int)i)) != 0 &&
                    (mp.MemoryTypes[(int)i].PropertyFlags & props) == props)
                {
                    index = i;
                    return true;
                }
            }
            index = 0;
            return false;
        }

        /// <summary>Whether a memory type carries <see cref="MemoryPropertyFlags.HostCoherentBit"/>.</summary>
        public bool MemoryTypeIsCoherent(uint index)
        {
            Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties mp);
            return (mp.MemoryTypes[(int)index].PropertyFlags & MemoryPropertyFlags.HostCoherentBit) != 0;
        }

        public static void Check(Result r, string what)
        {
            if (r != Result.Success)
            {
                throw new InvalidOperationException($"{what} failed: {r}");
            }
        }

        public void Dispose()
        {
            if (Device.Handle != 0)
            {
                Vk.DeviceWaitIdle(Device);
                if (CommandPool.Handle != 0)
                {
                    Vk.DestroyCommandPool(Device, CommandPool, null);
                }
                Vk.DestroyDevice(Device, null);
            }
            if (Instance.Handle != 0)
            {
                Vk.DestroyInstance(Instance, null);
            }
        }
    }
}
