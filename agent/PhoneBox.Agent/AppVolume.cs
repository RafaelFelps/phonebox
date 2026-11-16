using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PhoneBox.Agent;

/// <summary>
/// One app's own volume in the Windows mixer (its audio sessions on the default output), so the media widget can
/// turn Spotify down without touching the computer's volume. Only apps in <see cref="AppVolumeApps"/> are touched.
/// </summary>
public sealed class AppVolume
{
    private readonly Lock _lock = new();
    private (string App, AppAudioSnapshot? Snap, long At)? _cache;

    /// <summary>The app's volume and mute, or null when it has no audio session (not running, or never played yet).</summary>
    public AppAudioSnapshot? Snapshot(string app)
    {
        lock (_lock)
        {
            if (_cache is { } c && c.App == app && Environment.TickCount64 - c.At < 700) return c.Snap;
            var snap = Use(app, sessions =>
            {
                // Spotify has several processes; the one that plays has an active session, so prefer that one
                var s = sessions.FirstOrDefault(x => x.State == AudioSessionState.AudioSessionStateActive) ?? sessions[0];
                return new AppAudioSnapshot(app, (int)Math.Round(s.SimpleAudioVolume.Volume * 100), s.SimpleAudioVolume.Mute);
            });
            _cache = (app, snap, Environment.TickCount64);
            return snap;
        }
    }

    public AppAudioSnapshot? SetVolume(string app, int level) => Change(app, s =>
    {
        s.SimpleAudioVolume.Volume = Math.Clamp(level, 0, 100) / 100f;
        if (level > 0) s.SimpleAudioVolume.Mute = false;
    });

    public AppAudioSnapshot? Step(string app, int delta) =>
        Snapshot(app) is { } cur ? SetVolume(app, cur.Volume + delta) : null;

    public AppAudioSnapshot? ToggleMute(string app)
    {
        if (Snapshot(app) is not { } cur) return null;
        return Change(app, s => s.SimpleAudioVolume.Mute = !cur.Muted);
    }

    private AppAudioSnapshot? Change(string app, Action<AudioSessionControl> set)
    {
        if (Use(app, sessions => { foreach (var s in sessions) set(s); return true; }) != true) return null;
        lock (_lock) _cache = null;
        return Snapshot(app);
    }

    /// <summary>Runs <paramref name="f"/> on the app's sessions on the default output; null when there are none or Core Audio fails.</summary>
    private static T? Use<T>(string app, Func<List<AudioSessionControl>, T> f)
    {
        if (AppVolumeApps.Process(app) is not { } process) return default;
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var all = dev.AudioSessionManager.Sessions;
            var mine = new List<AudioSessionControl>();
            for (var i = 0; i < all.Count; i++)
            {
                var s = all[i];
                if (s.State == AudioSessionState.AudioSessionStateExpired || s.IsSystemSoundsSession) continue;
                if (ProcessName((int)s.GetProcessID) is { } name && string.Equals(name, process, StringComparison.OrdinalIgnoreCase)) mine.Add(s);
            }
            return mine.Count == 0 ? default : f(mine);
        }
        catch { return default; }
    }

    private static string? ProcessName(int pid)
    {
        if (pid <= 0) return null;
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }
}
