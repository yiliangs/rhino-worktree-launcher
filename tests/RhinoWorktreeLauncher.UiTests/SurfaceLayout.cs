using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// Runs a real WPF measure and arrange pass over a surface and reports where its named
/// parts actually landed. Alignment is a property of the arranged tree, not of the markup:
/// a margin only says what an element reserves, and WPF decides the rest from the
/// alignment, the available box, and whatever the neighbouring content measured to. So a
/// test that reads the markup back can only restate the numbers that produced the defect,
/// while these bounds come from the same layout pass the window itself runs.
/// </summary>
internal static class SurfaceLayout
{
    // One STA thread for the whole run. Resource lookup reaches Application.Current, which
    // has thread affinity, so every surface has to be built where the Application was.
    private static readonly Dispatcher Dispatcher = StartHost();

    /// <summary>
    /// Arranges <paramref name="create"/>'s content in a <paramref name="client"/>-sized box
    /// and returns the rendered bounds of every part carrying an x:Name.
    /// </summary>
    public static IReadOnlyDictionary<string, Rect> Arrange(Func<Window> create, Size client) =>
        Dispatcher.Invoke(() =>
        {
            Window window = create();
            FrameworkElement content = (FrameworkElement)window.Content;

            // The content is measured on its own rather than through the window, because a
            // window that is never shown never builds its chrome and so never lays out.
            window.Content = null;
            content.Measure(client);
            content.Arrange(new Rect(client));
            content.UpdateLayout();

            Dictionary<string, Rect> bounds = new Dictionary<string, Rect>(StringComparer.Ordinal);
            Collect(content, content, bounds);
            return bounds;
        });

    private static void Collect(
        DependencyObject visual,
        Visual root,
        Dictionary<string, Rect> bounds)
    {
        // A detached tree is never IsVisible, so the declared visibility is what can be
        // asked here. Template parts repeat their names once per templated control, and
        // the first arranged one wins so that a lookup is deterministic.
        if (visual is FrameworkElement { Name.Length: > 0, Visibility: Visibility.Visible } element &&
            !bounds.ContainsKey(element.Name))
        {
            bounds[element.Name] = element
                .TransformToAncestor(root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }

        int count = VisualTreeHelper.GetChildrenCount(visual);
        for (int index = 0; index < count; index++)
            Collect(VisualTreeHelper.GetChild(visual, index), root, bounds);
    }

    private static Dispatcher StartHost()
    {
        TaskCompletionSource<Dispatcher> ready = new TaskCompletionSource<Dispatcher>();
        Thread thread = new Thread(() =>
        {
            new App().InitializeComponent();
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}

internal static class RenderedBounds
{
    public static Rect Part(this IReadOnlyDictionary<string, Rect> bounds, string name) =>
        bounds.TryGetValue(name, out Rect rect)
            ? rect
            : throw new Xunit.Sdk.XunitException(
                $"No visible part named '{name}' was arranged. Arranged parts: " +
                string.Join(", ", bounds.Keys.OrderBy(key => key, StringComparer.Ordinal)));

    public static double CentreX(this Rect rect) => rect.X + rect.Width / 2;

    public static double CentreY(this Rect rect) => rect.Y + rect.Height / 2;
}
