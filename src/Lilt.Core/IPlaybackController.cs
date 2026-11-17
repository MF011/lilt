namespace Lilt.Core;

public enum PlaybackAction { PlayPause, Previous, Next }

public interface IPlaybackController
{
    Task ExecuteAsync(PlaybackAction action, CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default);
}
