using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lilt.Core;
using Lilt.Spotify;

const string clientId = "0123456789abcdef0123456789abcdef";
const string trackUri = "spotify:track:4iV5W9uYEdYUVa79Axb7Rh";
var tests = new List<(string Name, Func<Task> Run)>();

void Add(string name, Func<Task> action) => tests.Add((name, action));
SpotifySession Session(string access = "old", int seconds = 3600) => new(clientId, access, "refresh", DateTimeOffset.UtcNow.AddSeconds(seconds));
HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
string Playback(string device = "desktop", bool restricted = false, string type = "track", bool local = false) => System.Text.Json.JsonSerializer.Serialize(new
{
    device = new { id = device, name = "Desk", is_restricted = restricted, supports_volume = true, volume_percent = 72 },
    is_playing = true, progress_ms = 42000,
    actions = new { disallows = new { skipping_prev = true } },
    item = new
    {
        type, is_local = local, name = "Track", uri = trackUri, duration_ms = 200000,
        artists = new[] { new { name = "One" }, new { name = "Two" } },
        album = new { name = "Album", images = new[] { new { url = "https://i.scdn.co/image/test" } } }
    }
});

Add("PKCE uses a fresh state and S256 challenge", () =>
{
    var auth = SpotifyOAuth.CreateRequest(clientId);
    var query = Query(auth.AuthorizationUri);
    Equal("S256", query["code_challenge_method"]);
    Equal(SpotifyOAuth.RedirectUri, query["redirect_uri"]);
    Equal(Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(auth.CodeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'), query["code_challenge"]);
    Check(auth.CodeVerifier.Length is >= 43 and <= 128, "PKCE verifier length");
    Check(auth.State != SpotifyOAuth.CreateRequest(clientId).State, "fresh state");
    Check(SpotifyOAuth.IsMatchingState(auth.State, auth.State), "matching state");
    Check(!SpotifyOAuth.IsMatchingState(auth.State, "forged"), "forged state rejected");
    return Task.CompletedTask;
});

Add("Invalid Client IDs are rejected before sign-in", async () =>
{
    await Throws<ArgumentException>(() => Task.FromResult(SpotifyOAuth.CreateRequest("not-a-client-id")));
});

Add("A valid stored session is restored without a network request", async () =>
{
    var handler = new StubHandler(_ => throw new Exception("Unexpected network request"));
    using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
    Check(await client.RestoreSessionAsync(), "restored");
    Equal(clientId, client.ClientId);
    Equal(0, handler.Calls);
});

Add("Expired tokens refresh and retain an omitted refresh token", async () =>
{
    var store = new MemoryStore(Session(seconds: -1));
    var handler = new StubHandler(async request =>
    {
        Equal("accounts.spotify.com", request.RequestUri!.Host);
        var body = await request.Content!.ReadAsStringAsync();
        Check(body.Contains("grant_type=refresh_token"), "refresh grant");
        return Json("{\"access_token\":\"new\",\"expires_in\":3600}");
    });
    using var client = new SpotifyClient(store, new HttpClient(handler));
    Check(await client.RestoreSessionAsync(), "restored");
    Equal("new", store.Value!.AccessToken);
    Equal("refresh", store.Value.RefreshToken);
    Equal(1, store.Saves);
});

Add("Playback parsing preserves the account URI and device capabilities", async () =>
{
    var handler = new StubHandler(request =>
    {
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        Equal("old", request.Headers.Authorization.Parameter);
        return Task.FromResult(Json(Playback()));
    });
    using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
    await client.RestoreSessionAsync();
    var snapshot = (await client.GetPlaybackAsync())!;
    Equal(trackUri, snapshot.TrackUri);
    Equal("One, Two", snapshot.Artist);
    Equal("Album", snapshot.Album);
    Equal("desktop", snapshot.DeviceId);
    Equal(72, snapshot.VolumePercent);
    Check(snapshot.CanNext && !snapshot.CanPrevious && snapshot.CanPlayPause && snapshot.CanSetVolume, "capabilities");
    Equal(TimeSpan.FromSeconds(42), snapshot.Position);
});

Add("No-content playback does not leave a stale command target", async () =>
{
    var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
    using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
    await client.RestoreSessionAsync();
    Check(await client.GetPlaybackAsync() is null, "no playback");
    await Throws<SpotifyException>(() => client.ExecuteAsync(PlaybackAction.Next));
});

Add("Restricted devices expose no playback controls", async () =>
{
    var handler = new StubHandler(_ => Task.FromResult(Json(Playback(restricted: true))));
    using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
    await client.RestoreSessionAsync();
    var snapshot = (await client.GetPlaybackAsync())!;
    Check(!snapshot.CanNext && !snapshot.CanPrevious && !snapshot.CanPlayPause && !snapshot.CanSeek && !snapshot.CanSetVolume, "restricted controls");
});

Add("Episodes and local files never become saveable track IDs", async () =>
{
    foreach (var body in new[] { Playback(type: "episode"), Playback(local: true) })
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(body)));
        using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
        await client.RestoreSessionAsync();
        Check((await client.GetPlaybackAsync())!.TrackUri is null, "non-library track");
    }
});

Add("A command keeps its captured device when playback changes", async () =>
{
    var requestNumber = 0;
    Uri? commandUri = null;
    HttpMethod? commandMethod = null;
    var handler = new StubHandler(request =>
    {
        if (request.Method == HttpMethod.Get) return Task.FromResult(Json(Playback(Interlocked.Increment(ref requestNumber) == 1 ? "first" : "second")));
        commandUri = request.RequestUri;
        commandMethod = request.Method;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    });
    using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
    await client.RestoreSessionAsync();
    var first = (await client.GetPlaybackAsync())!;
    await client.GetPlaybackAsync();
    await client.ExecuteAsync(PlaybackAction.PlayPause, first);
    Equal(HttpMethod.Put, commandMethod);
    Equal("/v1/me/player/pause", commandUri!.AbsolutePath);
    Equal("first", Query(commandUri)["device_id"]);
});

Add("Library calls use the current URI-based endpoints", async () =>
{
    var methods = new List<HttpMethod>();
    var handler = new StubHandler(request =>
    {
        Equal(trackUri, Query(request.RequestUri!)["uris"]);
        Equal(request.Method == HttpMethod.Get ? "/v1/me/library/contains" : "/v1/me/library", request.RequestUri!.AbsolutePath);
        methods.Add(request.Method);
        return Task.FromResult(request.Method == HttpMethod.Get ? Json("[true]") : new HttpResponseMessage(HttpStatusCode.OK));
    });
    using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
    await client.RestoreSessionAsync();
    Check(await client.IsSavedAsync(trackUri), "saved");
    await client.SetSavedAsync(trackUri, true);
    await client.SetSavedAsync(trackUri, false);
    Check(methods.SequenceEqual(new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete }), "library methods");
    await Throws<ArgumentException>(() => client.SetSavedAsync("spotify:local:unknown", true));
    Equal(3, handler.Calls);
});

Add("Concurrent 401 responses share a single refresh", async () =>
{
    var refreshes = 0;
    var handler = new StubHandler(async request =>
    {
        if (request.RequestUri!.Host == "accounts.spotify.com")
        {
            Interlocked.Increment(ref refreshes);
            await Task.Delay(15);
            return Json("{\"access_token\":\"new\",\"expires_in\":3600}");
        }
        if (request.Headers.Authorization!.Parameter == "old")
        {
            await Task.Delay(5);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        return Json("[true]");
    });
    using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
    await client.RestoreSessionAsync();
    var results = await Task.WhenAll(client.IsSavedAsync(trackUri), client.IsSavedAsync(trackUri));
    Check(results.All(x => x), "requests recovered");
    Equal(1, refreshes);
});

Add("Rejected refresh tokens clear the stored account", async () =>
{
    var store = new MemoryStore(Session(seconds: -1));
    var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
    using var client = new SpotifyClient(store, new HttpClient(handler));
    Check(!await client.RestoreSessionAsync(), "invalid session");
    Check(!client.IsConnected && store.Value is null, "session cleared");
});

Add("Rate limits stop follow-up requests until Retry-After", async () =>
{
    var handler = new StubHandler(_ =>
    {
        var response = Json("{\"error\":{\"reason\":\"QUOTA_EXCEEDED\"}}", HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
        return Task.FromResult(response);
    });
    using var client = new SpotifyClient(new MemoryStore(Session()), new HttpClient(handler));
    await client.RestoreSessionAsync();
    var error = await Throws<SpotifyApiException>(() => client.GetPlaybackAsync());
    Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    Check(client.RetryAfterUtc > DateTimeOffset.UtcNow.AddSeconds(110), "backoff retained");
    await Throws<SpotifyApiException>(() => client.IsSavedAsync(trackUri));
    Equal(1, handler.Calls);
});

Add("OAuth callback rejects forged state, then saves a valid connection", async () =>
{
    var store = new MemoryStore(null);
    Task? callbackTask = null;
    var handler = new StubHandler(async request =>
    {
        var body = await request.Content!.ReadAsStringAsync();
        Check(body.Contains("code_verifier="), "code verifier sent");
        Check(body.Contains("code=test-code"), "authorization code sent");
        Check(!body.Contains("client_secret"), "no client secret");
        return Json("{\"access_token\":\"connected\",\"refresh_token\":\"connected-refresh\",\"expires_in\":3600}");
    });
    using var client = new SpotifyClient(store, new HttpClient(handler), uri =>
    {
        callbackTask = Task.Run(async () =>
        {
            using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var forged = await local.GetAsync(SpotifyOAuth.RedirectUri + "?code=bad&state=forged");
            Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            using var duplicate = await local.GetAsync(SpotifyOAuth.RedirectUri + "?code=bad&state=" + Query(uri)["state"] + "&state=second");
            Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            using var accepted = await local.GetAsync(SpotifyOAuth.RedirectUri + "?code=test-code&state=" + Query(uri)["state"]);
            Equal(HttpStatusCode.OK, accepted.StatusCode);
        });
    });
    await client.ConnectAsync(clientId);
    await callbackTask!;
    Check(client.IsConnected, "connected");
    Equal("connected", store.Value!.AccessToken);
});

Add("Cancelling sign-in releases the loopback port", async () =>
{
    using var client = new SpotifyClient(new MemoryStore(null), openBrowser: _ => { });
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
    await Throws<OperationCanceledException>(() => client.ConnectAsync(clientId, cts.Token));
    using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 43821);
    probe.Start();
    probe.Stop();
});

foreach (var blockRetry in new[] { false, true })
{
    Add($"A stale {(blockRetry ? "second" : "first")} 401 cannot clear or use a newly connected account", async () =>
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryStore(Session());
        var apiTokens = new List<string>();
        Task? callbackTask = null;
        var handler = new StubHandler(async request =>
        {
            if (request.RequestUri!.Host == "accounts.spotify.com")
            {
                var form = await request.Content!.ReadAsStringAsync();
                return Json(form.Contains("grant_type=refresh_token")
                    ? "{\"access_token\":\"refreshed-old\",\"expires_in\":3600}"
                    : "{\"access_token\":\"new-account\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}");
            }
            var token = request.Headers.Authorization!.Parameter!;
            apiTokens.Add(token);
            if (blockRetry && token == "old") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            blocked.TrySetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var client = new SpotifyClient(store, new HttpClient(handler), uri =>
        {
            callbackTask = Task.Run(async () =>
            {
                using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
                using var response = await local.GetAsync(SpotifyOAuth.RedirectUri + "?code=new-sign-in&state=" + Query(uri)["state"]);
                Equal(HttpStatusCode.OK, response.StatusCode);
            });
        });
        await client.RestoreSessionAsync();
        var oldRequest = client.IsSavedAsync(trackUri);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(clientId);
        await callbackTask!;
        release.SetResult();
        await Throws<OperationCanceledException>(() => oldRequest);
        Check(client.IsConnected, "new account remains connected");
        Equal("new-account", store.Value!.AccessToken);
        Check(!apiTokens.Contains("new-account"), "stale request never retries using the new account");
        Equal(blockRetry ? 2 : 1, apiTokens.Count);
    });
}

Add("Disconnect invalidates a sign-in waiting for its callback", async () =>
{
    Uri? authorizationUri = null;
    var store = new MemoryStore(null);
    var handler = new StubHandler(_ => throw new Exception("Token exchange must not run after disconnect"));
    using var client = new SpotifyClient(store, new HttpClient(handler), uri => authorizationUri = uri);
    var signIn = client.ConnectAsync(clientId);
    Check(authorizationUri is not null, "browser opened");
    await client.DisconnectAsync();
    using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
    using var response = await local.GetAsync(SpotifyOAuth.RedirectUri + "?code=late-code&state=" + Query(authorizationUri!)["state"]);
    Equal(HttpStatusCode.BadRequest, response.StatusCode);
    await Throws<OperationCanceledException>(() => signIn);
    Check(!client.IsConnected && store.Value is null, "account stays disconnected");
    Equal(0, handler.Calls);
});

Add("A late token response cannot restore a cancelled sign-in", async () =>
{
    var exchanging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new MemoryStore(null);
    Task? callbackTask = null;
    var handler = new StubHandler(async _ =>
    {
        exchanging.SetResult();
        await release.Task; // Deliberately ignore cancellation, as an already-completing response can do.
        return Json("{\"access_token\":\"late\",\"refresh_token\":\"late-refresh\",\"expires_in\":3600}");
    });
    using var client = new SpotifyClient(store, new HttpClient(handler), uri =>
    {
        callbackTask = Task.Run(async () =>
        {
            using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var response = await local.GetAsync(SpotifyOAuth.RedirectUri + "?code=late-code&state=" + Query(uri)["state"]);
            Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });
    });
    using var cts = new CancellationTokenSource();
    var signIn = client.ConnectAsync(clientId, cts.Token);
    await exchanging.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cts.Cancel();
    release.SetResult();
    await Throws<OperationCanceledException>(() => signIn);
    await callbackTask!;
    Check(!client.IsConnected && store.Value is null && store.Saves == 0, "late tokens are never persisted");
});

Add("Disconnect wins over a token exchange already in flight", async () =>
{
    var exchanging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new MemoryStore(null);
    Task? callbackTask = null;
    var handler = new StubHandler(async _ =>
    {
        exchanging.SetResult();
        await release.Task;
        return Json("{\"access_token\":\"late\",\"refresh_token\":\"late-refresh\",\"expires_in\":3600}");
    });
    using var client = new SpotifyClient(store, new HttpClient(handler), uri =>
    {
        callbackTask = Task.Run(async () =>
        {
            using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var response = await local.GetAsync(SpotifyOAuth.RedirectUri + "?code=late-code&state=" + Query(uri)["state"]);
            Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });
    });
    var signIn = client.ConnectAsync(clientId);
    await exchanging.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var disconnect = client.DisconnectAsync();
    release.SetResult();
    await Throws<OperationCanceledException>(() => signIn);
    await disconnect;
    await callbackTask!;
    Check(!client.IsConnected && store.Value is null && store.Saves == 0, "disconnect is final");
});

Add("Cancellation after saving a pending sign-in rolls its credentials back", async () =>
{
    var store = new PausingStore();
    Task? callbackTask = null;
    var handler = new StubHandler(_ => Task.FromResult(Json("{\"access_token\":\"pending\",\"refresh_token\":\"pending-refresh\",\"expires_in\":3600}")));
    using var client = new SpotifyClient(store, new HttpClient(handler), uri =>
    {
        callbackTask = Task.Run(async () =>
        {
            using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var response = await local.GetAsync(SpotifyOAuth.RedirectUri + "?code=code&state=" + Query(uri)["state"]);
            Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });
    });
    using var cts = new CancellationTokenSource();
    var signIn = client.ConnectAsync(clientId, cts.Token);
    await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cts.Cancel();
    store.CompleteSave.SetResult();
    await Throws<OperationCanceledException>(() => signIn);
    await callbackTask!;
    Check(!client.IsConnected && store.Value is null, "unfinished sign-in credentials rolled back");
});

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} Spotify checks passed.");
return failed == 0 ? 0 : 1;

static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length > 1 ? Uri.UnescapeDataString(x[1].Replace('+', ' ')) : "");
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T ex) { return ex; }
    throw new Exception("Expected " + typeof(T).Name);
}

sealed class MemoryStore(SpotifySession? value) : ITokenStore
{
    public SpotifySession? Value { get; private set; } = value;
    public int Saves { get; private set; }
    public Task<SpotifySession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    public Task SaveAsync(SpotifySession session, CancellationToken cancellationToken = default) { Value = session; Saves++; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
}

sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    private int _calls;
    public int Calls => _calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return response(request);
    }
}

sealed class PausingStore : ITokenStore
{
    public SpotifySession? Value { get; private set; }
    public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CompleteSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<SpotifySession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    public async Task SaveAsync(SpotifySession session, CancellationToken cancellationToken = default)
    {
        Value = session;
        SaveStarted.TrySetResult();
        await CompleteSave.Task;
    }
    public Task ClearAsync(CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
}
