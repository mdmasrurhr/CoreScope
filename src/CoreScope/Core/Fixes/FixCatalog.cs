using System.Collections.Generic;
using System.Linq;

namespace CoreScope.Core.Fixes;

/// <summary>Ids of the fixes CoreScope performs itself (<see cref="FixKind.Command"/>).</summary>
public static class FixIds
{
    public const string BalancedPower = "power.balanced";
    public const string Restart = "power.restart";
    public const string RestartToUefi = "power.uefi";
    public const string CleanTemp = "disk.temp";
    public const string EmptyRecycleBin = "disk.recycle";
    public const string EnableTrim = "disk.trim";
    public const string HibernateOff = "disk.hibernate.off";
    public const string SyncClock = "time.sync";
    public const string EnableFirewall = "security.firewall";
    public const string UpdateDefender = "security.defender.update";
    public const string StartWindowsUpdate = "update.service";
    public const string EnableRestore = "restore.enable";
    public const string CreateRestorePoint = "restore.create";
    public const string MaxRefreshRate = "display.refresh";
    public const string ScanHardware = "devices.scan";
    public const string DisableStartupApp = "startup.disable";
    public const string StorageSenseOn = "disk.storagesense";
    public const string Unlock = "app.unlock";
}

/// <summary>Builders for the buttons that appear on findings, plus the step-by-step guides.</summary>
public static class FixButtons
{
    // ───────────── Buttons ─────────────

    /// <summary>Opens a Windows settings page or built-in tool.</summary>
    public static FixAction Open(string label, string target, bool primary = false) =>
        new("open:" + target, label, FixKind.Settings, target, Primary: primary);

    public static FixAction Page(string label, string page, bool primary = false) =>
        new("page:" + page, label, FixKind.Page, page, Primary: primary);

    public static FixAction Web(string label, string url, bool primary = false) =>
        new("url:" + url, label, FixKind.Url, url, Primary: primary);

    public static FixAction Do(string id, string label, string? confirm = null, bool admin = false, bool primary = true, string target = "") =>
        new(id, label, FixKind.Command, target, confirm, admin, primary);

    public static FixAction Steps(string label, Guide guide, bool primary = false) =>
        new("guide:" + guide.Title, label, FixKind.Guide, "", Primary: primary, Guide: guide);

    // ───────────── Common settings pages ─────────────

    public static FixAction WindowsUpdate(bool primary = true) => Open("Open Windows Update", "ms-settings:windowsupdate", primary);
    public static FixAction OptionalUpdates() => Open("Optional driver updates", "ms-settings:windowsupdate-optionalupdates");
    public static FixAction WindowsSecurity(string label = "Open Windows Security", string uri = "windowsdefender:", bool primary = true) => Open(label, uri, primary);
    public static FixAction StorageSettings(bool primary = false) => Open("Open Storage settings", "ms-settings:storagesense", primary);
    public static FixAction DiskCleanup() => Open("Disk Cleanup", "cleanmgr");
    public static FixAction TaskManager() => Open("Open Task Manager", "taskmgr");
    public static FixAction DeviceManager(bool primary = false) => Open("Open Device Manager", "devmgmt.msc", primary);
    public static FixAction StartupSettings() => Open("Windows startup settings", "ms-settings:startupapps");
    public static FixAction PowerSettings(bool primary = false) => Open("Open power settings", "ms-settings:powersleep", primary);
    /// <summary>Opens Control Center, scrolled to the named section (for example "Startup apps", "Battery care", "Fans").</summary>
    public static FixAction ControlCenter(string label, bool primary = false, string? section = null) =>
        Page(label, section is null ? "Control" : "Control|" + section, primary);
    public static FixAction Restart(bool primary = false) =>
        Do(FixIds.Restart, "Restart now", "Your PC will restart in about 15 seconds. Save your work in other programs first. Restart now?", primary: primary);
    public static FixAction Unlock() => Do(FixIds.Unlock, "Unlock full access", primary: true);

    // ───────────── Guides ─────────────

    public static FixAction RestartToUefi() =>
        Do(FixIds.RestartToUefi, "Restart into BIOS / UEFI setup",
            "Your PC will restart in about 15 seconds straight into its BIOS / UEFI setup screen. Save your work in other programs first. Continue?",
            primary: false);

    private static List<FixAction> GuideActions(string? supportUrl, string supportLabel, bool includeUefi = true)
    {
        var list = new List<FixAction>();
        if (includeUefi) list.Add(RestartToUefi());
        if (!string.IsNullOrEmpty(supportUrl)) list.Add(Web(supportLabel, supportUrl));
        return list;
    }

    public static Guide EnableMemoryProfile(string? supportUrl, int ratedMts, int runningMts) => new(
        "Turn on your memory's rated speed (XMP / EXPO)",
        $"Your RAM is rated for {ratedMts} MT/s but runs at {runningMts} MT/s. The speed profile is a BIOS/UEFI setting; Windows can't change it.",
        new[]
        {
            "Save your work, then restart into BIOS / UEFI setup (button below, or press Del / F2 / F10 / Esc while the PC starts).",
            "Look for a section named Memory, Overclocking, Tweaker or AI Tweaker.",
            "Find the option called XMP (Intel), DOCP or EXPO (AMD) and set it to Profile 1 or Enabled.",
            "Save and exit (usually F10). The PC restarts.",
            "Come back to CoreScope's Memory page: the speed should now match the rating. If Windows fails to start, enter BIOS again and load defaults.",
        },
        GuideActions(supportUrl, "Your PC maker's support page"));

    public static Guide EnableVirtualization(string? supportUrl, bool amd) => new(
        "Turn on hardware virtualization",
        "Virtualization powers Windows Sandbox, WSL, Android apps, Docker and virtual machines. It is switched on in the BIOS / UEFI.",
        new[]
        {
            "Save your work, then restart into BIOS / UEFI setup (button below).",
            amd ? "Open Advanced, then CPU Configuration (sometimes Overclocking)." : "Open Advanced, then CPU Configuration or Processor (sometimes Security).",
            amd ? "Set SVM Mode (also called AMD-V) to Enabled." : "Set Intel Virtualization Technology (VT-x) to Enabled.",
            "Save and exit (usually F10).",
            "In Windows, open Task Manager › Performance › CPU: it should say Virtualization: Enabled.",
        },
        GuideActions(supportUrl, "Your PC maker's support page")
            .Append(Open("Open Windows features", "optionalfeatures")).ToList());

    public static Guide EnableSecureBoot(string? supportUrl) => new(
        "Turn on Secure Boot",
        "Secure Boot stops tampered boot software from loading. It is a BIOS / UEFI setting and needs the drive to use the GPT layout.",
        new[]
        {
            "Check first: press Win+R, type msinfo32, press Enter. If 'BIOS Mode' says UEFI, you can continue. If it says Legacy, the drive must be converted to GPT first (Windows can do this without losing data: search for 'mbr2gpt').",
            "Restart into BIOS / UEFI setup (button below).",
            "Open Boot or Security, find Secure Boot and set it to Enabled (some PCs want the OS type set to 'Windows UEFI' first).",
            "Save and exit (usually F10).",
            "Back in Windows, msinfo32 should now show Secure Boot State: On.",
        },
        GuideActions(supportUrl, "Your PC maker's support page")
            .Append(Open("Windows device security", "windowsdefender://devicesecurity")).ToList());

    public static Guide EnableTpm(string? supportUrl, bool amd) => new(
        "Turn on the security chip (TPM)",
        "Windows 11 and BitLocker use a TPM. Most PCs have one built into the processor, switched off in the BIOS / UEFI.",
        new[]
        {
            "Restart into BIOS / UEFI setup (button below).",
            amd ? "Open Advanced, then AMD fTPM configuration (or Security), and enable AMD fTPM / Firmware TPM." : "Open Security (or Advanced), and enable Intel PTT / Platform Trust Technology (also called Firmware TPM).",
            "If you see a discrete 'TPM Device' option, set it to Enabled / Available.",
            "Save and exit (usually F10).",
            "In Windows press Win+R, type tpm.msc, press Enter: it should say the TPM is ready.",
        },
        GuideActions(supportUrl, "Your PC maker's support page")
            .Append(Open("Open TPM manager", "tpm.msc")).ToList());

    public static Guide UpdateBios(string? supportUrl, string currentVersion) => new(
        "Update the BIOS / UEFI",
        $"BIOS updates fix security issues and add hardware support. Yours is {(string.IsNullOrWhiteSpace(currentVersion) ? "an older version" : currentVersion)}. A bad update can stop the PC from starting, so follow the maker's own instructions.",
        new[]
        {
            "Plug the PC into power. On a laptop keep the charger connected the whole time and never switch it off while updating.",
            "Open your PC maker's support page (button below), enter your exact model, and open Drivers or BIOS.",
            "Compare the newest BIOS version with the one in CoreScope's Motherboard page. Only continue if it is newer.",
            "Download it and run the maker's update tool (read the release notes first).",
            "The PC restarts a few times. Do not touch it until Windows is back.",
        },
        GuideActions(supportUrl, "Your PC maker's support page", includeUefi: false)
            .Append(Page("See my BIOS version", "Motherboard")).ToList());

    public static Guide FixSingleChannel(string? supportUrl) => new(
        "Run your memory in dual channel",
        "Dual channel roughly doubles memory bandwidth, which helps games, video and integrated graphics.",
        new[]
        {
            "Dual channel needs two matching modules (same size and speed) in the right slots. See CoreScope's Upgrade page for what to buy.",
            "Switch the PC off and unplug it. On a laptop, remove the battery cover or bottom panel as the maker's manual describes.",
            "Fit the second module (or move both into the slots the manual labels as dual channel, often slots 2 and 4 on desktops).",
            "Start the PC and check the Memory page: Channels should read Dual.",
        },
        new List<FixAction> { Page("What to buy", "Upgrade", true) }.Concat(GuideActions(supportUrl, "Your PC maker's support page", includeUefi: false)).ToList());

    public static Guide FailingDrive() => new(
        "A drive is failing: protect your files first",
        "A drive reporting trouble can stop working without warning. Copy anything you can't lose to another drive or the cloud before doing anything else.",
        new[]
        {
            "Stop heavy use of the drive. Do not defragment or run long scans on it yet.",
            "Back up your files now: to an external drive, or turn on cloud backup (button below).",
            "Check the details in CoreScope's Storage page, including health and error counts.",
            "Ask the drive's maker for a diagnostic tool, or have a technician test it, then replace the drive.",
            "After replacing it, restore your files from the backup.",
        },
        new List<FixAction> { Open("Windows backup settings", "ms-settings:backup", true), Page("Open Storage details", "Storage"), Page("What to buy", "Upgrade") });
}
