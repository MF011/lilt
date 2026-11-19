using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Lilt.App.Platform;
using Lilt.App.Settings;
using Lilt.App.UI;
using Lilt.Core;
using Lilt.Spotify;

namespace Lilt.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SettingsStore _settingsStore = new();
    private readonly ArtworkCache _artwork = new();
    private WidgetSettings _settings = new();
    private WidgetViewModel _viewModel = null!;
    private MainWindow _widget = null!;
    private WindowBehavior _windowBehavior = null!;
    private GlassLens? _lens;
    private TrayService _tray = null!;
    private SpotifyMediaService _local = null!;
    private SpotifyClient _spotify = null!;
    private SettingsWindow? _accountWindow;
    private RefractionSettingsWindow? _refractionSettingsWindow;
    private DispatcherTimer? _positionTimer;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _saveTimer;
    private Mutex? _instance;
    private CancellationTokenSource? _signInCancellation;
    private Task? _signInTask;
    private Task<bool>? _restoreTask;
    private int _accountGeneration;
    private bool _accountActive;
    private PlaybackSnapshot _lastLocalSnapshot = PlaybackSnapshot.Empty;
    private bool _connecting;
    private bool _quitting;
    private bool _smokeTest;
    private string? _savedTrack;
    private bool? _savedValue;
    private DateTimeOffset _savedCheckedAt = DateTimeOffset.MinValue;
    private string _status = "Local controls";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _smokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        _instance = new Mutex(true, _smokeTest ? $"Local\\Lilt.Smoke.{Environment.ProcessId}" : "Local\\Lilt.Widget", out var firstInstance);
        if (!firstInstance) { Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            if (_smokeTest) { Shutdown(1); return; }
            args.Handled = true;
            ShowError(args.Exception);
        };
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            _settings = _smokeTest ? new() : _settingsStore.Load();
            System.Windows.Forms.Application.EnableVisualStyles();
            _viewModel = new WidgetViewModel
            {
                GlassOpacity = _settings.GlassOpacity,
                OpaqueBackground = !_settings.Glass
            };
            _widget = new MainWindow(_viewModel)
            {
                Width = _settings.Width, Height = _settings.Height,
                Topmost = _settings.Topmost
            };
            MainWindow = _widget;
            if (_settings.Left is { } left && _settings.Top is { } top)
            {
                _widget.WindowStartupLocation = WindowStartupLocation.Manual;
                _widget.Left = left;
                _widget.Top = top;
            }
            else
            {
                _widget.WindowStartupLocation = WindowStartupLocation.Manual;
                PlaceAtDefaultPosition();
            }
            _windowBehavior = new WindowBehavior(_widget);
            _windowBehavior.SetTopmost(_settings.Topmost);
            _windowBehavior.SetClickThrough(_settings.IgnoreClicks);
            _windowBehavior.ToggleVisibilityRequested += ToggleVisibility;
            _windowBehavior.ToggleClickThroughRequested += () => SetIgnoreClicks(!_settings.IgnoreClicks);
            _widget.DragRequested += _windowBehavior.TryBeginDrag;
            _widget.ResizeRequested += _windowBehavior.TryBeginResize;
            _widget.Closing += OnWidgetClosing;
            _widget.LocationChanged += (_, _) => QueueSave();
            _widget.SizeChanged += (_, _) => QueueSave();
            _tray = new TrayService();
            WireTray();
            _spotify = new SpotifyClient(new SecureTokenStore());
            _local = new SpotifyMediaService();
            _local.SnapshotChanged += OnLocalSnapshotChanged;
            _viewModel.ActionRequested += action => _ = ExecuteAsync(action);
            _viewModel.LikeRequested += () => _ = ToggleLikeAsync();
            _viewModel.SeekRequested += position => _ = SeekAsync(position);
            _viewModel.VolumeRequested += percent => _ = SetVolumeAsync(percent);
            _widget.Show();
            _windowBehavior.RestoreToVisibleScreen();
            _lens = new GlassLens(_widget, _viewModel, _windowBehavior);
            _lens.SetStyle(_settings.RefractionStyle);
            _lens.AvailabilityChanged += UpdateTray;
            if (!_smokeTest) _lens.SetEnabled(_settings.Glass && _settings.Refraction);
            UpdateTray();
            _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                (_, _) => _viewModel.Tick(), Dispatcher);
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };

            if (_smokeTest)
            {
                await VerifyTokenProtectionAsync();
                await NativeSmokeChecks.RunAsync(_widget, _viewModel, _windowBehavior);
                // Exercise native initialization without a Spotify account or persistent changes.
                _windowBehavior.SetClickThrough(true);
                _windowBehavior.SetClickThrough(false);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += async (_, _) => { timer.Stop(); await QuitAsync(); };
                timer.Start();
                return;
            }

            await _local.StartAsync(_lifetime.Token);
            ApplySnapshot(_local.Snapshot, account: false);
            try
            {
                _restoreTask = _spotify.RestoreSessionAsync(_lifetime.Token);
                await _restoreTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log(ex); _status = "Local controls · account unavailable"; }
            await RefreshAccountAsync();
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _pollTimer.Tick += async (_, _) =>
            {
                _pollTimer.Interval = _widget.IsVisible && _viewModel.Snapshot.IsPlaying
                    ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(_widget.IsVisible ? 45 : 90);
                await RefreshAccountAsync();
            };
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            Log(ex);
            if (!_smokeTest)
                MessageBox.Show("Lilt couldn't start. Details are saved in %LocalAppData%\\Lilt\\lilt.log.", "Lilt", MessageBoxButton.OK, MessageBoxImage.Error);
            await QuitAsync(1);
        }
    }

    private void WireTray()
    {
        _tray.ToggleVisibilityRequested += ToggleVisibility;
        _tray.IgnoreClicksChanged += SetIgnoreClicks;
        _tray.TopmostChanged += value => { _settings = _settings with { Topmost = value }; _windowBehavior.SetTopmost(value); SaveSettings(); UpdateTray(); };
        _tray.GlassChanged += value =>
        {
            _settings = _settings with { Glass = value };
            _viewModel.OpaqueBackground = !value;
            _lens?.SetEnabled(value && _settings.Refraction);
            SaveSettings();
            UpdateTray();
        };
        _tray.RefractionChanged += value =>
        {
            _settings = _settings with { Refraction = value };
            _lens?.SetEnabled(_settings.Glass && value);
            SaveSettings();
            UpdateTray();
        };
        _tray.RefractionSettingsRequested += OpenRefractionSettings;
        _tray.OpacityChanged += value =>
        {
            _settings = (_settings with { GlassOpacity = value }).Validated();
            _viewModel.GlassOpacity = _settings.GlassOpacity;
            SaveSettings(); UpdateTray();
        };
        _tray.StartupChanged += value =>
        {
            try { StartupRegistration.SetEnabled(value); }
            catch (Exception ex) { ShowError(ex); }
            UpdateTray();
        };
        _tray.SettingsRequested += OpenAccount;
        _tray.ConnectRequested += OpenAccount;
        _tray.DisconnectRequested += () => _ = RunCommandAsync(DisconnectAsync);
        _tray.OpenSpotifyRequested += OpenSpotify;
        _tray.ResetPositionRequested += ResetPosition;
        _tray.ExitRequested += () => _ = QuitAsync();
    }

    private void OpenRefractionSettings()
    {
        if (_refractionSettingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new RefractionSettingsWindow(_settings.RefractionStyle);
        _refractionSettingsWindow = window;
        window.OptionsChanged += style =>
        {
            _settings = _settings with { RefractionStyle = style };
            _lens?.SetStyle(style);
            QueueSave();
        };
        window.Closed += (_, _) => _refractionSettingsWindow = null;
        window.Show();
    }

    private void OnLocalSnapshotChanged(object? sender, PlaybackSnapshot snapshot)
    {
        if (_quitting) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_quitting) return;
            var previous = _lastLocalSnapshot;
            _lastLocalSnapshot = snapshot;
            var mediaChanged = previous.Title != snapshot.Title || previous.Artist != snapshot.Artist ||
                previous.Album != snapshot.Album || previous.IsPlaying != snapshot.IsPlaying || previous.IsAvailable != snapshot.IsAvailable;
            if (!_accountActive) ApplySnapshot(snapshot, account: false);
            // Timeline ticks animate locally; they must not turn into Web API polling.
            if (_spotify.IsConnected && mediaChanged)
                _ = RefreshAccountAsync();
        });
    }

    private async Task RefreshAccountAsync()
    {
        if (_quitting || _connecting || _spotify is null || !await _refreshGate.WaitAsync(0)) return;
        var generation = _accountGeneration;
        try
        {
            if (!_spotify.IsConnected)
            {
                _accountActive = false;
                _status = "Local controls";
                ApplySnapshot(_local.Snapshot, account: false);
                return;
            }
            if (_spotify.RetryAfterUtc is { } retryAfter && retryAfter > DateTimeOffset.UtcNow)
            {
                UseLocalFallback($"Local controls · account retry at {retryAfter.LocalDateTime:HH:mm}");
                return;
            }
            var snapshot = await _spotify.GetPlaybackAsync(_lifetime.Token);
            if (_quitting || generation != _accountGeneration || !_spotify.IsConnected) return;
            _accountActive = snapshot is { IsAvailable: true };
            _status = _accountActive ? $"Spotify · {snapshot!.DeviceName}" : "Local controls · Spotify connected";
            ApplySnapshot(_accountActive ? snapshot! : _local.Snapshot, _accountActive);
            if (_accountActive) await RefreshLikeAsync(snapshot!.TrackUri);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != _accountGeneration || _quitting) return;
            Log(ex);
            UseLocalFallback(ex is SpotifyAuthorizationException ? "Local controls · reconnect Spotify" : "Local controls · account unavailable");
        }
        finally { _refreshGate.Release(); if (!_quitting) UpdateTray(); }
    }

    private void ApplySnapshot(PlaybackSnapshot snapshot, bool account)
    {
        var previous = _viewModel.Snapshot;
        _viewModel.ApplySnapshot(snapshot);
        _viewModel.ConnectionLabel = account ? snapshot.DeviceName : "This PC";
        _viewModel.LikeEnabled = account && snapshot.TrackUri is not null && _savedTrack == snapshot.TrackUri && _savedValue.HasValue;
        _viewModel.IsSaved = account && _savedTrack == snapshot.TrackUri ? _savedValue : null;
        _viewModel.LikeHint = account
            ? snapshot.TrackUri is null ? "Likes aren't available for this item" : _viewModel.IsSaved == true ? "Remove from Liked Songs" : "Save to Liked Songs"
            : "Connect Spotify from the tray to use likes";
        if (snapshot.ArtworkUrl is { } url && (url != previous.ArtworkUrl || !_viewModel.HasArtwork))
            _ = LoadArtworkAsync(url);
        UpdateTray();
    }

    private async Task LoadArtworkAsync(string url)
    {
        try
        {
            var bytes = await _artwork.GetAsync(url, _lifetime.Token);
            if (!_quitting && _viewModel.Snapshot.ArtworkUrl == url && bytes is not null) _viewModel.SetArtwork(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { }
    }

    private async Task RefreshLikeAsync(string? uri)
    {
        if (uri is null || (_savedTrack == uri && _savedValue.HasValue && DateTimeOffset.UtcNow - _savedCheckedAt < TimeSpan.FromSeconds(60))) return;
        var generation = _accountGeneration;
        _savedTrack = uri;
        _savedValue = null;
        _viewModel.LikeEnabled = false;
        try
        {
            var saved = await _spotify.IsSavedAsync(uri, _lifetime.Token);
            if (!_quitting && generation == _accountGeneration && _accountActive && _viewModel.Snapshot.TrackUri == uri)
            {
                _savedValue = saved;
                _savedCheckedAt = DateTimeOffset.UtcNow;
                _viewModel.IsSaved = saved;
                _viewModel.LikeEnabled = true;
                _viewModel.LikeHint = saved ? "Remove from Liked Songs" : "Save to Liked Songs";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_quitting || generation != _accountGeneration) return;
            Log(ex);
            if (ex is SpotifyApiException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
                UseLocalFallback("Local controls · Spotify account is busy");
            else _viewModel.LikeHint = "Couldn't check Liked Songs. Try again shortly.";
        }
    }

    private Task ExecuteAsync(PlaybackAction action)
    {
        var target = _viewModel.Snapshot;
        var account = _accountActive;
        return RunCommandAsync(async () =>
        {
            if (account) await _spotify.ExecuteAsync(action, target, _lifetime.Token);
            else await _local.ExecuteAsync(action, _lifetime.Token);
            if (account) { await Task.Delay(200, _lifetime.Token); await RefreshAccountAsync(); }
        });
    }

    private Task SeekAsync(TimeSpan position)
    {
        var target = _viewModel.Snapshot;
        var account = _accountActive;
        return RunCommandAsync(async () =>
        {
            if (account) await _spotify.SeekAsync(position, target, _lifetime.Token);
            else await _local.SeekAsync(position, _lifetime.Token);
            if (account) await RefreshAccountAsync();
        });
    }

    private Task SetVolumeAsync(int percent)
    {
        var target = _viewModel.Snapshot;
        if (!_accountActive) return Task.CompletedTask;
        return RunCommandAsync(async () =>
        {
            await _spotify.SetVolumeAsync(percent, target, _lifetime.Token);
            await RefreshAccountAsync();
        });
    }

    private Task ToggleLikeAsync()
    {
        var target = _viewModel.Snapshot;
        if (!_accountActive || !_viewModel.LikeEnabled || target.TrackUri is not { } uri || !_savedValue.HasValue)
            return Task.CompletedTask;
        var next = !_savedValue.Value;
        return RunCommandAsync(async () =>
        {
            await _spotify.SetSavedAsync(uri, next, _lifetime.Token);
            if (_accountActive && _viewModel.Snapshot.TrackUri == uri)
            {
                _savedTrack = uri; _savedValue = next;
                _savedCheckedAt = DateTimeOffset.UtcNow;
                _viewModel.IsSaved = next;
                _viewModel.LikeHint = next ? "Remove from Liked Songs" : "Save to Liked Songs";
            }
        });
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        if (_quitting || !await _commandGate.WaitAsync(0)) return;
        _viewModel.Busy = true;
        try { _viewModel.Notice = null; await command(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _viewModel.Busy = false; _commandGate.Release(); }
    }

    private void OpenAccount()
    {
        if (_accountWindow is { IsVisible: true }) { _accountWindow.Activate(); return; }
        _accountWindow = new SettingsWindow(_spotify.ClientId, _spotify.IsConnected, ConnectAsync, DisconnectAsync);
        _accountWindow.Closed += (_, _) => { _signInCancellation?.Cancel(); _accountWindow = null; };
        _accountWindow.Show();
    }

    private async Task ConnectAsync(string clientId)
    {
        _accountGeneration++;
        _connecting = true;
        UseLocalFallback("Local controls · connecting Spotify");
        _signInCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _signInCancellation = cancellation;
        try
        {
            var attempt = _spotify.ConnectAsync(clientId, cancellation.Token);
            _signInTask = attempt;
            await attempt;
            _savedTrack = null; _savedValue = null;
            await _refreshGate.WaitAsync(cancellation.Token);
            _refreshGate.Release();
            _connecting = false;
            await RefreshAccountAsync();
        }
        finally
        {
            if (ReferenceEquals(_signInCancellation, cancellation))
            {
                _signInCancellation = null;
                _signInTask = null;
                _connecting = false;
                UpdateTray();
            }
        }
    }

    private async Task DisconnectAsync()
    {
        _accountGeneration++;
        _connecting = false;
        _signInCancellation?.Cancel();
        try
        {
            await _spotify.DisconnectAsync(_lifetime.Token);
        }
        finally
        {
            _accountActive = false; _savedTrack = null; _savedValue = null;
            _status = "Local controls";
            ApplySnapshot(_local.Snapshot, account: false);
        }
    }

    private void ToggleVisibility()
    {
        if (_widget.IsVisible) _widget.Hide();
        else { _widget.Show(); _windowBehavior.RestoreToVisibleScreen(); }
        UpdateTray();
    }

    private void SetIgnoreClicks(bool enabled)
    {
        _settings = _settings with { IgnoreClicks = enabled };
        _windowBehavior.SetClickThrough(enabled);
        SaveSettings(); UpdateTray();
    }

    private void ResetPosition()
    {
        var defaults = new WidgetSettings();
        _widget.Width = defaults.Width; _widget.Height = defaults.Height;
        PlaceAtDefaultPosition();
        _widget.Show(); _windowBehavior.RestoreToVisibleScreen(); SaveSettings(); UpdateTray();
    }

    private void PlaceAtDefaultPosition()
    {
        var workArea = SystemParameters.WorkArea;
        // Leave room to pull the bottom-right grip from compact into the expanded layout.
        _widget.Left = Math.Max(workArea.Left, workArea.Right - _widget.Width - 120);
        _widget.Top = Math.Max(workArea.Top, workArea.Bottom - _widget.Height - 120);
    }

    private static void OpenSpotify()
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Spotify isn't installed, or Windows couldn't open it.", "Lilt", MessageBoxButton.OK, MessageBoxImage.Information); Log(ex); }
    }

    private void UpdateTray()
    {
        if (_quitting || _tray is null || _widget is null) return;
        bool startup;
        try { startup = StartupRegistration.IsEnabled(); } catch { startup = false; }
        _tray.SetState(_widget.IsVisible, _settings.IgnoreClicks, _settings.Topmost, _spotify?.IsConnected == true,
            _status, _settings.Glass, startup, _settings.GlassOpacity, _settings.Refraction, _lens?.IsAvailable);
    }

    private void QueueSave()
    {
        if (_saveTimer is null || _quitting) return;
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void SaveSettings()
    {
        if (_smokeTest || _widget is null) return;
        try
        {
            _settings = (_settings with { Width = _widget.Width, Height = _widget.Height, Left = _widget.Left, Top = _widget.Top }).Validated();
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(ex); }
    }

    private void OnWidgetClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true; _widget.Hide(); UpdateTray();
    }

    private void ShowError(Exception exception)
    {
        Log(exception);
        if (exception is SpotifyApiException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
            UseLocalFallback("Local controls · Spotify account is busy");
        var message = exception is SpotifyException or InvalidOperationException
            ? exception.Message : "Couldn't finish that action. Try again.";
        if (message.Length > 180) message = message[..180];
        if (_viewModel is not null) _viewModel.Notice = message;
        _tray?.SetStatus(message);
    }

    private void UseLocalFallback(string status)
    {
        _accountActive = false;
        _status = status;
        if (!_quitting) ApplySnapshot(_local.Snapshot, account: false);
    }

    private static async Task VerifyTokenProtectionAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lilt-smoke-{Guid.NewGuid():N}");
        try
        {
            var store = new SecureTokenStore(directory);
            var sample = new SpotifySession("smoke-client", "smoke-access", "smoke-refresh", DateTimeOffset.UtcNow.AddHours(1));
            await store.SaveAsync(sample);
            if (await store.LoadAsync() != sample) throw new InvalidOperationException("Credential storage check failed.");
            await store.ClearAsync();
            if (await store.LoadAsync() is not null) throw new InvalidOperationException("Credential cleanup check failed.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void Log(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var file = Path.Combine(SettingsStore.DataDirectory, "lilt.log");
            if (File.Exists(file) && new FileInfo(file).Length > 262144) File.WriteAllText(file, "");
            // Log types only. Tokens, callback URLs and Spotify response bodies stay out of logs.
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} {exception.GetType().Name}{Environment.NewLine}");
        }
        catch { }
    }

    private async Task QuitAsync(int exitCode = 0)
    {
        if (_quitting) return;
        _quitting = true;
        _positionTimer?.Stop(); _pollTimer?.Stop(); _saveTimer?.Stop();
        SaveSettings();
        _lifetime.Cancel();
        _signInCancellation?.Cancel();
        _accountWindow?.Close();
        _refractionSettingsWindow?.Close();
        _tray?.Dispose();
        _lens?.Dispose();
        _windowBehavior?.Dispose();
        await _refreshGate.WaitAsync();
        await _commandGate.WaitAsync();
        if (_signInTask is { } signIn) { try { await signIn; } catch (Exception) { } }
        if (_restoreTask is { } restore) { try { await restore; } catch (Exception) { } }
        if (_local is not null) await _local.DisposeAsync();
        _spotify?.Dispose();
        _artwork.Dispose();
        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lens?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
