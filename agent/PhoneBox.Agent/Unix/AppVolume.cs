namespace PhoneBox.Agent;

/// <summary>Per-app volume is Windows only (its mixer); on Linux / macOS the media widget keeps the master volume.</summary>
public sealed class AppVolume
{
    public AppAudioSnapshot? Snapshot(string app) => null;
    public AppAudioSnapshot? SetVolume(string app, int level) => null;
    public AppAudioSnapshot? Step(string app, int delta) => null;
    public AppAudioSnapshot? ToggleMute(string app) => null;
}
