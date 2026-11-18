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
    private LensRefraction? _renderer;
    private WriteableBitmap? _bitmap;
    private byte[]? _output;
    private CaptureSize _size;
    private long _nextProbe;
    private bool _enabled;
    private bool _disposed;

    public bool? IsAvailable { get; private set; }
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
            SetAvailability(VerifySelfExclusion(marker));
        }
        if (IsAvailable != true)
        {
            _viewModel.RefractedBackground = null;
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var dpi = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        var padding = (int)Math.Ceiling(24 * dpi);
        if (!_capture!.TryCapture(rect.Left - padding, rect.Top - padding, width + padding * 2, height + padding * 2, out var pixels))
        {
            padding = 0;
            if (!_capture.TryCapture(rect.Left, rect.Top, width, height, out pixels))
            {
                Unavailable(now);
                return;
            }
        }

        var stride = (width + padding * 2) * 4;
        if (MatchesMarker(pixels, stride, height + padding * 2,
            marker.X - rect.Left + padding, marker.Y - rect.Top + padding, marker.Color))
        {
            Unavailable(now);
            return;
        }

        var size = new CaptureSize(width, height, padding, dpi);
        if (_renderer is null || size != _size)
        {
            _renderer = new LensRefraction(width, height, padding, dpi);
            _size = size;
            _bitmap = new WriteableBitmap(_renderer.OutputWidth, _renderer.OutputHeight, 96, 96, PixelFormats.Bgra32, null);
            _output = new byte[_renderer.OutputWidth * _renderer.OutputHeight * 4];
        }

        var outputStride = _renderer.OutputWidth * 4;
        _renderer.Render(pixels, stride, _output!, outputStride, soften: true);
        _bitmap!.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), _output!, outputStride, 0);
        _viewModel.RefractedBackground = _bitmap;
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

    private void Unavailable(long now)
    {
        _viewModel.RefractedBackground = null;
        _nextProbe = now + 1500;
        SetAvailability(false);
    }

    private void SetAvailability(bool? value)
    {
        if (IsAvailable == value) return;
        IsAvailable = value;
        AvailabilityChanged?.Invoke();
    }

    private void ReleaseFrames()
    {
        _viewModel.RefractedBackground = null;
        _capture?.Dispose();
        _probe?.Dispose();
        _capture = null;
        _probe = null;
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
    private readonly record struct Marker(int X, int Y, Color Color);
}
