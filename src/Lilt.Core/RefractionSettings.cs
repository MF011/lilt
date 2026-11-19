namespace Lilt.Core;

public sealed record RefractionSettings
{
    public const double MaximumBend = 16;
    public const double MaximumSoftness = 3;
    public const double MaximumColorSplit = 1.5;

    public double Bend { get; init; } = 7;
    public double Softness { get; init; }
    public double ColorSplit { get; init; } = .25;
    public bool ReducedResolution { get; init; }

    public RefractionSettings Validated() => this with
    {
        Bend = Clamp(Bend, MaximumBend, 7),
        Softness = Clamp(Softness, MaximumSoftness, 0),
        ColorSplit = Clamp(ColorSplit, MaximumColorSplit, .25)
    };

    private static double Clamp(double value, double max, double fallback) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, 0, max), 2) : fallback;
}
