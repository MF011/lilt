using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lilt.App.Platform;
using Lilt.Core;

namespace Lilt.App.UI;

internal sealed class GlassLens : IDisposable
{
    private readonly MainWindow _window;
    private readonly WidgetViewModel _viewModel;
    private readonly WindowBehavior _behavior;
    private readonly DispatcherTimer _timer;
    private DesktopLensCapture? _capture;
    private DesktopLensCapture? _probe;
    private WindowLensCapture? _windowCapture;
    private DuplicateLensCapture? _duplicate;
    private IntPtr _excludedWindow;
    private Task<WindowFrame>? _pending;
    private LensRefraction? _renderer;
    private WriteableBitmap? _bitmap;
    private byte[]? _output;
    private CaptureSize _size;
    private long _nextProbe;
    private long _retryWindowAt;
    private long _windowRequestedAt;
    private int _requestGeneration;
    private bool _desktopExcludesWidget;
    private bool _enabled;
    private bool _disposed;

    public bool? IsAvailable { get; private set; }
    internal string? CaptureSource { get; private set; }
    public event Action? AvailabilityChanged;

    public GlassLens(MainWindow window, WidgetViewModel viewModel, WindowBehavior behavior)
    {
        _window = window;
        _viewModel = viewModel;
        _behavior = behavior;
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };
        _timer.Tick += Render;
        window.IsVisibleChanged += OnVisibilityChanged;
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || enabled == _enabled) return;
        _enabled = enabled;
        UpdateTimer();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTimer();

    private void UpdateTimer()
    {
        if (_enabled && _window.IsVisible)
        {
            _capture ??= new DesktopLensCapture();
            _probe ??= new DesktopLensCapture();
            _nextProbe = 0;
            _retryWindowAt = 0;
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            ReleaseFrames();
            SetAvailability(null);
        }
    }

    private void Render(object? sender, EventArgs args)
    {
        if (_behavior.IsGestureActive)
        {
            _viewModel.RefractedBackground = null;
            _nextProbe = 0;
            _requestGeneration++;
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (!NativeMethods.GetWindowRect(handle, out var rect) || !TryGetMarker(out var marker))
        {
            _viewModel.RefractedBackground = null;
            return;
        }

        var now = Environment.TickCount64;
        if (now >= _nextProbe)
        {
            _nextProbe = now + 1500;
            _desktopExcludesWidget = VerifySelfExclusion(marker);
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var dpi = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        if (width < 2 || height < 2) return;
        var padding = (int)Math.Ceiling(24 * dpi);
        if (_desktopExcludesWidget && TryCaptureDesktop(rect, padding, marker, out var pixels, out var usedPadding))
        {
            Paint(pixels, new CaptureSize(width, height, usedPadding, dpi), "Desktop tile");
            return;
        }
        if (_excludedWindow == IntPtr.Zero && _duplicate is null)
        {
            // A failed exclusion attempt is sticky for this enabled session.
            _excludedWindow = DuplicateLensCapture.MarkExcluded(handle, true) ? handle : new IntPtr(-1);
        }
        if (_excludedWindow == handle) _duplicate ??= new DuplicateLensCapture();
        RenderWindowBehind(new WindowRequest(handle, rect, new CaptureSize(width, height, padding, dpi), marker, _requestGeneration), now);
    }

    private bool TryCaptureDesktop(NativeMethods.Rect rect, int padding, Marker marker, out byte[] pixels, out int usedPadding)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        usedPadding = padding;
        if (!_capture!.TryCapture(rect.Left - padding, rect.Top - padding, width + padding * 2, height + padding * 2, out pixels))
        {
            usedPadding = 0;
            if (!_capture.TryCapture(rect.Left, rect.Top, width, height, out pixels))
                return false;
        }

        if (!MatchesMarker(pixels, (width + usedPadding * 2) * 4, height + usedPadding * 2,
            marker.X - rect.Left + usedPadding, marker.Y - rect.Top + usedPadding, marker.Color)) return true;
        _desktopExcludesWidget = false;
        return false;
    }

    private void RenderWindowBehind(WindowRequest request, long now)
    {
        if (_pending is { } pending)
        {
            if (!pending.IsCompleted)
            {
                if (now - _windowRequestedAt > 600)
                {
                    _viewModel.RefractedBackground = null;
                    SetAvailability(false);
                }
                return;
            }
            _pending = null;
            var completed = pending.GetAwaiter().GetResult();
            if (completed.Request == request)
            {
                if (completed.Pixels.Length > 0)
                    Paint(completed.Pixels, request.Size with { Padding = completed.Padding }, completed.Source!);
                else
                {
                    _viewModel.RefractedBackground = null;
                    SetAvailability(false);
                    _retryWindowAt = now + 350;
                }
            }
        }

        if (now < _retryWindowAt) return;
        var capture = _windowCapture ??= new WindowLensCapture();
        var duplicate = _duplicate;
        // Responding to a print request runs on the source app's thread. Keep it
        // off Lilt's dispatcher, with at most one in-flight capture.
        _windowRequestedAt = now;
        _pending = Task.Run(() => CaptureWindow(duplicate, capture, request));
    }

    private static WindowFrame CaptureWindow(DuplicateLensCapture? duplicate, WindowLensCapture capture, WindowRequest request)
    {
        var size = request.Size;
        var rect = request.Rect;
        if (duplicate is not null && duplicate.TryCapture(rect.Left, rect.Top, size.Width, size.Height, out var tile) &&
            !MatchesMarker(tile, size.Width * 4, size.Height, request.Marker.X - rect.Left, request.Marker.Y - rect.Top, request.Marker.Color) &&
            HasVisiblePixels(tile, size.Width, size.Height))
            return new WindowFrame(request, 0, tile, "Display duplication");

        var padding = size.Padding;
        if (capture.TryCapture(request.Handle, rect.Left - padding, rect.Top - padding,
            size.Width + padding * 2, size.Height + padding * 2, out var pixels))
            return new WindowFrame(request, padding, pixels, "Window sample");
        return capture.TryCapture(request.Handle, rect.Left, rect.Top, size.Width, size.Height, out pixels)
            ? new WindowFrame(request, 0, pixels, "Window sample")
            : new WindowFrame(request, 0, [], null);
    }

    private static bool HasVisiblePixels(byte[] pixels, int width, int height)
    {
        // Some drivers mask an excluded surface rather than revealing the backdrop.
        // Reject a black tile, allowing the per-window fallback to try instead.
        var pixelsFound = 0;
        for (var y = height / 4; y < height * 3 / 4; y += 4)
        for (var x = width / 4; x < width * 3 / 4; x += 4)
        {
            var i = (y * width + x) * 4;
            if ((pixels[i] | pixels[i + 1] | pixels[i + 2]) > 8 && ++pixelsFound >= 6) return true;
        }
        return false;
    }

    private void Paint(byte[] pixels, CaptureSize size, string source)
    {
        if (_renderer is null || size != _size)
        {
            _renderer = new LensRefraction(size.Width, size.Height, size.Padding, size.Dpi);
            _size = size;
            _bitmap = new WriteableBitmap(_renderer.OutputWidth, _renderer.OutputHeight, 96, 96, PixelFormats.Bgra32, null);
            _output = new byte[_renderer.OutputWidth * _renderer.OutputHeight * 4];
        }

        var outputStride = _renderer.OutputWidth * 4;
        _renderer.Render(pixels, _renderer.SourceWidth * 4, _output!, outputStride, soften: true);
        _bitmap!.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), _output!, outputStride, 0);
        _viewModel.RefractedBackground = _bitmap;
        CaptureSource = source;
        SetAvailability(true);
    }

    private bool TryGetMarker(out Marker marker)
    {
        // The opaque playback dot is already part of the card. It gives us a known
        // color and position to tell whether Windows included our own layer.
        var dot = _window.StatusMarker;
        if (!dot.IsVisible || dot.ActualWidth < 2 || dot.ActualHeight < 2 || dot.Fill is not SolidColorBrush brush)
        {
            marker = default;
            return false;
        }
        var screen = dot.PointToScreen(new Point(dot.ActualWidth * .5, dot.ActualHeight * .5));
        marker = new Marker((int)Math.Round(screen.X), (int)Math.Round(screen.Y), brush.Color);
        return true;
    }

    private bool VerifySelfExclusion(Marker marker)
    {
        const int side = 8;
        var x = marker.X - side / 2;
        var y = marker.Y - side / 2;
        if (!_probe!.TryCapture(x, y, side, side, out var sample, includeLayered: true) ||
            !MatchesMarker(sample, side * 4, side, side / 2, side / 2, marker.Color))
            return false;
        return _probe.TryCapture(x, y, side, side, out sample) &&
            !MatchesMarker(sample, side * 4, side, side / 2, side / 2, marker.Color);
    }

    private static bool MatchesMarker(byte[] pixels, int stride, int height, int x, int y, Color color)
    {
        var matches = 0;
        for (var row = Math.Max(0, y - 1); row <= Math.Min(height - 1, y + 1); row++)
        {
            for (var column = Math.Max(0, x - 1); column <= Math.Min(stride / 4 - 1, x + 1); column++)
            {
                var offset = row * stride + column * 4;
                if (Math.Abs(pixels[offset] - color.B) <= 12 && Math.Abs(pixels[offset + 1] - color.G) <= 12 &&
                    Math.Abs(pixels[offset + 2] - color.R) <= 12 && ++matches >= 3)
                    return true;
            }
        }
        return false;
    }

    private void SetAvailability(bool? value)
    {
        if (IsAvailable == value) return;
        IsAvailable = value;
        AvailabilityChanged?.Invoke();
    }

    private void ReleaseFrames()
    {
        _requestGeneration++;
        CaptureSource = null;
        _viewModel.RefractedBackground = null;
        _capture?.Dispose();
        _probe?.Dispose();
        _capture = null;
        _probe = null;
        if (_excludedWindow != IntPtr.Zero && _excludedWindow != new IntPtr(-1))
            DuplicateLensCapture.MarkExcluded(_excludedWindow, false);
        _excludedWindow = IntPtr.Zero;
        var duplicate = _duplicate;
        var capture = _windowCapture;
        if (duplicate is not null || capture is not null)
        {
            if (_pending is { IsCompleted: false } task)
                _ = task.ContinueWith(_ => { capture?.Dispose(); duplicate?.Dispose(); }, TaskScheduler.Default);
            else
            {
                capture?.Dispose();
                duplicate?.Dispose();
            }
            _windowCapture = null;
            _duplicate = null;
        }
        _renderer = null;
        _output = null;
        _bitmap = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _timer.Stop();
        _timer.Tick -= Render;
        ReleaseFrames();
    }

    private readonly record struct CaptureSize(int Width, int Height, int Padding, double Dpi);
    private readonly record struct WindowRequest(IntPtr Handle, NativeMethods.Rect Rect, CaptureSize Size, Marker Marker, int Generation);
    private readonly record struct WindowFrame(WindowRequest Request, int Padding, byte[] Pixels, string? Source);
    private readonly record struct Marker(int X, int Y, Color Color);
}
