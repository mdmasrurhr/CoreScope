using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CoreScope.Core.Control;

/// <summary>
/// Lenovo IdeaPad / Yoga "Conservation Mode" (stops charging at ~60–80% to slow battery wear) through
/// Lenovo's Energy Management driver — the same interface Lenovo Vantage uses. Protocol per the
/// open-source OpenLenovoSettings / Lenovo Legion Toolkit projects:
///   IOCTL 0x831020F8 · read: send 0xFF, bit 0x20 = conservation, bit 0x04 = rapid charge
///   write: 0x3 = conservation on, 0x5 = conservation off, 0x7 = rapid on, 0x8 = rapid off.
/// The feature is only offered when the driver exists AND answers a read; every write is verified by re-reading.
/// </summary>
public static class BatteryControl
{
    private const string DevicePath = @"\\.\EnergyDrv";
    private const uint IoctlChargeMode = 0x831020F8;
    private const uint QueryState = 0xFF;
    private const uint ConservationOn = 0x3, ConservationOff = 0x5, RapidOff = 0x8;
    private const uint ConservationBit = 0x20, RapidBit = 0x04;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint input, uint inputSize, out uint output, uint outputSize, out uint returned, IntPtr overlapped);

    private static SafeFileHandle? Open()
    {
        var handle = CreateFileW(DevicePath, 0xC0000000 /* read|write */, 3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    private static uint? Send(SafeFileHandle handle, uint command)
    {
        uint input = command;
        return DeviceIoControl(handle, IoctlChargeMode, ref input, 4, out uint output, 4, out uint returned, IntPtr.Zero) && returned >= 4
            ? output
            : null;
    }

    /// <summary>Null when unsupported (not a Lenovo with the Energy Management driver).</summary>
    public static bool? ConservationEnabled()
    {
        try
        {
            using var handle = Open();
            if (handle is null) return null;
            return Send(handle, QueryState) is { } state ? (state & ConservationBit) != 0 : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static bool SetConservation(bool enable)
    {
        try
        {
            using var handle = Open();
            if (handle is null || Send(handle, QueryState) is not { } state) return false;

            if (enable)
            {
                if ((state & RapidBit) != 0) Send(handle, RapidOff); // the two modes are mutually exclusive
                Send(handle, ConservationOn);
            }
            else
            {
                Send(handle, ConservationOff);
            }

            // Verify: the driver applies asynchronously on some models.
            for (int i = 0; i < 10; i++)
            {
                if (Send(handle, QueryState) is { } now && ((now & ConservationBit) != 0) == enable)
                {
                    Log.Info($"Control: Lenovo conservation mode → {(enable ? "on" : "off")} : ok");
                    return true;
                }
                Thread.Sleep(50);
            }
            Log.Info($"Control: Lenovo conservation mode → {(enable ? "on" : "off")} : not confirmed by driver");
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("Conservation mode", ex);
            return false;
        }
    }
}
