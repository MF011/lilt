using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace Lilt.App.Platform;

public sealed class WindowBehavior : IDisposable
{
    private const int VisibilityHotkey = 0x4C11;
    private const int ClickThroughHotkey = 0x4C12;
    private const int LeftMouseButton = 0x01;
    private readonly Window _window;
    private readonly DispatcherTimer _gestureTimer;
    private IntPtr _handle;
    private HwndSource? _source;
    private bool _disposed;
    private bool _ignoreClicks;
    private bool _topmost = true;
    private Gesture _gesture;
    private NativeMethods.Rect _startRect;
    private NativeMethods.Point _startPointer;
    private int _minimumWidth;
    private int _minimumHeight;
    private int _maximumWidth;
    private int _maximumHeight;

    [Flags]
    private enum Gesture
    {
        None = 0, Move = 1, Left = 2, Top = 4, Right = 8, Bottom = 16,
        TopLeft = Top | Left, TopRight = Top | Right,
        BottomLeft = Bottom | Left, BottomRight = Bottom | Right
    }

    public event Action? ToggleVisibilityRequested;
    public event Action? ToggleClickThroughRequested;
    public bool VisibilityHotkeyRegistered { get; private set; }
    public bool ClickThroughHotkeyRegistered { get; private set; }
    public bool IsGestureActive => _gesture != Gesture.None;
    internal bool IsResizing => _gesture != Gesture.None && _gesture != Gesture.Move;

    public WindowBehavior(Window window)
    {
        _window = window;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        _gestureTimer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _gestureTimer.Tick += OnGestureTick;
        window.SourceInitialized += OnSourceInitialized;
        window.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        window.PreviewMouseMove += OnPreviewMouseMove;
        window.IsVisibleChanged += OnIsVisibleChanged;
        window.Closed += OnClosed;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Initialize();
    }

    public void SetClickThrough(bool enabled)
    {
        if (enabled) EndGesture(constrain: true);
        _ignoreClicks = enabled;
        if (_handle == IntPtr.Zero) return;
        var style = NativeMethods.GetExStyle(_handle).ToInt64();
        style = enabled ? style | NativeMethods.WsExTransparent : style & ~NativeMethods.WsExTransparent;
        NativeMethods.SetExStyle(_handle, style);
    }

    public void SetTopmost(bool enabled)
    {
        _topmost = enabled;
        _window.Topmost = enabled;
        if (_handle != IntPtr.Zero)
            NativeMethods.SetWindowPos(_handle, new IntPtr(enabled ? -1 : -2), 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    public void TryBeginDrag() => BeginGesture(Gesture.Move);
    public void TryBeginResize() => BeginGesture(Gesture.BottomRight);

    public void RestoreToVisibleScreen()
    {
        if (_handle == IntPtr.Zero || !NativeMethods.GetWindowRect(_handle, out var rect)) return;
        var bounds = new System.Drawing.Rectangle(rect.Left, rect.Top,
            Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
        var area = Screen.FromRectangle(bounds).WorkingArea;
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        var x = Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - width));
        var y = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        NativeMethods.SetWindowPos(_handle, IntPtr.Zero, x, y, width, height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Initialize();
    private void OnClosed(object? sender, EventArgs e) => Dispose();
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_window.IsVisible) EndGesture(constrain: false);
    }

    private void Initialize()
    {
        if (_disposed || _handle != IntPtr.Zero) return;
        _handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        if (_source?.CompositionTarget is not { UsesPerPixelOpacity: true } target)
            throw new InvalidOperationException("The widget requires a transparent WPF window.");
        target.BackgroundColor = Colors.Transparent;
        _source.AddHook(WindowProc);
        var style = NativeMethods.GetExStyle(_handle).ToInt64();
        NativeMethods.SetExStyle(_handle,
            (style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate) & ~NativeMethods.WsExAppWindow);
        VisibilityHotkeyRegistered = NativeMethods.RegisterHotKey(_handle, VisibilityHotkey,
            NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, 0x4C);
        ClickThroughHotkeyRegistered = NativeMethods.RegisterHotKey(_handle, ClickThroughHotkey,
            NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat, 0x4C);
        SetTopmost(_topmost);
        SetClickThrough(_ignoreClicks);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmMouseActivate)
        {
            handled = true;
            return new IntPtr(NativeMethods.MaNoActivate);
        }
        if (message == NativeMethods.WmHotkey)
        {
            if (wParam.ToInt32() == VisibilityHotkey) ToggleVisibilityRequested?.Invoke();
            else if (wParam.ToInt32() == ClickThroughHotkey) ToggleClickThroughRequested?.Invoke();
            else return IntPtr.Zero;
            handled = true;
        }
        else if (message == NativeMethods.WmCancelMode) EndGesture(constrain: false);
        else if (message == NativeMethods.WmDisplayChange)
            _window.Dispatcher.BeginInvoke(new Action(RestoreToVisibleScreen));
        return IntPtr.Zero;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_ignoreClicks || _gesture != Gesture.None) return;
        var edge = ResizeEdgesAt(e.GetPosition(_window));
        if (edge == Gesture.None) return;
        BeginGesture(edge);
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_ignoreClicks || _gesture != Gesture.None) return;
        _window.Cursor = CursorFor(ResizeEdgesAt(e.GetPosition(_window)));
    }

    private Gesture ResizeEdgesAt(System.Windows.Point point)
    {
        const double edge = 6;
        const double corner = 24;
        var width = _window.ActualWidth;
        var height = _window.ActualHeight;
        if (point.X < 0 || point.Y < 0 || point.X > width || point.Y > height) return Gesture.None;
        var left = point.X <= edge;
        var right = point.X >= width - edge;
        var top = point.Y <= edge;
        var bottom = point.Y >= height - edge;
        if ((left && point.Y <= corner) || (top && point.X <= corner)) return Gesture.TopLeft;
        if ((right && point.Y <= corner) || (top && point.X >= width - corner)) return Gesture.TopRight;
        if ((left && point.Y >= height - corner) || (bottom && point.X <= corner)) return Gesture.BottomLeft;
        if ((right && point.Y >= height - corner) || (bottom && point.X >= width - corner)) return Gesture.BottomRight;
        if (left) return Gesture.Left;
        if (right) return Gesture.Right;
        if (top) return Gesture.Top;
        if (bottom) return Gesture.Bottom;
        return Gesture.None;
    }

    private void BeginGesture(Gesture gesture)
    {
        if (_disposed || _ignoreClicks || !_window.IsVisible || _handle == IntPtr.Zero ||
            NativeMethods.GetAsyncKeyState(LeftMouseButton) >= 0 ||
            !NativeMethods.GetCursorPos(out var pointer) || !NativeMethods.GetWindowRect(_handle, out var rect)) return;
        EndGesture(constrain: false);
        _startPointer = pointer;
        _startRect = rect;
        var dpi = VisualTreeHelper.GetDpi(_window);
        _minimumWidth = Math.Max(1, (int)Math.Ceiling(_window.MinWidth * dpi.DpiScaleX));
        _minimumHeight = Math.Max(1, (int)Math.Ceiling(_window.MinHeight * dpi.DpiScaleY));
        var area = Screen.FromRectangle(new System.Drawing.Rectangle(rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top)).WorkingArea;
        _maximumWidth = MaximumPixels(_window.MaxWidth, dpi.DpiScaleX, area.Width, _minimumWidth);
        _maximumHeight = MaximumPixels(_window.MaxHeight, dpi.DpiScaleY, area.Height, _minimumHeight);
        _gesture = gesture;
        _window.Cursor = gesture == Gesture.Move ? Cursors.SizeAll : CursorFor(gesture);
        Mouse.Capture(null);
        NativeMethods.SetCapture(_handle);
        _gestureTimer.Start();
    }

    private static int MaximumPixels(double value, double scale, int available, int minimum)
    {
        var maximum = double.IsFinite(value) ? Math.Min(value * scale, available) : available;
        return Math.Max(minimum, (int)Math.Floor(maximum));
    }

    private void OnGestureTick(object? sender, EventArgs e)
    {
        if (_gesture == Gesture.None) return;
        if (_disposed || !_window.IsVisible || _ignoreClicks)
        {
            EndGesture(constrain: false);
            return;
        }
        if (NativeMethods.GetAsyncKeyState(LeftMouseButton) >= 0)
        {
            UpdateGesture();
            EndGesture(constrain: true);
            return;
        }
        UpdateGesture();
    }

    private void UpdateGesture()
    {
        if (!NativeMethods.GetCursorPos(out var pointer)) return;
        var dx = pointer.X - _startPointer.X;
        var dy = pointer.Y - _startPointer.Y;
        var rect = _startRect;
        if (_gesture == Gesture.Move)
        {
            rect.Left += dx;
            rect.Right += dx;
            rect.Top += dy;
            rect.Bottom += dy;
        }
        else
        {
            if ((_gesture & Gesture.Left) != 0)
                rect.Left = Math.Clamp(_startRect.Left + dx, rect.Right - _maximumWidth, rect.Right - _minimumWidth);
            if ((_gesture & Gesture.Right) != 0)
                rect.Right = Math.Clamp(_startRect.Right + dx, rect.Left + _minimumWidth, rect.Left + _maximumWidth);
            if ((_gesture & Gesture.Top) != 0)
                rect.Top = Math.Clamp(_startRect.Top + dy, rect.Bottom - _maximumHeight, rect.Bottom - _minimumHeight);
            if ((_gesture & Gesture.Bottom) != 0)
                rect.Bottom = Math.Clamp(_startRect.Bottom + dy, rect.Top + _minimumHeight, rect.Top + _maximumHeight);
        }
        if (NativeMethods.GetWindowRect(_handle, out var current) &&
            current.Left == rect.Left && current.Top == rect.Top && current.Right == rect.Right && current.Bottom == rect.Bottom) return;
        NativeMethods.SetWindowPos(_handle, IntPtr.Zero, rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void EndGesture(bool constrain)
    {
        if (_gesture == Gesture.None) return;
        _gestureTimer.Stop();
        _gesture = Gesture.None;
        if (NativeMethods.GetCapture() == _handle) NativeMethods.ReleaseCapture();
        _window.Cursor = Cursors.Arrow;
        if (constrain && _window.IsVisible && !_disposed) RestoreToVisibleScreen();
    }

    private static Cursor CursorFor(Gesture edges) => edges switch
    {
        Gesture.TopLeft or Gesture.BottomRight => Cursors.SizeNWSE,
        Gesture.TopRight or Gesture.BottomLeft => Cursors.SizeNESW,
        Gesture.Left or Gesture.Right => Cursors.SizeWE,
        Gesture.Top or Gesture.Bottom => Cursors.SizeNS,
        _ => Cursors.Arrow
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EndGesture(constrain: false);
        _gestureTimer.Tick -= OnGestureTick;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        _window.PreviewMouseMove -= OnPreviewMouseMove;
        _window.IsVisibleChanged -= OnIsVisibleChanged;
        _window.Closed -= OnClosed;
        if (_handle != IntPtr.Zero)
        {
            if (VisibilityHotkeyRegistered) NativeMethods.UnregisterHotKey(_handle, VisibilityHotkey);
            if (ClickThroughHotkeyRegistered) NativeMethods.UnregisterHotKey(_handle, ClickThroughHotkey);
        }
        _source?.RemoveHook(WindowProc);
        _source = null;
    }
}
