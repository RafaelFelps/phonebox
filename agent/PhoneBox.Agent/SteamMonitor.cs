using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record SteamNow(long AppId, string Name, long Since, int? Players);
public sealed record SteamAchievement(string Name, string? Description, string? Icon, long UnlockedAt);
public sealed record SteamAchievements(long AppId, string Game, int Done, int Total, IReadOnlyList<SteamAchievement> Recent, IReadOnlyList<SteamAchievement> Next);
/// <param name="Phase">downloading, installing or queued</param>
public sealed record SteamDownload(long AppId, string Name, double Progress, long Done, long Total, double? BytesPerSec, string Phase);
/// <param name="State">0 offline, 1 online, 2 busy, 3 away, 4 snooze, 5 looking to trade, 6 looking to play</param>
public sealed record SteamFriend(string Id, string Name, string? Avatar, int State, string? Game, long? GameId, long? LastOff);
public sealed record SteamRecent(long AppId, string Name, long TwoWeeksMin, long TotalMin);
public sealed record SteamNews(string Title, string Url, long Date, string? Feed, string Contents);
public sealed record SteamSale(long AppId, string Name, int Discount, string Price, string? Was);
public sealed record SteamLibraryItem(long AppId, string Name, long SizeBytes, long LastPlayed, long PlaytimeMin, bool Downloading);

/// <param name="Status">ok, not-installed (no Steam client found) or off (no Steam widget on a page)</param>
public sealed record SteamSnapshot(string Status, string? Error, bool HasKey, bool ClientRunning, string? User, string? Avatar, int? State,
    SteamNow? Playing, SteamAchievements? Achievements, IReadOnlyList<SteamDownload> Downloads, IReadOnlyList<SteamFriend> Friends,
    IReadOnlyList<SteamRecent> Recent, long? NewsApp, string? NewsGame, IReadOnlyList<SteamNews> News, IReadOnlyList<SteamSale> Sales, long Updated);

/// <summary>
/// Steam on this computer and on the web. The client's own files give the installed games, downloads, play time and
/// screenshots (no account needed); the Steam Web API, with the user's key, gives friends, achievements, the recent
/// games, news and wishlist prices. Notifications go to the phone like a webhook's: a finished download, a new
/// achievement, a friend starting a game, a wishlist game on sale. Nothing runs unless a page has a Steam widget.
/// </summary>
public sealed class SteamMonitor(ConfigStore config, PushStore push, ILogger<SteamMonitor> log) : BackgroundService
{
    private const string Api = "https://api.steampowered.com";
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/1.0");
        return h;
    }

    private readonly object _lock = new();
    private string? _root;
    private List<SteamGame> _games = [];
    private Dictionary<long, SteamPlay> _play = [];
    private readonly Dictionary<long, (long Bytes, long At)> _dlPrev = [];
    private readonly Dictionary<long, double> _dlSpeed = [];
    private HashSet<long> _wasDownloading = [];
    private bool _gamesLoaded;
    private long _running, _runningSince;
    private bool _clientRunning;

    private long _steamId;
    private string? _user, _avatar, _error;
    private int? _state;
    private long? _webGame;
    private string? _webGameName;
    private List<SteamFriend> _friends = [];
    private List<string> _friendIds = [];
    private bool _friendsLoaded;
    private List<SteamRecent> _recent = [];
    private SteamAchievements? _ach;
    private HashSet<string>? _achDone;
    private long _achApp;
    private readonly Dictionary<string, Dictionary<string, (string Name, string? Desc, string? Icon, string? Gray)>> _schema = [];
    private readonly Dictionary<long, int?> _players = [];
    private long? _newsApp;
    private string? _newsGame;
    private List<SteamNews> _news = [];
    private List<SteamSale> _sales = [];
    private readonly Dictionary<long, string> _storeNames = [];
    private long _updated;

    private long _nextPlay, _nextSummary, _nextFriends, _nextRecent, _nextAch, _nextPlayers, _nextNews, _nextSales;
    private string? _lastKeyId; // key + id the web data belongs to: a change starts over

    private static readonly string SalesPath = Path.Combine(AppPaths.LocalDir, "steam-sales.json");
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string Type, long At)> _images = new();

    public static bool WidgetOn(AgentConfig cfg) => cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type.StartsWith("steam", StringComparison.Ordinal));

    // ------------------------------------------------------------------ what the phone reads

    public SteamSnapshot Snapshot()
    {
        var cfg = config.Get();
        lock (_lock)
        {
            if (!WidgetOn(cfg)) return Empty("off");
            if (_root is null) return Empty("not-installed");
            var games = _games.ToDictionary(g => g.AppId);
            SteamNow? playing = null;
            if (_running > 0)
                playing = new(_running, NameOf(_running), _runningSince, _players.GetValueOrDefault(_running));
            else if (_webGame is { } wg && wg > 0)
                playing = new(wg, _webGameName ?? NameOf(wg), _runningSince, _players.GetValueOrDefault(wg));
            var downloads = _games.Where(g => g.Downloading).Select(Download).OrderByDescending(d => d.Phase == "downloading").ToList();
            return new SteamSnapshot("ok", _error, !string.IsNullOrEmpty(cfg.Steam.ApiKey), _clientRunning, _user, _avatar, _state,
                playing, _ach, downloads,
                _friends.Where(f => f.State > 0).OrderByDescending(f => f.Game is not null).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Concat(_friends.Where(f => f.State == 0).OrderByDescending(f => f.LastOff ?? 0)).ToList(),
                _recent, _newsApp, _newsGame, _news, _sales, _updated);
        }
    }

    private static SteamSnapshot Empty(string status) =>
        new(status, null, false, false, null, null, null, null, null, [], [], [], null, null, [], [], 0);

    public List<SteamLibraryItem> Library()
    {
        lock (_lock)
            return _games.Select(g =>
            {
                var p = _play.GetValueOrDefault(g.AppId);
                return new SteamLibraryItem(g.AppId, g.Name, g.SizeBytes, p?.LastPlayed ?? 0, p?.PlaytimeMin ?? 0, g.Downloading);
            }).OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public object Screenshots()
    {
        string? root; long id;
        lock (_lock) { root = _root; id = _steamId; }
        if (root is null || id == 0) return new { shots = Array.Empty<object>() };
        return new
        {
            shots = SteamLocal.Screenshots(root, id).Select(s => new { app = s.AppId, game = NameOf(s.AppId), file = s.File, taken = s.Taken, size = s.Size }),
        };
    }

    public string? ScreenshotFile(long app, string file, bool thumb)
    {
        string? root; long id;
        lock (_lock) { root = _root; id = _steamId; }
        return root is null || id == 0 ? null : SteamLocal.ScreenshotPath(root, id, app, file, thumb);
    }

    private string NameOf(long app)
    {
        lock (_lock)
        {
            if (_games.FirstOrDefault(g => g.AppId == app) is { } g) return g.Name;
            if (_recent.FirstOrDefault(r => r.AppId == app) is { } r) return r.Name;
            if (_storeNames.TryGetValue(app, out var n)) return n;
            if (_webGame == app && _webGameName is { } w) return w;
        }
        return $"App {app}";
    }

    private SteamDownload Download(SteamGame g)
    {
        var dlLeft = g.BytesToDownload > 0 && g.BytesDownloaded < g.BytesToDownload;
        var stageLeft = g.BytesToStage > 0 && g.BytesStaged < g.BytesToStage;
        var (done, total, phase) = dlLeft ? (g.BytesDownloaded, g.BytesToDownload, "downloading")
            : stageLeft ? (g.BytesStaged, g.BytesToStage, "installing") : (0L, 0L, "queued");
        var speed = _dlSpeed.TryGetValue(g.AppId, out var s) && phase == "downloading" ? s : (double?)null;
        return new SteamDownload(g.AppId, g.Name, total > 0 ? Math.Round(done * 100.0 / total, 1) : 0, done, total, speed, phase);
    }

    // ------------------------------------------------------------------ the loop

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            var cfg = config.Get();
            if (!WidgetOn(cfg)) { await Wake.Delay(TimeSpan.FromSeconds(5), ct); continue; }
            try { ReadLocal(); }
            catch (Exception ex) { log.LogDebug("steam local: {Msg}", ex.Message); }
            try { await ReadWeb(cfg.Steam, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogDebug("steam web: {Msg}", ex.Message); }
            lock (_lock) _updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Wake.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private void ReadLocal()
    {
        var root = SteamLocal.Root();
        var now = Environment.TickCount64;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (root is null) { lock (_lock) _root = null; return; }
        var games = SteamLocal.Games(root);
        var running = SteamLocal.RunningAppId();
        var client = SteamLocal.ClientRunning();
        var user = SteamLocal.User(root);
        Dictionary<long, SteamPlay>? play = null;
        if (now >= _nextPlay && user is { } u) { play = SteamLocal.Playtime(root, u.SteamId); _nextPlay = now + 60_000; }

        var finished = new List<SteamGame>();
        lock (_lock)
        {
            _root = root;
            _clientRunning = client;
            if (play is not null) _play = play;
            var cfgId = config.Get().Steam.SteamId;
            _steamId = long.TryParse(cfgId, out var fixedId) && fixedId > SteamLocal.SteamId64Base ? fixedId : user?.SteamId ?? 0;
            _user ??= user?.Name;
            if (running != _running) { _running = running; _runningSince = running > 0 ? nowMs : 0; }

            // download speed from the change since the last reading
            foreach (var g in games.Where(g => g.Downloading))
            {
                if (_dlPrev.TryGetValue(g.AppId, out var prev) && now > prev.At && g.BytesDownloaded >= prev.Bytes)
                {
                    var rate = (g.BytesDownloaded - prev.Bytes) * 1000.0 / (now - prev.At);
                    _dlSpeed[g.AppId] = _dlSpeed.TryGetValue(g.AppId, out var old) ? old * 0.6 + rate * 0.4 : rate;
                }
                _dlPrev[g.AppId] = (g.BytesDownloaded, now);
            }
            var downloading = games.Where(g => g.Downloading).Select(g => g.AppId).ToHashSet();
            foreach (var id in _dlPrev.Keys.Where(k => !downloading.Contains(k)).ToList()) { _dlPrev.Remove(id); _dlSpeed.Remove(id); }
            if (_gamesLoaded)
                finished.AddRange(games.Where(g => _wasDownloading.Contains(g.AppId) && !g.Downloading));
            _wasDownloading = downloading;
            _games = games;
            _gamesLoaded = true;
        }
        if (config.Get().Steam.NotifyDownloads)
            foreach (var g in finished) Notify("Download finished", $"{g.Name} is ready to play", "ok");
    }

    // ------------------------------------------------------------------ Steam Web API

    private async Task ReadWeb(SteamConfig cfg, CancellationToken ct)
    {
        long id;
        lock (_lock) id = _steamId;
        var now = Environment.TickCount64;
        var focus = FocusApp();

        // the player count and news need no key
        if (focus is { } fa && (now >= _nextPlayers || !_players.ContainsKey(fa)))
        {
            _nextPlayers = now + 300_000;
            var j = await GetJson($"{Api}/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={fa}", ct);
            var count = Resp(j) is { } r && r.TryGetProperty("player_count", out var pc) ? pc.GetInt32() : (int?)null;
            lock (_lock) _players[fa] = count;
        }
        if (focus is { } na && (now >= _nextNews || _newsApp != na))
        {
            _nextNews = now + 1_800_000;
            var j = await GetJson($"{Api}/ISteamNews/GetNewsForApp/v2/?appid={na}&count=6&maxlength=400&format=json", ct);
            var items = new List<SteamNews>();
            if (j?.TryGetProperty("appnews", out var an) == true && an.TryGetProperty("newsitems", out var ni))
                foreach (var n in ni.EnumerateArray())
                    items.Add(new SteamNews(Str(n, "title") ?? "", Str(n, "url") ?? "", (n.TryGetProperty("date", out var d) ? d.GetInt64() : 0) * 1000,
                        Str(n, "feedlabel"), CleanNews(Str(n, "contents") ?? "")));
            lock (_lock) { _newsApp = na; _newsGame = NameOf(na); _news = items; }
        }

        if (string.IsNullOrEmpty(cfg.ApiKey) || id == 0)
        {
            lock (_lock)
            {
                _error = string.IsNullOrEmpty(cfg.ApiKey) ? null : "Steam account not found: sign in to Steam on this PC or set your SteamID in Settings › Steam";
                _friends = []; _recent = []; _ach = null; _sales = []; _webGame = null; _state = null; _avatar = null;
            }
            return;
        }
        var key = Uri.EscapeDataString(cfg.ApiKey);
        if (_lastKeyId != cfg.ApiKey + "|" + id)
        {
            _lastKeyId = cfg.ApiKey + "|" + id;
            _nextSummary = _nextFriends = _nextRecent = _nextAch = _nextSales = 0;
            lock (_lock) { _friendsLoaded = false; _friendIds = []; _achDone = null; _achApp = 0; _error = null; }
        }

        try
        {
            if (now >= _nextFriends)
            {
                _nextFriends = now + 600_000;
                var j = await GetJson($"{Api}/ISteamUser/GetFriendList/v1/?key={key}&steamid={id}&relationship=friend", ct, allowForbidden: true);
                var ids = new List<string>();
                if (j?.TryGetProperty("friendslist", out var fl) == true && fl.TryGetProperty("friends", out var fs))
                    foreach (var f in fs.EnumerateArray()) if (Str(f, "steamid") is { } s) ids.Add(s);
                lock (_lock) _friendIds = ids;
            }
            if (now >= _nextSummary)
            {
                _nextSummary = now + 60_000;
                await ReadSummaries(key, id, cfg, ct);
            }
            if (now >= _nextRecent)
            {
                _nextRecent = now + 1_800_000;
                var j = await GetJson($"{Api}/IPlayerService/GetRecentlyPlayedGames/v1/?key={key}&steamid={id}&count=10", ct);
                var list = new List<SteamRecent>();
                if (Resp(j) is { } r && r.TryGetProperty("games", out var gs))
                    foreach (var g in gs.EnumerateArray())
                        list.Add(new SteamRecent(g.GetProperty("appid").GetInt64(), Str(g, "name") ?? "",
                            g.TryGetProperty("playtime_2weeks", out var p2) ? p2.GetInt64() : 0, g.TryGetProperty("playtime_forever", out var pf) ? pf.GetInt64() : 0));
                lock (_lock) _recent = list;
            }
            focus = FocusApp();
            var playingNow = CurrentGame() is not null;
            if (focus is { } aa && (now >= _nextAch || aa != _achApp))
            {
                _nextAch = now + (playingNow ? 60_000 : 900_000);
                await ReadAchievements(key, id, aa, cfg, ct);
            }
            if (now >= _nextSales)
            {
                _nextSales = now + 6 * 3_600_000;
                await ReadSales(key, id, cfg, ct);
            }
            lock (_lock) _error = null;
        }
        catch (SteamApiException ex)
        {
            lock (_lock) _error = ex.Message;
        }
    }

    private sealed class SteamApiException(string message) : Exception(message);

    private async Task ReadSummaries(string key, long id, SteamConfig cfg, CancellationToken ct)
    {
        List<string> ids;
        lock (_lock) ids = [id.ToString(), .. _friendIds];
        var all = new List<JsonElement>();
        foreach (var chunk in ids.Chunk(100))
        {
            var j = await GetJson($"{Api}/ISteamUser/GetPlayerSummaries/v2/?key={key}&steamids={string.Join(',', chunk)}", ct);
            if (Resp(j) is { } r && r.TryGetProperty("players", out var ps)) all.AddRange(ps.EnumerateArray().Select(p => p.Clone()));
        }
        var started = new List<(string Who, string Game)>();
        lock (_lock)
        {
            var friends = new List<SteamFriend>();
            foreach (var p in all)
            {
                var sid = Str(p, "steamid");
                long? game = long.TryParse(Str(p, "gameid"), out var gid) ? gid : null;
                if (sid == id.ToString())
                {
                    _user = Str(p, "personaname") ?? _user;
                    _avatar = Str(p, "avatarfull") ?? Str(p, "avatarmedium");
                    _state = p.TryGetProperty("personastate", out var st) ? st.GetInt32() : null;
                    if (game != _webGame && game is not null && _running == 0) _runningSince = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _webGame = game;
                    _webGameName = Str(p, "gameextrainfo");
                    if (game is { } g && _webGameName is { } gn) _storeNames[g] = gn;
                    continue;
                }
                var f = new SteamFriend(sid ?? "", Str(p, "personaname") ?? "?", Str(p, "avatarmedium"),
                    p.TryGetProperty("personastate", out var s) ? s.GetInt32() : 0, Str(p, "gameextrainfo"), game,
                    p.TryGetProperty("lastlogoff", out var lo) ? lo.GetInt64() * 1000 : null);
                if (_friendsLoaded && f.Game is { } gameName && _friends.FirstOrDefault(o => o.Id == f.Id) is { } old && old.GameId != f.GameId)
                    started.Add((f.Name, gameName));
                friends.Add(f);
            }
            _friends = friends;
            _friendsLoaded = true;
        }
        if (cfg.NotifyFriends)
            foreach (var (who, game) in started.Take(3)) Notify("Steam", $"{who} started playing {game}", "");
    }

    private async Task ReadAchievements(string key, long id, long app, SteamConfig cfg, CancellationToken ct)
    {
        var lang = Uri.EscapeDataString(cfg.Language);
        var schemaKey = $"{app}|{cfg.Language}";
        if (!_schema.ContainsKey(schemaKey))
        {
            var s = await GetJson($"{Api}/ISteamUserStats/GetSchemaForGame/v2/?key={key}&appid={app}&l={lang}", ct, allowForbidden: true);
            var map = new Dictionary<string, (string, string?, string?, string?)>();
            if (s?.TryGetProperty("game", out var g) == true && g.TryGetProperty("availableGameStats", out var st) && st.TryGetProperty("achievements", out var achs))
                foreach (var a in achs.EnumerateArray())
                    if (Str(a, "name") is { } n) map[n] = (Str(a, "displayName") ?? n, Str(a, "description"), Str(a, "icon"), Str(a, "icongray"));
            _schema[schemaKey] = map;
        }
        var schema = _schema[schemaKey];
        if (schema.Count == 0) { lock (_lock) { _ach = null; _achApp = app; _achDone = null; } return; }
        var j = await GetJson($"{Api}/ISteamUserStats/GetPlayerAchievements/v1/?key={key}&steamid={id}&appid={app}&l={lang}", ct, allowForbidden: true);
        if (j?.TryGetProperty("playerstats", out var ps) != true || !ps.TryGetProperty("achievements", out var list))
        {
            lock (_lock) { _ach = null; _achApp = app; _achDone = null; }
            return;
        }
        var done = new List<(string Api, SteamAchievement A)>();
        var todo = new List<SteamAchievement>();
        foreach (var a in list.EnumerateArray())
        {
            var api = Str(a, "apiname") ?? "";
            var meta = schema.TryGetValue(api, out var m) ? m : (api, null, null, null);
            if (a.TryGetProperty("achieved", out var ach) && ach.GetInt32() == 1)
            {
                done.Add((api, new(meta.Item1, meta.Item2, meta.Item3, (a.TryGetProperty("unlocktime", out var ut) ? ut.GetInt64() : 0) * 1000)));
            }
            else todo.Add(new(meta.Item1, meta.Item2, meta.Item4 ?? meta.Item3, 0));
        }
        List<SteamAchievement> fresh = [];
        lock (_lock)
        {
            if (_achApp == app && _achDone is { } before)
                fresh = done.Where(d => !before.Contains(d.Api)).Select(d => d.A).ToList();
            _achApp = app;
            _achDone = done.Select(d => d.Api).ToHashSet();
            _ach = new SteamAchievements(app, NameOf(app), done.Count, done.Count + todo.Count,
                done.Select(d => d.A).OrderByDescending(d => d.UnlockedAt).Take(6).ToList(), todo.Take(6).ToList());
        }
        if (cfg.NotifyAchievements)
            foreach (var a in fresh.Take(3)) Notify("Achievement unlocked", $"{a.Name}" + (a.Description is { Length: > 0 } d ? $" — {d}" : ""), "ok");
    }

    private async Task ReadSales(string key, long id, SteamConfig cfg, CancellationToken ct)
    {
        var w = await GetJson($"{Api}/IWishlistService/GetWishlist/v1/?key={key}&steamid={id}", ct, allowForbidden: true);
        var apps = new List<long>();
        if (Resp(w) is { } r && r.TryGetProperty("items", out var items))
            foreach (var i in items.EnumerateArray()) apps.Add(i.GetProperty("appid").GetInt64());
        var onSale = new List<(long App, int Discount, string Price, string? Was)>();
        foreach (var chunk in apps.Take(300).Chunk(50))
        {
            var p = await GetJson($"https://store.steampowered.com/api/appdetails?appids={string.Join(',', chunk)}&filters=price_overview&cc={cfg.Country}", ct, allowForbidden: true);
            if (p is not { ValueKind: JsonValueKind.Object } prices) continue;
            foreach (var app in chunk)
                if (prices.TryGetProperty(app.ToString(), out var e) && e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("price_overview", out var po) && po.TryGetProperty("discount_percent", out var dp) && dp.GetInt32() > 0)
                    onSale.Add((app, dp.GetInt32(), Str(po, "final_formatted") ?? "", Str(po, "initial_formatted")));
        }
        var sales = new List<SteamSale>();
        foreach (var s in onSale.OrderByDescending(s => s.Discount).Take(30))
        {
            string? name;
            lock (_lock) name = _storeNames.GetValueOrDefault(s.App);
            if (name is null)
            {
                var d = await GetJson($"https://store.steampowered.com/api/appdetails?appids={s.App}&filters=basic&cc={cfg.Country}", ct, allowForbidden: true);
                name = d?.TryGetProperty(s.App.ToString(), out var e) == true && e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? Str(data, "name") : null;
                name ??= $"App {s.App}";
                lock (_lock) _storeNames[s.App] = name;
            }
            sales.Add(new SteamSale(s.App, name, s.Discount, s.Price, s.Was));
        }
        lock (_lock) _sales = sales;
        if (!cfg.NotifySales) return;
        var seen = LoadSeenSales();
        var current = sales.Select(s => $"{s.AppId}:{s.Discount}").ToHashSet();
        var fresh = sales.Where(s => !seen.Contains($"{s.AppId}:{s.Discount}")).ToList();
        if (seen.Count > 0 || fresh.Count <= 3) // the first run does not announce a whole wishlist at once
            foreach (var s in fresh.Take(3)) Notify("On sale", $"{s.Name}: -{s.Discount}% · {s.Price}", "ok");
        SaveSeenSales(current);
    }

    private static HashSet<string> LoadSeenSales()
    {
        try { return File.Exists(SalesPath) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(SalesPath)) ?? [] : []; }
        catch (Exception) { return []; }
    }

    private static void SaveSeenSales(HashSet<string> seen)
    {
        try { Directory.CreateDirectory(AppPaths.LocalDir); File.WriteAllText(SalesPath, JsonSerializer.Serialize(seen)); }
        catch (Exception) { }
    }

    /// <summary>The game being played (from the client, else from the web profile).</summary>
    private long? CurrentGame()
    {
        lock (_lock) return _running > 0 ? _running : _webGame is > 0 ? _webGame : null;
    }

    /// <summary>The game the widgets talk about: the one being played, else the last one played.</summary>
    private long? FocusApp()
    {
        if (CurrentGame() is { } g) return g;
        lock (_lock)
        {
            var last = _play.Where(p => _games.Any(g2 => g2.AppId == p.Key)).OrderByDescending(p => p.Value.LastPlayed).Select(p => p.Key).FirstOrDefault();
            if (last > 0) return last;
            return _recent.FirstOrDefault()?.AppId;
        }
    }

    private async Task<JsonElement?> GetJson(string url, CancellationToken ct, bool allowForbidden = false)
    {
        using var res = await Http.GetAsync(url, ct);
        if (res.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            if (url.Contains("key=", StringComparison.Ordinal) && !allowForbidden) throw new SteamApiException("Steam refused the API key: check it in Settings › Steam");
            return null; // a private profile, friends list or game details
        }
        if ((int)res.StatusCode == 429) throw new SteamApiException("Steam is limiting requests; trying again soon");
        if (!res.IsSuccessStatusCode) return null;
        var text = await res.Content.ReadAsStringAsync(ct);
        if (text.Length == 0 || text[0] is not ('{' or '[')) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static JsonElement? Resp(JsonElement? j) =>
        j is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object ? r : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>News bodies come as BBCode or HTML: plain text, short.</summary>
    internal static string CleanNews(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[a-zA-Z0-9*]+(=[^\]]*)?\]|<[^>]+>|\{STEAM_CLAN_IMAGE\}\S*", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 280 ? s[..277].TrimEnd() + "…" : s;
    }

    private void Notify(string title, string text, string level)
    {
        try { push.Emit("notify", JsonSerializer.SerializeToElement(new { title, text, level, seconds = 6 })); }
        catch (Exception ex) { log.LogDebug("steam notify: {Msg}", ex.Message); }
    }

    // ------------------------------------------------------------------ actions (steam:// links)

    private static readonly Dictionary<string, string> Statuses = new() { ["online"] = "online", ["away"] = "away", ["invisible"] = "invisible", ["offline"] = "offline" };
    private static readonly Dictionary<string, string> Opens = new()
    {
        ["friends"] = "steam://open/friends", ["bigpicture"] = "steam://open/bigpicture", ["downloads"] = "steam://open/downloads",
        ["library"] = "steam://open/games", ["screenshots"] = "steam://open/screenshots", ["store"] = "steam://store",
    };

    public string? Launch(long app)
    {
        lock (_lock) if (!_games.Any(g => g.AppId == app)) return "That game is not installed";
        return Open($"steam://rungameid/{app}");
    }

    public string? SetStatus(string status) =>
        Statuses.TryGetValue(status, out var s) ? Open($"steam://friends/status/{s}") : "unknown status";

    public string? OpenPage(string page) => Opens.TryGetValue(page, out var url) ? Open(url) : "unknown page";

    private static string? Open(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
            return null;
        }
        catch (Exception ex) { return "Could not open Steam: " + ex.Message; }
    }

    // ------------------------------------------------------------------ images (covers, avatars, achievement icons)

    public async Task<(byte[] Bytes, string Type)?> Cover(long app, bool portrait, CancellationToken ct)
    {
        string? root;
        lock (_lock) root = _root;
        if (root is not null && SteamLocal.CoverPath(root, app, portrait) is { } local)
        {
            try { return (await File.ReadAllBytesAsync(local, ct), "image/jpeg"); } catch (IOException) { }
        }
        var name = portrait ? "library_600x900.jpg" : "header.jpg";
        return await Remote($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{app}/{name}", ct)
            ?? await Remote($"https://cdn.cloudflare.steamstatic.com/steam/apps/{app}/{name}", ct);
    }

    /// <summary>Only Steam's own image hosts: the phone cannot make the agent fetch anything else.</summary>
    public static bool SteamImageHost(Uri u) =>
        u.Scheme == "https" && (u.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase));

    public async Task<(byte[] Bytes, string Type)?> Image(string? url, CancellationToken ct) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && SteamImageHost(u) ? await Remote(u.ToString(), ct) : null;

    private async Task<(byte[] Bytes, string Type)?> Remote(string url, CancellationToken ct)
    {
        if (_images.TryGetValue(url, out var hit) && Environment.TickCount64 - hit.At < 12 * 3_600_000) return (hit.Bytes, hit.Type);
        try
        {
            using var res = await Http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            var type = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            if (!type.StartsWith("image/", StringComparison.Ordinal)) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 2 * 1024 * 1024) return null;
            if (_images.Count > 400) _images.Clear();
            _images[url] = (bytes, type, Environment.TickCount64);
            return (bytes, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>Settings › Steam › Test: is the key accepted, and whose account is it.</summary>
    public async Task<object> Test(CancellationToken ct)
    {
        var cfg = config.Get().Steam;
        var root = SteamLocal.Root();
        var local = root is null ? null : SteamLocal.User(root);
        long id = long.TryParse(cfg.SteamId, out var fixedId) && fixedId > SteamLocal.SteamId64Base ? fixedId : local?.SteamId ?? 0;
        var installed = root is null ? 0 : SteamLocal.Games(root).Count;
        if (string.IsNullOrEmpty(cfg.ApiKey)) return new { ok = root is not null, client = root is not null, installed, user = local?.Name, message = "No API key: friends, achievements and wishlist stay off" };
        if (id == 0) return new { ok = false, client = root is not null, installed, user = (string?)null, message = "Steam account not found: sign in to Steam here or set your SteamID64" };
        try
        {
            var j = await GetJson($"{Api}/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(cfg.ApiKey)}&steamids={id}", ct);
            var p = Resp(j) is { } rr && rr.TryGetProperty("players", out var pl) ? pl.EnumerateArray().FirstOrDefault() : default;
            if (p is not { ValueKind: JsonValueKind.Object } player) return new { ok = false, client = root is not null, installed, user = (string?)null, message = "Steam did not find that account" };
            var visible = player.TryGetProperty("communityvisibilitystate", out var v) && v.GetInt32() == 3;
            return new
            {
                ok = true, client = root is not null, installed, user = Str(player, "personaname"),
                message = visible ? "Key accepted" : "Key accepted, but the profile is private: friends and achievements need a public profile and game details",
            };
        }
        catch (SteamApiException ex) { return new { ok = false, client = root is not null, installed, user = (string?)null, message = ex.Message }; }
        catch (Exception ex) { return new { ok = false, client = root is not null, installed, user = (string?)null, message = "Could not reach Steam: " + ex.Message }; }
    }
}
