using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record DiscordMember(string Id, string Name, string? Avatar, bool Speaking, bool Mute, bool Deaf, bool SelfMute,
    bool SelfDeaf, bool Bot);

public sealed record DiscordChannel(string Id, string Name, string? Guild);

public sealed record DiscordSnapshot(bool Enabled, string Status, string? Message, string? User, DiscordChannel? Channel,
    bool SelfMute, bool SelfDeaf, IReadOnlyList<DiscordMember> Members, long Updated);

/// <summary>
/// The Discord widget: the voice channel you are in, who is talking, and mute / deafen / leave. It talks to the Discord
/// desktop app on this PC over its local RPC socket (discord-ipc-N), with a Discord application of the user's own
/// (Settings › General › Discord: client ID and secret, authorised once in a Discord pop-up). Nothing goes through a
/// server of ours; the only network calls are Discord's OAuth token endpoint and its avatar CDN.
/// </summary>
public sealed partial class DiscordMonitor(ConfigStore config, ILogger<DiscordMonitor> log) : BackgroundService
{
    private sealed record Token(string ClientId, string Access, string Refresh, long Expires);

    private static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string TokenPath = Path.Combine(AppPaths.ConfigDir, "discord-token.json");

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, (byte[] Bytes, long At)> _avatars = new();
    private Stream? _pipe;
    private volatile bool _authorizeRequested;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string _status = "off";
    private string? _message, _user, _subscribed;
    private DiscordChannel? _channel;
    private bool _selfMute, _selfDeaf;
    private readonly Dictionary<string, DiscordMember> _members = [];
    private readonly HashSet<string> _speaking = [];
    private long _updated;

    public DiscordSnapshot Snapshot()
    {
        lock (_lock)
        {
            var cfg = config.Get().Discord;
            var members = _members.Values
                .Select(m => m with { Speaking = _speaking.Contains(m.Id) })
                .OrderBy(m => m.Bot).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return new(cfg.Enabled, _status, _message, _user, _channel, _selfMute, _selfDeaf, members, _updated);
        }
    }

    /// <summary>Settings › Discord › Connect: shows Discord's own "Authorise" pop-up on the PC.</summary>
    public void RequestAuthorize()
    {
        _authorizeRequested = true;
        Wake();
    }

    public void Disconnect()
    {
        try { File.Delete(TokenPath); } catch (IOException) { }
        Wake();
    }

    private void Wake()
    {
        _wake.TrySetResult();
        try { _pipe?.Dispose(); } catch (Exception) { }
    }

    private void SetStatus(string status, string? message = null)
    {
        lock (_lock)
        {
            _status = status; _message = message; _updated = Environment.TickCount64;
            if (status != "ready") { _channel = null; _members.Clear(); _speaking.Clear(); _subscribed = null; }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var cfg = config.Get().Discord;
            var wait = TimeSpan.FromSeconds(5);
            if (!cfg.Enabled) SetStatus("off");
            else if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
                SetStatus("setup", "Add your Discord application's client ID and secret in Settings › General › Discord");
            else
            {
                try { await RunSession(cfg, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (DiscordNotRunning) { SetStatus("not-running", "Discord is not running on this PC"); wait = TimeSpan.FromSeconds(8); }
                catch (Exception ex)
                {
                    if (_status is "ready" or "connecting") log.LogInformation("Discord: {Error}", ex.Message);
                    if (_status == "authorize") lock (_lock) { _message = ex.Message; _updated = Environment.TickCount64; }
                    else SetStatus("error", ex.Message);
                }
                finally
                {
                    try { _pipe?.Dispose(); } catch (Exception) { }
                    _pipe = null;
                    foreach (var p in _pending.Values) p.TrySetCanceled();
                    _pending.Clear();
                }
            }
            await Task.WhenAny(_wake.Task, Task.Delay(wait, ct));
        }
    }

    private sealed class DiscordNotRunning : Exception;

    private async Task RunSession(DiscordConfig cfg, CancellationToken ct)
    {
        _pipe = await Connect(ct) ?? throw new DiscordNotRunning();
        if (_status is "not-running" or "off" or "setup" or "error") SetStatus("connecting");
        await Send(0, new JsonObject { ["v"] = 1, ["client_id"] = cfg.ClientId!.Trim() }, ct);
        var (_, ready) = await Read(_pipe, ct);
        if (ready.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
            throw new IOException(ErrorText(ready));
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = ReadLoop(_pipe, session.Token);
        try
        {
            var token = await Authenticate(cfg, session.Token);
            if (token is null) { await Hold(reader, cfg, session.Token); return; }
            await Subscribe("VOICE_CHANNEL_SELECT", null, session.Token);
            await Subscribe("VOICE_SETTINGS_UPDATE", null, session.Token);
            var settings = await Command("GET_VOICE_SETTINGS", new JsonObject(), session.Token);
            lock (_lock) { ApplySettings(settings); _status = "ready"; _message = null; _updated = Environment.TickCount64; }
            await LoadChannel(session.Token);
            await Hold(reader, cfg, session.Token);
            if (reader.IsFaulted) await reader;
        }
        finally { session.Cancel(); }
    }

    /// <summary>Keeps the session until Discord closes it, a button wakes the loop, or the Discord settings change.</summary>
    private async Task Hold(Task reader, DiscordConfig cfg, CancellationToken ct)
    {
        while (!reader.IsCompleted && !_wake.Task.IsCompleted && !ct.IsCancellationRequested)
        {
            await Task.WhenAny(reader, _wake.Task, Task.Delay(3000, ct));
            var now = config.Get().Discord;
            if (!now.Enabled || now.ClientId != cfg.ClientId || now.ClientSecret != cfg.ClientSecret) return;
        }
    }

    /// <summary>A stored token (refreshed when needed), or the authorisation pop-up when the user asked for it.</summary>
    private async Task<Token?> Authenticate(DiscordConfig cfg, CancellationToken ct)
    {
        var client = cfg.ClientId!.Trim();
        var token = LoadToken();
        if (token is not null && token.ClientId != client) token = null;
        if (token is not null && token.Expires < DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())
        {
            try { token = await TokenRequest(cfg, "refresh_token", token.Refresh, ct); }
            catch (Exception ex) { log.LogInformation("Discord token refresh failed: {Error}", ex.Message); token = null; }
        }
        if (token is null)
        {
            if (!_authorizeRequested)
            {
                SetStatus("authorize", "Press Connect in Settings › General › Discord, then Authorise in Discord");
                return null;
            }
            _authorizeRequested = false;
            SetStatus("authorize", "Waiting for you to press Authorise in Discord…");
            // AUTHORIZE must not carry a redirect URI (Discord refuses it in the RPC flow); Discord uses the application's
            // registered OAuth2 redirect instead, and says "Missing redirect_uri" when the application has none.
            JsonElement auth;
            try
            {
                auth = await Command("AUTHORIZE", new JsonObject
                {
                    ["client_id"] = client,
                    ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)s).ToArray()),
                }, ct, TimeSpan.FromMinutes(3));
            }
            catch (DiscordError ex) when (ex.Message.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Your Discord application has no redirect. In the Discord Developer Portal open your application › OAuth2 › Redirects, add {RedirectUri(cfg)}, save, then press Connect again.");
            }
            var code = auth.GetProperty("code").GetString() ?? throw new IOException("Discord returned no code");
            token = await TokenRequest(cfg, "authorization_code", code, ct);
        }
        try
        {
            var me = await Command("AUTHENTICATE", new JsonObject { ["access_token"] = token.Access }, ct);
            lock (_lock) _user = me.TryGetProperty("user", out var u) ? UserName(u, null) : null;
            return token;
        }
        catch (DiscordError ex)
        {
            log.LogInformation("Discord AUTHENTICATE failed: {Error}", ex.Message);
            try { File.Delete(TokenPath); } catch (IOException) { }
            SetStatus("authorize", "Discord signed this app out. Press Connect in Settings › General › Discord");
            return null;
        }
    }

    private static string RedirectUri(DiscordConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.RedirectUri) ? "http://localhost" : cfg.RedirectUri.Trim();

    private async Task<Token> TokenRequest(DiscordConfig cfg, string grant, string value, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = cfg.ClientId!.Trim(),
            ["client_secret"] = cfg.ClientSecret!.Trim(),
            ["grant_type"] = grant,
            [grant == "refresh_token" ? "refresh_token" : "code"] = value,
        };
        if (grant == "authorization_code") fields["redirect_uri"] = RedirectUri(cfg);
        using var response = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(fields), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var reason = body.Length > 200 ? body[..200] : body;
            throw new IOException($"Discord refused the token request ({(int)response.StatusCode}): {reason}");
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = new Token(cfg.ClientId!.Trim(), root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (root.TryGetProperty("expires_in", out var e) ? e.GetInt64() : 3600));
        AppPaths.WriteAllTextPrivate(TokenPath, JsonSerializer.Serialize(token));
        return token;
    }

    private static Token? LoadToken()
    {
        try { return File.Exists(TokenPath) ? JsonSerializer.Deserialize<Token>(File.ReadAllText(TokenPath)) : null; }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ voice channel state

    private async Task LoadChannel(CancellationToken ct)
    {
        var channel = await Command("GET_SELECTED_VOICE_CHANNEL", new JsonObject(), ct);
        string? id = channel.ValueKind == JsonValueKind.Object && channel.TryGetProperty("id", out var i) ? i.GetString() : null;
        string? old;
        lock (_lock) old = _subscribed;
        if (old is not null && old != id)
            foreach (var evt in ChannelEvents) await Command("UNSUBSCRIBE", new JsonObject { ["channel_id"] = old }, ct, evt: evt).ConfigureAwait(false);
        if (id is null)
        {
            lock (_lock) { _channel = null; _members.Clear(); _speaking.Clear(); _subscribed = null; _updated = Environment.TickCount64; }
            return;
        }
        string? guild = null;
        if (channel.TryGetProperty("guild_id", out var g) && g.GetString() is { Length: > 0 } guildId)
        {
            try { guild = (await Command("GET_GUILD", new JsonObject { ["guild_id"] = guildId }, ct)).GetProperty("name").GetString(); }
            catch (Exception) { }
        }
        lock (_lock)
        {
            _channel = new(id, channel.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", guild);
            _members.Clear(); _speaking.Clear();
            if (channel.TryGetProperty("voice_states", out var states))
                foreach (var s in states.EnumerateArray()) Upsert(s);
            _updated = Environment.TickCount64;
        }
        if (old != id)
        {
            foreach (var evt in ChannelEvents) await Subscribe(evt, id, ct);
            lock (_lock) _subscribed = id;
        }
    }

    private static readonly string[] ChannelEvents = ["VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP"];

    private void Upsert(JsonElement s)
    {
        if (!s.TryGetProperty("user", out var u) || u.GetProperty("id").GetString() is not { } id) return;
        var vs = s.TryGetProperty("voice_state", out var v) ? v : default;
        bool Flag(string name) => vs.ValueKind == JsonValueKind.Object && vs.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.True;
        var nick = s.TryGetProperty("nick", out var nn) ? nn.GetString() : null;
        var avatar = u.TryGetProperty("avatar", out var a) && a.GetString() is { } hash && AvatarHash().IsMatch(hash) ? hash : null;
        _members[id] = new(id, UserName(u, nick), avatar, false, Flag("mute"), Flag("deaf"), Flag("self_mute"), Flag("self_deaf"),
            u.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.True);
    }

    private static string UserName(JsonElement u, string? nick) =>
        !string.IsNullOrWhiteSpace(nick) ? nick
        : u.TryGetProperty("global_name", out var g) && g.GetString() is { Length: > 0 } global ? global
        : u.TryGetProperty("username", out var n) ? n.GetString() ?? "?" : "?";

    private void ApplySettings(JsonElement s)
    {
        if (s.TryGetProperty("mute", out var m)) _selfMute = m.ValueKind == JsonValueKind.True;
        if (s.TryGetProperty("deaf", out var d)) _selfDeaf = d.ValueKind == JsonValueKind.True;
    }

    private void OnEvent(string evt, JsonElement data, CancellationToken ct)
    {
        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                _ = Task.Run(async () => { try { await LoadChannel(ct); } catch (Exception ex) { log.LogDebug("Discord channel: {Error}", ex.Message); } }, ct);
                return;
            case "VOICE_SETTINGS_UPDATE":
                lock (_lock) { ApplySettings(data); _updated = Environment.TickCount64; }
                return;
            case "VOICE_STATE_CREATE" or "VOICE_STATE_UPDATE":
                lock (_lock) { Upsert(data); _updated = Environment.TickCount64; }
                return;
            case "VOICE_STATE_DELETE":
                if (data.TryGetProperty("user", out var du) && du.GetProperty("id").GetString() is { } gone)
                    lock (_lock) { _members.Remove(gone); _speaking.Remove(gone); _updated = Environment.TickCount64; }
                return;
            case "SPEAKING_START" or "SPEAKING_STOP":
                if (data.TryGetProperty("user_id", out var su) && su.GetString() is { } who)
                    lock (_lock)
                    {
                        if (evt == "SPEAKING_START") _speaking.Add(who); else _speaking.Remove(who);
                        _updated = Environment.TickCount64;
                    }
                return;
        }
    }

    /// <summary>The phone's buttons: mute, deafen (toggles) and leave the voice channel.</summary>
    public async Task<string?> Act(string action, CancellationToken ct)
    {
        if (_status != "ready") return _message ?? "Discord is not connected";
        bool mute, deaf;
        lock (_lock) { mute = _selfMute; deaf = _selfDeaf; }
        try
        {
            switch (action)
            {
                case "mute": await Command("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = !mute }, ct); break;
                case "deafen": await Command("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = !deaf }, ct); break;
                case "leave": await Command("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null }, ct); break;
                default: return "unknown action";
            }
            if (action != "leave")
            {
                var settings = await Command("GET_VOICE_SETTINGS", new JsonObject(), ct);
                lock (_lock) { ApplySettings(settings); _updated = Environment.TickCount64; }
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ------------------------------------------------------------------ avatars (through the agent: the phone may be offline)

    public async Task<byte[]?> Avatar(string user, string hash, CancellationToken ct)
    {
        if (!UserId().IsMatch(user) || !AvatarHash().IsMatch(hash)) return null;
        var key = user + "/" + hash;
        if (_avatars.TryGetValue(key, out var hit) && Environment.TickCount64 - hit.At < 6 * 3600_000) return hit.Bytes;
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://cdn.discordapp.com/avatars/{user}/{hash}.png?size=64", ct);
            if (bytes.Length > 256 * 1024) return null;
            if (_avatars.Count > 300) _avatars.Clear();
            _avatars[key] = (bytes, Environment.TickCount64);
            return bytes;
        }
        catch (Exception) { return null; }
    }

    [GeneratedRegex("^[0-9]{5,25}$")] private static partial Regex UserId();
    [GeneratedRegex("^(a_)?[0-9a-f]{16,40}$")] private static partial Regex AvatarHash();

    // ------------------------------------------------------------------ the IPC protocol

    private sealed class DiscordError(string message) : Exception(message);

    private static async Task<Stream?> Connect(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(250, ct); return pipe; }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { await pipe.DisposeAsync(); }
            }
            else
            {
                var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";
                foreach (var sub in new[] { "", "app/com.discordapp.Discord", "snap.discord" })
                {
                    var path = Path.Combine(dir, sub, $"discord-ipc-{i}");
                    if (!File.Exists(path)) continue;
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch (SocketException) { socket.Dispose(); }
                }
            }
        }
        return null;
    }

    private async Task Send(int op, JsonNode payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _write.WaitAsync(ct);
        try
        {
            var pipe = _pipe ?? throw new IOException("Discord closed the connection");
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
        finally { _write.Release(); }
    }

    private static async Task<(int Op, JsonElement Body)> Read(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 4 * 1024 * 1024) throw new IOException("Discord sent an unexpected frame");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);
        using var doc = JsonDocument.Parse(body);
        return (op, doc.RootElement.Clone());
    }

    private async Task ReadLoop(Stream pipe, CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            var (op, body) = await Read(pipe, ct);
            if (op == 2) throw new IOException("Discord closed the connection: " + ErrorText(body));
            if (op == 3) { await Send(4, JsonNode.Parse(body.GetRawText())!, ct); continue; }
            if (op != 1) continue;
            var evt = body.TryGetProperty("evt", out var e) ? e.GetString() : null;
            var data = body.TryGetProperty("data", out var d) ? d : default;
            if (body.TryGetProperty("nonce", out var n) && n.GetString() is { } nonce && _pending.TryRemove(nonce, out var waiter))
            {
                if (evt == "ERROR") waiter.TrySetException(new DiscordError(ErrorText(body)));
                else waiter.TrySetResult(data);
                continue;
            }
            if (evt is not null && body.TryGetProperty("cmd", out var c) && c.GetString() == "DISPATCH") OnEvent(evt, data, ct);
        }
    }

    private static string ErrorText(JsonElement body) =>
        body.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var m)
            ? m.GetString() ?? "error"
            : body.TryGetProperty("message", out var mm) ? mm.GetString() ?? "error" : "error";

    private Task<JsonElement> Subscribe(string evt, string? channel, CancellationToken ct) =>
        Command("SUBSCRIBE", channel is null ? new JsonObject() : new JsonObject { ["channel_id"] = channel }, ct, evt: evt);

    private async Task<JsonElement> Command(string cmd, JsonObject args, CancellationToken ct, TimeSpan? timeout = null, string? evt = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = waiter;
        var payload = new JsonObject { ["cmd"] = cmd, ["args"] = args, ["nonce"] = nonce };
        if (evt is not null) payload["evt"] = evt;
        try
        {
            await Send(1, payload, ct);
            return await waiter.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(8), ct);
        }
        catch (TimeoutException) { throw new IOException($"Discord did not answer {cmd}"); }
        finally { _pending.TryRemove(nonce, out _); }
    }
}
