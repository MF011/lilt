using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lilt.App.Platform;
using Lilt.Core;

namespace Lilt.App.UI;

/// <summary>Exercises the actual WPF tree only when the app is launched with --smoke-test.</summary>
public static class NativeSmokeChecks
{
    public static async Task RunAsync(MainWindow window, WidgetViewModel vm, WindowBehavior behavior)
    {
        if (!window.Dispatcher.CheckAccess())
        {
            await window.Dispatcher.InvokeAsync(() => RunAsync(window, vm, behavior)).Task.Unwrap();
            return;
        }

        var originalSnapshot = vm.Snapshot;
        var originalArtwork = vm.Artwork is { } artwork ? EncodePng(artwork) : null;
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        var originalBusy = vm.Busy;
        var originalLikeEnabled = vm.LikeEnabled;
        var originalSaved = vm.IsSaved;
        var originalLikeHint = vm.LikeHint;
        var originalConnection = vm.ConnectionLabel;
        var originalNotice = vm.Notice;
        var handle = new WindowInteropHelper(window).Handle;
        Require(handle != IntPtr.Zero, "The native widget handle was not created.");
        var originalClickThrough = (NativeMethods.GetExStyle(handle).ToInt64() & NativeMethods.WsExTransparent) != 0;
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "smoke");
        Directory.CreateDirectory(output);
        var report = new List<string>
        {
            "Native WPF layout captures from --smoke-test.",
            "The track and artwork are test fixtures. Desktop blur is not captured by RenderTargetBitmap.",
            ""
        };

        try
        {
            behavior.SetClickThrough(true);
            var transparentStyle = NativeMethods.GetExStyle(handle).ToInt64();
            Require((transparentStyle & NativeMethods.WsExTransparent) != 0, "Click-through did not set WS_EX_TRANSPARENT.");
            Require((transparentStyle & NativeMethods.WsExLayered) != 0, "Click-through did not set WS_EX_LAYERED.");
            Require(NativeMethods.GetLayeredWindowAttributes(handle, out _, out var alpha, out var flags) && alpha == 255 && (flags & 2) != 0,
                "The native click-through surface was not initialized.");
            behavior.SetClickThrough(false);
            Require((NativeMethods.GetExStyle(handle).ToInt64() & NativeMethods.WsExTransparent) == 0,
                "Click-through could not be disabled.");

            var sample = new PlaybackSnapshot
            {
                Title = "Layout check — a deliberately long track title",
                Artist = "Smoke test artists",
                Album = "Native layout test",
                Artwork = CreateTestArtwork(),
                TrackUri = "spotify:track:smoke-test-fixture",
                DeviceName = "Smoke test",
                IsAvailable = true,
                IsPlaying = true,
                CanPlayPause = true,
                CanPrevious = true,
                CanNext = true,
                CanSeek = true,
                CanSetVolume = true,
                VolumePercent = 64,
                Position = TimeSpan.FromSeconds(84),
                Duration = TimeSpan.FromSeconds(228)
            };
            vm.Busy = false;
            vm.Notice = null;
            vm.ConnectionLabel = "Smoke test";
            vm.ApplySnapshot(sample);
            vm.LikeEnabled = true;
            vm.IsSaved = true;
            vm.LikeHint = "Remove from Liked Songs";

            foreach (var test in new[]
            {
                (Name: "compact", Width: 288d, Height: 88d, Size: WidgetSize.Compact),
                (Name: "comfortable", Width: 400d, Height: 136d, Size: WidgetSize.Comfortable),
                (Name: "expanded", Width: 440d, Height: 220d, Size: WidgetSize.Expanded)
            })
            {
                window.Width = test.Width;
                window.Height = test.Height;
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Require(vm.Size == test.Size, $"{test.Name}: expected {test.Size}, got {vm.Size}.");
                Require(window.Content is FrameworkElement, "The widget has no WPF content root.");
                var content = (FrameworkElement)window.Content;
                Require(content.ActualWidth > 0 && content.ActualHeight > 0, $"{test.Name}: content has no size.");
                Require(Math.Abs(content.ActualWidth - window.ActualWidth) < 1 && Math.Abs(content.ActualHeight - window.ActualHeight) < 1,
                    $"{test.Name}: a native frame is taking space away from the card.");
                var elements = Descendants(content).OfType<FrameworkElement>().Where(element => element.IsVisible).ToArray();
                var buttons = elements.OfType<Button>().ToArray();
                Require(buttons.Length == 4, $"{test.Name}: three transport buttons and the cover Like button must exist.");
                Require(buttons.All(button => button.IsEnabled), $"{test.Name}: a fixture capability was not reflected by its button.");
                foreach (var button in buttons) RequireInside(content, button, test.Name);

                var coverHost = window.FindName("AlbumCover") as FrameworkElement;
                var likeOverlay = window.FindName("LikeOverlay") as Button;
                Require(coverHost is not null && likeOverlay is not null, $"{test.Name}: the cover Like overlay is missing.");
                RequireInside(coverHost!, likeOverlay!, test.Name);
                Require(likeOverlay!.Focusable && System.Windows.Automation.AutomationProperties.GetName(likeOverlay) == vm.SaveLabel,
                    $"{test.Name}: the cover Like button must remain accessible.");
                // Moving the window can leave the pointer over it. A pointer outside the
                // cover is the state in which the artwork must remain unobscured.
                await Task.Delay(200);
                if (!coverHost!.IsMouseOver && !likeOverlay.IsKeyboardFocusWithin)
                {
                    Require(likeOverlay.Opacity <= .01 && !likeOverlay.IsHitTestVisible,
                        $"{test.Name}: the inactive cover Like overlay must be hidden and pass pointer hits to the cover.");
                    Require(buttons.Count(button => button.Opacity > .01) == 3,
                        $"{test.Name}: only three transport buttons should be shown outside the cover.");
                }
                else
                {
                    Require(likeOverlay.Opacity >= .99 && likeOverlay.IsHitTestVisible,
                        $"{test.Name}: hovering or focusing the cover must reveal its Like button.");
                }

                var title = elements.OfType<TextBlock>().SingleOrDefault(text => text.Text == sample.Title);
                var artist = elements.OfType<TextBlock>().SingleOrDefault(text => text.Text == sample.Artist);
                Require(title is not null && title.ActualWidth >= 32, $"{test.Name}: the title has no usable space.");
                Require(artist is not null && artist.ActualWidth >= 32, $"{test.Name}: the artist has no usable space.");
                RequireInside(content, title!, test.Name);
                RequireInside(content, artist!, test.Name);
                Require(title!.TextTrimming == TextTrimming.CharacterEllipsis && artist!.TextTrimming == TextTrimming.CharacterEllipsis,
                    $"{test.Name}: long metadata must be trimmed.");

                var albumVisible = elements.OfType<TextBlock>().Any(text => text.Text == sample.Album);
                Require(albumVisible == (test.Size != WidgetSize.Compact), $"{test.Name}: album visibility does not match the layout.");
                var cover = elements.OfType<Border>().SingleOrDefault(border => border.Background is ImageBrush);
                Require(cover is not null && cover.ActualWidth >= 40 && cover.ActualHeight >= 40, $"{test.Name}: the album cover is missing.");
                RequireInside(content, cover!, test.Name);

                var seek = window.FindName("SeekControl") as Slider;
                var volume = window.FindName("VolumeControl") as Slider;
                Require(seek is not null && volume is not null, "The expanded controls could not be found.");
                Require(seek!.IsVisible == (test.Size == WidgetSize.Expanded), $"{test.Name}: seek visibility is incorrect.");
                Require(volume!.IsVisible == (test.Size == WidgetSize.Expanded), $"{test.Name}: volume visibility is incorrect.");
                if (test.Size == WidgetSize.Expanded)
                {
                    RequireInside(content, seek, test.Name);
                    RequireInside(content, volume, test.Name);
                    foreach (var text in elements.OfType<TextBlock>()) RequireInside(content, text, test.Name);
                }

                SaveVisual(content, Path.Combine(output, test.Name + ".png"));
                await Task.Delay(100);
                CaptureScreen(window, Path.Combine(output, test.Name + "-screen.png"), report);
                behavior.SetClickThrough(true);
                await Task.Delay(100);
                Require((NativeMethods.GetExStyle(handle).ToInt64() & NativeMethods.WsExLayered) != 0,
                    $"{test.Name}: the layered style was lost after rendering.");
                CaptureScreen(window, Path.Combine(output, test.Name + "-click-through.png"), report);
                behavior.SetClickThrough(false);
                report.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{test.Name}: {window.ActualWidth:0.##} × {window.ActualHeight:0.##} DIP; content {content.ActualWidth:0.##} × {content.ActualHeight:0.##}; three transport controls and cover Like; passed"));
            }
            File.WriteAllLines(Path.Combine(output, "layout.txt"), report);
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), exception.ToString());
            throw;
        }
        finally
        {
            behavior.SetClickThrough(originalClickThrough);
            window.Width = originalWidth;
            window.Height = originalHeight;
            vm.ApplySnapshot(originalSnapshot);
            vm.SetArtwork(originalArtwork);
            vm.Busy = originalBusy;
            vm.LikeEnabled = originalLikeEnabled;
            vm.IsSaved = originalSaved;
            vm.LikeHint = originalLikeHint;
            vm.ConnectionLabel = originalConnection;
            vm.Notice = originalNotice;
            window.UpdateLayout();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RequireInside(FrameworkElement root, FrameworkElement element, string layout)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        const double tolerance = .75;
        Require(bounds.Left >= -tolerance && bounds.Top >= -tolerance &&
                bounds.Right <= root.ActualWidth + tolerance && bounds.Bottom <= root.ActualHeight + tolerance,
            $"{layout}: {element.GetType().Name} extends outside the widget ({bounds}).");
    }

    private static void SaveVisual(FrameworkElement element, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(element);
        File.WriteAllBytes(file, EncodePng(image));
    }

    private static byte[] CreateTestArtwork()
    {
        const int size = 96;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var offset = (y * size + x) * 4;
            var glow = Math.Max(0, 1 - Math.Sqrt(Math.Pow((x - 35) / 68d, 2) + Math.Pow((y - 31) / 68d, 2)));
            pixels[offset] = (byte)(34 + glow * 70);
            pixels[offset + 1] = (byte)(26 + glow * 124);
            pixels[offset + 2] = (byte)(14 + glow * 101);
            pixels[offset + 3] = 255;
        }
        return EncodePng(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4));
    }

    private static void CaptureScreen(MainWindow window, string file, List<string> report)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return;
        try
        {
            using var bitmap = new System.Drawing.Bitmap(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            var screen = NativeMethods.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var target = IntPtr.Zero;
            try
            {
                target = graphics.GetHdc();
                // SRCCOPY | CAPTUREBLT includes layered windows. Graphics.CopyFromScreen
                // rejects this valid Win32 flag combination on current .NET.
                if (!NativeMethods.BitBlt(target, 0, 0, bitmap.Width, bitmap.Height, screen, rect.Left, rect.Top, 0x40CC0020))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally
            {
                if (target != IntPtr.Zero) graphics.ReleaseHdc(target);
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            report.Add("Desktop capture was unavailable in this Windows session.");
        }
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
