using System.Management;
using NAudio.CoreAudioApi;

namespace Lintel.Services;

/// <summary>Master output volume + mute via the Windows Core Audio API (NAudio).</summary>
public static class SystemVolume
{
    private static readonly MMDeviceEnumerator Enumerator = new();

    private static MMDevice? Device()
    {
        try { return Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return null; }
    }

    public static bool Available => Device() != null;

    /// <summary>0–100, or -1 if unavailable.</summary>
    public static int Level()
    {
        try { using var d = Device(); return d == null ? -1 : (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100); }
        catch { return -1; }
    }

    public static bool Muted()
    {
        try { using var d = Device(); return d != null && d.AudioEndpointVolume.Mute; }
        catch { return false; }
    }

    public static void SetLevel(int percent)
    {
        try { using var d = Device(); if (d != null) d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f; }
        catch { }
    }

    public static void ToggleMute()
    {
        try { using var d = Device(); if (d != null) d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute; }
        catch { }
    }
}

/// <summary>Laptop / integrated-display brightness via WMI. Returns -1 when unsupported (most desktops).</summary>
public static class Brightness
{
    public static bool Available => Level() >= 0;

    public static int Level()
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }

    public static void SetLevel(int percent)
    {
        try
        {
            percent = Math.Clamp(percent, 0, 100);
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
        }
        catch { }
    }
}
