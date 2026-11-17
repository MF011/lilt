namespace Lilt.App.Settings;

internal sealed class ArtworkCache : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private const int Limit = 3 * 1024 * 1024;

    public async Task<byte[]?> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(url, out var cached)) return cached;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > Limit) return null;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var result = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (result.Length + read > Limit) return null;
            result.Write(buffer, 0, read);
        }
        var bytes = result.ToArray();
        _cache[url] = bytes;
        _order.Enqueue(url);
        while (_order.Count > 8) _cache.Remove(_order.Dequeue());
        return bytes;
    }

    public void Dispose() => _http.Dispose();
}
