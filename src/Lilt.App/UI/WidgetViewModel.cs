using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lilt.Core;

namespace Lilt.App.UI;

public sealed class WidgetViewModel : INotifyPropertyChanged
{
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private BitmapSource? _artwork;
    private WidgetSize _size;
    private bool _busy;
    private bool _likeEnabled;
    private bool? _isSaved;
    private bool _isSeeking;
    private bool _isAdjustingVolume;
    private string _likeHint = "Connect Spotify from the tray to save tracks";
    private string _connectionLabel = "This PC";
    private string? _notice;
    private double _positionSeconds;
    private double _volumePercent = 50;
    private double _glassOpacity = .72;
    private readonly DelegateCommand _playPause;
    private readonly DelegateCommand _previous;
    private readonly DelegateCommand _next;
    private readonly DelegateCommand _like;

    public WidgetViewModel()
    {
        _playPause = new(() => ActionRequested?.Invoke(PlaybackAction.PlayPause), () => !Busy && Snapshot.CanPlayPause);
        _previous = new(() => ActionRequested?.Invoke(PlaybackAction.Previous), () => !Busy && Snapshot.CanPrevious);
        _next = new(() => ActionRequested?.Invoke(PlaybackAction.Next), () => !Busy && Snapshot.CanNext);
        _like = new(() => LikeRequested?.Invoke(), () => !Busy && LikeEnabled);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<PlaybackAction>? ActionRequested;
    public event Action? LikeRequested;
    public event Action<TimeSpan>? SeekRequested;
    public event Action<int>? VolumeRequested;

    public PlaybackSnapshot Snapshot => _snapshot;
    public string Title => Snapshot.Title;
    public string Artist => Snapshot.Artist;
    public string Album => Snapshot.Album;
    public bool IsPlaying => Snapshot.IsPlaying;
    public bool IsAvailable => Snapshot.IsAvailable;
    public bool HasArtwork => Artwork is not null;
    public BitmapSource? Artwork => _artwork;
    public bool ShowAlbum => Size != WidgetSize.Compact && !string.IsNullOrWhiteSpace(Album);
    public bool IsExpanded => Size == WidgetSize.Expanded;
    public bool ShowVolume => IsExpanded && Snapshot.CanSetVolume;
    public bool CanSeek => !Busy && Snapshot.CanSeek && Snapshot.Duration > TimeSpan.Zero;
    public double DurationSeconds => Math.Max(1, Snapshot.Duration.TotalSeconds);
    public string PositionLabel => FormatTime(TimeSpan.FromSeconds(PositionSeconds));
    public string RemainingLabel => "−" + FormatTime(Snapshot.Duration - TimeSpan.FromSeconds(PositionSeconds));
    public string PlaybackHint => IsPlaying ? "Pause" : "Play";
    public string StatusLabel => Notice ?? (IsAvailable ? $"{(IsPlaying ? "Playing" : "Paused")} · {ConnectionLabel}" : "Open Spotify");
    public string ArtworkHint => IsAvailable ? $"{Title} — {Artist}" : "Spotify";
    public Brush HeartFill => IsSaved == true ? AccentBrush : Brushes.Transparent;
    public Brush HeartStroke => IsSaved == true ? AccentBrush : SecondaryBrush;
    public Brush StatusBrush => IsPlaying ? AccentBrush : MutedBrush;
    public string SaveLabel => IsSaved == true ? "Remove from Liked Songs" : "Save to Liked Songs";

    private static readonly Brush AccentBrush = FrozenBrush("#C9F8E4");
    private static readonly Brush SecondaryBrush = FrozenBrush("#C8CFD6");
    private static readonly Brush MutedBrush = FrozenBrush("#7B8490");

    public ICommand PlayPauseCommand => _playPause;
    public ICommand PreviousCommand => _previous;
    public ICommand NextCommand => _next;
    public ICommand LikeCommand => _like;

    public WidgetSize Size
    {
        get => _size;
        set
        {
            if (!Set(ref _size, value)) return;
            Changed(nameof(ShowAlbum), nameof(IsExpanded), nameof(ShowVolume));
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            Changed(nameof(CanSeek));
            RefreshCommands();
        }
    }

    public bool LikeEnabled
    {
        get => _likeEnabled;
        set { if (Set(ref _likeEnabled, value)) _like.RaiseCanExecuteChanged(); }
    }

    public bool? IsSaved
    {
        get => _isSaved;
        set
        {
            if (Set(ref _isSaved, value)) Changed(nameof(HeartFill), nameof(HeartStroke), nameof(SaveLabel));
        }
    }

    public string LikeHint
    {
        get => _likeHint;
        set => Set(ref _likeHint, value);
    }

    public string ConnectionLabel
    {
        get => _connectionLabel;
        set { if (Set(ref _connectionLabel, value)) Changed(nameof(StatusLabel)); }
    }

    public string? Notice
    {
        get => _notice;
        set { if (Set(ref _notice, value)) Changed(nameof(StatusLabel)); }
    }

    public double GlassOpacity
    {
        get => _glassOpacity;
        set => Set(ref _glassOpacity, Math.Clamp(value, .18, .95));
    }

    public bool IsSeeking
    {
        get => _isSeeking;
        set => Set(ref _isSeeking, value);
    }

    public bool IsAdjustingVolume
    {
        get => _isAdjustingVolume;
        set => Set(ref _isAdjustingVolume, value);
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (Set(ref _positionSeconds, Math.Clamp(value, 0, Math.Max(0, Snapshot.Duration.TotalSeconds))))
                Changed(nameof(PositionLabel), nameof(RemainingLabel));
        }
    }

    public double VolumePercent
    {
        get => _volumePercent;
        set => Set(ref _volumePercent, Math.Clamp(value, 0, 100));
    }

    public void ApplySnapshot(PlaybackSnapshot snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;
        if (snapshot.Artwork is not null)
        {
            if (!ReferenceEquals(previous.Artwork, snapshot.Artwork) || Artwork is null) SetArtwork(snapshot.Artwork);
        }
        else if (!snapshot.IsAvailable || previous.TrackUri != snapshot.TrackUri || previous.Title != snapshot.Title || previous.Artist != snapshot.Artist)
            SetArtwork(null);

        if (!IsAdjustingVolume) VolumePercent = snapshot.VolumePercent;
        Tick(snapshot.CapturedAt);
        Changed(nameof(Snapshot), nameof(Title), nameof(Artist), nameof(Album), nameof(IsPlaying), nameof(IsAvailable),
            nameof(ShowAlbum), nameof(ShowVolume), nameof(CanSeek), nameof(DurationSeconds), nameof(PlaybackHint),
            nameof(StatusLabel), nameof(StatusBrush), nameof(ArtworkHint));
        RefreshCommands();
    }

    public void SetArtwork(byte[]? bytes)
    {
        BitmapSource? image = null;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(bytes, false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 256;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // An unavailable cover should never interrupt playback controls.
            }
        }
        _artwork = image;
        Changed(nameof(Artwork), nameof(HasArtwork));
    }

    public void Tick(DateTimeOffset? now = null)
    {
        if (!IsSeeking) PositionSeconds = Snapshot.PositionAt(now ?? DateTimeOffset.UtcNow).TotalSeconds;
    }

    public void CommitSeek()
    {
        IsSeeking = false;
        if (CanSeek) SeekRequested?.Invoke(TimeSpan.FromSeconds(PositionSeconds));
    }

    public void CommitVolume()
    {
        IsAdjustingVolume = false;
        if (!Busy && Snapshot.CanSetVolume) VolumeRequested?.Invoke((int)Math.Round(VolumePercent));
    }

    private void RefreshCommands()
    {
        _playPause.RaiseCanExecuteChanged();
        _previous.RaiseCanExecuteChanged();
        _next.RaiseCanExecuteChanged();
        _like.RaiseCanExecuteChanged();
    }

    private static string FormatTime(TimeSpan time)
    {
        time = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static Brush FrozenBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
        return true;
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new(name));
    }

    private sealed class DelegateCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) => execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
