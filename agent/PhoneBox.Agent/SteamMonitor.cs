using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record SteamNow(long AppId, string Name, long Since, int? Players);
public sealed record SteamAchievement(string Name, string? Description, string? Icon, long UnlockedAt);
public sealed record SteamAchievements(long AppId, string Game, int Done, int Total, IReadOnlyList<SteamAchievement> Recent, IReadOnlyList<SteamAchievement> Next);
/// <param name="Phase">downloading, installing, verifying, preparing, paused or queued</param>
public sealed record SteamDownload(long AppId, string Name, double Progress, long Done, long Total, double? BytesPerSec, string Phase);

/// <summary>An installed game with a newer build that Steam has not started on.</summary>
public sealed record SteamUpdate(long AppId, string Name, long SizeBytes);
/// <param name="State">0 offline, 1 online, 2 busy, 3 away, 4 snooze, 5 looking to trade, 6 looking to play</param>
public sealed record SteamFriend(string Id, string Name, string? Avatar, int State, string? Game, long? GameId, long? LastOff);
public sealed record SteamRecent(long AppId, string Name, long TwoWeeksMin, long TotalMin);
public sealed record SteamLibraryItem(long AppId, string Name, long SizeBytes, long LastPlayed, long PlaytimeMin, bool Downloading,
    bool UpdateAvailable = false);

/// <param name="Percent">Share of all players who have it (null when Steam does not say).</param>
/// <param name="Hidden">A secret achievement: the description stays hidden until it is unlocked.</param>
public sealed record SteamAchievementItem(string Name, string? Description, string? Icon, bool Achieved, long UnlockedAt, double? Percent, bool Hidden);

/// <summary>One game in the library sheet: what is on this PC, play time and, with the API key, every achievement.</summary>
/// <param name="AchievementsNote">Why the list is empty: no key, a private profile, or a game without achievements.</param>
public sealed record SteamGameDetails(long AppId, string Name, bool Installed, long SizeBytes, bool Downloading, bool Playing,
    long LastPlayed, long PlaytimeMin, long TwoWeeksMin, int? Players, bool HasKey, string? AchievementsNote,
    int Done, int Total, IReadOnlyList<SteamAchievementItem> Achievements, bool UpdateAvailable = false);

internal sealed record SteamSchemaEntry(string Name, string? Description, string? Icon, string? Gray, bool Hidden);

/// <param name="Status">ok, not-installed (no Steam client found) or off (no Steam widget on a page)</param>
public sealed record SteamSnapshot(string Status, string? Error, bool HasKey, bool ClientRunning, string? User, string? Avatar, int? State,
    SteamNow? Playing, SteamAchievements? Achievements, IReadOnlyList<SteamDownload> Downloads, IReadOnlyList<SteamFriend> Friends,
    IReadOnlyList<SteamRecent> Recent, long Updated,
    IReadOnlyList<SteamUpdate>? Updates = null);

/// <summary>
/// Steam on this computer and on the web. The client's own files give the installed games, downloads and play time
/// (no account needed); the Steam Web API, with the user's key, gives friends, achievements and the recent games.
/// Notifications go to the phone like a webhook's: a finished download, a new achievement, a friend starting a game. Nothing runs unless a page has a Steam widget.
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
    private readonly SteamContentLog _contentLog = new();
    // the manifest's downloaded bytes and when they last changed, and the bytes last shown (the bar never goes back)
    private readonly Dictionary<long, (long Bytes, long At)> _acfDl = [];
    private readonly Dictionary<long, (long Total, long Done)> _shown = [];
    private List<SteamDownload> _downloads = [];
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
    // read by the loop and by the game sheet's requests
    private readonly ConcurrentDictionary<string, Dictionary<string, SteamSchemaEntry>> _schema = new();
    private readonly ConcurrentDictionary<long, (Dictionary<string, double> Pct, long At)> _globalPct = new();
    private readonly ConcurrentDictionary<long, (SteamGameDetails Details, long At)> _details = new();
    private readonly ConcurrentDictionary<long, (int? Count, long At)> _playersAt = new();
    private readonly Dictionary<long, int?> _players = [];
    private readonly Dictionary<long, string> _storeNames = [];
    private long _updated;

    private long _nextPlay, _nextSummary, _nextFriends, _nextRecent, _nextAch, _nextPlayers;
    private string? _lastKeyId; // key + id the web data belongs to: a change starts over

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
            var downloads = _downloads;
            return new SteamSnapshot("ok", _error, !string.IsNullOrEmpty(cfg.Steam.ApiKey), _clientRunning, _user, _avatar, _state,
                playing, _ach, downloads,
                _friends.Where(f => f.State > 0).OrderByDescending(f => f.Game is not null).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Concat(_friends.Where(f => f.State == 0).OrderByDescending(f => f.LastOff ?? 0)).ToList(),
                _recent, _updated,
                _games.Where(g => g.UpdateAvailable).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new SteamUpdate(g.AppId, g.Name, g.SizeBytes)).ToList());
        }
    }

    private static SteamSnapshot Empty(string status) =>
        new(status, null, false, false, null, null, null, null, null, [], [], [], 0);

    public List<SteamLibraryItem> Library()
    {
        lock (_lock)
            return _games.Select(g =>
            {
                var p = _play.GetValueOrDefault(g.AppId);
                return new SteamLibraryItem(g.AppId, g.Name, g.SizeBytes, p?.LastPlayed ?? 0, p?.PlaytimeMin ?? 0, g.Downloading, g.UpdateAvailable);
            }).OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
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

    /// <summary>
    /// One game's download as the Steam window shows it. The manifest's byte count is only written now and then, so
    /// between writes the bar moves at Steam's own download rate (from content_log.txt) and never goes backwards.
    /// </summary>
    private SteamDownload Download(SteamGame g, long now)
    {
        var log = _contentLog.Get(g.AppId);
        var step = log?.Step ?? "";
        var state = log?.State ?? "";
        long dlTotal = g.BytesToDownload > 0 ? g.BytesToDownload : log?.DownloadTotal ?? 0;
        long stTotal = g.BytesToStage > 0 ? g.BytesToStage : log?.StageTotal ?? 0;
        var dlLeft = dlTotal > 0 && g.BytesDownloaded < dlTotal;
        var stageLeft = stTotal > 0 && g.BytesStaged < stTotal;
        var phase = state.Contains("Update Paused") ? "paused"
            : step.Contains("Verifying") ? "verifying"
            : step.Contains("Committing") ? "installing"
            : step.Contains("Preallocating") || step.Contains("Reconfiguring") ? "preparing"
            : step.Contains("Downloading") || (step.Length == 0 && dlLeft) ? "downloading"
            : stageLeft ? "installing"
            : "queued";
        // waiting its turn in Steam's queue
        if (state.Length > 0 && !state.Contains("Update Running") && phase != "paused") phase = "queued";

        if (!_acfDl.TryGetValue(g.AppId, out var acf) || acf.Bytes != g.BytesDownloaded) _acfDl[g.AppId] = acf = (g.BytesDownloaded, now);
        double? speed = null;
        long done = 0, total = 0;
        if (phase == "downloading" && dlTotal > 0)
        {
            var logRate = _contentLog.Rate > 0 && (DateTime.Now - _contentLog.RateAt).TotalSeconds < 75 ? _contentLog.Rate : (double?)null;
            speed = logRate ?? (_dlSpeed.TryGetValue(g.AppId, out var sp) ? sp : null);
            var est = acf.Bytes + (long)((speed ?? 0) * Math.Max(0, now - acf.At) / 1000);
            total = dlTotal;
            done = Math.Min(Math.Max(est, g.BytesDownloaded), dlTotal);
            if (_shown.TryGetValue(g.AppId, out var prev) && prev.Total == total) done = Math.Max(done, prev.Done);
            _shown[g.AppId] = (total, done);
        }
        else if (phase == "installing" && stTotal > 0) (done, total) = (Math.Min(g.BytesStaged, stTotal), stTotal);
        else if (phase is "paused" or "queued" && dlTotal > 0) (done, total) = (Math.Min(g.BytesDownloaded, dlTotal), dlTotal);
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
        _contentLog.Read(root); // only this loop touches it
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
            foreach (var id in _acfDl.Keys.Where(k => !downloading.Contains(k)).ToList()) { _acfDl.Remove(id); _shown.Remove(id); }
            _games = games;
            _downloads = games.Where(g => g.Downloading).Select(g => Download(g, now))
                .OrderByDescending(d => d.Phase is "downloading" or "verifying" or "installing" or "preparing").ToList();
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

        // the player count needs no key
        if (focus is { } fa && (now >= _nextPlayers || !_players.ContainsKey(fa)))
        {
            _nextPlayers = now + 300_000;
            var j = await GetJson($"{Api}/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={fa}", ct);
            var count = Resp(j) is { } r && r.TryGetProperty("player_count", out var pc) ? pc.GetInt32() : (int?)null;
            lock (_lock) _players[fa] = count;
        }
        // achievements from the client's own files: no key, and a private profile still sees its own
        var localAch = focus is { } la && ReadLocalAchievements(la, cfg);

        if (string.IsNullOrEmpty(cfg.ApiKey) || id == 0)
        {
            lock (_lock)
            {
                _error = string.IsNullOrEmpty(cfg.ApiKey) ? null : "Steam account not found: sign in to Steam on this PC or set your SteamID in Settings › Steam";
                _friends = []; _recent = [];
                if (!localAch) _ach = null; _webGame = null; _state = null; _avatar = null;
            }
            return;
        }
        var key = Uri.EscapeDataString(cfg.ApiKey);
        if (_lastKeyId != cfg.ApiKey + "|" + id)
        {
            _lastKeyId = cfg.ApiKey + "|" + id;
            _nextSummary = _nextFriends = _nextRecent = _nextAch = 0;
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
            if (!localAch && focus is { } aa && (now >= _nextAch || aa != _achApp))
            {
                _nextAch = now + (playingNow ? 60_000 : 900_000);
                await ReadAchievements(key, id, aa, cfg, ct);
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
        var schema = await Schema(key, app, cfg, ct);
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
            var meta = schema.TryGetValue(api, out var m) ? m : new SteamSchemaEntry(api, null, null, null, false);
            if (a.TryGetProperty("achieved", out var ach) && ach.GetInt32() == 1)
            {
                done.Add((api, new(meta.Name, meta.Description, meta.Icon, (a.TryGetProperty("unlocktime", out var ut) ? ut.GetInt64() : 0) * 1000)));
            }
            else todo.Add(new(meta.Name, meta.Hidden ? null : meta.Description, meta.Gray ?? meta.Icon, 0));
        }
        ApplyAchievements(app, done, todo, cfg);
    }

    /// <summary>Publishes a game's achievements to the widget and announces the ones unlocked since the last reading.</summary>
    private void ApplyAchievements(long app, List<(string Api, SteamAchievement A)> done, List<SteamAchievement> todo, SteamConfig cfg)
    {
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

    private string? _localAchStamp;

    /// <summary>
    /// An achievement icon's address. Steam moved them to community_assets; the old steamcommunity/public/images
    /// links (which the Web API still returns) answer 404, so a known old link is rewritten too.
    /// </summary>
    internal static string? IconUrl(long app, string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        if (icon.StartsWith("https://", StringComparison.Ordinal))
        {
            if (!icon.Contains("/images/apps/", StringComparison.Ordinal)) return icon;
            icon = icon[(icon.LastIndexOf('/') + 1)..];
        }
        return $"https://shared.akamai.steamstatic.com/community_assets/images/apps/{app}/{icon}";
    }

    private List<SteamLocalAchievement>? LocalAchievements(long app, SteamConfig cfg)
    {
        string? root; long id;
        lock (_lock) { root = _root; id = _steamId; }
        return root is null || id == 0 ? null : SteamLocal.Achievements(root, id, app, cfg.Language);
    }

    /// <summary>The widget's achievements from appcache/stats; false when the client has none for this game.</summary>
    private bool ReadLocalAchievements(long app, SteamConfig cfg)
    {
        string? root; long id;
        lock (_lock) { root = _root; id = _steamId; }
        if (root is null || id == 0) return false;
        var file = new FileInfo(Path.Combine(root, "appcache", "stats", $"UserGameStats_{id - SteamLocal.SteamId64Base}_{app}.bin"));
        if (!file.Exists) return false;
        var stamp = $"{app}|{cfg.Language}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
        if (stamp == _localAchStamp) return true; // nothing changed since the last reading
        var list = SteamLocal.Achievements(root, id, app, cfg.Language);
        if (list is null) return false;
        _localAchStamp = stamp;
        if (list.Count == 0) { lock (_lock) { _ach = null; _achApp = app; _achDone = null; } return true; }
        var done = list.Where(a => a.Achieved).Select(a => (a.ApiName, new SteamAchievement(a.Name, a.Description, IconUrl(app, a.Icon), a.UnlockedAt * 1000))).ToList();
        var todo = list.Where(a => !a.Achieved).Select(a => new SteamAchievement(a.Name, a.Hidden ? null : a.Description, IconUrl(app, a.IconGray ?? a.Icon), 0)).ToList();
        ApplyAchievements(app, done, todo, cfg);
        return true;
    }

    /// <summary>The game's achievement names, texts and icons in the chosen language (kept for the agent's life).</summary>
    private async Task<Dictionary<string, SteamSchemaEntry>> Schema(string key, long app, SteamConfig cfg, CancellationToken ct)
    {
        var schemaKey = $"{app}|{cfg.Language}";
        if (_schema.TryGetValue(schemaKey, out var known)) return known;
        var s = await GetJson($"{Api}/ISteamUserStats/GetSchemaForGame/v2/?key={key}&appid={app}&l={Uri.EscapeDataString(cfg.Language)}", ct, allowForbidden: true);
        var map = new Dictionary<string, SteamSchemaEntry>();
        if (s?.TryGetProperty("game", out var g) == true && g.TryGetProperty("availableGameStats", out var st) && st.TryGetProperty("achievements", out var achs))
            foreach (var a in achs.EnumerateArray())
                if (Str(a, "name") is { } n)
                    map[n] = new(Str(a, "displayName") ?? n, Str(a, "description"), IconUrl(app, Str(a, "icon")), IconUrl(app, Str(a, "icongray")),
                        a.TryGetProperty("hidden", out var hd) && hd.ValueKind == JsonValueKind.Number && hd.GetInt32() == 1);
        if (s is not null) _schema[schemaKey] = map; // a failed request is tried again next time
        return map;
    }

    /// <summary>How rare each achievement is (no key needed), refreshed every 6 hours.</summary>
    private async Task<Dictionary<string, double>> GlobalPercents(long app, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (_globalPct.TryGetValue(app, out var c) && now - c.At < 6 * 3_600_000) return c.Pct;
        var j = await GetJson($"{Api}/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={app}", ct, allowForbidden: true);
        var map = new Dictionary<string, double>();
        if (j?.TryGetProperty("achievementpercentages", out var ap) == true && ap.TryGetProperty("achievements", out var list))
            foreach (var a in list.EnumerateArray())
                if (Str(a, "name") is { } n && a.TryGetProperty("percent", out var p))
                    map[n] = p.ValueKind == JsonValueKind.Number ? p.GetDouble() : double.TryParse(p.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        _globalPct[app] = (map, now);
        return map;
    }

    /// <summary>The library sheet for one game. Answers from a 60-second cache so tapping around costs Steam nothing.</summary>
    public async Task<SteamGameDetails> GameDetails(long app, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (_details.TryGetValue(app, out var hit) && now - hit.At < 60_000) return hit.Details;
        var cfg = config.Get().Steam;
        SteamGame? game; SteamPlay? play; SteamRecent? recent; long id; bool playing;
        lock (_lock)
        {
            game = _games.FirstOrDefault(g => g.AppId == app);
            play = _play.GetValueOrDefault(app);
            recent = _recent.FirstOrDefault(r => r.AppId == app);
            id = _steamId;
            playing = _running == app || (_running == 0 && _webGame == app);
        }
        int? players;
        if (_playersAt.TryGetValue(app, out var pc) && now - pc.At < 300_000) players = pc.Count;
        else
        {
            var pj = await GetJson($"{Api}/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={app}", ct, allowForbidden: true);
            players = Resp(pj) is { } r && r.TryGetProperty("player_count", out var pcount) ? pcount.GetInt32() : null;
            _playersAt[app] = (players, now);
        }

        var hasKey = !string.IsNullOrEmpty(cfg.ApiKey);
        string? note = null;
        var items = new List<SteamAchievementItem>();
        var local = LocalAchievements(app, cfg);
        if (local is not null)
        {
            Dictionary<string, double> pct = [];
            try { pct = await GlobalPercents(app, ct); } catch (SteamApiException) { }
            if (local.Count == 0) note = "This game has no achievements";
            items.AddRange(local.Select(a => new SteamAchievementItem(a.Name, a.Achieved || !a.Hidden ? a.Description : null,
                IconUrl(app, a.Achieved ? a.Icon : a.IconGray ?? a.Icon), a.Achieved, a.UnlockedAt * 1000,
                pct.TryGetValue(a.ApiName, out var p) ? Math.Round(p, 1) : null, a.Hidden && !a.Achieved)));
        }
        else if (!hasKey) note = "Open this game once on this PC: Steam then keeps its achievements here";
        else if (id == 0) note = "Steam account not found: sign in to Steam on this PC";
        else
        {
            try
            {
                var key = Uri.EscapeDataString(cfg.ApiKey!);
                var schema = await Schema(key, app, cfg, ct);
                if (schema.Count == 0) note = "This game has no achievements";
                else
                {
                    var j = await GetJson($"{Api}/ISteamUserStats/GetPlayerAchievements/v1/?key={key}&steamid={id}&appid={app}&l={Uri.EscapeDataString(cfg.Language)}", ct, allowForbidden: true);
                    var pct = await GlobalPercents(app, ct);
                    if (j?.TryGetProperty("playerstats", out var ps) == true && ps.TryGetProperty("achievements", out var list))
                    {
                        foreach (var a in list.EnumerateArray())
                        {
                            var api = Str(a, "apiname") ?? "";
                            var m = schema.TryGetValue(api, out var e) ? e : new SteamSchemaEntry(api, null, null, null, false);
                            var got = a.TryGetProperty("achieved", out var ach) && ach.GetInt32() == 1;
                            items.Add(new SteamAchievementItem(m.Name, got || !m.Hidden ? m.Description : null, got ? m.Icon : m.Gray ?? m.Icon, got,
                                got && a.TryGetProperty("unlocktime", out var ut) ? ut.GetInt64() * 1000 : 0,
                                pct.TryGetValue(api, out var p) ? Math.Round(p, 1) : null, m.Hidden && !got));
                        }
                    }
                    else note = game is null && play is null && recent is null
                        ? "You do not own this game"
                        : "Steam did not share your achievements: set Game details to Public in your Steam privacy settings";
                }
            }
            catch (SteamApiException ex) { note = ex.Message; }
        }
        // unlocked first (newest on top), then the rest from most to least common
        var ordered = items.Where(i => i.Achieved).OrderByDescending(i => i.UnlockedAt)
            .Concat(items.Where(i => !i.Achieved).OrderByDescending(i => i.Percent ?? -1)).ToList();
        var details = new SteamGameDetails(app, NameOf(app), game is not null, game?.SizeBytes ?? 0, game?.Downloading ?? false, playing,
            play?.LastPlayed ?? 0, Math.Max(play?.PlaytimeMin ?? 0, recent?.TotalMin ?? 0), recent?.TwoWeeksMin ?? 0, players, hasKey, note,
            items.Count(i => i.Achieved), items.Count, ordered, game?.UpdateAvailable ?? false);
        _details[app] = (details, now);
        return details;
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

    /// <summary>
    /// Starts a pending update now. Steam has no "update" link, so this asks it to verify the game's files, which
    /// brings the game to the newest build (Steam first re-reads the installed files, so big games take a while).
    /// </summary>
    public string? Update(long app)
    {
        lock (_lock)
        {
            var g = _games.FirstOrDefault(x => x.AppId == app);
            if (g is null) return "That game is not installed";
            if (g.Downloading) return "Steam is already updating it";
        }
        return Open($"steam://validate/{app}");
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

    public async Task<(byte[] Bytes, string Type)?> Cover(long app, string kind, CancellationToken ct)
    {
        string? root;
        lock (_lock) root = _root;
        if (root is not null && SteamLocal.CoverPath(root, app, kind) is { } local)
        {
            try { return (await File.ReadAllBytesAsync(local, ct), "image/jpeg"); } catch (IOException) { }
        }
        var name = kind switch { "header" => "header.jpg", "hero" => "library_hero.jpg", _ => "library_600x900.jpg" };
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
