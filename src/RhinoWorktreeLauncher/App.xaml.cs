using System.Runtime.InteropServices;
using System.Windows;

namespace RhinoWorktreeLauncher;

public partial class App : Application
{
    public App()
    {
        _ = SetCurrentProcessExplicitAppUserModelID("RhinoWorktreeLauncher.App");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LauncherBackend backend = new LauncherBackend();
        MainWindow window = new MainWindow(backend);
        MainWindow = window;
        window.Show();
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);
}
