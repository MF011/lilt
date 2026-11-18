using System.Text.Json;
using Lilt.Core;

var checks = new List<(string Name, Action Run)>();
void Check(string name, Action check) => checks.Add((name, check));
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

Check("Compact keeps small windows usable", () => Equal(WidgetSize.Compact, WidgetLayout.ForSize(288, 88)));
Check("Width alone never adds controls that do not fit", () => Equal(WidgetSize.Compact, WidgetLayout.ForSize(900, 88)));
Check("Height alone never adds controls that do not fit", () => Equal(WidgetSize.Compact, WidgetLayout.ForSize(288, 500)));
Check("Comfortable layout appears when both axes fit", () => Equal(WidgetSize.Comfortable, WidgetLayout.ForSize(380, 126)));
Check("Expanded controls require sufficient height", () => Equal(WidgetSize.Comfortable, WidgetLayout.ForSize(400, 189)));
Check("Expanded controls fit at the boundary", () => Equal(WidgetSize.Expanded, WidgetLayout.ForSize(400, 190)));

var captured = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
var playing = new PlaybackSnapshot { IsPlaying = true, Position = TimeSpan.FromSeconds(20), Duration = TimeSpan.FromSeconds(120), CapturedAt = captured };
Check("Progress advances without network polling", () => Equal(TimeSpan.FromSeconds(23), playing.PositionAt(captured.AddSeconds(3))));
Check("Paused progress stays still", () => Equal(TimeSpan.FromSeconds(20), (playing with { IsPlaying = false }).PositionAt(captured.AddMinutes(1))));
Check("Progress stops at duration", () => Equal(TimeSpan.FromSeconds(120), playing.PositionAt(captured.AddHours(1))));
Check("Clock rollback does not move progress backward", () => Equal(TimeSpan.FromSeconds(20), playing.PositionAt(captured.AddSeconds(-20))));
Check("Negative source progress is clamped", () => Equal(TimeSpan.Zero, (playing with { Position = TimeSpan.FromSeconds(-10), IsPlaying = false }).PositionAt(captured)));
Check("Empty playback has no progress", () => Equal(TimeSpan.Zero, PlaybackSnapshot.Empty.PositionAt(captured)));
Check("Unknown duration has no unbounded progress", () => Equal(TimeSpan.Zero, (playing with { Duration = TimeSpan.Zero }).PositionAt(captured.AddHours(1))));

Check("Window dimensions are bounded after loading", () =>
{
    var value = new WidgetSettings { Width = -40, Height = 90000 }.Validated();
    Equal(288d, value.Width); Equal(1000d, value.Height);
});
Check("Nonfinite settings recover to usable values", () =>
{
    var value = new WidgetSettings { Width = double.NaN, Height = double.PositiveInfinity, Left = double.NegativeInfinity, Top = double.NaN, GlassOpacity = double.NaN }.Validated();
    Equal(320d, value.Width); Equal(96d, value.Height); Equal<double?>(null, value.Left); Equal<double?>(null, value.Top); Equal(.48, value.GlassOpacity);
});
Check("Negative monitor coordinates are preserved", () => Equal<double?>(-1920, new WidgetSettings { Left = -1920 }.Validated().Left));
Check("Extreme saved coordinates remain valid for Windows", () => Equal<double?>(100000, new WidgetSettings { Left = double.MaxValue }.Validated().Left));
Check("Tint opacity cannot hide the widget", () => Equal(.18, new WidgetSettings { GlassOpacity = 0 }.Validated().GlassOpacity));
Check("Settings round trip keeps user preferences", () =>
{
    var settings = new WidgetSettings { Width = 480, Height = 220, Left = -800, Top = 45, IgnoreClicks = true, Topmost = false, GlassOpacity = .6 };
    Equal(settings, JsonSerializer.Deserialize<WidgetSettings>(JsonSerializer.Serialize(settings))!.Validated());
});

var failed = 0;
foreach (var check in checks)
{
    try { check.Run(); Console.WriteLine($"PASS {check.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {check.Name}: {ex.Message}"); }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} checks passed.");
return failed == 0 ? 0 : 1;
