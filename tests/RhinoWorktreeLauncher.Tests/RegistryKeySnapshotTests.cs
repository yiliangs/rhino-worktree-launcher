using Microsoft.Win32;
using System.Text.Json;

namespace RhinoWorktreeLauncher.Tests;

public sealed class RegistryKeySnapshotTests
{
    [Fact]
    public void Snapshot_restores_every_value_kind_and_subkey_after_a_journal_round_trip()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string sandbox = $@"Software\RhinoWorktreeLauncherTests\{Guid.NewGuid():N}";
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(sandbox + @"\Sample", writable: true)!)
            {
                key.SetValue("Text", "hello", RegistryValueKind.String);
                key.SetValue("Expand", @"%TEMP%\Sample.rhp", RegistryValueKind.ExpandString);
                key.SetValue("Number", unchecked((int)0x89ABCDEF), RegistryValueKind.DWord);
                key.SetValue("Big", 12345678901234L, RegistryValueKind.QWord);
                key.SetValue("Lines", new[] { "a", "b" }, RegistryValueKind.MultiString);
                key.SetValue("Blob", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
                using RegistryKey sub = key.CreateSubKey("PlugIn", writable: true)!;
                sub.SetValue("FileName", @"C:\worktree\Sample.rhp", RegistryValueKind.String);
            }

            using RegistryKey parent = Registry.CurrentUser.OpenSubKey(sandbox, writable: true)!;
            RegistryKeySnapshot captured;
            using (RegistryKey key = parent.OpenSubKey("Sample", writable: false)!)
                captured = RegistryKeySnapshot.Capture(key);
            RegistryKeySnapshot journaled = JsonSerializer.Deserialize<RegistryKeySnapshot>(
                JsonSerializer.Serialize(captured))!;
            parent.DeleteSubKeyTree("Sample", throwOnMissingSubKey: false);

            journaled.RestoreUnder(parent);

            using RegistryKey restored = parent.OpenSubKey("Sample", writable: false)!;
            Assert.Equal("hello", restored.GetValue("Text"));
            Assert.Equal(RegistryValueKind.ExpandString, restored.GetValueKind("Expand"));
            Assert.Equal(
                @"%TEMP%\Sample.rhp",
                restored.GetValue("Expand", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(unchecked((int)0x89ABCDEF), restored.GetValue("Number"));
            Assert.Equal(12345678901234L, restored.GetValue("Big"));
            Assert.Equal(new[] { "a", "b" }, restored.GetValue("Lines"));
            Assert.Equal(new byte[] { 1, 2, 3 }, restored.GetValue("Blob"));
            using RegistryKey restoredSub = restored.OpenSubKey("PlugIn", writable: false)!;
            Assert.Equal(@"C:\worktree\Sample.rhp", restoredSub.GetValue("FileName"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
        }
    }
}
