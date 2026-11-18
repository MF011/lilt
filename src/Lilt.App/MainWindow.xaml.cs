using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lilt.App.UI;
using Lilt.Core;

namespace Lilt.App;

public partial class MainWindow : Window
{
    private readonly WidgetViewModel _viewModel;
    public event Action? DragRequested;
    public event Action? ResizeRequested;

    public MainWindow(WidgetViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Size = WidgetLayout.ForSize(Width, Height);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.Size = WidgetLayout.ForSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void OnBodyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || IsInteractive(e.OriginalSource as DependencyObject)) return;
        DragRequested?.Invoke();
        e.Handled = true;
    }

    private void OnResizeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;
        e.Handled = true;
        ResizeRequested?.Invoke();
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var current = source; current is not null;)
        {
            if (current is ButtonBase or System.Windows.Controls.Slider) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnSeekStarted(object sender, MouseButtonEventArgs e) => _viewModel.IsSeeking = true;
    private void OnSeekCommitted(object sender, MouseButtonEventArgs e) { if (_viewModel.IsSeeking) _viewModel.CommitSeek(); }
    private void OnSeekLostCapture(object sender, MouseEventArgs e) { if (_viewModel.IsSeeking) _viewModel.CommitSeek(); }
    private void OnSeekKeyUp(object sender, System.Windows.Input.KeyEventArgs e) { if (IsSliderKey(e.Key)) _viewModel.CommitSeek(); }
    private void OnVolumeStarted(object sender, MouseButtonEventArgs e) => _viewModel.IsAdjustingVolume = true;
    private void OnVolumeCommitted(object sender, MouseButtonEventArgs e) { if (_viewModel.IsAdjustingVolume) _viewModel.CommitVolume(); }
    private void OnVolumeLostCapture(object sender, MouseEventArgs e) { if (_viewModel.IsAdjustingVolume) _viewModel.CommitVolume(); }
    private void OnVolumeKeyUp(object sender, System.Windows.Input.KeyEventArgs e) { if (IsSliderKey(e.Key)) _viewModel.CommitVolume(); }
    private static bool IsSliderKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
}
