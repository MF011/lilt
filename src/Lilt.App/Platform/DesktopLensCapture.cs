using System.Runtime.InteropServices;

namespace Lilt.App.Platform;

/// <summary>Copies a bounded desktop tile into reusable memory; never saves or sends a frame.</summary>
internal sealed class DesktopLensCapture : IDisposable
{
    internal const int MaximumDimension = 1920;
    internal const int MaximumPixels = 1920 * 1080;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureLayered = 0x40000000;
    private readonly object _gate = new();
    private readonly List<Rectangle> _monitors = new(8);
    private readonly List<int> _rows = new(18);
    private readonly List<Interval> _intervals = new(8);
    private readonly MonitorCallback _monitorCallback;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _originalBitmap;
    private IntPtr _bits;
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private bool _disposed;

    internal DesktopLensCapture() => _monitorCallback = OnMonitor;

    /// <summary>
    /// Uses physical desktop coordinates. On success pixels is a top-down packed BGRA
    /// buffer with alpha normalized to 255. Its length is width * height * 4; the next
    /// capture reuses it, so callers must consume it before capturing again.
    /// Failure returns Array.Empty. Layered capture is reserved for the exclusion probe:
    /// omitting CAPTUREBLT must not itself be treated as proof of self exclusion.
    /// </summary>
    internal bool TryCapture(int x, int y, int width, int height, out byte[] pixels, bool includeLayered = false)
    {
        pixels = Array.Empty<byte>();
        if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension ||
            (long)width * height > MaximumPixels || !OperatingSystem.IsWindows()) return false;

        lock (_gate)
        {
            if (_disposed || !IsCoveredByDesktop(x, y, width, height)) return false;
            // A screen DC is acquired and released on this call's thread. Only the
            // compatible memory DC and DIB survive between captures.
            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            try
            {
                if (!EnsureSurface(screenDc, width, height)) return false;
                var operation = SourceCopy | (includeLayered ? CaptureLayered : 0);
                if (!BitBlt(_memoryDc, 0, 0, width, height, screenDc, x, y, operation) || !GdiFlush()) return false;
                Marshal.Copy(_bits, _pixels, 0, _pixels.Length);
                // BI_RGB supplies BGR bytes, but its fourth byte is unspecified.
                for (var index = 3; index < _pixels.Length; index += 4) _pixels[index] = 255;
                pixels = _pixels;
                return true;
            }
            finally { ReleaseDC(IntPtr.Zero, screenDc); }
        }
    }

    private bool EnsureSurface(IntPtr screenDc, int width, int height)
    {
        if (_memoryDc == IntPtr.Zero)
        {
            _memoryDc = CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero) return false;
        }
        if (_bitmap != IntPtr.Zero && _width == width && _height == height) return true;

        var byteCount = checked(width * height * 4);
        var buffer = _pixels.Length == byteCount ? _pixels : new byte[byteCount];
        var information = new BitmapInformation
        {
            Header = new BitmapInformationHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInformationHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                SizeImage = (uint)byteCount
            }
        };
        var bitmap = CreateDIBSection(screenDc, ref information, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) return false;
        var previous = SelectObject(_memoryDc, bitmap);
        if (previous == IntPtr.Zero || previous == new IntPtr(-1))
        {
            DeleteObject(bitmap);
            return false;
        }
        if (_originalBitmap == IntPtr.Zero) _originalBitmap = previous;
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        _bitmap = bitmap;
        _bits = bits;
        _pixels = buffer;
        _width = width;
        _height = height;
        return true;
    }

    private bool IsCoveredByDesktop(int x, int y, int width, int height)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var desktopWidth = GetSystemMetrics(78);
        var desktopHeight = GetSystemMetrics(79);
        var right = (long)x + width;
        var bottom = (long)y + height;
        if (desktopWidth <= 0 || desktopHeight <= 0 || x < left || y < top ||
            right > (long)left + desktopWidth || bottom > (long)top + desktopHeight ||
            right > int.MaxValue || bottom > int.MaxValue) return false;

        _monitors.Clear();
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _monitorCallback, IntPtr.Zero) || _monitors.Count == 0) return false;
        // The virtual desktop's bounding rectangle can contain empty gaps. Prove
        // every horizontal strip is covered by the actual monitor union instead.
        _rows.Clear();
        _rows.Add(y);
        _rows.Add((int)bottom);
        foreach (var monitor in _monitors)
        {
            if (monitor.Bottom <= y || monitor.Top >= bottom || monitor.Right <= x || monitor.Left >= right) continue;
            _rows.Add(Math.Max(y, monitor.Top));
            _rows.Add((int)Math.Min(bottom, monitor.Bottom));
        }
        _rows.Sort();
        for (var row = 1; row < _rows.Count; row++)
        {
            var y0 = _rows[row - 1];
            var y1 = _rows[row];
            if (y0 == y1) continue;
            _intervals.Clear();
            foreach (var monitor in _monitors)
            {
                if (monitor.Top > y0 || monitor.Bottom < y1 || monitor.Right <= x || monitor.Left >= right) continue;
                _intervals.Add(new Interval(Math.Max(x, monitor.Left), (int)Math.Min(right, monitor.Right)));
            }
            _intervals.Sort(static (a, b) => a.Left.CompareTo(b.Left));
            var coveredUntil = x;
            foreach (var interval in _intervals)
            {
                if (interval.Left > coveredUntil) return false;
                coveredUntil = Math.Max(coveredUntil, interval.Right);
                if (coveredUntil >= right) break;
            }
            if (coveredUntil < right) return false;
        }
        return true;
    }

    private bool OnMonitor(IntPtr monitor, IntPtr dc, ref Rectangle rectangle, IntPtr data)
    {
        _monitors.Add(rectangle);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_memoryDc != IntPtr.Zero && _originalBitmap != IntPtr.Zero) SelectObject(_memoryDc, _originalBitmap);
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
            _bitmap = _memoryDc = _bits = _originalBitmap = IntPtr.Zero;
            Array.Clear(_pixels);
            _pixels = [];
        }
    }

    private readonly record struct Interval(int Left, int Right);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInformationHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInformation { internal BitmapInformationHeader Header; internal uint Color; }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rectangle rectangle, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInformation information, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
}
