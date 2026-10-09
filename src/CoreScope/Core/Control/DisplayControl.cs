using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.Control;

public sealed record DisplayMode(int Width, int Height, int RefreshHz)
{
    public string Resolution => $"{Width} × {Height}";
}

public sealed record DisplayTarget(string DeviceName, string Label, bool IsPrimary, DisplayMode Current, IReadOnlyList<DisplayMode> Modes);

/// <summary>Resolution / refresh rate via user32 and laptop panel brightness via WMI.</summary>
public static class DisplayControl
{
    private const int EnumCurrentSettings = -1;
    private const int DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x1, CdsTest = 0x2;
    private const int DispChangeSuccessful = 0;
    private const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DevMode mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref DevMode mode, IntPtr hwnd, uint flags, IntPtr lParam);

    private static DevMode NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DevMode>(), dmDeviceName = "", dmFormName = "" };

    public static List<DisplayTarget> Displays()
    {
        var result = new List<DisplayTarget>();
        for (uint i = 0; i < 16; i++)
        {
            var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & AttachedToDesktop) == 0) continue;

            var current = NewDevMode();
            if (!EnumDisplaySettingsW(dd.DeviceName, EnumCurrentSettings, ref current)) continue;

            var modes = new HashSet<DisplayMode>();
            for (int m = 0; m < 2000; m++)
            {
                var mode = NewDevMode();
                if (!EnumDisplaySettingsW(dd.DeviceName, m, ref mode)) break;
                if (mode.dmBitsPerPel == 32 && mode.dmPelsWidth >= 800 && mode.dmDisplayFrequency > 1)
                    modes.Add(new DisplayMode(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency));
            }

            // Monitor name: the first child device of the adapter output.
            var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            var monitorName = EnumDisplayDevicesW(dd.DeviceName, 0, ref monitor, 0) ? monitor.DeviceString : "";
            bool primary = (dd.StateFlags & PrimaryDevice) != 0;
            var number = dd.DeviceName.Replace(@"\\.\DISPLAY", "", StringComparison.OrdinalIgnoreCase);
            var label = $"Display {number}{(primary ? " (main)" : "")}{(monitorName.Length > 0 && !monitorName.StartsWith("Generic", StringComparison.OrdinalIgnoreCase) ? $" · {monitorName}" : "")}";

            result.Add(new DisplayTarget(dd.DeviceName, label, primary,
                new DisplayMode(current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency),
                modes.OrderByDescending(x => x.Width * x.Height).ThenByDescending(x => x.RefreshHz).ToList()));
        }
        return result;
    }

    public static bool SetMode(string deviceName, DisplayMode target)
    {
        var mode = NewDevMode();
        if (!EnumDisplaySettingsW(deviceName, EnumCurrentSettings, ref mode)) return false;
        mode.dmPelsWidth = target.Width;
        mode.dmPelsHeight = target.Height;
        mode.dmDisplayFrequency = target.RefreshHz;
        mode.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

        // Test first so an unsupported mode never reaches the screen.
        if (ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, CdsTest, IntPtr.Zero) != DispChangeSuccessful)
        {
            Log.Info($"Control: display mode {target} rejected by driver test");
            return false;
        }
        var result = ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        Log.Info($"Control: {deviceName} → {target.Width}x{target.Height}@{target.RefreshHz} : {result}");
        return result == DispChangeSuccessful;
    }

    // ───────── Brightness (internal laptop panels) ─────────

    public static int? Brightness()
    {
        var b = Wmi.First("SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE", Wmi.RootWmi);
        return b is null ? null : b.Int("CurrentBrightness");
    }

    public static bool SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(Wmi.RootWmi, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            foreach (ManagementObject method in searcher.Get())
            {
                method.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                return true;
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            Log.Error("Setting brightness", ex);
        }
        return false;
    }
}
