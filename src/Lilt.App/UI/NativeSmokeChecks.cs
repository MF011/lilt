using System.Globalization;
using System.Runtime.InteropServices;
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
        var originalLeft = window.Left;
        var originalTop = window.Top;
        var originalOpaque = vm.OpaqueBackground;
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
            "The track and artwork are test fixtures.",
            ""
        };

        try
        {
            Require(window.AllowsTransparency && window.ResizeMode == ResizeMode.NoResize,
                "The card needs a per-pixel layer with no native resize frame.");
            Require((NativeMethods.GetExStyle(handle).ToInt64() & NativeMethods.WsExLayered) != 0,
                "WPF did not create a per-pixel layer.");
            behavior.SetClickThrough(true);
            var transparentStyle = NativeMethods.GetExStyle(handle).ToInt64();
            Require((transparentStyle & NativeMethods.WsExTransparent) != 0, "Click-through did not set WS_EX_TRANSPARENT.");
            Require((transparentStyle & NativeMethods.WsExLayered) != 0, "Click-through did not set WS_EX_LAYERED.");
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

            if (Environment.GetCommandLineArgs().Contains("--pointer-check", StringComparer.OrdinalIgnoreCase))
            {
                await CheckPointerGesturesAsync(window, handle, report);
                await CheckDesktopGlassAsync(window, handle, vm, behavior, output, report);
            }

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
                behavior.RestoreToVisibleScreen();
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
                var cover = Descendants(coverHost!).OfType<Border>().SingleOrDefault(border => border.IsVisible && border.Background is ImageBrush);
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

                var visual = SaveVisual(content, Path.Combine(output, test.Name + ".png"));
                Require(AlphaAt(visual, .001, .001) <= 8, $"{test.Name}: the corner is no longer transparent.");
                var tintAlpha = AlphaAt(visual, .5, .12);
                Require(tintAlpha is > 28 and < 240, $"{test.Name}: the glass body is not translucent (alpha {tintAlpha}).");
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
            File.WriteAllLines(Path.Combine(output, "layout.txt"), report);
            File.WriteAllText(Path.Combine(output, "failure.txt"), exception.ToString());
            throw;
        }
        finally
        {
            behavior.SetClickThrough(originalClickThrough);
            window.Width = originalWidth;
            window.Height = originalHeight;
            window.Left = originalLeft;
            window.Top = originalTop;
            vm.OpaqueBackground = originalOpaque;
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

    private static RenderTargetBitmap SaveVisual(FrameworkElement element, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(element);
        File.WriteAllBytes(file, EncodePng(image));
        return image;
    }

    private static byte AlphaAt(BitmapSource image, double x, double y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect((int)(image.PixelWidth * x), (int)(image.PixelHeight * y), 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static async Task CheckPointerGesturesAsync(MainWindow window, IntPtr handle, List<string> report)
    {
        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Left + 40;
        window.Top = workArea.Top + 40;
        window.UpdateLayout();
        await Task.Delay(150);
        Require(NativeMethods.GetWindowRect(handle, out var start), "Could not locate the widget for pointer checks.");
        Require(GetCursorPos(out var originalPointer), "Could not read the desktop pointer.");
        try
        {
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            var inset = (int)Math.Ceiling(13 * scale);
            await DragPointerAsync(start.Right - inset, start.Bottom - inset, 90, 72);
            Require(NativeMethods.GetWindowRect(handle, out var resized), "Could not read the resized widget bounds.");
            Require(resized.Right - resized.Left >= start.Right - start.Left + 70 &&
                    resized.Bottom - resized.Top >= start.Bottom - start.Top + 55,
                $"Dragging the visible corner did not resize the card ({start.Right - start.Left} × {start.Bottom - start.Top} to {resized.Right - resized.Left} × {resized.Bottom - resized.Top}).");
            await DragPointerAsync((resized.Left + resized.Right) / 2, resized.Top + inset, 32, 24);
            Require(NativeMethods.GetWindowRect(handle, out var moved), "Could not read the moved widget bounds.");
            Require(Math.Abs(moved.Left - resized.Left - 32) <= 2 && Math.Abs(moved.Top - resized.Top - 24) <= 2,
                "Dragging the body did not move the card.");
            Require(moved.Right - moved.Left == resized.Right - resized.Left && moved.Bottom - moved.Top == resized.Bottom - resized.Top,
                "Moving the card changed its size.");
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            Require(Math.Abs(content.ActualWidth - window.ActualWidth) < 1 && Math.Abs(content.ActualHeight - window.ActualHeight) < 1,
                "A native frame appeared after releasing the pointer.");
            Require(Math.Abs(window.Width - (moved.Right - moved.Left) / scale) < 2 &&
                    Math.Abs(window.Height - (moved.Bottom - moved.Top) / scale) < 2,
                "The resized dimensions were not retained by WPF.");
            report.Add("Desktop pointer: corner resize and body move completed without a native frame.");
        }
        finally { SetCursorPos(originalPointer.X, originalPointer.Y); }
    }

    private static async Task DragPointerAsync(int x, int y, int dx, int dy)
    {
        Require(SetCursorPos(x, y), "Could not place the desktop pointer.");
        await Task.Delay(120);
        SendMouseButton(0x0002);
        try
        {
            await Task.Delay(100);
            Require(SetCursorPos(x + dx, y + dy), "Could not move the desktop pointer.");
            await Task.Delay(180);
        }
        finally { SendMouseButton(0x0004); }
        await Task.Delay(120);
    }

    private static async Task CheckDesktopGlassAsync(MainWindow window, IntPtr handle, WidgetViewModel vm, WindowBehavior behavior,
        string output, List<string> report)
    {
        var panel = new Border { Background = Brushes.MidnightBlue };
        var backdrop = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, Content = panel
        };
        var originalOpaque = vm.OpaqueBackground;
        try
        {
            Require(NativeMethods.GetWindowRect(handle, out var rect), "Could not locate the widget over the test background.");
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var scale = VisualTreeHelper.GetDpi(window);
            backdrop.Left = (rect.Left - 12) / scale.DpiScaleX;
            backdrop.Top = (rect.Top - 12) / scale.DpiScaleY;
            backdrop.Width = (width + 24) / scale.DpiScaleX;
            backdrop.Height = (height + 24) / scale.DpiScaleY;
            backdrop.Show();
            var backdropHandle = new WindowInteropHelper(backdrop).Handle;
            Require(NativeMethods.SetWindowPos(backdropHandle, new IntPtr(-1), rect.Left - 12, rect.Top - 12,
                width + 24, height + 24, NativeMethods.SwpNoActivate), "Could not place the glass test background.");
            Require(NativeMethods.SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize), "Could not raise the widget above the test background.");
            var x = width / 2;
            var y = height / 10;
            vm.OpaqueBackground = false;
            var visual = SaveVisual((FrameworkElement)window.Content, Path.Combine(output, "glass-alpha.png"));
            report.Add($"Rendered glass alpha at the desktop probe: {AlphaAt(visual, .5, .1)}/255.");
            using var light = await ReadFixtureAsync(panel, rect, Colors.White);
            using var dark = await ReadFixtureAsync(panel, rect, Colors.Black);
            light.Save(Path.Combine(output, "glass-over-white.png"), System.Drawing.Imaging.ImageFormat.Png);
            dark.Save(Path.Combine(output, "glass-over-black.png"), System.Drawing.Imaging.ImageFormat.Png);
            var cornerDifference = light.GetPixel(0, 0).R - dark.GetPixel(0, 0).R;
            Require(cornerDifference > 220, $"The test background was not visible at the transparent corner (difference {cornerDifference}).");
            var difference = light.GetPixel(x, y).R - dark.GetPixel(x, y).R;
            Require(difference is > 20 and < 245, $"The desktop did not blend through the glass body (difference {difference}).");
            using var whiteFixture = await ReadFixtureAsync(panel, rect, Colors.White);
            vm.OpaqueBackground = true;
            await Task.Delay(120);
            using var solid = ReadDesktop(rect);
            Require(solid.GetPixel(x, y).R < light.GetPixel(x, y).R - 20, "Glass off did not restore the opaque card.");
            report.Add($"Desktop glass: the same interior pixel changed by {difference}/255 red over black and white backgrounds.");
            vm.OpaqueBackground = false;
            await CheckDesktopRefractionAsync(window, rect, vm, behavior, panel, output, report);
        }
        finally
        {
            vm.OpaqueBackground = originalOpaque;
            backdrop.Close();
        }
    }

    private static async Task<System.Drawing.Bitmap> ReadFixtureAsync(Border panel, NativeMethods.Rect rect, Color color)
    {
        panel.Background = new SolidColorBrush(color);
        panel.UpdateLayout();
        await panel.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(80);
            var image = ReadDesktop(rect);
            // The corner lies outside the rounded card, so it is a reference pixel
            // for the backing window rather than a test of Lilt's tint.
            var pixel = image.GetPixel(0, 0);
            if (Math.Abs(pixel.R - color.R) <= 8 && Math.Abs(pixel.G - color.G) <= 8 && Math.Abs(pixel.B - color.B) <= 8)
                return image;
            image.Dispose();
        }
        throw new InvalidOperationException($"The controlled background did not paint {color} at the open corner.");
    }

    private static async Task CheckDesktopRefractionAsync(MainWindow window, NativeMethods.Rect rect, WidgetViewModel vm,
        WindowBehavior behavior, Border panel, string output, List<string> report)
    {
        panel.Background = CreateGridBrush(Color.FromRgb(28, 160, 151));
        await Task.Delay(200);
        using var plain = ReadDesktop(rect);
        plain.Save(Path.Combine(output, "refraction-off.png"), System.Drawing.Imaging.ImageFormat.Png);

        using var lens = new GlassLens(window, vm, behavior);
        lens.SetEnabled(true);
        for (var attempt = 0; attempt < 30 && vm.RefractedBackground is null; attempt++)
            await Task.Delay(100);

        Require(lens.IsAvailable == true && vm.RefractedBackground is not null,
            "Refraction could not obtain a desktop sample without the widget's own layer.");
        using var refracted = ReadDesktop(rect);
        refracted.Save(Path.Combine(output, "refraction-on.png"), System.Drawing.Imaging.ImageFormat.Png);
        var firstFrame = EncodePng(vm.RefractedBackground!);

        panel.Background = CreateGridBrush(Color.FromRgb(211, 117, 81));
        var changed = false;
        for (var attempt = 0; attempt < 20 && !changed; attempt++)
        {
            await Task.Delay(100);
            if (vm.RefractedBackground is { } frame) changed = !EncodePng(frame).AsSpan().SequenceEqual(firstFrame);
        }
        Require(changed, "Refraction did not update when the window behind the card changed.");
        lens.SetEnabled(false);
        Require(vm.RefractedBackground is null, "Disabling refraction did not clear the lens frame.");
        report.Add("Desktop refraction: the capture excluded the card, the lens rendered, and changing the backdrop updated the frame.");
    }

    private static DrawingBrush CreateGridBrush(Color color)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 29, 48)), null, new Rect(0, 0, 32, 32));
            var line = new Pen(new SolidColorBrush(color), 2);
            context.DrawLine(line, new Point(3, 0), new Point(3, 32));
            context.DrawLine(line, new Point(0, 3), new Point(32, 3));
        }
        drawing.Freeze();
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 32, 32),
            Stretch = Stretch.Fill
        };
    }

    private static void SendMouseButton(uint flags)
    {
        var input = new MouseInput { Data = new MouseInputData { Flags = flags } };
        Require(SendInput(1, ref input, Marshal.SizeOf<MouseInput>()) == 1, "Could not send pointer input to the test desktop.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public uint Type; public MouseInputData Data; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref MouseInput input, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

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
            using var bitmap = ReadDesktop(rect);
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            report.Add("Desktop capture was unavailable in this Windows session.");
        }
    }

    private static System.Drawing.Bitmap ReadDesktop(NativeMethods.Rect rect)
    {
        var bitmap = new System.Drawing.Bitmap(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
        try
        {
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
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
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
