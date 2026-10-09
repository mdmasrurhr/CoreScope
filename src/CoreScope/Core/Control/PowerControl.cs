using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreScope.Core.Control;

public sealed record PowerPlan(Guid Id, string Name);

/// <summary>
/// Windows power management through powrprof.dll: plans, the Windows 11 "power mode" overlay,
/// processor boost and screen/sleep timeouts. Every setter returns false instead of throwing.
/// </summary>
public static class PowerControl
{
    private const uint AccessScheme = 16;
    private const uint ErrorSuccess = 0;

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr activeGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subGroup, uint accessFlags, uint index, byte[]? buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup, IntPtr setting, byte[]? buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    // Power-mode overlays (Settings › System › Power › Power mode)
    public static readonly Guid ModeBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid ModeBalanced = Guid.Empty;
    public static readonly Guid ModeBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    // Setting GUIDs
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid SettingBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid SettingVideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid SettingStandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    public enum Timeout { ScreenOff, Sleep }

    public static Guid? ActivePlan()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != ErrorSuccess || ptr == IntPtr.Zero) return null;
            var guid = Marshal.PtrToStructure<Guid>(ptr);
            LocalFree(ptr);
            return guid;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public static List<PowerPlan> Plans()
    {
        var plans = new List<PowerPlan>();
        try
        {
            for (uint i = 0; ; i++)
            {
                uint size = 16;
                var buffer = new byte[16];
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref size) != ErrorSuccess) break;
                var id = new Guid(buffer);
                plans.Add(new PowerPlan(id, FriendlyName(id)));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { Log.Error("Enumerating power plans", ex); }
        return plans;
    }

    private static string FriendlyName(Guid scheme)
    {
        uint size = 0;
        // Size query: the return code is ERROR_MORE_DATA by design; the next check on `size` is what matters.
        _ = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0) return scheme.ToString();
        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != ErrorSuccess) return scheme.ToString();
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static bool SetActivePlan(Guid plan)
    {
        var ok = PowerSetActiveScheme(IntPtr.Zero, ref plan) == ErrorSuccess;
        Log.Info($"Control: power plan → {plan} : {(ok ? "ok" : "failed")}");
        return ok;
    }

    /// <summary>Windows 11 power mode. Only meaningful while the Balanced plan is active.</summary>
    public static Guid? PowerMode()
    {
        try { return PowerGetEffectiveOverlayScheme(out var g) == ErrorSuccess ? g : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static bool SetPowerMode(Guid mode)
    {
        try
        {
            var ok = PowerSetActiveOverlayScheme(mode) == ErrorSuccess;
            Log.Info($"Control: power mode → {mode} : {(ok ? "ok" : "failed")}");
            return ok;
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static uint? Read(Guid sub, Guid setting, bool ac)
    {
        if (ActivePlan() is not { } plan) return null;
        uint value;
        var result = ac ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out value)
                        : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out value);
        return result == ErrorSuccess ? value : null;
    }

    /// <summary>Writes a value into the active plan and re-applies the plan so it takes effect immediately.</summary>
    private static bool Write(Guid sub, Guid setting, uint value, bool ac)
    {
        if (ActivePlan() is not { } plan) return false;
        var result = ac ? PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, value)
                        : PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, value);
        var ok = result == ErrorSuccess && PowerSetActiveScheme(IntPtr.Zero, ref plan) == ErrorSuccess;
        Log.Info($"Control: power setting {setting} ({(ac ? "AC" : "DC")}) → {value} : {(ok ? "ok" : $"failed ({result})")}");
        return ok;
    }

    /// <summary>Processor boost mode: 0 = disabled, 2 = aggressive (Windows default), others = vendor variants.</summary>
    public static uint? BoostMode(bool ac) => Read(SubProcessor, SettingBoostMode, ac);

    public static bool SetBoostMode(uint mode) =>
        Write(SubProcessor, SettingBoostMode, mode, ac: true) & Write(SubProcessor, SettingBoostMode, mode, ac: false);

    /// <summary>Timeout in seconds (0 = never).</summary>
    public static uint? GetTimeout(Timeout which, bool ac) => which == Timeout.ScreenOff
        ? Read(SubVideo, SettingVideoIdle, ac)
        : Read(SubSleep, SettingStandbyIdle, ac);

    public static bool SetTimeout(Timeout which, bool ac, uint seconds) => which == Timeout.ScreenOff
        ? Write(SubVideo, SettingVideoIdle, seconds, ac)
        : Write(SubSleep, SettingStandbyIdle, seconds, ac);
}
