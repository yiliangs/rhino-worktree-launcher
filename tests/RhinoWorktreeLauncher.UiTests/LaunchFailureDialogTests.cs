using System.IO;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// A launch failure has two audiences in one window: the person who needs one sentence, and
/// the person who needs the build's own output. `MessageBox` can only serve one, so the
/// surface owns the dialog and keeps the transcript behind a disclosure.
/// </summary>
public sealed class LaunchFailureDialogTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_failure_dialog_leads_with_the_message_and_hides_the_detail()
    {
        XDocument document = LoadDialog();

        // What is always shown.
        Assert.NotNull(FindNamed(document, "FailureMessageText"));
        // What is offered, not shown.
        Assert.NotNull(FindNamed(document, "DetailsToggle"));
        XElement panel = Named(document, "DetailPanel");
        Assert.Equal("Collapsed", panel.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void The_detail_is_scrollable_and_monospaced_because_it_is_build_output()
    {
        XDocument document = LoadDialog();
        XElement detail = Named(document, "DetailText");

        Assert.Equal("{StaticResource MonoFont}", detail.Attribute("FontFamily")?.Value);
        Assert.Contains(
            detail.Ancestors(),
            element => element.Name.LocalName == "ScrollViewer");
    }

    // The failure that is now four lines still has to lead somewhere complete.
    [Fact]
    public void The_dialog_names_the_launch_log()
    {
        Assert.NotNull(FindNamed(LoadDialog(), "LogPathText"));
    }

    [Fact]
    public void The_dialog_carries_the_surface_theme_rather_than_system_chrome()
    {
        XElement window = LoadDialog().Root!;

        Assert.Equal("Window", window.Name.LocalName);
        Assert.Equal("{StaticResource UiFont}", window.Attribute("FontFamily")?.Value);
        Assert.Equal("{DynamicResource WindowBrush}", window.Attribute("Background")?.Value);
        Assert.Equal("CenterOwner", window.Attribute("WindowStartupLocation")?.Value);
    }

    [Fact]
    public void A_launch_failure_is_presented_through_the_dialog_and_not_a_message_box()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "RhinoWorktreeLauncher",
            "MainWindow.xaml.cs"));
        int start = source.IndexOf("private async void Launch(WorktreeSnapshot", StringComparison.Ordinal);
        Assert.True(start >= 0, "The launch handler was not found.");
        int end = source.IndexOf("private void BeginLaunchProgress", start, StringComparison.Ordinal);
        Assert.True(end > start, "The end of the launch handler was not found.");
        string launch = source.Substring(start, end - start);

        Assert.Contains("LaunchFailureDialog", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", launch, StringComparison.Ordinal);
    }

    private static XDocument LoadDialog() => XDocument.Load(Path.Combine(
        RepositoryRoot(),
        "src",
        "RhinoWorktreeLauncher",
        "LaunchFailureDialog.xaml"));

    private static XElement Named(XDocument document, string name) =>
        FindNamed(document, name) ?? throw new XmlSchemaMissingElement(name);

    private static XElement? FindNamed(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Name")?.Value,
            name,
            StringComparison.Ordinal));

    private sealed class XmlSchemaMissingElement : Xunit.Sdk.XunitException
    {
        public XmlSchemaMissingElement(string name)
            : base($"The dialog has no element named '{name}'.")
        {
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhinoWorktreeLauncher.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Repository root was not found from the UI test output directory.");
        return directory.FullName;
    }
}
