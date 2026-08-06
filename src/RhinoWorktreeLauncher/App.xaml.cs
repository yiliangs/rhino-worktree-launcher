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
        ProjectCatalog catalog = ProjectCatalog.Load();
        string? registrationPath = ReadArgument(e.Args, "--register-project");
        if (!string.IsNullOrWhiteSpace(registrationPath))
        {
            catalog.AddProject(registrationPath);
            Shutdown(0);
            return;
        }

        MainWindow window = new MainWindow(catalog);
        MainWindow = window;
        window.Show();
    }

    private static string? ReadArgument(string[] arguments, string name)
    {
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        }
        return null;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);
}
