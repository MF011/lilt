namespace Lilt.Core;

public enum WidgetSize { Compact, Comfortable, Expanded }

public static class WidgetLayout
{
    public const double MinimumWidth = 288;
    public const double MinimumHeight = 88;
    public static WidgetSize ForSize(double width, double height) =>
        width >= 400 && height >= 190 ? WidgetSize.Expanded :
        width >= 380 && height >= 126 ? WidgetSize.Comfortable : WidgetSize.Compact;
}
