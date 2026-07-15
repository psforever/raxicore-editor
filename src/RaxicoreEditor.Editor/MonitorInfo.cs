using System;
using System.Runtime.InteropServices;

namespace RaxicoreEditor.Editor
{
    /// <summary>Queries the display for its refresh capability, so the framerate-cap menu can flag caps that
    /// exceed what the monitor can show. Windows-only detection (via the Win32 display API); returns
    /// <c>null</c> on other platforms or if it can't be determined, in which case callers should treat every
    /// cap as available.</summary>
    public static class MonitorInfo
    {
        /// <summary>The primary display's current refresh rate in Hz, or <c>null</c> if unknown.</summary>
        public static int? CurrentRefreshHz() => QueryRefresh(ENUM_CURRENT_SETTINGS);

        /// <summary>The highest refresh rate the primary display supports across all of its modes, or
        /// <c>null</c> if unknown. Used to decide which framerate caps to offer — a monitor that <em>can</em>
        /// do 120 Hz should allow a 120 fps cap even if Windows is currently driving it at 60 Hz.</summary>
        public static int? MaxSupportedRefreshHz()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }
            try
            {
                int max = 0;
                for (int mode = 0; ; mode++)
                {
                    var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                    if (!EnumDisplaySettings(null, mode, ref dm))
                    {
                        break; // enumerated all modes
                    }
                    if (dm.dmDisplayFrequency > max)
                    {
                        max = (int)dm.dmDisplayFrequency;
                    }
                }
                return max > 1 ? max : (int?)null;
            }
            catch
            {
                return null;
            }
        }

        private static int? QueryRefresh(int mode)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }
            try
            {
                var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(null, mode, ref dm))
                {
                    return null;
                }
                // 0 or 1 are the "default/unknown" sentinels the API returns for some drivers.
                return dm.dmDisplayFrequency > 1 ? (int)dm.dmDisplayFrequency : (int?)null;
            }
            catch
            {
                return null;
            }
        }

        private const int ENUM_CURRENT_SETTINGS = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }
    }
}
