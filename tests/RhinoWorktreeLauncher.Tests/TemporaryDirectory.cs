namespace RhinoWorktreeLauncher.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "RhinoWorktreeLauncher.Tests",
        Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(_root);

    public string CreateDirectory(string relativePath)
    {
        string path = PathFor(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string PathFor(string relativePath) => System.IO.Path.GetFullPath(
        System.IO.Path.Combine(_root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public void WriteFile(string relativePath, string contents)
    {
        string path = PathFor(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public string Run(string fileName, string workingDirectory, params string[] arguments)
    {
        System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error);
        return output;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;

        foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
