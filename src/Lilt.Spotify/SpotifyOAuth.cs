using System.Security.Cryptography;
using System.Text;

namespace Lilt.Spotify;

public sealed record SpotifyAuthorizationRequest(Uri AuthorizationUri, string State, string CodeVerifier)
{
    public override string ToString() => "SpotifyAuthorizationRequest";
}

public static class SpotifyOAuth
{
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    public const string Scopes = "user-read-playback-state user-modify-playback-state user-library-read user-library-modify";

    public static SpotifyAuthorizationRequest CreateRequest(string clientId)
    {
        ValidateClientId(clientId);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge
        };
        return new(new Uri("https://accounts.spotify.com/authorize?" + string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))), state, verifier);
    }

    public static bool IsMatchingState(string expected, string? actual)
    {
        if (actual is null || expected.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    public static void ValidateClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Trim().Length != 32 ||
            !clientId.Trim().All(Uri.IsHexDigit))
            throw new ArgumentException("Enter the Client ID from your Spotify app settings.", nameof(clientId));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
