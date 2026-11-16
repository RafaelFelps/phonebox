using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Valve's text KeyValues format (.vdf / .acf): <c>"key" "value"</c> pairs and <c>"key" { … }</c> blocks.
/// Keys are case-insensitive, as Steam treats them.
/// </summary>
public sealed class Vdf
{
    private readonly Dictionary<string, object> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    public IEnumerable<string> Keys => _order;
    public string? this[string key] => _items.TryGetValue(key, out var v) ? v as string : null;
    public Vdf? Child(string key) => _items.TryGetValue(key, out var v) ? v as Vdf : null;
    public IEnumerable<(string Key, Vdf Value)> Children() =>
        _order.Select(k => (k, _items[k] as Vdf)).Where(x => x.Item2 is not null).Select(x => (x.k, x.Item2!));

    /// <summary>The block under a path of keys (null if any is missing).</summary>
    public Vdf? Path(params string[] keys)
    {
        Vdf? at = this;
        foreach (var k in keys) at = at?.Child(k);
        return at;
    }

    public long Long(string key) => long.TryParse(this[key], out var v) ? v : 0;

    private void Set(string key, object value)
    {
        if (!_items.ContainsKey(key)) _order.Add(key);
        _items[key] = value;
    }

    public static Vdf Parse(string text)
    {
        var root = new Vdf();
        var stack = new Stack<Vdf>();
        stack.Push(root);
        string? pendingKey = null;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '{')
            {
                var block = new Vdf();
                stack.Peek().Set(pendingKey ?? "", block);
                stack.Push(block);
                pendingKey = null; i++; continue;
            }
            if (c == '}') { if (stack.Count > 1) stack.Pop(); pendingKey = null; i++; continue; }
            string token;
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        var n = text[i + 1];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
                        i += 2; continue;
                    }
                    sb.Append(text[i++]);
                }
                i++; // closing quote
                token = sb.ToString();
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
                token = text[start..i];
            }
            if (pendingKey is null) pendingKey = token;
            else { stack.Peek().Set(pendingKey, token); pendingKey = null; }
        }
        return root;
    }

    public static Vdf? Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <param name="Downloading">Steam is fetching or installing an update for it.</param>
public sealed record SteamGame(long AppId, string Name, long SizeBytes, string Library, bool Downloading,
    long BytesToDownload, long BytesDownloaded, long BytesToStage, long BytesStaged, long LastUpdated);

public sealed record SteamPlay(long LastPlayed, long PlaytimeMin);

public sealed record SteamShot(long AppId, string File, long Taken, long Size);

/// <summary>What the Steam client keeps on this computer: install folders, games, play time, screenshots.</summary>
public static class SteamLocal
{
    /// <summary>The account id in userdata/ is the low 32 bits of the SteamID64.</summary>
    public const long SteamId64Base = 76561197960265728;

    public static string? Root()
    {
#if WINDOWS
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch (Exception) { }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var guess = Path.Combine(pf, "Steam");
        return Directory.Exists(guess) ? guess : null;
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[] { ".steam/steam", ".local/share/Steam", "Library/Application Support/Steam", ".var/app/com.valvesoftware.Steam/data/Steam" })
        {
            var full = Path.Combine(home, p);
            if (Directory.Exists(full)) return full;
        }
        return null;
#endif
    }

    /// <summary>The game running now (0 = none), from the client's registry value.</summary>
    public static long RunningAppId()
    {
#if WINDOWS
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return k?.GetValue("RunningAppID") is int id ? id : 0;
        }
        catch (Exception) { return 0; }
#else
        return 0;
#endif
    }

    /// <summary>Whether the Steam client is running.</summary>
    public static bool ClientRunning()
    {
        try
        {
            var ps = System.Diagnostics.Process.GetProcessesByName("steam");
            var any = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            return any;
        }
        catch (Exception) { return false; }
    }

    public static List<string> Libraries(string root)
    {
        var list = new List<string> { root };
        var vdf = Vdf.Load(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
        foreach (var (_, lib) in vdf?.Child("libraryfolders")?.Children() ?? [])
            if (lib["path"] is { Length: > 0 } p && Directory.Exists(p) && !list.Any(x => string.Equals(Path.GetFullPath(x), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)))
                list.Add(p);
        return list;
    }

    /// <summary>Tools and redistributables Steam lists as apps; not games anyone launches.</summary>
    private static readonly HashSet<long> NotGames = [228980, 1070560, 1391110, 1493710, 1628350, 1826330, 2180100, 2348590, 250820, 1007, 431960];

    public static SteamGame? ParseManifest(Vdf manifest, string library)
    {
        var s = manifest.Child("AppState");
        if (s is null) return null;
        var id = s.Long("appid");
        if (id <= 0 || NotGames.Contains(id)) return null;
        var name = s["name"] ?? $"App {id}";
        if (name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.Ordinal)
            || name.StartsWith("Steam Linux Runtime", StringComparison.Ordinal)) return null;
        var flags = s.Long("StateFlags");
        long toDl = s.Long("BytesToDownload"), dl = s.Long("BytesDownloaded"), toStage = s.Long("BytesToStage"), staged = s.Long("BytesStaged");
        // 1024 = update running, 2 = update required, 1048576 = downloading; a pending byte count means work left
        var downloading = (flags & (1024 | 1048576)) != 0 || (toDl > 0 && dl < toDl) || (toStage > 0 && staged < toStage && (flags & 4) == 0);
        return new SteamGame(id, name, s.Long("SizeOnDisk"), library, downloading, toDl, dl, toStage, staged, s.Long("LastUpdated"));
    }

    public static List<SteamGame> Games(string root)
    {
        var games = new List<SteamGame>();
        foreach (var lib in Libraries(root))
        {
            string[] files;
            try { files = Directory.GetFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"); }
            catch (Exception) { continue; }
            foreach (var f in files)
                if (Vdf.Load(f) is { } m && ParseManifest(m, lib) is { } g && !games.Any(x => x.AppId == g.AppId)) games.Add(g);
        }
        return games;
    }

    /// <summary>The signed-in account: the one marked MostRecent in loginusers.vdf, else the newest.</summary>
    public static (long SteamId, string? Name)? User(string root)
    {
        var users = Vdf.Load(Path.Combine(root, "config", "loginusers.vdf"))?.Child("users");
        if (users is null) return null;
        var best = users.Children()
            .Select(u => (Id: long.TryParse(u.Key, out var id) ? id : 0, u.Value))
            .Where(u => u.Id > SteamId64Base)
            .OrderByDescending(u => u.Value["MostRecent"] == "1")
            .ThenByDescending(u => u.Value.Long("Timestamp"))
            .FirstOrDefault();
        return best.Id == 0 ? null : (best.Id, best.Value["PersonaName"]);
    }

    public static Dictionary<long, SteamPlay> Playtime(string root, long steamId)
    {
        var map = new Dictionary<long, SteamPlay>();
        var cfg = Vdf.Load(Path.Combine(root, "userdata", (steamId - SteamId64Base).ToString(), "config", "localconfig.vdf"));
        var apps = cfg?.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps")
                   ?? cfg?.Path("UserLocalConfigStore", "Software", "valve", "Steam", "apps");
        foreach (var (key, app) in apps?.Children() ?? [])
            if (long.TryParse(key, out var id) && (app.Long("LastPlayed") > 0 || app.Long("Playtime") > 0))
                map[id] = new SteamPlay(app.Long("LastPlayed"), app.Long("Playtime"));
        return map;
    }

    /// <summary>Screenshots taken with Steam's F12 key, newest first.</summary>
    public static List<SteamShot> Screenshots(string root, long steamId, int max = 60)
    {
        var list = new List<SteamShot>();
        var remote = Path.Combine(root, "userdata", (steamId - SteamId64Base).ToString(), "760", "remote");
        if (!Directory.Exists(remote)) return list;
        foreach (var appDir in Directory.EnumerateDirectories(remote))
        {
            if (!long.TryParse(Path.GetFileName(appDir), out var app)) continue;
            var shots = Path.Combine(appDir, "screenshots");
            if (!Directory.Exists(shots)) continue;
            foreach (var f in Directory.EnumerateFiles(shots, "*.jpg"))
            {
                var info = new FileInfo(f);
                list.Add(new SteamShot(app, info.Name, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), info.Length));
            }
        }
        return list.OrderByDescending(s => s.Taken).Take(max).ToList();
    }

    public static string? ScreenshotPath(string root, long steamId, long app, string file, bool thumb)
    {
        if (app <= 0 || file.Contains('/') || file.Contains('\\') || file.Contains("..") || !file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return null;
        var dir = Path.Combine(root, "userdata", (steamId - SteamId64Base).ToString(), "760", "remote", app.ToString(), "screenshots");
        var p = thumb ? Path.Combine(dir, "thumbnails", file) : Path.Combine(dir, file);
        return File.Exists(p) ? p : thumb && File.Exists(Path.Combine(dir, file)) ? Path.Combine(dir, file) : null;
    }

    /// <summary>
    /// A cover the client cached, in librarycache/&lt;app&gt;/ or its subfolders. Older clients name them
    /// library_600x900 (portrait) and header (wide); newer ones library_capsule and library_header(_language).
    /// </summary>
    public static string? CoverPath(string root, long app, bool portrait)
    {
        string[] names = portrait ? ["library_600x900", "library_capsule"] : ["header", "library_header"];
        var cache = Path.Combine(root, "appcache", "librarycache");
        foreach (var name in names)
        {
            var flat = Path.Combine(cache, $"{app}_{name}.jpg");
            if (File.Exists(flat)) return flat;
        }
        var dir = Path.Combine(cache, app.ToString());
        if (!Directory.Exists(dir)) return null;
        try
        {
            foreach (var name in names)
                if (Directory.EnumerateFiles(dir, name + "*.jpg", SearchOption.AllDirectories)
                        .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length).FirstOrDefault() is { } hit)
                    return hit;
        }
        catch (Exception) { }
        return null;
    }
}
