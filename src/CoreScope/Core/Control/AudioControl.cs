using System;
using System.Runtime.InteropServices;

namespace CoreScope.Core.Control;

/// <summary>Master volume and mute of the default playback device (Windows Core Audio, documented API).</summary>
public static class AudioControl
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private const int Render = 0, Multimedia = 1, ClsCtxAll = 0x17;

    private static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out var device) != 0 || device is null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            return device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var obj) == 0 ? obj as IAudioEndpointVolume : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Error("Opening default audio endpoint", ex);
            return null;
        }
    }

    /// <summary>Volume 0–100, or null when there is no playback device.</summary>
    public static int? Volume() => Endpoint() is { } ep && ep.GetMasterVolumeLevelScalar(out float level) == 0
        ? (int)Math.Round(level * 100)
        : null;

    public static bool? Muted() => Endpoint() is { } ep && ep.GetMute(out bool muted) == 0 ? muted : null;

    public static bool SetVolume(int percent)
    {
        if (Endpoint() is not { } ep) return false;
        var context = Guid.Empty;
        return ep.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context) == 0;
    }

    public static bool SetMuted(bool muted)
    {
        if (Endpoint() is not { } ep) return false;
        var context = Guid.Empty;
        return ep.SetMute(muted, ref context) == 0;
    }
}
