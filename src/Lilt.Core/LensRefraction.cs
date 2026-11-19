using System.Runtime.CompilerServices;

namespace Lilt.Core;

/// <summary>
/// Refracts a padded desktop capture through a rounded rectangular lens.
/// The sampling map is built once per card size; Render does not allocate.
/// </summary>
public sealed class LensRefraction
{
    public const int MaximumOutputPixels = 600_000;
    private readonly SamplingMap[] _map;

    public LensRefraction(int cardPixelWidth, int cardPixelHeight, int paddingPixels = 24,
        double pixelsPerDip = 1, double resolutionScale = 1, double cornerRadiusDip = 22,
        double bevelWidthDip = 18, RefractionSettings? settings = null)
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
        Settings = (settings ?? new()).Validated();
        if (Settings.ReducedResolution) resolutionScale = Math.Min(resolutionScale, .5);
        var scale = Math.Min(resolutionScale, Math.Sqrt(MaximumOutputPixels / ((double)cardPixelWidth * cardPixelHeight)));
        OutputWidth = Math.Max(2, (int)Math.Floor(cardPixelWidth * scale));
        OutputHeight = Math.Max(2, (int)Math.Floor(cardPixelHeight * scale));
        _map = new SamplingMap[OutputWidth * OutputHeight];
        BuildMap(cornerRadiusDip, Math.Max(bevelWidthDip, Settings.Bend * 2));
    }

    public int CardPixelWidth { get; }
    public int CardPixelHeight { get; }
    public int PaddingPixels { get; }
    public double PixelsPerDip { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public RefractionSettings Settings { get; }

    /// <summary>
    /// Reads top-down BGR24 or BGRA32 pixels and writes opaque, top-down BGRA32.
    /// Alpha is always 255, so the output is also valid Pbgra32. The WPF card clips
    /// the rounded outline. Source and destination may have padded rows but must
    /// not overlap. Softening adds two light tangential taps to the bilinear map.
    /// </summary>
    public void Render(ReadOnlySpan<byte> source, int sourceStride, Span<byte> destination,
        int destinationStride, int sourceBytesPerPixel = 4, int offsetX = 0, int offsetY = 0)
    {
        if (sourceBytesPerPixel is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(sourceBytesPerPixel));
        if (sourceStride < SourceWidth * sourceBytesPerPixel) throw new ArgumentOutOfRangeException(nameof(sourceStride));
        if (destinationStride < OutputWidth * 4) throw new ArgumentOutOfRangeException(nameof(destinationStride));
        if ((long)(SourceHeight - 1) * sourceStride + SourceWidth * sourceBytesPerPixel > source.Length)
            throw new ArgumentException("The source does not contain the padded capture.", nameof(source));
        if ((long)(OutputHeight - 1) * destinationStride + OutputWidth * 4 > destination.Length)
            throw new ArgumentException("The destination is smaller than the output image.", nameof(destination));
        if (source.Overlaps(destination)) throw new ArgumentException("Source and destination must not overlap.", nameof(destination));
        if (offsetX < -PaddingPixels || offsetX > PaddingPixels || offsetY < -PaddingPixels || offsetY > PaddingPixels)
            throw new ArgumentOutOfRangeException(nameof(offsetX), "The frame offset must fit in the capture padding.");

        var soften = Settings.Softness > 0;
        var sourceLimitX = SourceWidth - 2;
        var sourceLimitY = SourceHeight - 2;

        for (var y = 0; y < OutputHeight; y++)
        {
            var row = destination.Slice(y * destinationStride, OutputWidth * 4);
            var mapOffset = y * OutputWidth;
            for (var x = 0; x < OutputWidth; x++)
            {
                ref readonly var sample = ref _map[mapOffset + x];
                var blue = ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.Blue, 0, offsetX, offsetY, sourceLimitX, sourceLimitY);
                var green = ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.Green, 1, offsetX, offsetY, sourceLimitX, sourceLimitY);
                var red = ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.Red, 2, offsetX, offsetY, sourceLimitX, sourceLimitY);
                if (soften)
                {
                    blue = (blue * 4 + 2 * (ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftA, 0, offsetX, offsetY, sourceLimitX, sourceLimitY)
                        + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftB, 0, offsetX, offsetY, sourceLimitX, sourceLimitY)) + 4) >> 3;
                    green = (green * 4 + 2 * (ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftA, 1, offsetX, offsetY, sourceLimitX, sourceLimitY)
                        + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftB, 1, offsetX, offsetY, sourceLimitX, sourceLimitY)) + 4) >> 3;
                    red = (red * 4 + 2 * (ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftA, 2, offsetX, offsetY, sourceLimitX, sourceLimitY)
                        + ReadChannel(source, sourceStride, sourceBytesPerPixel, sample.SoftB, 2, offsetX, offsetY, sourceLimitX, sourceLimitY)) + 4) >> 3;
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
        var bend = Math.Min(Settings.Bend * PixelsPerDip, bevel * .5);

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
            var sampleX = PaddingPixels + halfWidth - .5 + px - nx * bend * profile;
            var sampleY = PaddingPixels + halfHeight - .5 + py - ny * bend * profile;
            var chroma = Settings.ColorSplit * PixelsPerDip * profile;
            var softRadius = Settings.Softness * PixelsPerDip;
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
        var maxX = SourceWidth - 1;
        var maxY = SourceHeight - 1;
        x = Math.Clamp(x, 0, maxX);
        y = Math.Clamp(y, 0, maxY);
        var left = (int)x;
        var top = (int)y;
        return new SamplePoint((ushort)left, (ushort)top, (byte)((x - left) * 256), (byte)((y - top) * 256));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadChannel(ReadOnlySpan<byte> source, int stride, int bytesPerPixel, SamplePoint point, int channel,
        int offsetX, int offsetY, int limitX, int limitY)
    {
        var x = Math.Clamp(point.X + offsetX, 0, limitX + 1);
        var y = Math.Clamp(point.Y + offsetY, 0, limitY + 1);
        var offset = y * stride + x * bytesPerPixel + channel;
        var fractionX = x > limitX || point.X + offsetX < 0 ? 0 : point.FractionX;
        var fractionY = y > limitY || point.Y + offsetY < 0 ? 0 : point.FractionY;
        if (fractionX == 0 && fractionY == 0) return source[offset];
        var top = fractionX == 0 ? source[offset] * 256 : source[offset] * (256 - fractionX) + source[offset + bytesPerPixel] * fractionX;
        if (fractionY == 0) return (top + 128) >> 8;
        var bottom = fractionX == 0 ? source[offset + stride] * 256 : source[offset + stride] * (256 - fractionX) + source[offset + stride + bytesPerPixel] * fractionX;
        return (top * (256 - fractionY) + bottom * fractionY + 32768) >> 16;
    }

    private readonly record struct SamplePoint(ushort X, ushort Y, byte FractionX, byte FractionY);
    private readonly record struct SamplingMap(SamplePoint Red, SamplePoint Green, SamplePoint Blue, SamplePoint SoftA, SamplePoint SoftB);
}
