namespace PhoneBox.Agent;

// Data shared by the Windows and the Linux / macOS implementations.

public sealed record MediaSnapshot(string App, string Title, string Artist, string Album, bool Playing,
    long PosMs, long DurMs, int ArtId, bool CanSeek)
{
    /// <summary>When the position was read (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>); 0 = not dated.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long SampledAt { get; init; }

    /// <summary>
    /// How old PosMs is at the moment this is serialized. The phone adds it to what it counts from the arrival, so a
    /// sample that waited for the next 1 s tick (and one a second old when the monitor read it) is not shown late.
    /// </summary>
    public long AgeMs => SampledAt == 0 ? 0 : Math.Max(0, (long)(System.Diagnostics.Stopwatch.GetElapsedTime(SampledAt).TotalMilliseconds));
}

public sealed record AlbumArt(byte[] Bytes, string Mime);

public sealed record AudioSnapshot(int Volume, bool Muted, bool CanControl = true);

/// <summary>One app's own volume in the system mixer (the media widget uses it while that app is playing).</summary>
public sealed record AppAudioSnapshot(string App, int Volume, bool Muted);

/// <summary>Media apps whose own volume the media widget controls instead of the computer's (media app name → process).</summary>
public static class AppVolumeApps
{
    public static string? Process(string? mediaApp) => mediaApp switch
    {
        "Spotify" => "Spotify",
        _ => null,
    };
}
