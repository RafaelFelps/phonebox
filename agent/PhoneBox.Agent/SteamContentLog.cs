using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Steam's logs/content_log.txt, read as it grows. The app manifests (appmanifest_*.acf) only get the downloaded byte
/// count now and then, so a progress bar built from them alone lags the Steam window and then jumps. This log says
/// right away when an update starts (with its size), which step it is in (verifying, downloading, committing…) and
/// the current download rate, which is enough to move the bar between manifest writes.
/// </summary>
public sealed partial class SteamContentLog
{
    /// <param name="DownloadTotal">Bytes to download, from the "update started" line (0 when unknown).</param>
    /// <param name="StageTotal">Bytes to write into the game folder.</param>
    /// <param name="Step">Steam's "App update changed" flags, e.g. "Running Update,Downloading,Staging".</param>
    /// <param name="State">Steam's "state changed" flags, e.g. "Update Required,Update Queued,Update Running".</param>
    public sealed record App(long DownloadTotal, long StageTotal, string Step, string State);

    private const int FirstRead = 512 * 1024;
    private readonly Dictionary<long, App> _apps = [];
    private string? _path;
    private long _pos = -1;

    /// <summary>The last "Current download rate" in bytes per second, and when Steam wrote it.</summary>
    public double Rate { get; private set; }
    public DateTime RateAt { get; private set; }

    public App? Get(long app) => _apps.GetValueOrDefault(app);

    /// <summary>Reads what was added since the last call (the tail of the file the first time).</summary>
    public void Read(string steamRoot)
    {
        var path = Path.Combine(steamRoot, "logs", "content_log.txt");
        if (path != _path) { _path = path; _pos = -1; _apps.Clear(); Rate = 0; }
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = f.Length;
            if (_pos > len) { _pos = -1; _apps.Clear(); } // Steam started a new log
            var start = _pos < 0 ? Math.Max(0, len - FirstRead) : _pos;
            if (start >= len) return;
            f.Position = start;
            var buf = new byte[len - start];
            f.ReadExactly(buf);
            var end = Array.LastIndexOf(buf, (byte)'\n');
            if (end < 0) return; // half a line: wait for the rest
            var text = Encoding.UTF8.GetString(buf, 0, end + 1);
            // the first read can start mid-line
            if (_pos < 0 && start > 0) text = text[(text.IndexOf('\n') + 1)..];
            _pos = start + end + 1;
            foreach (var line in text.Split('\n')) Feed(line.TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>One line of the log.</summary>
    internal void Feed(string line)
    {
        if (line.Length < 22 || line[0] != '[') return;
        if (RateLine().Match(line) is { Success: true } r)
        {
            Rate = double.Parse(r.Groups[2].Value, CultureInfo.InvariantCulture) * 1_000_000 / 8;
            RateAt = When(r.Groups[1].Value);
            return;
        }
        if (AppLine().Match(line) is not { Success: true } m) return;
        var id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var rest = m.Groups[2].Value;
        var app = _apps.GetValueOrDefault(id) ?? new App(0, 0, "", "");
        if (Started().Match(rest) is { Success: true } s)
            app = app with { DownloadTotal = Long(s.Groups[1]), StageTotal = Long(s.Groups[2]) };
        else if (rest.StartsWith("App update changed : ", StringComparison.Ordinal))
            app = app with { Step = rest["App update changed : ".Length..].Trim().TrimEnd(',') };
        else if (rest.StartsWith("state changed : ", StringComparison.Ordinal))
            app = app with { State = rest["state changed : ".Length..].Trim().TrimEnd(',') };
        else if (rest.StartsWith("scheduler finished", StringComparison.Ordinal) || rest.StartsWith("finished update", StringComparison.Ordinal))
        {
            _apps.Remove(id);
            return;
        }
        else return;
        _apps[id] = app;
    }

    private static long Long(Group g) => long.Parse(g.Value, CultureInfo.InvariantCulture);

    private static DateTime When(string stamp) =>
        DateTime.TryParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : DateTime.MinValue;

    [GeneratedRegex(@"^\[([\d-]+ [\d:]+)\] Current download rate: ([\d.]+) Mbps")]
    private static partial Regex RateLine();

    [GeneratedRegex(@"^\[[\d-]+ [\d:]+\] AppID (\d+) (.*)$")]
    private static partial Regex AppLine();

    [GeneratedRegex(@"^update started : download \d+/(\d+),.* stage \d+/(\d+)")]
    private static partial Regex Started();
}
