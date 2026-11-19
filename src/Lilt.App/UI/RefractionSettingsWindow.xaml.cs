using System.Windows;
using Lilt.Core;

namespace Lilt.App.UI;

public partial class RefractionSettingsWindow : Window
{
    private bool _ready;

    public RefractionSettingsWindow(RefractionSettings settings)
    {
        InitializeComponent();
        SetOptions(settings);
    }

    public event Action<RefractionSettings>? OptionsChanged;

    private void SetOptions(RefractionSettings settings)
    {
        _ready = false;
        BendSlider.Value = settings.Bend;
        SoftnessSlider.Value = settings.Softness;
        ColorSlider.Value = settings.ColorSplit;
        ReducedResolution.IsChecked = settings.ReducedResolution;
        _ready = true;
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        OptionsChanged?.Invoke(new RefractionSettings
        {
            Bend = BendSlider.Value,
            Softness = SoftnessSlider.Value,
            ColorSplit = ColorSlider.Value,
            ReducedResolution = ReducedResolution.IsChecked == true
        }.Validated());
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var defaults = new RefractionSettings();
        SetOptions(defaults);
        OptionsChanged?.Invoke(defaults);
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
