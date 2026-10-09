using CoreScope.Core.Network;

namespace CoreScope.Core.Fixes;

/// <summary>
/// The "what this will do" line shown in the confirmation before a fix changes anything: the change in plain words and,
/// where there is one, the exact command. Kept next to the runner's actions so a new fix gets a preview in one place.
/// </summary>
public static class FixPreviews
{
    public static string? For(FixAction action) => action.Kind != FixKind.Command ? null : action.Id switch
    {
        FixIds.BalancedPower => "Switches the active Windows power plan to Balanced.",
        FixIds.Restart => "Restarts this PC after a 15-second countdown.\nCommand: shutdown /r /t 15",
        FixIds.RestartToUefi => "Restarts this PC into the BIOS / UEFI setup screen after 15 seconds.\nCommand: shutdown /r /fw /t 15",
        FixIds.CleanTemp => "Deletes temporary files older than 24 hours from your temp folder (%TEMP%) and C:\\Windows\\Temp, then removes empty folders. Files in use are kept.",
        FixIds.EmptyRecycleBin => "Permanently deletes everything in the Recycle Bin on every drive. This cannot be undone.",
        FixIds.EnableTrim => "Tells Windows to send TRIM to your SSDs so they can clean up free blocks.\nCommand: fsutil behavior set DisableDeleteNotify 0",
        FixIds.HibernateOff => "Turns hibernation off and deletes hiberfil.sys, freeing roughly the size of your RAM. Fast Startup is turned off with it.\nCommand: powercfg /hibernate off",
        FixIds.SyncClock => "Sets the Windows Time service to start automatically, starts it, and syncs your clock now.\nCommand: w32tm /resync /force",
        FixIds.EnableFirewall => "Turns Windows Firewall on for the domain, private and public networks.\nCommand: netsh advfirewall set allprofiles state on",
        FixIds.UpdateDefender => "Downloads the latest virus definitions for Microsoft Defender. Nothing else changes.\nCommand: MpCmdRun.exe -SignatureUpdate",
        FixIds.StartWindowsUpdate => "Sets the Windows Update service to start on demand and starts it. Nothing is downloaded or installed by this; open Windows Update to check.\nCommand: net start wuauserv",
        FixIds.EnableRestore => "Turns System Restore on for your system drive, so Windows can keep restore points.\nCommand: Enable-ComputerRestore",
        FixIds.CreateRestorePoint => "Creates a System Restore point named \"CoreScope restore point\". Windows normally allows one every 24 hours.\nCommand: Checkpoint-Computer",
        FixIds.MaxRefreshRate => "Sets your display to its highest refresh rate at the current resolution.",
        FixIds.ScanHardware => "Asks Windows to rescan for new or missing hardware. Nothing is installed or removed by CoreScope.\nCommand: pnputil /scan-devices",
        FixIds.DisableStartupApp => $"Stops this app from starting with Windows. The app is not uninstalled and you can turn it back on in Control Center.\nApp: {action.Target}",
        FixIds.StorageSenseOn => "Turns on Storage Sense for your account so Windows tidies temporary files itself.\nChange: sets StoragePolicy\\01 = 1 under HKEY_CURRENT_USER",
        NetworkFixes.DisableRsc => "Turns off Receive Segment Coalescing, a network setting that can add delay.\nCommand: netsh int tcp set global rsc=disabled",
        NetworkFixes.EnableRsc => "Turns Receive Segment Coalescing back on.\nCommand: netsh int tcp set global rsc=enabled",
        NetworkFixes.AutoTuningNormal => "Sets TCP receive auto-tuning to its default, which lets fast connections use their full speed.\nCommand: netsh int tcp set global autotuninglevel=normal",
        NetworkFixes.FlushDns => "Clears the cached website addresses on this PC. They are looked up again as needed.\nCommand: ipconfig /flushdns",
        _ => null,
    };

    /// <summary>The question to ask first: the fix's own, or a short default. Every fix that CoreScope performs asks before it changes anything.</summary>
    public static string? Question(FixAction action) =>
        action.Kind != FixKind.Command ? null : action.Confirm ?? "CoreScope will make this change on your PC. Go ahead?";
}
