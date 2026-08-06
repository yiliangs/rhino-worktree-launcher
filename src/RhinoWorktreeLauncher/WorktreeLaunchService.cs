using System.Diagnostics;

namespace RhinoWorktreeLauncher;

public sealed class WorktreeLaunchService
{
    public void Launch(WorktreeEntry worktree)
    {
        if (!worktree.CanLaunch)
            throw new InvalidOperationException("The selected worktree does not have an available launcher.");

        if (worktree.IsPrimary)
        {
            string rhinoPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                $"Rhino {worktree.Project.PrimaryLaunch.RhinoVersion}",
                "System",
                "Rhino.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = rhinoPath,
                UseShellExecute = true
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = worktree.LauncherPath,
            WorkingDirectory = worktree.Path,
            UseShellExecute = true
        });
    }
}
