using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The README is the public description of a surface nobody can run before installing it,
/// so the image it promises has to be there and has to be named for a reader who cannot
/// see it.
/// </summary>
public sealed class ReadmeScreenshotTests
{
    private static readonly Regex MarkdownImage = new Regex(
        @"!\[(?<alt>[^\]]*)\]\((?<path>[^)\s]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void The_readme_shows_the_desktop_surface()
    {
        MatchCollection images = MarkdownImage.Matches(Readme());

        Assert.True(
            images.Count > 0,
            "The README describes the desktop surface in prose but shows no image of it.");
    }

    [Fact]
    public void Every_image_the_readme_promises_is_committed_beside_it()
    {
        foreach (Match image in MarkdownImage.Matches(Readme()))
        {
            string reference = image.Groups["path"].Value;
            string resolved = Path.Combine(RepositoryRoot(), reference.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(
                File.Exists(resolved),
                $"The README references '{reference}', which is not committed at that path.");
            Assert.True(
                new FileInfo(resolved).Length > 0,
                $"The README references '{reference}', which is committed but empty.");
        }
    }

    [Fact]
    public void Every_image_the_readme_promises_is_named_for_a_reader_who_cannot_see_it()
    {
        foreach (Match image in MarkdownImage.Matches(Readme()))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(image.Groups["alt"].Value),
                $"The README image '{image.Groups["path"].Value}' carries no alternative text.");
        }
    }

    private static string Readme() => File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));

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
