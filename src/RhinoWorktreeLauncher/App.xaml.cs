using System.Runtime.InteropServices;
using System.Windows;

namespace RhinoWorktreeLauncher;

public partial class App : Application
{
    public App()
    {
        EnsureWpfWindowsDirectory();
        _ = SetCurrentProcessExplicitAppUserModelID("RhinoWorktreeLauncher.App");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions { HostKind = "desktop" });
        MainWindow window = new MainWindow(backend);
        MainWindow = window;
        window.Show();
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    private static void EnsureWpfWindowsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            return;

        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
            Environment.SetEnvironmentVariable("windir", systemRoot, EnvironmentVariableTarget.Process);
    }
}
