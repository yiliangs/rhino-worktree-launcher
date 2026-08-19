using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Rwl.Bootstrap;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// Installation, exercised against scratch directories rather than a real machine. Nothing
/// here starts a process: the install is the one step a user cannot retry blind, and the
/// suite's process-spawning tests are what fails to terminate on a hosted runner (#65).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerTests
{
    [Fact]
    public void An_install_places_the_payload_the_pointer_and_the_stable_bootstrap()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string package = PayloadIn(temporary, "desktop-contents");
        string dataRoot = temporary.PathFor("data");

        InstallResult result = Installer.Install(Request(package, dataRoot, temporary));

        Assert.True(
            File.Exists(Path.Combine(result.ReleaseDirectory, "desktop", "RhinoWorktreeLauncher.exe")),
            "The desktop component was not installed into the release directory.");
        Assert.True(File.Exists(result.StableBootstrapPath), "The stable bootstrap was not written.");
        Assert.True(File.Exists(result.PointerPath), "The release pointer was not written.");
        Assert.Equal(
            "desktop-contents",
            File.ReadAllText(Path.Combine(result.ReleaseDirectory, "desktop", "RhinoWorktreeLauncher.exe")));
    }

    [Fact]
    public void The_pointer_names_every_component_in_the_casing_its_readers_require()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        InstallResult result = Installer.Install(
            Request(PayloadIn(temporary), temporary.PathFor("data"), temporary));

        JsonElement pointer = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(result.PointerPath));

        // RwlProcessInventory.ReadCurrentReleaseId looks these up with a case-sensitive
        // TryGetProperty, so anything but camel case leaves doctor unable to name the
        // installed release.
        foreach (string component in new[] { "releaseId", "desktop", "cli", "mcp" })
        {
            Assert.True(
                pointer.TryGetProperty(component, out JsonElement value),
                $"The release pointer has no '{component}' property.");
            Assert.False(
                string.IsNullOrWhiteSpace(value.GetString()),
                $"The release pointer's '{component}' is empty.");
        }
        Assert.Equal(
            Path.Combine(result.ReleaseDirectory, "mcp", "rwl-mcp.exe"),
            pointer.GetProperty("mcp").GetString());
    }

    [Fact]
    public void A_second_install_replaces_a_stable_bootstrap_that_is_already_there()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string dataRoot = temporary.PathFor("data");
        Installer.Install(Request(PayloadIn(temporary, "first"), dataRoot, temporary, "20260101-000000-000"));

        InstallResult second = Installer.Install(
            Request(PayloadIn(temporary, "second"), dataRoot, temporary, "20260202-000000-000"));

        Assert.Equal("second", File.ReadAllText(second.StableBootstrapPath));
        Assert.Equal(
            "20260202-000000-000",
            JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(second.PointerPath))
                .GetProperty("releaseId")
                .GetString());
        // Both releases stay on disk. A process started from the first is still running
        // against it, which is exactly what doctor reports on.
        Assert.True(Directory.Exists(Path.Combine(dataRoot, "releases", "20260101-000000-000")));
    }

    [Fact]
    public void An_incomplete_payload_is_refused_before_anything_is_written()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string package = PayloadIn(temporary);
        File.Delete(Path.Combine(package, "mcp", "rwl-mcp.exe"));
        string dataRoot = temporary.PathFor("data");

        FileNotFoundException failure = Assert.Throws<FileNotFoundException>(
            () => Installer.Install(Request(package, dataRoot, temporary)));

        Assert.Contains("rwl-mcp.exe", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            Directory.Exists(Path.Combine(dataRoot, "releases")),
            "A broken package left a half-written installation behind.");
    }

    [Fact]
    public void A_release_id_sorts_by_when_it_was_installed()
    {
        string earlier = Installer.CreateReleaseId(new DateTimeOffset(2026, 8, 19, 11, 10, 41, 738, TimeSpan.Zero));
        string later = Installer.CreateReleaseId(new DateTimeOffset(2026, 8, 19, 11, 10, 41, 739, TimeSpan.Zero));

        Assert.Equal("20260819-111041-738", earlier);
        Assert.True(
            string.CompareOrdinal(earlier, later) < 0,
            "Release directories no longer sort into the order they were installed.");
    }

    [Fact]
    public void The_shortcut_points_at_the_stable_bootstrap_and_opens_the_desktop()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string startMenu = temporary.PathFor("start-menu");

        InstallResult result = Installer.Install(new InstallRequest(
            PayloadIn(temporary),
            temporary.PathFor("data"),
            startMenu,
            "20260819-111041-738"));

        Assert.NotNull(result.ShortcutPath);
        Assert.True(File.Exists(result.ShortcutPath), "No shortcut was written.");

        // Read back through Windows Script Host rather than through the IShellLink the
        // installer wrote with, so the assertion does not depend on the writer being right
        // about its own output.
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(result.ShortcutPath);
        Assert.Equal(result.StableBootstrapPath, (string)shortcut.TargetPath);
        Assert.Equal("desktop", ((string)shortcut.Arguments).Trim());
    }

    private static InstallRequest Request(
        string package,
        string dataRoot,
        TemporaryDirectory temporary,
        string releaseId = "20260819-111041-738") => new InstallRequest(
            package,
            dataRoot,
            temporary.PathFor("start-menu"),
            releaseId)
    {
        // The shortcut writes into a real shell folder and is covered separately.
        CreateShortcut = false
    };

    private static string PayloadIn(TemporaryDirectory temporary, string marker = "payload")
    {
        string package = temporary.PathFor("package-" + marker);
        foreach ((string component, string executable) in new[]
        {
            ("desktop", "RhinoWorktreeLauncher.exe"),
            ("cli", "rwl-cli.exe"),
            ("mcp", "rwl-mcp.exe"),
            ("bootstrap", "rwl.exe")
        })
        {
            Directory.CreateDirectory(Path.Combine(package, component));
            File.WriteAllText(Path.Combine(package, component, executable), marker);
        }
        return package;
    }
}
