using System.Net;

namespace Lilt.Spotify;

public class SpotifyException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class SpotifyAuthorizationException(string message, Exception? innerException = null)
    : SpotifyException(message, innerException);

public sealed class SpotifyApiException(HttpStatusCode statusCode, string message, DateTimeOffset? retryAfterUtc = null)
    : SpotifyException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public DateTimeOffset? RetryAfterUtc { get; } = retryAfterUtc;
}
