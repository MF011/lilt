using System.Runtime.InteropServices;

namespace Lilt.App.Platform;

/// <summary>Reads one opaque window behind the widget. Frames remain in memory.</summary>
internal sealed class WindowLensCapture : IDisposable
{
    private const uint FullContent = 2;
    private const long Layered = 0x80000;
    private const uint Sentinel = 0x00D136F7;
    private readonly object _gate = new();
    private readonly Surface _tile = new();
    private readonly Surface _full = new();
    private IntPtr _brush;
    private IntPtr _source;
    private Rectangle _sourceRect;
    private int _sampleX, _sampleY;
    private bool _cropProven;
    private byte[] _pixels = [];
    private byte[] _comparison = [];
    private int _disposeRequested;

    /// <summary>
    /// Physical coordinates; successful pixels are reusable, top-down packed BGRA with
    /// alpha 255. Consume before the next call. PrintWindow must run on a single background
    /// worker: its preflight hung-window check is not a cancellation or timeout guarantee.
    /// </summary>
    internal bool TryCapture(IntPtr widget, int x, int y, int width, int height, out byte[] pixels)
    {
        pixels = Array.Empty<byte>();
        if (!OperatingSystem.IsWindows() || widget == IntPtr.Zero || width <= 0 || height <= 0 ||
            width > 1920 || height > 1920 || (long)width * height > 1920 * 1080 ||
            (long)x + width > int.MaxValue || (long)y + height > int.MaxValue) return false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0) return false;
            try
            {
                var sample = new Rectangle { Left = x, Top = y, Right = x + width, Bottom = y + height };
                if (!FindSource(widget, sample, out var window, out var bounds) || IsHungAppWindow(window)) return false;
                var windowWidth = bounds.Right - bounds.Left;
                var windowHeight = bounds.Bottom - bounds.Top;
                var offsetX = x - bounds.Left;
                var offsetY = y - bounds.Top;
                if (window != _source || !SameRectangle(bounds, _sourceRect) || x != _sampleX || y != _sampleY)
                {
                    _cropProven = false;
                    _source = window;
                    _sourceRect = bounds;
                    _sampleX = x;
                    _sampleY = y;
                }
                var byteCount = checked(width * height * 4);
                if (_pixels.Length != byteCount) _pixels = new byte[byteCount];
                if (_comparison.Length != byteCount) _comparison = new byte[byteCount];
                var screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return false;
                try
                {
                    if (_brush == IntPtr.Zero) _brush = CreateSolidBrush(Sentinel);
                    if (_brush == IntPtr.Zero || !_tile.Ensure(screen, width, height)) return false;
                    var cropValid = Print(window, _tile, -offsetX, -offsetY) &&
                        ReadFrame(_tile, 0, 0, width, height, _pixels, out _);
                    if (_cropProven && cropValid)
                    {
                        if (Volatile.Read(ref _disposeRequested) != 0) return false;
                        pixels = _pixels;
                        return true;
                    }
                    // A viewport is not honored by every WM_PRINT implementation.
                    // Compare it with a bounded full-window rendering before using it.
                    if (windowWidth <= 0 || windowHeight <= 0 || windowWidth > 4096 || windowHeight > 4096 ||
                        (long)windowWidth * windowHeight > 9 * 1024 * 1024 ||
                        !_full.Ensure(screen, windowWidth, windowHeight) || !Print(window, _full, 0, 0) ||
                        !ReadFrame(_full, offsetX, offsetY, width, height, _comparison, out var fullInformative)) return false;
                    _cropProven = cropValid && fullInformative && (offsetX != 0 || offsetY != 0) &&
                        FramesMatch(_pixels, _comparison);
                    (_pixels, _comparison) = (_comparison, _pixels);
                    if (Volatile.Read(ref _disposeRequested) != 0) return false;
                    pixels = _pixels;
                    return true;
                }
                finally { ReleaseDC(IntPtr.Zero, screen); }
            }
            finally
            {
                if (Volatile.Read(ref _disposeRequested) != 0) ReleaseResources();
            }
        }
    }

    private static bool FindSource(IntPtr widget, Rectangle sample, out IntPtr source, out Rectangle bounds)
    {
        source = IntPtr.Zero;
        bounds = default;
        var current = GetWindow(widget, 2);
        for (var count = 0; current != IntPtr.Zero && count < 512; count++, current = GetWindow(current, 2))
        {
            if (!IsWindowVisible(current) || IsIconic(current) ||
                (DwmGetWindowAttribute(current, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) ||
                !GetWindowRect(current, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top ||
                rect.Right <= sample.Left || rect.Left >= sample.Right || rect.Bottom <= sample.Top || rect.Top >= sample.Bottom) continue;
            var style = GetWindowLongPtr(current, -20).ToInt64();
            if ((style & Layered) != 0)
            {
                if (!GetLayeredWindowAttributes(current, out _, out var alpha, out var flags)) return false;
                if ((flags & 2) != 0 && alpha == 0) continue;
                if ((flags & 1) != 0 || ((flags & 2) != 0 && alpha < 255)) return false;
            }
            // Do not look through a partial occluder: a farther window would be wrong.
            if (rect.Left > sample.Left || rect.Top > sample.Top || rect.Right < sample.Right || rect.Bottom < sample.Bottom) return false;
            if (GetWindowDisplayAffinity(current, out var affinity) && affinity != 0) return false;
            if (!RegionCovers(current, rect, sample)) return false;
            if (DwmGetWindowAttribute(current, 9, out Rectangle visible, Marshal.SizeOf<Rectangle>()) == 0 &&
                (visible.Left > sample.Left || visible.Top > sample.Top || visible.Right < sample.Right || visible.Bottom < sample.Bottom)) return false;
            source = current;
            bounds = rect;
            return true;
        }
        return false;
    }

    private static bool RegionCovers(IntPtr window, Rectangle bounds, Rectangle sample)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        var required = CreateRectRgn(sample.Left - bounds.Left, sample.Top - bounds.Top,
            sample.Right - bounds.Left, sample.Bottom - bounds.Top);
        if (region == IntPtr.Zero || required == IntPtr.Zero)
        {
            if (region != IntPtr.Zero) DeleteObject(region);
            if (required != IntPtr.Zero) DeleteObject(required);
            return false;
        }
        try
        {
            var kind = GetWindowRgn(window, region);
            if (kind == 0) return true;
            if (kind == 1) return false;
            return CombineRgn(required, required, region, 4) == 1;
        }
        finally { DeleteObject(region); DeleteObject(required); }
    }

    private bool Print(IntPtr window, Surface surface, int viewportX, int viewportY)
    {
        var rectangle = new Rectangle { Right = surface.Width, Bottom = surface.Height };
        SetViewportOrgEx(surface.Dc, 0, 0, out _);
        if (FillRect(surface.Dc, ref rectangle, _brush) == 0) return false;
        if (!SetViewportOrgEx(surface.Dc, viewportX, viewportY, out _)) return false;
        try { return PrintWindow(window, surface.Dc, FullContent) && GdiFlush(); }
        finally { SetViewportOrgEx(surface.Dc, 0, 0, out _); }
    }

    private static bool ReadFrame(Surface source, int x, int y, int width, int height, byte[] output, out bool informative)
    {
        informative = false;
        if (x < 0 || y < 0 || x + width > source.Width || y + height > source.Height) return false;
        var rowBytes = width * 4;
        for (var row = 0; row < height; row++)
            Marshal.Copy(IntPtr.Add(source.Bits, ((row + y) * source.Width + x) * 4), output, row * rowBytes, rowBytes);
        var black = 0;
        var minimum = 765;
        var maximum = 0;
        for (var index = 0; index < output.Length; index += 4)
        {
            var b = output[index];
            var g = output[index + 1];
            var r = output[index + 2];
            if (r == 247 && g == 54 && b == 209) return false;
            if (r <= 8 && g <= 8 && b <= 8) black++;
            var intensity = r + g + b;
            minimum = Math.Min(minimum, intensity);
            maximum = Math.Max(maximum, intensity);
            output[index + 3] = 255;
        }
        informative = maximum - minimum > 24;
        return black < (long)width * height * .99;
    }

    private static bool FramesMatch(byte[] a, byte[] b)
    {
        var different = 0;
        for (var index = 0; index < a.Length; index += 4)
            if (Math.Abs(a[index] - b[index]) > 3 || Math.Abs(a[index + 1] - b[index + 1]) > 3 || Math.Abs(a[index + 2] - b[index + 2]) > 3) different++;
        return different <= a.Length / 4 / 200;
    }

    private static bool SameRectangle(Rectangle a, Rectangle b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        // Never block the UI waiting for another process's synchronous WM_PRINT.
        if (!Monitor.TryEnter(_gate)) return;
        try { ReleaseResources(); }
        finally { Monitor.Exit(_gate); }
    }

    private void ReleaseResources()
    {
        _tile.Dispose();
        _full.Dispose();
        if (_brush != IntPtr.Zero) DeleteObject(_brush);
        _brush = IntPtr.Zero;
        Array.Clear(_pixels);
        Array.Clear(_comparison);
        _pixels = _comparison = [];
    }

    private sealed class Surface : IDisposable
    {
        internal IntPtr Dc, Bitmap, Original, Bits;
        internal int Width, Height;
        internal bool Ensure(IntPtr screen, int width, int height)
        {
            if (Dc == IntPtr.Zero) Dc = CreateCompatibleDC(screen);
            if (Dc == IntPtr.Zero) return false;
            if (Bitmap != IntPtr.Zero && Width == width && Height == height) return true;
            var info = new BitmapInformation
            {
                Header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 }
            };
            var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) return false;
            var previous = SelectObject(Dc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) { DeleteObject(bitmap); return false; }
            if (Original == IntPtr.Zero) Original = previous;
            if (Bitmap != IntPtr.Zero) DeleteObject(Bitmap);
            Bitmap = bitmap;
            Bits = bits;
            Width = width;
            Height = height;
            return true;
        }
        public void Dispose()
        {
            if (Dc != IntPtr.Zero && Original != IntPtr.Zero) SelectObject(Dc, Original);
            if (Bitmap != IntPtr.Zero) DeleteObject(Bitmap);
            if (Dc != IntPtr.Zero) DeleteDC(Dc);
            Dc = Bitmap = Original = Bits = IntPtr.Zero;
            Width = Height = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInformation { internal BitmapHeader Header; internal uint Color; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint key, out byte alpha, out uint flags);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out Rectangle value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInformation info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rectangle rectangle, IntPtr brush);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetViewportOrgEx(IntPtr dc, int x, int y, out Point previous);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
}
