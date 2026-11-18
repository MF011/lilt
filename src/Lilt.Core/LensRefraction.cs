using System.Runtime.CompilerServices;

namespace Lilt.Core;

/// <summary>
/// Refracts a padded desktop capture through a rounded rectangular lens.
/// The sampling map is built once per card size; Render does not allocate.
/// </summary>
public sealed class LensRefraction
{
    private const int MaximumOutputWidth = 800;
    private const int MaximumOutputHeight = 400;
    private readonly SamplingMap[] _map;

    public LensRefraction(int cardPixelWidth, int cardPixelHeight, int paddingPixels = 24,
        double pixelsPerDip = 1, double resolutionScale = .5, double cornerRadiusDip = 22,
        double bevelWidthDip = 18)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cardPixelWidth, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(cardPixelHeight, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cardPixelWidth, 16384);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cardPixelHeight, 16384);
        if (paddingPixels is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(paddingPixels));
        if (!double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0 || pixelsPerDip > 16)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
        if (!double.IsFinite(resolutionScale) || resolutionScale <= 0 || resolutionScale > 1)
            throw new ArgumentOutOfRangeException(nameof(resolutionScale));
        if (!double.IsFinite(cornerRadiusDip) || cornerRadiusDip < 0)
            throw new ArgumentOutOfRangeException(nameof(cornerRadiusDip));
        if (!double.IsFinite(bevelWidthDip) || bevelWidthDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(bevelWidthDip));

        CardPixelWidth = cardPixelWidth;
        CardPixelHeight = cardPixelHeight;
        PaddingPixels = paddingPixels;
        PixelsPerDip = pixelsPerDip;
        SourceWidth = cardPixelWidth + paddingPixels * 2;
        SourceHeight = cardPixelHeight + paddingPixels * 2;
        var scale = Math.Min(resolutionScale, Math.Min(MaximumOutputWidth / (double)cardPixelWidth,
            MaximumOutputHeight / (double)cardPixelHeight));
        OutputWidth = Math.Max(2, (int)Math.Round(cardPixelWidth * scale));
        OutputHeight = Math.Max(2, (int)Math.Round(cardPixelHeight * scale));
        _map = new SamplingMap[OutputWidth * OutputHeight];
        BuildMap(cornerRadiusDip, bevelWidthDip);
    }

    public int CardPixelWidth { get; }
    public int CardPixelHeight { get; }
    public int PaddingPixels { get; }
    public double PixelsPerDip { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }

    /// <summary>
    /// Reads top-down BGR24 or BGRA32 pixels and writes opaque, top-down BGRA32.
    /// Alpha is always 255, so the output is also valid Pbgra32. The WPF card clips
    /// the rounded outline. Source and destination may have padded rows but must
    /// not overlap. Softening adds two light tangential taps to the bilinear map.
    /// </summary>
    public void Render(ReadOnlySpan<byte> source, int sourceStride, Span<byte> destination,
        int destinationStride, int sourceBytesPerPixel = 4, bool soften = false)
    {
        if (sourceBytesPerPixel is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(sourceBytesPerPixel));
        if (sourceStride < SourceWidth * sourceBytesPerPixel) throw new ArgumentOutOfRangeException(nameof(sourceStride));
        if (destinationStride < OutputWidth * 4) throw new ArgumentOutOfRangeException(nameof(destinationStride));
        if ((long)(SourceHeight - 1) * sourceStride + SourceWidth * sourceBytesPerPixel > source.Length)
            throw new ArgumentException("The source does not contain the padded capture.", nameof(source));
        if ((long)(OutputHeight - 1) * destinationStride + OutputWidth * 4 > destination.Length)
            throw new ArgumentException("The destination is smaller than the output image.", nameof(destination));
        if (source.Overlaps(destination)) throw new ArgumentException("Source and destination must not overlap.", nameof(destination));

        for (var y = 0; y < OutputHeight; y++)
        {
            var row = destination.Slice(y * destinationStride, OutputWidth * 4);
            var mapOffset = y * OutputWidth;
            for (var x = 0; x < OutputWidth; x++)
            {
                ref readonly var sample = ref _map[mapOffset + x];
                var blue = ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.Blue, 0);
                var green = ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.Green, 1);
                var red = ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.Red, 2);
                if (soften)
                {
                    blue = (blue * 6 + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftA, 0)
                        + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftB, 0) + 4) >> 3;
                    green = (green * 6 + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftA, 1)
                        + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftB, 1) + 4) >> 3;
                    red = (red * 6 + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftA, 2)
                        + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftB, 2) + 4) >> 3;
                }
                var offset = x * 4;
                row[offset] = (byte)blue;
                row[offset + 1] = (byte)green;
                row[offset + 2] = (byte)red;
                row[offset + 3] = 255;
            }
        }
    }

    private void BuildMap(double cornerRadiusDip, double bevelWidthDip)
    {
        var halfWidth = CardPixelWidth * .5;
        var halfHeight = CardPixelHeight * .5;
        var radius = Math.Min(cornerRadiusDip * PixelsPerDip, Math.Min(halfWidth, halfHeight));
        var bevel = Math.Min(bevelWidthDip * PixelsPerDip, Math.Min(halfWidth, halfHeight));
        var bend = Math.Min(7 * PixelsPerDip, bevel * .42);
        const double magnification = 1.008;

        for (var y = 0; y < OutputHeight; y++)
        for (var x = 0; x < OutputWidth; x++)
        {
            // Pixel centres are expressed in the full-resolution captured card.
            var px = (x + .5) * CardPixelWidth / OutputWidth - halfWidth;
            var py = (y + .5) * CardPixelHeight / OutputHeight - halfHeight;
            var qx = Math.Abs(px) - (halfWidth - radius);
            var qy = Math.Abs(py) - (halfHeight - radius);
            var outsideX = Math.Max(qx, 0);
            var outsideY = Math.Max(qy, 0);
            var outsideLength = Math.Sqrt(outsideX * outsideX + outsideY * outsideY);
            var distance = outsideLength + Math.Min(Math.Max(qx, qy), 0) - radius;
            double nx, ny;
            if (outsideLength > .000001)
            {
                nx = Math.Sign(px) * outsideX / outsideLength;
                ny = Math.Sign(py) * outsideY / outsideLength;
            }
            else if (qx > qy) { nx = Math.Sign(px); ny = 0; }
            else { nx = 0; ny = Math.Sign(py); }

            var t = Math.Clamp(-distance / bevel, 0, 1);
            // A rounded bevel, rather than a whole-card radial bulge. It peaks
            // one third of the way in, then joins the quiet interior smoothly.
            var profile = 6.75 * t * (1 - t) * (1 - t);
            var sampleX = PaddingPixels + halfWidth - .5 + px / magnification - nx * bend * profile;
            var sampleY = PaddingPixels + halfHeight - .5 + py / magnification - ny * bend * profile;
            var chroma = .55 * profile; // R/B separation stays below 1.1 physical pixels.
            var softRadius = .65;
            _map[y * OutputWidth + x] = new SamplingMap(
                SampleAt(sampleX + nx * chroma, sampleY + ny * chroma),
                SampleAt(sampleX, sampleY),
                SampleAt(sampleX - nx * chroma, sampleY - ny * chroma),
                SampleAt(sampleX - ny * softRadius, sampleY + nx * softRadius),
                SampleAt(sampleX + ny * softRadius, sampleY - nx * softRadius));
        }
    }

    private SamplePoint SampleAt(double x, double y)
    {
        x = Math.Clamp(x, 0, SourceWidth - 1.000001);
        y = Math.Clamp(y, 0, SourceHeight - 1.000001);
        var left = (int)x;
        var top = (int)y;
        return new SamplePoint((ushort)left, (ushort)top,
            (byte)Math.Clamp((int)((x - left) * 256), 0, 255),
            (byte)Math.Clamp((int)((y - top) * 256), 0, 255));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadChannel(ReadOnlySpan<byte> source, int stride, int bytesPerPixel, SamplePoint point, int channel)
    {
        var offset = point.Y * stride + point.X * bytesPerPixel + channel;
        var top = source[offset] * (256 - point.FractionX) + source[offset + bytesPerPixel] * point.FractionX;
        var bottom = source[offset + stride] * (256 - point.FractionX) + source[offset + stride + bytesPerPixel] * point.FractionX;
        return (top * (256 - point.FractionY) + bottom * point.FractionY + 32768) >> 16;
    }

    private readonly record struct SamplePoint(ushort X, ushort Y, byte FractionX, byte FractionY);
    private readonly record struct SamplingMap(SamplePoint Red, SamplePoint Green, SamplePoint Blue, SamplePoint SoftA, SamplePoint SoftB);
}
