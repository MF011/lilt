using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lilt.Core;

namespace Lilt.Spotify;

/// <summary>A small Spotify remote. Playback and library actions share the same account state.</summary>
public sealed class SpotifyClient : IPlaybackController, IDisposable
{
    private const string ApiBase = "https://api.spotify.com/v1/";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    private readonly ITokenStore _tokenStore;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action<Uri> _openBrowser;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private SpotifySession? _session;
    private PlaybackSnapshot? _lastPlayback;
    private DateTimeOffset? _retryAfterUtc;
    private long _sessionGeneration;
    private long _signInGeneration;

    public SpotifyClient(ITokenStore tokenStore, HttpClient? httpClient = null, Action<Uri>? openBrowser = null,
        TimeProvider? timeProvider = null)
    {
        _tokenStore = tokenStore;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttp = httpClient is null;
        _openBrowser = openBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool IsConnected => _session is not null;
    public string? ClientId => _session?.ClientId;
    public DateTimeOffset? RetryAfterUtc => _retryAfterUtc > _time.GetUtcNow() ? _retryAfterUtc : null;

    public async Task<bool> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var signInGeneration = Interlocked.Increment(ref _signInGeneration);
        long sessionGeneration;
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSignInGeneration(signInGeneration);
            sessionGeneration = Interlocked.Increment(ref _sessionGeneration);
            _session = stored;
            _lastPlayback = null;
            if (_session is null) return false;
            if (string.IsNullOrWhiteSpace(_session.RefreshToken) || string.IsNullOrWhiteSpace(_session.AccessToken))
            {
                _session = null;
                await _tokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        finally { _sessionLock.Release(); }

        try
        {
            await GetAccessTokenAsync(null, cancellationToken, sessionGeneration).ConfigureAwait(false);
            return true;
        }
        catch (SpotifyAuthorizationException) { return false; }
    }

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var auth = SpotifyOAuth.CreateRequest(clientId);
        var signInGeneration = Interlocked.Increment(ref _signInGeneration);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var listener = new TcpListener(IPAddress.Loopback, 43821);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            throw new SpotifyAuthorizationException("The sign-in port is busy. Close the other sign-in window and try again.", ex);
        }

        try
        {
            _openBrowser(auth.AuthorizationUri);
            string? code = null;
            while (code is null)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                await using var stream = connection.GetStream();
                Uri? callback;
                try { callback = await ReadCallbackAsync(stream, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { continue; }
                catch (IOException) { continue; }
                if (callback is null || callback.AbsolutePath != "/callback")
                {
                    await ReplyAsync(stream, 404, "Page not found.").ConfigureAwait(false);
                    continue;
                }
                if (!SpotifyOAuth.IsMatchingState(auth.State, QueryValue(callback, "state")))
                {
                    await ReplyAsync(stream, 400, "This sign-in link has expired. Use the latest Spotify sign-in window.").ConfigureAwait(false);
                    continue;
                }
                var error = QueryValue(callback, "error");
                code = QueryValue(callback, "code");
                if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
                {
                    await ReplyAsync(stream, 400, "Sign-in cancelled. You can close this tab.").ConfigureAwait(false);
                    throw new SpotifyAuthorizationException("Spotify sign-in was cancelled.");
                }
                // Exchange before showing success so the browser never reports a connection that failed.
                await _sessionLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                var previousSession = _session;
                var savedPendingSession = false;
                try
                {
                    EnsureSignInGeneration(signInGeneration);
                    var next = await RequestTokenAsync(new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code", ["client_id"] = clientId.Trim(),
                        ["redirect_uri"] = SpotifyOAuth.RedirectUri, ["code_verifier"] = auth.CodeVerifier, ["code"] = code
                    }, clientId.Trim(), null, timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    EnsureSignInGeneration(signInGeneration);
                    await _tokenStore.SaveAsync(next, timeout.Token).ConfigureAwait(false);
                    savedPendingSession = true;
                    timeout.Token.ThrowIfCancellationRequested();
                    EnsureSignInGeneration(signInGeneration);
                    Interlocked.Increment(ref _sessionGeneration);
                    _session = next;
                    _lastPlayback = null;
                    _retryAfterUtc = null;
                }
                catch
                {
                    // Saving is atomic, but cancellation can arrive immediately after the write completes.
                    // An unfinished sign-in must not become a restored account on the next launch.
                    if (savedPendingSession)
                    {
                        if (previousSession is null) await _tokenStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                        else await _tokenStore.SaveAsync(previousSession, CancellationToken.None).ConfigureAwait(false);
                    }
                    await ReplyAsync(stream, 400, "Lilt couldn't finish signing in. Return to Lilt and try again.").ConfigureAwait(false);
                    throw;
                }
                finally { _sessionLock.Release(); }
                await ReplyAsync(stream, 200, "Connected to Lilt. You can close this tab.").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
            signInGeneration == Volatile.Read(ref _signInGeneration))
        {
            throw new SpotifyAuthorizationException("Sign-in timed out. Try connecting again.");
        }
        finally { listener.Stop(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _signInGeneration);
        Interlocked.Increment(ref _sessionGeneration);
        _session = null;
        _lastPlayback = null;
        _retryAfterUtc = null;
        // Finish removing credentials once disconnect has begun, even if the caller later cancels.
        await _sessionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _session = null;
            _lastPlayback = null;
            _retryAfterUtc = null;
            await _tokenStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { _sessionLock.Release(); }
    }

    public async Task<PlaybackSnapshot?> GetPlaybackAsync(CancellationToken cancellationToken = default)
    {
        var generation = Volatile.Read(ref _sessionGeneration);
        using var response = await SendAsync(HttpMethod.Get, "me/player", cancellationToken, generation).ConfigureAwait(false);
        EnsureSessionGeneration(generation);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            _lastPlayback = null;
            return null;
        }
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSessionGeneration(generation);
        var root = document.RootElement;
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            _lastPlayback = null;
            return null;
        }
        var device = Property(root, "device");
        var album = Property(item, "album");
        var show = Property(item, "show");
        var disallows = Property(Property(root, "actions"), "disallows");
        var isPlaying = Boolean(root, "is_playing");
        var restricted = Boolean(device, "is_restricted");
        var controllable = !restricted && !string.IsNullOrWhiteSpace(String(device, "id"));
        var artists = Property(item, "artists");
        var artist = artists.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artists.EnumerateArray().Select(x => String(x, "name")).Where(x => !string.IsNullOrWhiteSpace(x)))
            : String(show, "publisher");
        var images = Property(album.ValueKind == JsonValueKind.Object ? album : item, "images");
        var imageUrl = images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0
            ? String(images[0], "url") : null;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri) || imageUri.Scheme != Uri.UriSchemeHttps) imageUrl = null;
        var duration = Math.Max(0, Number(item, "duration_ms"));
        var trackUri = String(item, "uri");
        if (Boolean(item, "is_local") || String(item, "type") != "track" || !IsTrackUri(trackUri)) trackUri = null;
        var snapshot = new PlaybackSnapshot
        {
            Title = String(item, "name") ?? "Untitled",
            Artist = string.IsNullOrWhiteSpace(artist) ? "Spotify" : artist,
            Album = String(album, "name") ?? String(show, "name") ?? "",
            ArtworkUrl = imageUrl, TrackUri = trackUri,
            DeviceId = String(device, "id"), DeviceName = String(device, "name") ?? "Spotify",
            IsAvailable = true, IsPlaying = isPlaying,
            CanPlayPause = controllable && !Boolean(disallows, isPlaying ? "pausing" : "resuming"),
            CanNext = controllable && !Boolean(disallows, "skipping_next"),
            CanPrevious = controllable && !Boolean(disallows, "skipping_prev"),
            CanSeek = controllable && duration > 0 && !Boolean(disallows, "seeking"),
            CanSetVolume = controllable && Boolean(device, "supports_volume"),
            VolumePercent = (int)Math.Clamp(Number(device, "volume_percent"), 0, 100),
            Duration = TimeSpan.FromMilliseconds(duration),
            Position = TimeSpan.FromMilliseconds(Math.Clamp(Number(root, "progress_ms"), 0, duration)),
            CapturedAt = _time.GetUtcNow()
        };
        EnsureSessionGeneration(generation);
        _lastPlayback = snapshot;
        return snapshot;
    }

    public Task ExecuteAsync(PlaybackAction action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(action, CurrentTarget(), cancellationToken);

    public async Task ExecuteAsync(PlaybackAction action, PlaybackSnapshot target, CancellationToken cancellationToken = default)
    {
        var (method, path, allowed) = action switch
        {
            PlaybackAction.PlayPause => (HttpMethod.Put, target.IsPlaying ? "me/player/pause" : "me/player/play", target.CanPlayPause),
            PlaybackAction.Next => (HttpMethod.Post, "me/player/next", target.CanNext),
            PlaybackAction.Previous => (HttpMethod.Post, "me/player/previous", target.CanPrevious),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (!allowed) throw new SpotifyException("This control isn't available right now.");
        using var response = await SendAsync(method, WithDevice(path, target), cancellationToken).ConfigureAwait(false);
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => SeekAsync(position, CurrentTarget(), cancellationToken);

    public async Task SeekAsync(TimeSpan position, PlaybackSnapshot target, CancellationToken cancellationToken = default)
    {
        if (!target.CanSeek) throw new SpotifyException("Seeking isn't available on this device.");
        var milliseconds = (long)Math.Clamp(position.TotalMilliseconds, 0, target.Duration.TotalMilliseconds);
        using var response = await SendAsync(HttpMethod.Put, WithDevice($"me/player/seek?position_ms={milliseconds}", target), cancellationToken).ConfigureAwait(false);
    }

    public Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default) => SetVolumeAsync(percent, CurrentTarget(), cancellationToken);

    public async Task SetVolumeAsync(int percent, PlaybackSnapshot target, CancellationToken cancellationToken = default)
    {
        if (!target.CanSetVolume) throw new SpotifyException("Volume isn't available on this device.");
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        using var response = await SendAsync(HttpMethod.Put, WithDevice($"me/player/volume?volume_percent={percent}", target), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsSavedAsync(string trackUri, CancellationToken cancellationToken = default)
    {
        ValidateTrackUri(trackUri);
        using var response = await SendAsync(HttpMethod.Get, "me/library/contains?uris=" + Uri.EscapeDataString(trackUri), cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var result = document.RootElement;
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() != 1 ||
            result[0].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new SpotifyException("Spotify returned an unreadable library status.");
        return result[0].GetBoolean();
    }

    public async Task SetSavedAsync(string trackUri, bool saved, CancellationToken cancellationToken = default)
    {
        ValidateTrackUri(trackUri);
        using var response = await SendAsync(saved ? HttpMethod.Put : HttpMethod.Delete,
            "me/library?uris=" + Uri.EscapeDataString(trackUri), cancellationToken).ConfigureAwait(false);
    }

    private PlaybackSnapshot CurrentTarget() => _lastPlayback ?? throw new SpotifyException("Start playing something in Spotify first.");

    private static string WithDevice(string path, PlaybackSnapshot target)
    {
        if (string.IsNullOrWhiteSpace(target.DeviceId)) throw new SpotifyException("Spotify couldn't find the playback device. Refresh and try again.");
        return path + (path.Contains('?') ? "&" : "?") + "device_id=" + Uri.EscapeDataString(target.DeviceId);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken,
        long? expectedGeneration = null)
    {
        var generation = expectedGeneration ?? Volatile.Read(ref _sessionGeneration);
        EnsureSessionGeneration(generation);
        if (RetryAfterUtc is { } retry) throw new SpotifyApiException(HttpStatusCode.TooManyRequests,
            "Spotify needs a short break. Local controls are still available.", retry);
        var accessToken = await GetAccessTokenAsync(null, cancellationToken, generation).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, ApiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new SpotifyException("Couldn't reach Spotify. Check your connection.", ex); }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            { throw new SpotifyException("Spotify took too long to respond. Try again shortly.", ex); }
            if (generation != Volatile.Read(ref _sessionGeneration))
            {
                response.Dispose();
                EnsureSessionGeneration(generation);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                if (attempt == 0)
                {
                    accessToken = await GetAccessTokenAsync(accessToken, cancellationToken, generation).ConfigureAwait(false);
                    continue;
                }
                await ForgetInvalidSessionAsync(accessToken, generation, cancellationToken).ConfigureAwait(false);
                throw new SpotifyAuthorizationException("Reconnect your Spotify account.");
            }
            if (response.IsSuccessStatusCode) return response;
            var code = response.StatusCode;
            DateTimeOffset? retryAt = null;
            if (code == HttpStatusCode.TooManyRequests)
            {
                retryAt = response.Headers.RetryAfter?.Date ??
                    _time.GetUtcNow() + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Contains("QUOTA_EXCEEDED", StringComparison.Ordinal) && response.Headers.RetryAfter is null)
                    retryAt = _time.GetUtcNow().AddMinutes(15);
                if (retryAt <= _time.GetUtcNow()) retryAt = _time.GetUtcNow().AddSeconds(1);
                EnsureSessionGeneration(generation);
                _retryAfterUtc = retryAt;
            }
            response.Dispose();
            throw new SpotifyApiException(code, code switch
            {
                HttpStatusCode.Forbidden => "Spotify didn't allow this action. Check your Premium subscription and app access.",
                HttpStatusCode.NotFound => "This Spotify device is no longer available.",
                HttpStatusCode.TooManyRequests => "Spotify needs a short break. Local controls are still available.",
                HttpStatusCode.BadRequest => "Spotify couldn't complete this action.",
                _ when (int)code >= 500 => "Spotify is having trouble. Try again shortly.",
                _ => "Spotify couldn't complete this action."
            }, retryAt);
        }
        throw new SpotifyAuthorizationException("Reconnect your Spotify account.");
    }

    private async Task<string> GetAccessTokenAsync(string? rejectedToken, CancellationToken cancellationToken,
        long? expectedGeneration = null)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = expectedGeneration ?? Volatile.Read(ref _sessionGeneration);
            EnsureSessionGeneration(generation);
            var session = _session ?? throw new SpotifyAuthorizationException("Connect your Spotify account first.");
            if (session.ExpiresAt > _time.GetUtcNow().AddSeconds(60) &&
                (rejectedToken is null || session.AccessToken != rejectedToken)) return session.AccessToken;
            try
            {
                var next = await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken, ["client_id"] = session.ClientId
                }, session.ClientId, session.RefreshToken, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSessionGeneration(generation);
                await _tokenStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
                EnsureSessionGeneration(generation);
                _session = next;
                cancellationToken.ThrowIfCancellationRequested();
                return next.AccessToken;
            }
            catch (SpotifyAuthorizationException)
            {
                EnsureSessionGeneration(generation);
                Interlocked.Increment(ref _sessionGeneration);
                _session = null;
                _lastPlayback = null;
                await _tokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally { _sessionLock.Release(); }
    }

    private async Task<SpotifySession> RequestTokenAsync(Dictionary<string, string> form, string clientId,
        string? previousRefreshToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new SpotifyException("Couldn't reach Spotify. Check your connection.", ex); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new SpotifyException("Spotify took too long to respond. Try again shortly.", ex); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SpotifyAuthorizationException("Spotify sign-in has expired. Connect your account again.");
            if (!response.IsSuccessStatusCode) throw new SpotifyException("Spotify couldn't finish signing in. Try again shortly.");
            using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var accessToken = String(root, "access_token");
            var refreshToken = String(root, "refresh_token") ?? previousRefreshToken;
            var lifetime = Number(root, "expires_in");
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken) || lifetime <= 0)
                throw new SpotifyAuthorizationException("Spotify returned an incomplete sign-in. Try connecting again.");
            return new SpotifySession(clientId, accessToken, refreshToken, _time.GetUtcNow().AddSeconds(Math.Min(lifetime, 86400)));
        }
    }

    private async Task ForgetInvalidSessionAsync(string rejectedToken, long generation, CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSessionGeneration(generation);
            if (_session?.AccessToken != rejectedToken) return;
            Interlocked.Increment(ref _sessionGeneration);
            _session = null;
            _lastPlayback = null;
            await _tokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _sessionLock.Release(); }
    }

    private void EnsureSessionGeneration(long generation)
    {
        if (generation != Volatile.Read(ref _sessionGeneration))
            throw new OperationCanceledException("The Spotify account changed.");
    }

    private void EnsureSignInGeneration(long generation)
    {
        if (generation != Volatile.Read(ref _signInGeneration))
            throw new OperationCanceledException("Spotify sign-in was cancelled.");
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex) { throw new SpotifyException("Spotify returned an unreadable response. Try again shortly.", ex); }
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string? String(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static bool Boolean(JsonElement element, string name) => Property(element, name).ValueKind == JsonValueKind.True;
    private static long Number(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : 0;
    private static bool IsTrackUri(string? uri) => uri is { Length: 36 } && uri.StartsWith("spotify:track:", StringComparison.Ordinal)
        && uri.AsSpan(14).ToString().All(char.IsAsciiLetterOrDigit);
    private static void ValidateTrackUri(string uri)
    {
        if (!IsTrackUri(uri)) throw new ArgumentException("Only Spotify tracks can be saved here.", nameof(uri));
    }

    private static async Task<Uri?> ReadCallbackAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[8192];
        var used = 0;
        while (used < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), readTimeout.Token).ConfigureAwait(false);
            if (count == 0) return null;
            used += count;
            var headers = Encoding.ASCII.GetString(buffer, 0, used);
            if (!headers.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;
            var firstLine = headers.Split("\r\n", 2, StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ');
            if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith('/') || parts[1].StartsWith("//", StringComparison.Ordinal)) return null;
            return Uri.TryCreate("http://127.0.0.1:43821" + parts[1], UriKind.Absolute, out var callback) ? callback : null;
        }
        return null;
    }

    private static string? QueryValue(Uri uri, string key)
    {
        string? found = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = pair.Split('=', 2);
            if (Uri.UnescapeDataString(pieces[0].Replace('+', ' ')) != key) continue;
            // Reject duplicate state/code values rather than accepting a value chosen by an intermediary.
            if (found is not null) return null;
            found = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace('+', ' ')) : "";
        }
        return found;
    }

    private static async Task ReplyAsync(NetworkStream stream, int status, string message)
    {
        var html = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Lilt</title>" +
            "<style>html{color-scheme:dark}body{background:#111418;color:#eef0f2;font:16px system-ui;display:grid;min-height:90vh;place-content:center;margin:24px}p{color:#b1bac4;line-height:1.6}h1{font-size:24px;font-weight:500}</style>" +
            "<main><h1>Lilt</h1><p>" + WebUtility.HtmlEncode(message) + "</p></main></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        try
        {
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request")}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\n" +
                "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'\r\n" +
                $"Content-Length: {bytes.Length}\r\n\r\n");
            using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(headers, writeTimeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, writeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _sessionLock.Dispose();
    }
}
