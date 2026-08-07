using System.Collections;
using System.Diagnostics;

namespace RhinoWorktreeLauncher;

internal static class ShellRhinoProcessStarter
{
    public static Process Start(ProcessStartInfo configured) => Start(
        configured,
        startInfo => Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start Rhino."));

    internal static Process Start(
        ProcessStartInfo configured,
        Func<ProcessStartInfo, Process> starter) => ProcessLaunchGate.Start(() =>
    {
        Dictionary<string, string?> overrides = FindEnvironmentOverrides(configured);
        Dictionary<string, string?> originalValues = overrides.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (KeyValuePair<string, string?> environmentVariable in overrides)
            {
                Environment.SetEnvironmentVariable(
                    environmentVariable.Key,
                    environmentVariable.Value,
                    EnvironmentVariableTarget.Process);
            }

            return starter(CreateShellStartInfo(configured));
        }
        finally
        {
            foreach (KeyValuePair<string, string?> environmentVariable in originalValues)
            {
                Environment.SetEnvironmentVariable(
                    environmentVariable.Key,
                    environmentVariable.Value,
                    EnvironmentVariableTarget.Process);
            }
        }
    });

    private static Dictionary<string, string?> FindEnvironmentOverrides(ProcessStartInfo configured)
    {
        Dictionary<string, string?> current = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => entry.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string?> environmentVariable in configured.Environment)
        {
            if (!current.TryGetValue(environmentVariable.Key, out string? value) ||
                !string.Equals(value, environmentVariable.Value, StringComparison.Ordinal))
            {
                overrides[environmentVariable.Key] = environmentVariable.Value;
            }
        }

        return overrides;
    }

    private static ProcessStartInfo CreateShellStartInfo(ProcessStartInfo configured)
    {
        ProcessStartInfo shellStart = new ProcessStartInfo
        {
            FileName = configured.FileName,
            WorkingDirectory = configured.WorkingDirectory,
            UseShellExecute = true,
            WindowStyle = configured.WindowStyle,
            Verb = configured.Verb
        };
        foreach (string argument in configured.ArgumentList)
            shellStart.ArgumentList.Add(argument);
        return shellStart;
    }
}
