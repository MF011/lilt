using Lilt.Core;

public static class LensChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Refraction preserves constant fields and emits opaque alpha", ConstantField);
        yield return ("Rounded bevel bends scene lines more than the lens centre", BevelAndCentre);
        yield return ("Rounded corners refract along both axes", RoundedCorner);
        yield return ("Refraction is deterministic and leaves the capture unchanged", Deterministic);
        yield return ("Refraction safely clamps an unpadded monitor-edge capture", ZeroPadding);
        yield return ("BGR captures and padded output strides remain valid", PaddedStrides);
        yield return ("Refraction limits output work while keeping the capture geometry", OutputLimit);
        yield return ("Refraction rejects incomplete captures before reading pixels", InvalidBuffers);
        yield return ("A sharp lens preserves native pixels outside the bevel", NativeDetail);
        yield return ("A moving lens reads the new position inside a padded frame", MovingSample);
        yield return ("The bend setting changes the edge displacement", BendSetting);
    }

    private static void ConstantField()
    {
        var lens = new LensRefraction(192, 112, settings: new RefractionSettings { Softness = 2 });
        var source = new byte[lens.SourceWidth * lens.SourceHeight * 4];
        for (var i = 0; i < source.Length; i += 4)
        { source[i] = 27; source[i + 1] = 108; source[i + 2] = 219; source[i + 3] = 0; }
        var output = new byte[lens.OutputWidth * lens.OutputHeight * 4];
        lens.Render(source, lens.SourceWidth * 4, output, lens.OutputWidth * 4);
        for (var i = 0; i < output.Length; i += 4)
            Require(output[i] == 27 && output[i + 1] == 108 && output[i + 2] == 219 && output[i + 3] == 255,
                "A constant scene changed colour or retained source alpha.");
    }

    private static void BevelAndCentre()
    {
        var lens = new LensRefraction(192, 112, resolutionScale: 1);
        var output = RenderCoordinates(lens);
        int Channel(int x, int y, int channel) => output[(y * lens.OutputWidth + x) * 4 + channel];
        var topBend = Math.Abs(Channel(96, 6, 1) - (lens.PaddingPixels + 6));
        var sideBend = Math.Abs(Channel(6, 56, 0) - (lens.PaddingPixels + 6));
        Require(topBend >= 5 && sideBend >= 5, "Straight scene lines did not bend in the bevel.");
        Require(Math.Abs(Channel(96, 6, 0) - (lens.PaddingPixels + 96)) <= 1,
            "The straight top edge bent the scene sideways.");
        Require(Math.Abs(Channel(6, 56, 1) - (lens.PaddingPixels + 56)) <= 1,
            "The straight side edge bent the scene vertically.");
        Require(Math.Abs(Channel(96, 56, 0) - (lens.PaddingPixels + 96)) <= 1 &&
                Math.Abs(Channel(96, 56, 1) - (lens.PaddingPixels + 56)) <= 1,
            "The lens centre has a significant displacement.");
    }

    private static void RoundedCorner()
    {
        var lens = new LensRefraction(192, 112, resolutionScale: 1);
        var output = RenderCoordinates(lens);
        var index = (12 * lens.OutputWidth + 12) * 4;
        Require(output[index] >= lens.PaddingPixels + 15 && output[index + 1] >= lens.PaddingPixels + 15,
            "The rounded bevel did not bend the background along both axes.");
    }

    private static void Deterministic()
    {
        var lens = new LensRefraction(192, 112);
        var source = Coordinates(lens, 4, lens.SourceWidth * 4);
        var original = source.ToArray();
        var first = new byte[lens.OutputWidth * lens.OutputHeight * 4];
        var second = new byte[first.Length];
        lens.Render(source, lens.SourceWidth * 4, first, lens.OutputWidth * 4);
        lens.Render(source, lens.SourceWidth * 4, second, lens.OutputWidth * 4);
        Require(first.AsSpan().SequenceEqual(second), "The same capture produced different frames.");
        Require(source.AsSpan().SequenceEqual(original), "Rendering changed the input capture.");
    }

    private static void ZeroPadding()
    {
        var lens = new LensRefraction(31, 17, 0, settings: new RefractionSettings { Softness = 3 });
        var source = Coordinates(lens, 4, lens.SourceWidth * 4);
        var output = new byte[lens.OutputWidth * lens.OutputHeight * 4];
        lens.Render(source, lens.SourceWidth * 4, output, lens.OutputWidth * 4);
        Require(output.Where((_, index) => index % 4 == 3).All(value => value == 255),
            "An edge-clamped frame contains unpainted pixels.");
    }

    private static void PaddedStrides()
    {
        var lens = new LensRefraction(120, 70, 0, resolutionScale: 1);
        var sourceStride = lens.SourceWidth * 3 + 5;
        var destinationStride = lens.OutputWidth * 4 + 12;
        var source = Coordinates(lens, 3, sourceStride);
        var output = Enumerable.Repeat((byte)0x5A, destinationStride * lens.OutputHeight).ToArray();
        lens.Render(source, sourceStride, output, destinationStride, 3);
        for (var y = 0; y < lens.OutputHeight; y++)
        for (var x = lens.OutputWidth * 4; x < destinationStride; x++)
            Require(output[y * destinationStride + x] == 0x5A, "Rendering overwrote output row padding.");
        var centre = (35 * destinationStride) + 60 * 4;
        Require(Math.Abs(output[centre] - 60) <= 1 && Math.Abs(output[centre + 1] - 35) <= 1,
            "The BGR capture was read with the wrong channel or row stride.");
    }

    private static void OutputLimit()
    {
        var lens = new LensRefraction(1600, 1000, 24, 2, 1);
        Require(lens.OutputWidth * lens.OutputHeight <= LensRefraction.MaximumOutputPixels, "Output exceeded the CPU frame budget.");
        Require(lens.SourceWidth == 1648 && lens.SourceHeight == 1048, "The padded physical capture was resized unexpectedly.");
        Require(Math.Abs(lens.OutputWidth / (double)lens.OutputHeight - 1.6) < .01,
            "Limiting output changed the card aspect ratio.");
    }

    private static void NativeDetail()
    {
        var lens = new LensRefraction(192, 112, 0);
        Require(lens.OutputWidth == 192 && lens.OutputHeight == 112, "A normal card was downsampled.");
        var stride = lens.SourceWidth * 4;
        var capture = Coordinates(lens, 4, stride);
        // One-pixel alternating lines reveal downsampling or an unwanted center zoom.
        for (var i = 0; i < capture.Length; i += 4)
            capture[i] = capture[i + 1] = capture[i + 2] = (i / 4 % 2 == 0) ? (byte)0 : (byte)255;
        var output = new byte[capture.Length];
        lens.Render(capture, stride, output, stride);
        var offset = 56 * stride + 50 * 4;
        Require(capture.AsSpan(offset, 90 * 4).SequenceEqual(output.AsSpan(offset, 90 * 4)), "The centre lost pixel detail.");
    }

    private static void MovingSample()
    {
        var lens = new LensRefraction(160, 96, 12);
        var stride = lens.OutputWidth * 4;
        var capture = Coordinates(lens, 4, lens.SourceWidth * 4);
        var output = new byte[lens.OutputHeight * stride];
        lens.Render(capture, lens.SourceWidth * 4, output, stride, offsetX: 10, offsetY: -6);
        var index = 48 * stride + 80 * 4;
        Require(output[index] == 102 && output[index + 1] == 54, "The lens used the old card position.");
    }

    private static void BendSetting()
    {
        var clear = new LensRefraction(192, 112, settings: new RefractionSettings { Bend = 0, ColorSplit = 0 });
        var strong = new LensRefraction(192, 112, settings: new RefractionSettings { Bend = 15, ColorSplit = 0 });
        var index = (10 * clear.OutputWidth + 96) * 4 + 1;
        Require(RenderCoordinates(strong)[index] - RenderCoordinates(clear)[index] >= 10, "Higher bend did not increase the edge displacement.");
    }

    private static void InvalidBuffers()
    {
        var lens = new LensRefraction(32, 24, 0);
        try
        {
            lens.Render(new byte[4], lens.SourceWidth * 4, new byte[lens.OutputWidth * lens.OutputHeight * 4], lens.OutputWidth * 4);
        }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("An incomplete capture was accepted.");
    }

    private static byte[] RenderCoordinates(LensRefraction lens)
    {
        var source = Coordinates(lens, 4, lens.SourceWidth * 4);
        var output = new byte[lens.OutputWidth * lens.OutputHeight * 4];
        lens.Render(source, lens.SourceWidth * 4, output, lens.OutputWidth * 4);
        return output;
    }

    private static byte[] Coordinates(LensRefraction lens, int bytesPerPixel, int stride)
    {
        var source = new byte[stride * lens.SourceHeight];
        for (var y = 0; y < lens.SourceHeight; y++)
        for (var x = 0; x < lens.SourceWidth; x++)
        {
            var index = y * stride + x * bytesPerPixel;
            source[index] = (byte)Math.Clamp(x, 0, 255);
            source[index + 1] = (byte)Math.Clamp(y, 0, 255);
            source[index + 2] = (byte)((x / 5 + y / 5) % 2 == 0 ? 24 : 232);
            if (bytesPerPixel == 4) source[index + 3] = 255;
        }
        return source;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
