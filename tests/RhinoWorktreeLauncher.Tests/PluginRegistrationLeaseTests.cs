using Microsoft.Win32;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class PluginRegistrationLeaseTests
{
    [Fact]
    public async Task Lease_redirects_and_restores_an_existing_registration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string pluginId = Guid.NewGuid().ToString("D");
        string keyPath = PluginKeyPath(pluginId);
        string original = temporary.PathFor("primary/Sample.rhp");
        string selected = temporary.PathFor("selected/Sample.rhp");
        using (RegistryKey existing = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            existing.SetValue("FileName", original, RegistryValueKind.String);

        try
        {
            using (await PluginRegistrationLease.AcquireAsync(
                temporary.CreateDirectory("locks"),
                8,
                pluginId,
                "Sample",
                selected,
                CancellationToken.None))
            {
                using RegistryKey leased = Registry.CurrentUser.OpenSubKey(keyPath)!;
                Assert.Equal(Path.GetFullPath(selected), leased.GetValue("FileName"));
                using RegistryKey leasedRoot = Registry.CurrentUser.OpenSubKey(PluginRootKeyPath(pluginId))!;
                Assert.Equal(1, leasedRoot.GetValue("LoadMode"));
            }

            using RegistryKey restored = Registry.CurrentUser.OpenSubKey(keyPath)!;
            Assert.Equal(original, restored.GetValue("FileName"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(PluginRootKeyPath(pluginId), throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Lease_restores_the_original_registry_value_kind()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string pluginId = Guid.NewGuid().ToString("D");
        string keyPath = PluginKeyPath(pluginId);
        const string original = @"%TEMP%\Sample\Sample.rhp";
        string selected = temporary.PathFor("selected/Sample.rhp");
        using (RegistryKey existing = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            existing.SetValue("FileName", original, RegistryValueKind.ExpandString);

        try
        {
            using (await PluginRegistrationLease.AcquireAsync(
                temporary.CreateDirectory("locks"),
                8,
                pluginId,
                "Sample",
                selected,
                CancellationToken.None))
            {
            }

            using RegistryKey restored = Registry.CurrentUser.OpenSubKey(keyPath)!;
            Assert.Equal(RegistryValueKind.ExpandString, restored.GetValueKind("FileName"));
            Assert.Equal(
                original,
                restored.GetValue("FileName", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(PluginRootKeyPath(pluginId), throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Same_plugin_leases_serialize_across_callers()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string pluginId = Guid.NewGuid().ToString("D");
        string keyPath = PluginKeyPath(pluginId);
        string locks = temporary.CreateDirectory("locks");
        using (RegistryKey existing = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            existing.SetValue("FileName", temporary.PathFor("primary/Sample.rhp"), RegistryValueKind.String);

        try
        {
            using PluginRegistrationLease first = await PluginRegistrationLease.AcquireAsync(
                locks,
                8,
                pluginId,
                "Sample",
                temporary.PathFor("first/Sample.rhp"),
                CancellationToken.None);
            Task<PluginRegistrationLease> secondTask = PluginRegistrationLease.AcquireAsync(
                locks,
                8,
                pluginId,
                "Sample",
                temporary.PathFor("second/Sample.rhp"),
                CancellationToken.None);

            await Task.Delay(250);
            Assert.False(secondTask.IsCompleted);

            first.Dispose();
            using PluginRegistrationLease second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(PluginRootKeyPath(pluginId), throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Lease_seeds_an_unseen_plugin_with_the_documented_install_registration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string pluginId = Guid.NewGuid().ToString("D");
        string rootPath = PluginRootKeyPath(pluginId);
        string selected = temporary.PathFor("selected/Sample.rhp");

        try
        {
            using (await PluginRegistrationLease.AcquireAsync(
                temporary.CreateDirectory("locks"),
                8,
                pluginId,
                "Sample",
                selected,
                CancellationToken.None))
            {
                using RegistryKey root = Registry.CurrentUser.OpenSubKey(rootPath)!;
                Assert.Equal("Sample", root.GetValue("Name"));
                Assert.Equal(Path.GetFullPath(selected), root.GetValue("FileName"));
                // The seed must stay exactly Name and FileName: extra values risk
                // reading as an already installed registration, which Rhino ignores.
                Assert.Equal(2, root.GetValueNames().Length);
                Assert.Empty(root.GetSubKeyNames());
            }

            Assert.Null(Registry.CurrentUser.OpenSubKey(rootPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task Lease_restores_every_value_it_writes_over_an_existing_registration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string pluginId = Guid.NewGuid().ToString("D");
        string rootPath = PluginRootKeyPath(pluginId);
        using (RegistryKey existing = Registry.CurrentUser.CreateSubKey(rootPath, writable: true)!)
        {
            existing.SetValue("Name", "Installed", RegistryValueKind.String);
            existing.SetValue("LoadMode", 2, RegistryValueKind.DWord);
        }

        try
        {
            using (await PluginRegistrationLease.AcquireAsync(
                temporary.CreateDirectory("locks"),
                8,
                pluginId,
                "Sample",
                temporary.PathFor("selected/Sample.rhp"),
                CancellationToken.None))
            {
            }

            using RegistryKey restored = Registry.CurrentUser.OpenSubKey(rootPath)!;
            Assert.Equal("Installed", restored.GetValue("Name"));
            Assert.Equal(2, restored.GetValue("LoadMode"));
            Assert.Null(restored.GetValue("FileName"));
            Assert.Null(Registry.CurrentUser.OpenSubKey(PluginKeyPath(pluginId)));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
        }
    }

    private static string PluginRootKeyPath(string pluginId) =>
        $@"Software\McNeel\Rhinoceros\8.0\Plug-ins\{pluginId}";

    private static string PluginKeyPath(string pluginId) => PluginRootKeyPath(pluginId) + @"\PlugIn";
}
