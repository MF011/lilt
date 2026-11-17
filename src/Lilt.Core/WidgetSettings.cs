namespace Lilt.Core;

public sealed record WidgetSettings
{
    public double Width { get; init; } = 400;
    public double Height { get; init; } = 136;
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double GlassOpacity { get; init; } = .72;
    public bool Topmost { get; init; } = true;
    public bool Glass { get; init; } = true;
    public bool IgnoreClicks { get; init; }

    public WidgetSettings Validated() => this with
    {
        Width = FiniteClamp(Width, WidgetLayout.MinimumWidth, 1600, 400),
        Height = FiniteClamp(Height, WidgetLayout.MinimumHeight, 1000, 136),
        GlassOpacity = FiniteClamp(GlassOpacity, .18, .95, .72),
        Left = Left is { } left && double.IsFinite(left) ? Math.Clamp(left, -100000, 100000) : null,
        Top = Top is { } top && double.IsFinite(top) ? Math.Clamp(top, -100000, 100000) : null
    };

    private static double FiniteClamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
