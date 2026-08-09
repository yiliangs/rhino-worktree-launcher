using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RhinoWorktreeLauncher.UiTests;

public sealed class CheckboxStyleTests
{
    [Fact]
    public void McpSessionContextMatchesProjectSettingsCheckboxAppearance() =>
        RunInSta(() =>
        {
            App app = new App();
            app.InitializeComponent();

            MainWindow mainWindow = new MainWindow(new LauncherBackend());
            _ = new WindowInteropHelper(mainWindow).EnsureHandle();
            ProjectSettingsDialog settingsWindow = new ProjectSettingsDialog(new ProjectRegistration(
                    "project-id",
                    "Project",
                    @"C:\repo\.git",
                    @"C:\repo",
                    8,
                    ProjectAccessGrant.Full,
                    BuildProfile.Unconfigured))
            {
                Owner = mainWindow
            };
            settingsWindow.Show();
            settingsWindow.Hide();

            CheckBox mcpCheckBox = Assert.IsType<CheckBox>(
                mainWindow.FindName("ClaudeSessionContextCheckBox"));
            CheckBox settingsCheckBox = Assert.IsType<CheckBox>(
                settingsWindow.FindName("ClearCacheToggle"));

            mcpCheckBox.IsChecked = true;
            settingsCheckBox.IsChecked = true;

            AssertGlyphsMatch(
                RenderGlyph(settingsCheckBox),
                RenderGlyph(mcpCheckBox));

            settingsWindow.Close();
            mainWindow.Close();
            app.Shutdown();
        });

    private static (int Width, int Height, byte[] Pixels) RenderGlyph(CheckBox checkBox)
    {
        checkBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        checkBox.Arrange(new Rect(checkBox.DesiredSize));
        checkBox.UpdateLayout();

        DpiScale dpi = VisualTreeHelper.GetDpi(checkBox);
        int width = (int)Math.Ceiling(24 * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(24 * dpi.DpiScaleY);
        int stride = width * 4;
        RenderTargetBitmap bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);

        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            VisualBrush brush = new VisualBrush(checkBox)
            {
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Stretch = Stretch.None,
                Viewbox = new Rect(0, 0, 24, 24),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, 24, 24));
        }

        bitmap.Render(visual);

        byte[] pixels = new byte[height * stride];
        bitmap.CopyPixels(pixels, stride, 0);
        return TrimTransparentPadding(width, height, pixels);
    }

    private static (int Width, int Height, byte[] Pixels) TrimTransparentPadding(
        int width,
        int height,
        byte[] pixels)
    {
        int minimumX = width;
        int minimumY = height;
        int maximumX = -1;
        int maximumY = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[((y * width) + x) * 4 + 3] == 0)
                    continue;

                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        }

        if (maximumX < minimumX || maximumY < minimumY)
            throw new InvalidOperationException("The checkbox glyph rendered no visible pixels.");

        int trimmedWidth = maximumX - minimumX + 1;
        int trimmedHeight = maximumY - minimumY + 1;
        int trimmedStride = trimmedWidth * 4;
        byte[] trimmedPixels = new byte[trimmedHeight * trimmedStride];

        for (int y = 0; y < trimmedHeight; y++)
        {
            Buffer.BlockCopy(
                pixels,
                (((minimumY + y) * width) + minimumX) * 4,
                trimmedPixels,
                y * trimmedStride,
                trimmedStride);
        }

        return (trimmedWidth, trimmedHeight, trimmedPixels);
    }

    private static void AssertGlyphsMatch(
        (int Width, int Height, byte[] Pixels) expected,
        (int Width, int Height, byte[] Pixels) actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
