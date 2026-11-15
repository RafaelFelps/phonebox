using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record ChessPlayer(string Name, int? Rating, double? Accuracy);

public sealed record ChessGameInfo(string Id, string Site, string Url, ChessPlayer White, ChessPlayer Black, string Result,
    string Me, string Outcome, string Speed, long End, string? Opening);

public sealed record ChessPly(string San, string Uci, string Fen, double? Eval, int? Mate, string? Best, string? BestSan,
    string? Class);

public sealed record ChessReview(ChessGameInfo Game, string StartFen, IReadOnlyList<ChessPly> Moves, string Status,
    double Progress, string? Source, double? WhiteAccuracy, double? BlackAccuracy, string? Error);

/// <summary>
/// The Chess widget: your recent chess.com / Lichess games and a move-by-move review of a finished game, with an
/// evaluation bar and the move quality (best, inaccuracy, mistake, blunder) from Lichess's own analysis or, when a
/// game has none, from Stockfish on this PC. Only finished games: the widget never evaluates a game in progress.
/// Uses the sites' public APIs (no account or key).
/// </summary>
public sealed partial class ChessMonitor(ConfigStore config, ILogger<ChessMonitor> log)
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly ConcurrentDictionary<string, (object Value, long At)> _lists = new();
    private readonly ConcurrentDictionary<string, ChessReview> _reviews = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PhoneBox/{AppPaths.Version} (desk panel chess widget)");
        return http;
    }

    public static bool ValidUser(string? user) => user is { Length: >= 2 and <= 30 } && UserName().IsMatch(user);
    public static bool ValidSite(string? site) => site is "lichess" or "chesscom";

    // ------------------------------------------------------------------ recent games and ratings

    public async Task<object> Games(string site, string user, CancellationToken ct)
    {
        var key = site + ":" + user.ToLowerInvariant();
        if (_lists.TryGetValue(key, out var hit) && Environment.TickCount64 - hit.At < 120_000) return hit.Value;
        object value;
        try
        {
            value = site == "lichess" ? await LichessGames(user, ct) : await ChessComGames(user, ct);
            _lists[key] = (value, Environment.TickCount64);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (hit.Value is not null) return hit.Value;
            value = new { error = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } ? $"No player \"{user}\" on {SiteName(site)}" : $"{SiteName(site)} did not answer" };
        }
        return value;
    }

    private static string SiteName(string site) => site == "lichess" ? "Lichess" : "chess.com";

    private async Task<object> LichessGames(string user, CancellationToken ct)
    {
        var games = new List<ChessGameInfo>();
        using (var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://lichess.org/api/games/user/{Uri.EscapeDataString(user)}?max=15&moves=true&evals=true&accuracy=true&opening=true&finished=true&perfType=ultraBullet,bullet,blitz,rapid,classical,correspondence"))
        {
            request.Headers.Accept.ParseAdd("application/x-ndjson");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var doc = JsonDocument.Parse(line);
                var g = doc.RootElement;
                if (Str(g, "variant") is not ("standard" or null)) continue;
                var info = LichessInfo(g, user);
                games.Add(info);
                if (!_reviews.ContainsKey(info.Id)) _lichessRaw[info.Id] = (g.Clone(), user);
            }
        }
        var ratings = new Dictionary<string, int>();
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://lichess.org/api/user/{Uri.EscapeDataString(user)}", ct));
            if (doc.RootElement.TryGetProperty("perfs", out var perfs))
                foreach (var name in new[] { "bullet", "blitz", "rapid", "classical" })
                    if (perfs.TryGetProperty(name, out var p) && p.TryGetProperty("games", out var n) && n.GetInt32() > 0 && p.TryGetProperty("rating", out var r))
                        ratings[name] = r.GetInt32();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return new { site = "lichess", user, ratings, games };
    }

    private readonly ConcurrentDictionary<string, (JsonElement Game, string User)> _lichessRaw = new();

    private static ChessGameInfo LichessInfo(JsonElement g, string user)
    {
        ChessPlayer Player(string color)
        {
            var p = g.GetProperty("players").GetProperty(color);
            var name = p.TryGetProperty("user", out var u) ? Str(u, "name") ?? "?" : p.TryGetProperty("aiLevel", out var ai) ? $"Stockfish level {ai.GetInt32()}" : "Anonymous";
            int? rating = p.TryGetProperty("rating", out var r) ? r.GetInt32() : null;
            double? accuracy = p.TryGetProperty("analysis", out var a) && a.TryGetProperty("accuracy", out var acc) ? acc.GetDouble() : null;
            return new(name, rating, accuracy);
        }
        var white = Player("white");
        var black = Player("black");
        var me = string.Equals(white.Name, user, StringComparison.OrdinalIgnoreCase) ? "white" : "black";
        var winner = Str(g, "winner");
        var result = winner == "white" ? "1-0" : winner == "black" ? "0-1" : "½-½";
        var outcome = winner is null ? "draw" : winner == me ? "win" : "loss";
        var id = Str(g, "id")!;
        var opening = g.TryGetProperty("opening", out var o) ? Str(o, "name") : null;
        var end = g.TryGetProperty("lastMoveAt", out var l) ? l.GetInt64() : g.TryGetProperty("createdAt", out var c) ? c.GetInt64() : 0;
        return new(id, "lichess", $"https://lichess.org/{id}", white, black, result, me, outcome, Str(g, "speed") ?? "", end, opening);
    }

    private async Task<object> ChessComGames(string user, CancellationToken ct)
    {
        var lower = user.ToLowerInvariant();
        using var archives = JsonDocument.Parse(await Http.GetStringAsync($"https://api.chess.com/pub/player/{Uri.EscapeDataString(lower)}/games/archives", ct));
        var months = archives.RootElement.GetProperty("archives").EnumerateArray().Select(a => a.GetString()!).Reverse().Take(2).ToList();
        var games = new List<ChessGameInfo>();
        foreach (var month in months)
        {
            if (games.Count >= 15) break;
            if (!month.StartsWith("https://api.chess.com/pub/player/", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(month, ct));
            foreach (var g in doc.RootElement.GetProperty("games").EnumerateArray().Reverse())
            {
                if (Str(g, "rules") != "chess" || Str(g, "pgn") is not { } pgn) continue;
                var info = ChessComInfo(g, lower);
                games.Add(info);
                _chessComPgn[info.Id] = (pgn, info);
                if (games.Count >= 15) break;
            }
        }
        var ratings = new Dictionary<string, int>();
        try
        {
            using var stats = JsonDocument.Parse(await Http.GetStringAsync($"https://api.chess.com/pub/player/{Uri.EscapeDataString(lower)}/stats", ct));
            foreach (var (key, name) in new[] { ("chess_bullet", "bullet"), ("chess_blitz", "blitz"), ("chess_rapid", "rapid"), ("chess_daily", "daily") })
                if (stats.RootElement.TryGetProperty(key, out var s) && s.TryGetProperty("last", out var last) && last.TryGetProperty("rating", out var r))
                    ratings[name] = r.GetInt32();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return new { site = "chesscom", user, ratings, games };
    }

    private readonly ConcurrentDictionary<string, (string Pgn, ChessGameInfo Info)> _chessComPgn = new();

    private static ChessGameInfo ChessComInfo(JsonElement g, string user)
    {
        double? Acc(string color) => g.TryGetProperty("accuracies", out var a) && a.TryGetProperty(color, out var v) ? v.GetDouble() : null;
        ChessPlayer Player(string color)
        {
            var p = g.GetProperty(color);
            return new(Str(p, "username") ?? "?", p.TryGetProperty("rating", out var r) ? r.GetInt32() : null, Acc(color));
        }
        var white = Player("white");
        var black = Player("black");
        var me = string.Equals(white.Name, user, StringComparison.OrdinalIgnoreCase) ? "white" : "black";
        var whiteResult = Str(g.GetProperty("white"), "result");
        var blackResult = Str(g.GetProperty("black"), "result");
        var result = whiteResult == "win" ? "1-0" : blackResult == "win" ? "0-1" : "½-½";
        var mine = me == "white" ? whiteResult : blackResult;
        var outcome = mine == "win" ? "win" : result == "½-½" ? "draw" : "loss";
        var url = Str(g, "url") ?? "";
        var id = "cc" + url[(url.LastIndexOf('/') + 1)..];
        var eco = Str(g, "eco");
        var opening = eco is not null && eco.Contains("/openings/") ? Uri.UnescapeDataString(eco[(eco.LastIndexOf('/') + 1)..]).Replace('-', ' ') : null;
        return new(id, "chesscom", url, white, black, result, me, outcome, Str(g, "time_class") ?? "",
            (g.TryGetProperty("end_time", out var e) ? e.GetInt64() : 0) * 1000, opening);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ------------------------------------------------------------------ one game, move by move

    /// <summary>The review of a game from the last list; analysis starts in the background on the first request.</summary>
    public ChessReview? Review(string id)
    {
        if (_reviews.TryGetValue(id, out var review))
        {
            return review;
        }
        ChessReview? built = null;
        try
        {
            if (_lichessRaw.TryGetValue(id, out var raw)) built = FromLichess(raw.Game, raw.User);
            else if (_chessComPgn.TryGetValue(id, out var cc)) built = FromPgn(cc.Pgn, cc.Info);
        }
        catch (Exception ex) { log.LogInformation("chess game {Id}: {Error}", id, ex.Message); return null; }
        if (built is null) return null;
        if (!_reviews.TryAdd(id, built)) return _reviews[id];
        if (_reviews.Count > 40)
            foreach (var old in _reviews.Keys.Where(k => k != id).Take(_reviews.Count - 40)) _reviews.TryRemove(old, out _);
        if (built.Status == "pending") _ = Task.Run(() => Analyse(id));
        return built;
    }

    private static ChessReview FromLichess(JsonElement g, string user)
    {
        var info = LichessInfo(g, user);
        var sans = (Str(g, "moves") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var analysis = g.TryGetProperty("analysis", out var a) ? a.EnumerateArray().ToList() : [];
        var plies = Replay(sans, out var start);
        if (analysis.Count >= plies.Count && plies.Count > 0)
        {
            // Lichess: analysis[i] is the evaluation after move i, with a judgement and the better move when it was a mistake
            for (var i = 0; i < plies.Count; i++)
            {
                var e = analysis[i];
                double? eval = e.TryGetProperty("eval", out var ev) ? ev.GetInt32() / 100.0 : null;
                int? mate = e.TryGetProperty("mate", out var m) ? m.GetInt32() : null;
                var judgement = e.TryGetProperty("judgment", out var j) ? Str(j, "name")?.ToLowerInvariant() : null;
                var best = Str(e, "best");
                string? bestSan = null;
                if (best is not null)
                    try
                    {
                        var before = ChessBoard.FromFen(i == 0 ? start : plies[i - 1].Fen);
                        if (before.ParseUci(best) is { } bm) bestSan = before.San(bm);
                    }
                    catch (FormatException) { }
                plies[i] = plies[i] with { Eval = eval, Mate = mate, Best = best, BestSan = bestSan, Class = judgement ?? "good" };
            }
            return new(info, start, plies, "done", 1, "Lichess analysis", info.White.Accuracy, info.Black.Accuracy, null);
        }
        return new(info, start, plies, "pending", 0, null, info.White.Accuracy, info.Black.Accuracy, null);
    }

    private static ChessReview FromPgn(string pgn, ChessGameInfo info)
    {
        var start = PgnFen().Match(pgn) is { Success: true } fen ? fen.Groups[1].Value : ChessBoard.StartFen;
        var plies = Replay(ChessBoard.PgnMoves(pgn), out _, start);
        return new(info, start, plies, "pending", 0, null, info.White.Accuracy, info.Black.Accuracy, null);
    }

    private static List<ChessPly> Replay(List<string> sans, out string start, string? from = null)
    {
        start = from ?? ChessBoard.StartFen;
        var board = ChessBoard.FromFen(start);
        var plies = new List<ChessPly>();
        foreach (var san in sans)
        {
            if (board.ParseSan(san) is not { } move) break;
            var clean = board.San(move);
            board.Play(move);
            plies.Add(new(clean, move.Uci, board.Fen(), null, null, null, null, null));
        }
        return plies;
    }

    // ------------------------------------------------------------------ Stockfish

    /// <summary>Stockfish: Settings › General › Chess, or found on PATH / in winget's packages.</summary>
    public string? Engine()
    {
        var configured = config.Get().Chess.StockfishPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var exe = OperatingSystem.IsWindows() ? "stockfish.exe" : "stockfish";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe); } catch (ArgumentException) { }
        }
        if (OperatingSystem.IsWindows())
        {
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            try
            {
                foreach (var pkg in Directory.EnumerateDirectories(winget, "Stockfish*"))
                    foreach (var file in Directory.EnumerateFiles(pkg, "stockfish*.exe", SearchOption.AllDirectories))
                        return file;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var path in new[] { "/opt/homebrew/bin/stockfish", "/usr/local/bin/stockfish", "/usr/games/stockfish", "/usr/bin/stockfish" })
            if (File.Exists(path)) return path;
        return null;
    }

    private void Update(string id, Func<ChessReview, ChessReview> change)
    {
        while (_reviews.TryGetValue(id, out var current) && !_reviews.TryUpdate(id, change(current), current)) { }
    }

    private async Task Analyse(string id)
    {
        var engine = Engine();
        if (engine is null)
        {
            Update(id, r => r with { Status = "no-engine", Error = "Install Stockfish to analyse this game (Settings › General › Chess)" });
            return;
        }
        await _engineGate.WaitAsync();
        Process? sf = null;
        try
        {
            if (!_reviews.TryGetValue(id, out var review)) return;
            Update(id, r => r with { Status = "analysing" });
            sf = Process.Start(new ProcessStartInfo(engine)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd,
            }) ?? throw new IOException("Stockfish did not start");
            sf.ErrorDataReceived += (_, _) => { };
            sf.BeginErrorReadLine();
            await Uci(sf, "uci", "uciok");
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            await sf.StandardInput.WriteLineAsync($"setoption name Threads value {threads}");
            await sf.StandardInput.WriteLineAsync("setoption name Hash value 128");
            await Uci(sf, "isready", "readyok");
            var depth = Math.Clamp(config.Get().Chess.Depth, 8, 24);
            var fens = new[] { review.StartFen }.Concat(review.Moves.Select(m => m.Fen)).ToList();
            var evals = new List<(double? Cp, int? Mate, string? Best)>();
            for (var i = 0; i < fens.Count; i++)
            {
                evals.Add(await Evaluate(sf, fens[i], depth));
                var progress = (double)(i + 1) / fens.Count;
                Update(id, r => r with { Progress = progress });
            }
            var moves = review.Moves.ToList();
            var firstWhite = ChessBoard.FromFen(review.StartFen).WhiteToMove;
            for (var i = 0; i < moves.Count; i++)
            {
                var whiteMoved = (i % 2 == 0) == firstWhite;
                var (beforeCp, beforeMate, best) = evals[i];
                var (afterCp, afterMate, _) = evals[i + 1];
                var winBefore = Win(beforeCp, beforeMate, whiteMoved);
                var winAfter = Win(afterCp, afterMate, whiteMoved);
                var loss = Math.Max(0, winBefore - winAfter);
                var cls = best == moves[i].Uci ? "best" : loss >= 15 ? "blunder" : loss >= 10 ? "mistake" : loss >= 5 ? "inaccuracy" : "good";
                string? bestSan = null;
                if (best is not null && cls != "best")
                    try
                    {
                        var board = ChessBoard.FromFen(fens[i]);
                        if (board.ParseUci(best) is { } bm) bestSan = board.San(bm);
                    }
                    catch (FormatException) { }
                moves[i] = moves[i] with
                {
                    Eval = afterCp, Mate = afterMate, Class = cls,
                    Best = cls is "best" or "good" ? null : best, BestSan = cls is "best" or "good" ? null : bestSan,
                };
                _accuracy[(id, i)] = Math.Clamp(103.1668 * Math.Exp(-0.04354 * loss) - 3.1669, 0, 100);
            }
            double? Accuracy(bool white)
            {
                var first = ChessBoard.FromFen(review.StartFen).WhiteToMove;
                var list = Enumerable.Range(0, moves.Count).Where(i => (i % 2 == 0) == (first == white)).Select(i => _accuracy[(id, i)]).ToList();
                return list.Count == 0 ? null : Math.Round(list.Average(), 1);
            }
            var whiteAcc = review.WhiteAccuracy ?? Accuracy(true);
            var blackAcc = review.BlackAccuracy ?? Accuracy(false);
            Update(id, r => r with { Moves = moves, Status = "done", Progress = 1, Source = $"Stockfish, depth {depth}", WhiteAccuracy = whiteAcc, BlackAccuracy = blackAcc });
        }
        catch (Exception ex)
        {
            log.LogInformation("chess analysis {Id}: {Error}", id, ex.Message);
            Update(id, r => r with { Status = "error", Error = "Stockfish could not analyse this game: " + ex.Message });
        }
        finally
        {
            try { if (sf is { HasExited: false }) { sf.StandardInput.WriteLine("quit"); if (!sf.WaitForExit(1000)) sf.Kill(); } } catch (Exception) { }
            sf?.Dispose();
            foreach (var key in _accuracy.Keys.Where(k => k.Id == id)) _accuracy.TryRemove(key, out _);
            _engineGate.Release();
        }
    }

    private readonly ConcurrentDictionary<(string Id, int Ply), double> _accuracy = new();

    /// <summary>Lichess's win percentage for the side that moved (0–100).</summary>
    private static double Win(double? cp, int? mate, bool white)
    {
        double w;
        if (mate is { } m) w = m > 0 ? 100 : 0;
        else w = 50 + 50 * (2 / (1 + Math.Exp(-0.00368208 * Math.Clamp((cp ?? 0) * 100, -1000, 1000))) - 1);
        return white ? w : 100 - w;
    }

    private static async Task Uci(Process sf, string command, string until)
    {
        await sf.StandardInput.WriteLineAsync(command);
        await sf.StandardInput.FlushAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await sf.StandardOutput.ReadLineAsync(cts.Token) is { } line)
            if (line.StartsWith(until, StringComparison.Ordinal)) return;
        throw new IOException("Stockfish stopped");
    }

    /// <summary>The evaluation of a position from White's side (pawns), or mate in N (+ white mates), and the best move.</summary>
    private static async Task<(double? Cp, int? Mate, string? Best)> Evaluate(Process sf, string fen, int depth)
    {
        await sf.StandardInput.WriteLineAsync($"position fen {fen}");
        await sf.StandardInput.WriteLineAsync($"go depth {depth}");
        await sf.StandardInput.FlushAsync();
        var whiteToMove = fen.Split(' ')[1] == "w";
        int? cp = null, mate = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await sf.StandardOutput.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("info ", StringComparison.Ordinal) && ScoreRe().Match(line) is { Success: true } s && !line.Contains(" lowerbound") && !line.Contains(" upperbound"))
            {
                var v = int.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture);
                if (s.Groups[1].Value == "cp") { cp = v; mate = null; } else { mate = v; cp = null; }
            }
            else if (line.StartsWith("bestmove", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                var best = parts.Length > 1 && parts[1] != "(none)" ? parts[1] : null;
                var sign = whiteToMove ? 1 : -1;
                if (best is null) // no legal moves: checkmate (the side to move lost) or stalemate
                    return ChessBoard.FromFen(fen).InCheck() ? (whiteToMove ? -99.0 : 99.0, null, null) : (0.0, null, null);
                return (cp is { } c ? sign * c / 100.0 : null, mate is { } m ? sign * m : null, best);
            }
        }
        throw new IOException("Stockfish stopped");
    }

    [GeneratedRegex(@" score (cp|mate) (-?\d+)")] private static partial Regex ScoreRe();
    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")] private static partial Regex UserName();
    [GeneratedRegex(@"\[FEN ""([^""]+)""\]")] private static partial Regex PgnFen();
}
