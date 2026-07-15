using System;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

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
            catch
            {
                _failed = true;
                return null;
            }
        }

        private VulkanContext()
        {
            Vk = Vk.GetApi();
            CreateInstance();
            PickPhysicalDevice();
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
                    ApiVersion = Vk.Version11,
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
            nint devExtPtr = 0;
            uint devExtCount = 0;
            if (OperatingSystem.IsMacOS() && DeviceExtensionAvailable(PhysicalDevice, "VK_KHR_portability_subset"))
            {
                devExtPtr = SilkMarshal.StringArrayToPtr(new[] { "VK_KHR_portability_subset" });
                devExtCount = 1;
            }

            var dci = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &qci,
                PEnabledFeatures = &features,
                EnabledExtensionCount = devExtCount,
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
