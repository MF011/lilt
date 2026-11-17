namespace Lilt.Core;

public sealed record PlaybackSnapshot
{
    public string Title { get; init; } = "Nothing playing";
    public string Artist { get; init; } = "Open Spotify to get started";
    public string Album { get; init; } = "";
    public byte[]? Artwork { get; init; }
    public string? ArtworkUrl { get; init; }
    public string? TrackUri { get; init; }
    public string? DeviceId { get; init; }
    public string DeviceName { get; init; } = "This PC";
    public bool IsAvailable { get; init; }
    public bool IsPlaying { get; init; }
    public bool CanPlayPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanSeek { get; init; }
    public bool CanSetVolume { get; init; }
    public int VolumePercent { get; init; } = 50;
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public static PlaybackSnapshot Empty { get; } = new();

    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var seconds = Position.TotalSeconds + (IsPlaying ? Math.Max(0, (now - CapturedAt).TotalSeconds) : 0);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, Duration.TotalSeconds)));
    }
}
