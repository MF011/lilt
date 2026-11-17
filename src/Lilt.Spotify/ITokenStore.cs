namespace Lilt.Spotify;

public sealed record SpotifySession(string ClientId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"SpotifySession {{ ClientId = {ClientId}, ExpiresAt = {ExpiresAt:O} }}";
}

public interface ITokenStore
{
    Task<SpotifySession?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SpotifySession session, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
