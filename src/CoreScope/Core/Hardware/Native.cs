using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CoreScope.Core.Hardware;

public static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out int firmwareType);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private const int RelationProcessorCore = 0;
    private const byte SmtFlag = 0x1;

    public sealed record CoreTopology(int Cores, int Threads, int PerformanceCores, int EfficiencyCores, bool Smt);

    /// <summary>
    /// Counts physical cores per efficiency class. On hybrid CPUs (Intel 12th gen+) the highest
    /// class is the P-cores; everything below it is E-cores.
    /// </summary>
    public static CoreTopology? GetCoreTopology()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return null;

            var coresByClass = new Dictionary<byte, int>();
            int cores = 0, smtCores = 0;
            int offset = 0;
            while (offset < length)
            {
                var ptr = IntPtr.Add(buffer, offset);
                int relationship = Marshal.ReadInt32(ptr);
                int size = Marshal.ReadInt32(ptr, 4);
                if (size <= 0) break;
                if (relationship == RelationProcessorCore)
                {
                    byte flags = Marshal.ReadByte(ptr, 8);
                    byte efficiencyClass = Marshal.ReadByte(ptr, 9);
                    cores++;
                    if ((flags & SmtFlag) != 0) smtCores++;
                    coresByClass[efficiencyClass] = coresByClass.GetValueOrDefault(efficiencyClass) + 1;
                }
                offset += size;
            }

            int threads = Environment.ProcessorCount;
            if (coresByClass.Count < 2) return new CoreTopology(cores, threads, 0, 0, smtCores > 0);

            byte top = 0;
            foreach (var key in coresByClass.Keys) if (key > top) top = key;
            int pCores = coresByClass[top];
            return new CoreTopology(cores, threads, pCores, cores - pCores, smtCores > 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string GetFirmwareTypeName()
    {
        try
        {
            if (GetFirmwareType(out int type))
                return type switch { 1 => "Legacy BIOS", 2 => "UEFI", _ => "Unknown" };
        }
        catch (EntryPointNotFoundException) { }
        return "Unknown";
    }

    /// <summary>Returns (total, available) physical memory in bytes and load percentage.</summary>
    public static (ulong Total, ulong Available, uint LoadPercent)? GetMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (status.TotalPhys, status.AvailPhys, status.MemoryLoad) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>(plugged in, charge %, seconds remaining or -1) — null on desktops without a battery.</summary>
    public static (bool PluggedIn, int Percent, int SecondsLeft)? GetPowerStatus()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryLifePercent == 255) return null;
        return (s.ACLineStatus == 1, s.BatteryLifePercent, s.BatteryLifeTime);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder? packageFullName);

    /// <summary>True when running from an MSIX package (Microsoft Store install) — some features use packaged APIs instead.</summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        try
        {
            int length = 0;
            const int AppModelErrorNoPackage = 15700;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
