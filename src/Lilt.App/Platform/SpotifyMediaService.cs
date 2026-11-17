using System.Runtime.InteropServices;
using Lilt.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Lilt.App.Platform;

/// <summary>Controls only the desktop Spotify media session. No global media keys are sent.</summary>
public sealed class SpotifyMediaService : IPlaybackController, IAsyncDisposable
{
    private const uint MaximumArtworkBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private Task _refreshTask = Task.CompletedTask;
    private string _title = "Spotify";
    private string _artist = "";
    private string _album = "";
    private byte[]? _artwork;
    private bool _hasMetadata;
    private bool _started;
    private int _disposed;
    private int _refreshRequested;
    private int _metadataRequested;
    private int _refreshWorkerRunning;

    public SpotifyMediaService() => _lifetimeToken = _lifetime.Token;

    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_started)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.SessionsChanged += OnSessionsChanged;
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                _started = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        await RefreshAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(PlaybackAction action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            var playback = session.GetPlaybackInfo();
            var controls = playback.Controls;
            cancellationToken.ThrowIfCancellationRequested();

            bool accepted;
            switch (action)
            {
                case PlaybackAction.Previous when controls.IsPreviousEnabled:
                    accepted = await session.TrySkipPreviousAsync();
                    break;
                case PlaybackAction.Next when controls.IsNextEnabled:
                    accepted = await session.TrySkipNextAsync();
                    break;
                case PlaybackAction.PlayPause when controls.IsPlayPauseToggleEnabled:
                    accepted = await session.TryTogglePlayPauseAsync();
                    break;
                case PlaybackAction.PlayPause
                    when playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                         && controls.IsPauseEnabled:
                    accepted = await session.TryPauseAsync();
                    break;
                case PlaybackAction.PlayPause when controls.IsPlayEnabled:
                    accepted = await session.TryPlayAsync();
                    break;
                default:
                    throw new InvalidOperationException("This action is not available in Spotify right now.");
            }

            if (!accepted)
                throw new InvalidOperationException("Spotify did not accept the playback command.");
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException("Spotify is no longer available. Open it and try again.", exception);
        }
        finally
        {
            _gate.Release();
        }

        QueueRefresh(false);
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            if (!session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled)
                throw new InvalidOperationException("Spotify is not allowing seeking right now.");

            var timeline = session.GetTimelineProperties();
            var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).Ticks);
            var target = Math.Clamp(position.Ticks, 0, duration);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await session.TryChangePlaybackPositionAsync(timeline.StartTime.Ticks + target))
                throw new InvalidOperationException("Spotify did not accept the new playback position.");
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException("Spotify is no longer available. Open it and try again.", exception);
        }
        finally
        {
            _gate.Release();
        }

        QueueRefresh(false);
    }

    public Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Connect your Spotify account to control its volume."));

    private GlobalSystemMediaTransportControlsSession RequireSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return FindSpotifySession() ?? throw new InvalidOperationException("Open Spotify to control playback.");
    }

    private GlobalSystemMediaTransportControlsSession? FindSpotifySession()
    {
        if (_manager is null)
            return null;

        GlobalSystemMediaTransportControlsSession? first = null;
        foreach (var candidate in _manager.GetSessions())
        {
            try
            {
                if (!candidate.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                    continue;

                first ??= candidate;
                if (candidate.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return candidate;
            }
            catch (COMException)
            {
                // A session can disappear while the manager's snapshot is being enumerated.
            }
        }

        var current = _manager.GetCurrentSession();
        if (current is not null)
        {
            try
            {
                if (current.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                    return current;
            }
            catch (COMException)
            {
            }
        }

        return first;
    }

    private async Task RefreshAsync(bool readMetadata, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PlaybackSnapshot? next = null;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            var selected = FindSpotifySession();
            if (!ReferenceEquals(_session, selected))
            {
                DetachSession();
                _session = selected;
                _hasMetadata = false;
                _artwork = null;
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
            }

            if (_session is null)
            {
                next = PlaybackSnapshot.Empty;
            }
            else
            {
                if (readMetadata || !_hasMetadata)
                {
                    var properties = await _session.TryGetMediaPropertiesAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    _title = string.IsNullOrWhiteSpace(properties.Title) ? "Spotify" : properties.Title;
                    _artist = string.IsNullOrWhiteSpace(properties.Artist) ? properties.AlbumArtist : properties.Artist;
                    _album = properties.AlbumTitle ?? "";
                    _artwork = await ReadArtworkAsync(properties.Thumbnail, cancellationToken).ConfigureAwait(false);
                    _hasMetadata = true;
                }

                var playback = _session.GetPlaybackInfo();
                var controls = playback.Controls;
                var timeline = _session.GetTimelineProperties();
                var durationTicks = Math.Max(0, (timeline.EndTime - timeline.StartTime).Ticks);
                var positionTicks = Math.Clamp((timeline.Position - timeline.StartTime).Ticks, 0, durationTicks);
                var now = DateTimeOffset.UtcNow;
                var capturedAt = timeline.LastUpdatedTime;
                if (capturedAt <= DateTimeOffset.UnixEpoch || capturedAt > now)
                    capturedAt = now;
                var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                next = new PlaybackSnapshot
                {
                    Title = _title,
                    Artist = _artist ?? "",
                    Album = _album,
                    Artwork = _artwork,
                    DeviceName = "This PC",
                    IsAvailable = true,
                    IsPlaying = isPlaying,
                    CanPlayPause = controls.IsPlayPauseToggleEnabled || (isPlaying ? controls.IsPauseEnabled : controls.IsPlayEnabled),
                    CanNext = controls.IsNextEnabled,
                    CanPrevious = controls.IsPreviousEnabled,
                    CanSeek = controls.IsPlaybackPositionEnabled && durationTicks > 0,
                    CanSetVolume = false,
                    Position = TimeSpan.FromTicks(positionTicks),
                    Duration = TimeSpan.FromTicks(durationTicks),
                    CapturedAt = capturedAt
                };
            }

            Volatile.Write(ref _snapshot, next);
        }
        finally
        {
            _gate.Release();
        }

        if (next is not null && Volatile.Read(ref _disposed) == 0)
            Publish(next);
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? thumbnail, CancellationToken cancellationToken)
    {
        if (thumbnail is null)
            return null;

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size is 0 or > MaximumArtworkBytes)
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            using var input = stream.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            var loaded = await reader.LoadAsync((uint)stream.Size);
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded == 0)
                return null;

            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => QueueRefresh(true);
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => QueueRefresh(false);
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => QueueRefresh(true);
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => QueueRefresh(false);
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => QueueRefresh(false);

    private void QueueRefresh(bool readMetadata)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (readMetadata)
            Interlocked.Exchange(ref _metadataRequested, 1);
        Interlocked.Exchange(ref _refreshRequested, 1);
        if (Interlocked.CompareExchange(ref _refreshWorkerRunning, 1, 0) == 0)
            _refreshTask = ProcessRefreshQueueAsync();
    }

    private async Task ProcessRefreshQueueAsync()
    {
        try
        {
            while (!_lifetimeToken.IsCancellationRequested && Interlocked.Exchange(ref _refreshRequested, 0) != 0)
            {
                var readMetadata = Interlocked.Exchange(ref _metadataRequested, 0) != 0;
                try
                {
                    await RefreshAsync(readMetadata, _lifetimeToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // Spotify may be closing or replacing its session. Keep the last good state
                    // until the manager supplies the next session event.
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshWorkerRunning, 0);
            if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _refreshRequested) != 0)
                QueueRefresh(false);
        }
    }

    private void Publish(PlaybackSnapshot snapshot)
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
            return;
        foreach (EventHandler<PlaybackSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // A consumer's dispatcher can already be shutting down.
            }
        }
    }

    private void DetachSession()
    {
        if (_session is null)
            return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DetachSession();
            if (_manager is not null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        await _refreshTask.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
