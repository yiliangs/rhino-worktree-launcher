using System.ComponentModel;

namespace RhinoWorktreeLauncher;

/// <summary>
/// Which live Rhino processes exist and which plug-in artifacts each one holds mapped.
/// Concurrent launches from separate sessions legitimately produce several verified Rhino
/// instances, each running a different build, so the ambiguity is the launcher's to answer
/// rather than every consumer's to rediscover.
///
/// The reading is the ADR 0002 mechanism, unchanged: an assembly Rhino has loaded is a file
/// mapped into that process's address space. It is plug-in agnostic (every mapped
/// <c>.rhp</c>, not one expected path), point-in-time, and read-only, so it observes the
/// machine without becoming a session monitor.
/// </summary>
internal static class RhinoInstanceReader
{
    public const string RhinoExecutableName = "Rhino.exe";
    public const string PlugInExtension = ".rhp";

    /// <summary>
    /// The default reader: the plug-in artifacts one process holds mapped. Injectable at the
    /// backend so a test decides what a process is holding.
    /// </summary>
    public static IReadOnlyList<string> MappedPlugIns(int processId) =>
        FileUse.MappedFilesWithExtension(processId, PlugInExtension);

    public static bool IsRhino(string executableName) =>
        string.Equals(executableName, RhinoExecutableName, StringComparison.OrdinalIgnoreCase);

    public static RhinoInstanceAttribution Describe(
        IReadOnlyList<RunningProcess> processes,
        Func<int, IReadOnlyList<string>> mappedPlugIns)
    {
        List<RhinoInstance> instances = new List<RhinoInstance>();
        foreach (RunningProcess process in processes.Where(candidate => IsRhino(candidate.Name)))
        {
            instances.Add(Attribute(process, mappedPlugIns));
        }
        return new RhinoInstanceAttribution(
            DateTimeOffset.UtcNow,
            instances.OrderBy(instance => instance.ProcessId).ToArray());
    }

    private static RhinoInstance Attribute(
        RunningProcess process,
        Func<int, IReadOnlyList<string>> mappedPlugIns)
    {
        try
        {
            return new RhinoInstance(
                process.ProcessId,
                process.StartedAt,
                process.ExecutablePath,
                mappedPlugIns(process.ProcessId),
                UnattributableReason: null);
        }
        // Windows refusing this account a read of one process's address space is the only
        // failure the scan raises for a single process, and it is a fact about that
        // process. Omitting it would leave a live Rhino out of a list whose whole purpose
        // is to account for every one of them, so it is reported with the reason instead.
        // Anything else is a failure of the query and is left to the caller.
        catch (Win32Exception exception)
        {
            return new RhinoInstance(
                process.ProcessId,
                process.StartedAt,
                process.ExecutablePath,
                Array.Empty<string>(),
                exception.Message);
        }
    }
}
