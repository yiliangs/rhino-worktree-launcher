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
                selected,
                CancellationToken.None))
            {
                using RegistryKey leased = Registry.CurrentUser.OpenSubKey(keyPath)!;
                Assert.Equal(Path.GetFullPath(selected), leased.GetValue("FileName"));
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
                temporary.PathFor("first/Sample.rhp"),
                CancellationToken.None);
            Task<PluginRegistrationLease> secondTask = PluginRegistrationLease.AcquireAsync(
                locks,
                8,
                pluginId,
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

    private static string PluginRootKeyPath(string pluginId) =>
        $@"Software\McNeel\Rhinoceros\8.0\Plug-ins\{pluginId}";

    private static string PluginKeyPath(string pluginId) => PluginRootKeyPath(pluginId) + @"\PlugIn";
}
