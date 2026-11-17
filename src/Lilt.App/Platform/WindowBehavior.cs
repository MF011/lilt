using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace Lilt.App.Platform;

public sealed class WindowBehavior : IDisposable
{
    private const int VisibilityHotkey = 0x4C11;
    private const int ClickThroughHotkey = 0x4C12;
    private readonly Window _window;
    private IntPtr _handle;
    private HwndSource? _source;
    private bool _disposed;
    private bool _ignoreClicks;
    private bool _topmost = true;
    private bool _backdrop = true;
    private bool _originallyLayered;
    private bool _moving;
    private double _opacity = .72;
    private double _cornerRadius = 20;

    public event Action? ToggleVisibilityRequested;
    public event Action? ToggleClickThroughRequested;
    public bool VisibilityHotkeyRegistered { get; private set; }
    public bool ClickThroughHotkeyRegistered { get; private set; }
    public bool BackdropApplied { get; private set; }

    public WindowBehavior(Window window)
    {
        _window = window;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.SourceInitialized += OnSourceInitialized;
        window.SizeChanged += OnSizeChanged;
        window.DpiChanged += OnDpiChanged;
        window.Closed += OnClosed;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Initialize();
    }

    public void SetClickThrough(bool enabled)
    {
        _ignoreClicks = enabled;
        if (_handle == IntPtr.Zero) return;
        var style = NativeMethods.GetExStyle(_handle).ToInt64();
        if (enabled)
        {
            style |= NativeMethods.WsExTransparent | NativeMethods.WsExLayered;
            NativeMethods.SetExStyle(_handle, style);
            NativeMethods.SetLayeredWindowAttributes(_handle, 0, 255, 0x2);
        }
        else
        {
            style &= ~NativeMethods.WsExTransparent;
            if (!_originallyLayered) style &= ~NativeMethods.WsExLayered;
            NativeMethods.SetExStyle(_handle, style);
        }
        NativeMethods.SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
        ApplyBackdrop();
    }

    public void SetTopmost(bool enabled)
    {
        _topmost = enabled;
        _window.Topmost = enabled;
        if (_handle != IntPtr.Zero)
            NativeMethods.SetWindowPos(_handle, new IntPtr(enabled ? -1 : -2), 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    public bool SetBackdrop(bool enabled)
    {
        _backdrop = enabled;
        return ApplyBackdrop();
    }

    public void SetOpacity(double opacity)
    {
        _opacity = Math.Clamp(opacity, .25, .95);
        ApplyBackdrop();
    }

    public void UpdateCornerRadius(double radius)
    {
        _cornerRadius = Math.Clamp(radius, 0, 64);
        UpdateRegion();
    }

    public void TryBeginDrag()
    {
        if (!_ignoreClicks) BeginDrag(_window);
    }

    public static void BeginDrag(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        Mouse.Capture(null);
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(handle, NativeMethods.WmNcLButtonDown, new IntPtr(NativeMethods.HtCaption), IntPtr.Zero);
    }

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
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRegion();
    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => UpdateRegion();
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void Initialize()
    {
        if (_disposed || _handle != IntPtr.Zero) return;
        _handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProc);
        if (_source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var style = NativeMethods.GetExStyle(_handle).ToInt64();
        _originallyLayered = (style & NativeMethods.WsExLayered) != 0;
        NativeMethods.SetExStyle(_handle,
            (style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate) & ~NativeMethods.WsExAppWindow);
        var margins = new NativeMethods.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(_handle, ref margins);
        VisibilityHotkeyRegistered = NativeMethods.RegisterHotKey(_handle, VisibilityHotkey,
            NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, 0x4C);
        ClickThroughHotkeyRegistered = NativeMethods.RegisterHotKey(_handle, ClickThroughHotkey,
            NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat, 0x4C);
        SetTopmost(_topmost);
        SetClickThrough(_ignoreClicks);
        UpdateRegion();
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
        else if (message == NativeMethods.WmDwmCompositionChanged) ApplyBackdrop();
        else if (message == NativeMethods.WmEnterSizeMove)
        {
            // Win10 Acrylic can stall interactive window moves. Restore it when the gesture ends.
            _moving = true;
            ApplyBackdrop();
        }
        else if (message == NativeMethods.WmExitSizeMove)
        {
            _moving = false;
            ApplyBackdrop();
        }
        else if (message == NativeMethods.WmDisplayChange)
            _window.Dispatcher.BeginInvoke(new Action(RestoreToVisibleScreen));
        else if (message == NativeMethods.WmNcHitTest && !_ignoreClicks && NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            var raw = lParam.ToInt64();
            var x = unchecked((short)(raw & 0xFFFF));
            var y = unchecked((short)((raw >> 16) & 0xFFFF));
            var border = Math.Max(5, (int)Math.Round(7 * VisualTreeHelper.GetDpi(_window).DpiScaleX));
            var left = x < rect.Left + border;
            var right = x >= rect.Right - border;
            var top = y < rect.Top + border;
            var bottom = y >= rect.Bottom - border;
            var hit = top && left ? NativeMethods.HtTopLeft : top && right ? NativeMethods.HtTopRight :
                bottom && left ? NativeMethods.HtBottomLeft : bottom && right ? NativeMethods.HtBottomRight :
                left ? NativeMethods.HtLeft : right ? NativeMethods.HtRight :
                top ? NativeMethods.HtTop : bottom ? NativeMethods.HtBottom : 0;
            if (hit != 0)
            {
                handled = true;
                return new IntPtr(hit);
            }
        }
        return IntPtr.Zero;
    }

    private bool ApplyBackdrop()
    {
        if (_handle == IntPtr.Zero) return false;
        var enabled = _backdrop && !_moving && !SystemParameters.HighContrast;
        var accent = new NativeMethods.AccentPolicy
        {
            State = enabled ? 4 : 0,
            Flags = enabled ? 2 : 0,
            GradientColor = ((uint)Math.Round(_opacity * 255) << 24) | 0x0018110E
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = 19,
                Data = pointer,
                Size = Marshal.SizeOf<NativeMethods.AccentPolicy>()
            };
            BackdropApplied = NativeMethods.SetWindowCompositionAttribute(_handle, ref data) && enabled;
        }
        catch (EntryPointNotFoundException) { BackdropApplied = false; }
        catch (DllNotFoundException) { BackdropApplied = false; }
        finally { Marshal.FreeHGlobal(pointer); }
        return BackdropApplied;
    }

    private void UpdateRegion()
    {
        if (_handle == IntPtr.Zero || !NativeMethods.GetWindowRect(_handle, out var rect)) return;
        var radius = (int)Math.Round(_cornerRadius * VisualTreeHelper.GetDpi(_window).DpiScaleX * 2);
        var region = NativeMethods.CreateRoundRectRgn(0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, radius, radius);
        if (region != IntPtr.Zero && NativeMethods.SetWindowRgn(_handle, region, true) == 0)
            NativeMethods.DeleteObject(region);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.SizeChanged -= OnSizeChanged;
        _window.DpiChanged -= OnDpiChanged;
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
