using Microsoft.Win32;

namespace RhinoWorktreeLauncher.Tests;

// The suspension normally targets HKLM; these tests run its full journal, remove, and
// restore cycle against an isolated current-user sandbox standing in for the machine
// Plug-ins key.
public sealed class MachineRegistrationSuspensionTests
{
    [Fact]
    public async Task Suspension_removes_the_registration_and_restores_it_on_dispose()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string sandbox = $@"Software\RhinoWorktreeLauncherTests\{Guid.NewGuid():N}";
        Guid pluginId = Guid.NewGuid();
        string journalPath = temporary.PathFor("locks/suspension.json");
        string lockPath = temporary.PathFor("locks/suspension.lock");
        Directory.CreateDirectory(temporary.PathFor("locks"));
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                $@"{sandbox}\{pluginId:D}\PlugIn", writable: true)!)
                key.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);

            IDisposable? suspension = await MachineRegistrationSuspension.TryAcquireAsync(
                Registry.CurrentUser,
                sandbox,
                journalPath,
                lockPath,
                pluginId,
                CancellationToken.None);

            Assert.NotNull(suspension);
            Assert.True(File.Exists(journalPath));
            using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(sandbox, writable: false)!)
                Assert.Null(parent.OpenSubKey(pluginId.ToString("D"), writable: false));

            suspension!.Dispose();

            Assert.False(File.Exists(journalPath));
            using RegistryKey restored = Registry.CurrentUser.OpenSubKey(
                $@"{sandbox}\{pluginId:D}\PlugIn", writable: false)!;
            Assert.Equal(@"C:\primary\Sample.rhp", restored.GetValue("FileName"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Suspension_without_a_registration_is_a_no_op()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string sandbox = $@"Software\RhinoWorktreeLauncherTests\{Guid.NewGuid():N}";
        Guid pluginId = Guid.NewGuid();
        string journalPath = temporary.PathFor("locks/suspension.json");
        string lockPath = temporary.PathFor("locks/suspension.lock");
        Directory.CreateDirectory(temporary.PathFor("locks"));
        try
        {
            Registry.CurrentUser.CreateSubKey(sandbox, writable: true)!.Dispose();

            IDisposable? suspension = await MachineRegistrationSuspension.TryAcquireAsync(
                Registry.CurrentUser,
                sandbox,
                journalPath,
                lockPath,
                pluginId,
                CancellationToken.None);

            Assert.NotNull(suspension);
            Assert.False(File.Exists(journalPath));
            suspension!.Dispose();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Recovery_restores_a_pending_journal_left_by_a_crashed_suspension()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string sandbox = $@"Software\RhinoWorktreeLauncherTests\{Guid.NewGuid():N}";
        Guid pluginId = Guid.NewGuid();
        string journalPath = temporary.PathFor("locks/suspension.json");
        string lockPath = temporary.PathFor("locks/suspension.lock");
        Directory.CreateDirectory(temporary.PathFor("locks"));
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                $@"{sandbox}\{pluginId:D}\PlugIn", writable: true)!)
                key.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);

            // A crash is a suspension whose process died after removing the key: the
            // journal remains on disk and the registration is gone.
            IDisposable? suspension = await MachineRegistrationSuspension.TryAcquireAsync(
                Registry.CurrentUser,
                sandbox,
                journalPath,
                lockPath,
                pluginId,
                CancellationToken.None);
            Assert.NotNull(suspension);
            string journal = await File.ReadAllTextAsync(journalPath);
            suspension!.Dispose();
            using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(sandbox, writable: true)!)
                parent.DeleteSubKeyTree(pluginId.ToString("D"), throwOnMissingSubKey: false);
            await File.WriteAllTextAsync(journalPath, journal);

            await MachineRegistrationSuspension.RecoverAsync(
                Registry.CurrentUser,
                sandbox,
                journalPath,
                lockPath,
                CancellationToken.None);

            Assert.False(File.Exists(journalPath));
            using RegistryKey restored = Registry.CurrentUser.OpenSubKey(
                $@"{sandbox}\{pluginId:D}\PlugIn", writable: false)!;
            Assert.Equal(@"C:\primary\Sample.rhp", restored.GetValue("FileName"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
        }
    }
}
