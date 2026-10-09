using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.Control;

public sealed record ToggleableDevice(string DeviceId, string Name, string Category, string Warning, bool Enabled);

/// <summary>
/// Turns individual devices on/off (what Device Manager's "Disable device" does), restricted to an
/// allow-list of categories where turning something off can't leave the PC unusable.
/// Keyboards, displays, storage and system devices are never offered.
/// </summary>
public static class DeviceControl
{
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Enable_DevNode(uint devInst, uint flags);

    private const int CrSuccess = 0;
    private const uint DisableUiNotOk = 0x4, DisablePersist = 0x8;
    private const uint LocatePhantom = 0x1;

    private static readonly Regex Touchpad = new(@"touch ?pad|precision", RegexOptions.IgnoreCase);
    private static readonly Regex Fingerprint = new(@"fingerprint|biometric", RegexOptions.IgnoreCase);

    public static List<ToggleableDevice> Candidates(IReadOnlyList<DeviceCategory> categories)
    {
        var list = new List<ToggleableDevice>();
        foreach (var category in categories)
        {
            foreach (var d in category.Devices)
            {
                if (d.HasProblem) continue;
                string? warning = category.Title switch
                {
                    "Cameras" when d.ClassName is "Camera" or "Image" => "Apps won't be able to use the camera until you turn it back on.",
                    "Bluetooth" when !d.DeviceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) => "Bluetooth mice, keyboards and headphones will disconnect.",
                    "Network adapters" => "You'll lose this network connection while it's off.",
                    "Keyboard, mouse & touch" when Touchpad.IsMatch(d.Name) => "Make sure you have a mouse connected, or you'll need the keyboard to turn it back on.",
                    "Security & biometrics" when Fingerprint.IsMatch(d.Name) => "Windows Hello fingerprint sign-in won't work while it's off.",
                    "Audio" when d.ClassName == "MEDIA" => "Speakers and microphone on this device will stop working.",
                    _ => null,
                };
                if (warning is null) continue;
                list.Add(new ToggleableDevice(d.DeviceId, d.Name, category.Title, warning, !d.IsDisabled));
            }
        }
        return list;
    }

    public static bool SetEnabled(string deviceId, bool enable)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, deviceId, LocatePhantom) != CrSuccess) return false;
            int result = enable ? CM_Enable_DevNode(node, 0) : CM_Disable_DevNode(node, DisableUiNotOk | DisablePersist);
            Log.Info($"Control: device {deviceId} → {(enable ? "enabled" : "disabled")} : CR {result}");
            return result == CrSuccess;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("Toggling device", ex);
            return false;
        }
    }
}
