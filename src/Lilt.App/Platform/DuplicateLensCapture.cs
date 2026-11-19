using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Lilt.App.Platform;

/// <summary>Reads just the card's tile from a D3D desktop frame. One worker owns the device.</summary>
internal sealed class DuplicateLensCapture : IDisposable
{
    private const int ExcludedFromDesktopDuplication = 24;
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);
    private static readonly FeatureLevel[] DeviceFeatureLevels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
    private IDXGIOutputDuplication? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _tile;
    private ID3D11Texture2D? _desktopFrame;
    private RawRect _display;
    private byte[] _pixels = [];
    private (int X, int Y, int Width, int Height) _lastTile;
    private long _retryAt;

    // This is a DDA-only attribute. Other screenshot paths still see Lilt, which
    // lets the caller verify in pixels that desktop duplication omitted it.
    internal static bool MarkExcluded(IntPtr window, bool excluded)
    {
        var value = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(value, excluded ? 1 : 0);
            var data = new CompositionData { Attribute = ExcludedFromDesktopDuplication, Value = value, Size = sizeof(int) };
            return SetWindowCompositionAttribute(window, ref data);
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(value); }
    }

    internal bool TryCapture(int x, int y, int width, int height, ref int padding, out byte[] pixels)
    {
        pixels = [];
        if (!ValidTile(width, height)) return false;

        try
        {
            if (!EnsureOutput(x, y, width, height)) return false;
            if (!ValidTile(width + 2 * padding, height + 2 * padding) ||
                !Contains(_display, x - padding, y - padding, width + 2 * padding, height + 2 * padding)) padding = 0;
            x -= padding;
            y -= padding;
            width += 2 * padding;
            height += 2 * padding;
            var result = _output!.AcquireNextFrame(0, out var frameInfo, out var resource);
            if (result.Failure)
            {
                resource?.Dispose();
                if (result.Code == WaitTimeout)
                {
                    if ((x, y, width, height) == _lastTile)
                    {
                        pixels = _pixels;
                        return pixels.Length > 0;
                    }
                    if (_desktopFrame is null) return false;
                    // Excluding our HWND stops a drag from generating desktop frames on a
                    // static background. Recut the new location from the last GPU frame.
                    ReadTile(_desktopFrame, x, y, width, height);
                    _lastTile = (x, y, width, height);
                    pixels = _pixels;
                    return true;
                }
                Reset(result.Code == AccessLost ? 250 : 4000);
                return false;
            }
            try
            {
                using (resource)
                using (var texture = resource!.QueryInterface<ID3D11Texture2D>())
                {
                    if (texture.Description.Format != Format.B8G8R8A8_UNorm) return false;
                    CacheDesktopFrame(texture, frameInfo.LastPresentTime != 0);
                    ReadTile(_desktopFrame ?? texture, x, y, width, height);
                }
            }
            finally { _output.ReleaseFrame(); }
            _lastTile = (x, y, width, height);
            pixels = _pixels;
            return true;
        }
        catch (Exception exception) when (exception is SharpGenException or DllNotFoundException or EntryPointNotFoundException)
        {
            Reset(4000);
            return false;
        }
    }

    private static bool ValidTile(int width, int height) =>
        width >= 2 && height >= 2 && width <= DesktopLensCapture.MaximumDimension && height <= DesktopLensCapture.MaximumDimension &&
        (long)width * height <= DesktopLensCapture.MaximumPixels;

    private void CacheDesktopFrame(ID3D11Texture2D source, bool changed)
    {
        // Keep a GPU-only copy so moving the card can sample a static desktop.
        // Cap it at 64 MiB. The working tile still captures on larger outputs.
        var description = source.Description;
        if ((long)description.Width * description.Height > 16 * 1024 * 1024) return;
        if (_desktopFrame is null || _desktopFrame.Description.Width != description.Width || _desktopFrame.Description.Height != description.Height)
        {
            _desktopFrame?.Dispose();
            _desktopFrame = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = description.Width, Height = description.Height, MipLevels = 1, ArraySize = 1,
                Format = description.Format, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default
            });
            changed = true;
        }
        if (changed) _context!.CopyResource(_desktopFrame, source);
    }

    private bool EnsureOutput(int x, int y, int width, int height)
    {
        if (_output is not null && Contains(_display, x, y, width, height)) return true;
        if (Environment.TickCount64 < _retryAt) return false;
        Reset(0);
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; factory.EnumAdapters1(index, out var adapter).Success; index++)
        {
            using (adapter)
            for (uint outputIndex = 0; adapter!.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
            {
                using (output)
                {
                    var description = output!.Description;
                    if (!description.AttachedToDesktop || description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified) ||
                        !Contains(description.DesktopCoordinates, x, y, width, height)) continue;

                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    var created = D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                        DeviceFeatureLevels, out _device, out _, out _context);
                    if (created.Failure || _device is null) { Reset(4000); return false; }
                    _output = output1.DuplicateOutput(_device);
                    _display = description.DesktopCoordinates;
                    return true;
                }
            }
        }
        Reset(1000);
        return false;
    }

    private void ReadTile(ID3D11Texture2D source, int x, int y, int width, int height)
    {
        var expectedLength = checked(width * height * 4);
        if (_tile is null || _tile.Description.Width != width || _tile.Description.Height != height)
        {
            _tile?.Dispose();
            _tile = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
            });
        }
        if (_pixels.Length != expectedLength) _pixels = new byte[expectedLength];
        var left = x - _display.Left;
        var top = y - _display.Top;
        _context!.CopySubresourceRegion(_tile, 0, 0, 0, 0, source, 0,
            new Box(left, top, 0, left + width, top + height, 1));
        var mapped = _context.Map(_tile, 0, MapMode.Read);
        try
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, _pixels, row * width * 4, width * 4);
        }
        finally { _context.Unmap(_tile, 0); }
    }

    private static bool Contains(RawRect display, int x, int y, int width, int height) =>
        x >= display.Left && y >= display.Top && (long)x + width <= display.Right && (long)y + height <= display.Bottom;

    private void Reset(int retryDelay)
    {
        _lastTile = default;
        _output?.Dispose();
        _tile?.Dispose();
        _desktopFrame?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _output = null;
        _tile = null;
        _desktopFrame = null;
        _context = null;
        _device = null;
        _retryAt = Environment.TickCount64 + retryDelay;
    }

    public void Dispose()
    {
        Reset(0);
        Array.Clear(_pixels);
        _pixels = [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData { public int Attribute; public IntPtr Value; public nuint Size; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);
}
