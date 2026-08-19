using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RhinoWorktreeLauncher;

/// <summary>
/// One live process as an external observer sees it. Path and start time are filled only for
/// the processes the reader was asked to describe, because obtaining them costs a handle per
/// process; a null value means not read rather than not present.
/// </summary>
internal sealed record RunningProcess(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    DateTimeOffset? StartedAt);

/// <summary>
/// Reads the machine's process table. This is the one process-enumeration mechanism in RWL:
/// the doctor inventory classifies what it returns, instance attribution reads the Rhino
/// processes out of it, and a stdio server resolves its own parent from it rather than
/// carrying a second way to ask the same question.
/// </summary>
internal static class ProcessSnapshot
{
    // Names RWL owns.
    public static readonly IReadOnlyList<string> RwlExecutableNames = new[]
    {
        "rwl.exe",
        "rwl-mcp.exe",
        "rwl-cli.exe",
        "RhinoWorktreeLauncher.exe"
    };

    // What RWL describes: its own processes, and Rhino, which RWL starts, verifies, and
    // attributes. Anything else is enumerated (parents have to be findable) but never
    // described, so no handle is opened for an unrelated process.
    public static readonly IReadOnlyList<string> DescribedExecutableNames = RwlExecutableNames
        .Append(RhinoInstanceReader.RhinoExecutableName)
        .ToArray();

    /// <summary>
    /// Every live process, with the executable path and start time filled for the processes
    /// RWL describes and for their parents. A parent's start time is what tells an orphaned
    /// child from one whose parent's process id was reused.
    /// </summary>
    public static IReadOnlyList<RunningProcess> Read()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process enumeration requires Windows.");

        List<RunningProcess> processes = Enumerate();
        // The current process is described whether or not RWL owns its name, so a caller
        // resolving its own parent gets the start time the reuse guard needs.
        HashSet<int> describe = new HashSet<int> { Environment.ProcessId };
        foreach (RunningProcess process in processes)
        {
            if (!IsDescribed(process.Name) && process.ProcessId != Environment.ProcessId)
                continue;
            _ = describe.Add(process.ProcessId);
            _ = describe.Add(process.ParentProcessId);
        }

        for (int index = 0; index < processes.Count; index++)
        {
            if (describe.Contains(processes[index].ProcessId))
                processes[index] = Describe(processes[index]);
        }
        return processes;
    }

    public static bool IsDescribed(string executableName) => DescribedExecutableNames.Any(name =>
        string.Equals(name, executableName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The parent of the given process id, or null when no live process holds that id or the
    /// id was reused by a process that started after the child did.
    /// </summary>
    public static RunningProcess? ParentOf(IReadOnlyList<RunningProcess> processes, int processId)
    {
        RunningProcess? child = processes.FirstOrDefault(process => process.ProcessId == processId);
        if (child is null)
            return null;

        RunningProcess? parent = processes.FirstOrDefault(process =>
            process.ProcessId == child.ParentProcessId);
        if (parent is null)
            return null;
        // Windows reuses process ids. A "parent" that started after its child is a different
        // process wearing the dead parent's id.
        if (parent.StartedAt is not null && child.StartedAt is not null && parent.StartedAt > child.StartedAt)
            return null;
        return parent;
    }

    [SupportedOSPlatform("windows")]
    private static RunningProcess Describe(RunningProcess process)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, process.ProcessId);
        if (handle == IntPtr.Zero)
        {
            // A process RWL may not open is reported as it was enumerated. The doctor check
            // that consumes this names the unknown rather than inventing a value.
            return process;
        }
        try
        {
            return process with
            {
                ExecutablePath = ImagePath(handle),
                StartedAt = StartTime(handle)
            };
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ImagePath(IntPtr handle)
    {
        StringBuilder path = new StringBuilder(1024);
        int capacity = path.Capacity;
        return QueryFullProcessImageNameW(handle, 0, path, ref capacity) ? path.ToString() : null;
    }

    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? StartTime(IntPtr handle) => GetProcessTimes(
        handle,
        out long creation,
        out _,
        out _,
        out _)
            ? DateTimeOffset.FromFileTime(creation)
            : null;

    [SupportedOSPlatform("windows")]
    private static List<RunningProcess> Enumerate()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows refused a process snapshot, so the live RWL processes cannot be listed.");
        }
        try
        {
            List<RunningProcess> processes = new List<RunningProcess>();
            PROCESSENTRY32W entry = new PROCESSENTRY32W
            {
                dwSize = Marshal.SizeOf<PROCESSENTRY32W>()
            };
            if (!Process32FirstW(snapshot, ref entry))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows returned an empty process snapshot, which cannot be true.");
            }
            do
            {
                processes.Add(new RunningProcess(
                    (int)entry.th32ProcessID,
                    (int)entry.th32ParentProcessID,
                    entry.szExeFile,
                    null,
                    null));
            }
            while (Process32NextW(snapshot, ref entry));
            return processes;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private const uint SnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly IntPtr InvalidHandle = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public int dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process,
        uint flags,
        StringBuilder path,
        ref int capacity);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out long creation,
        out long exit,
        out long kernel,
        out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
