using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RhinoWorktreeLauncher;

/// <summary>
/// Answers what a process holds mapped into its address space: whether one named file is
/// there, which is how a launch verifies its own Rhino, and which files of one kind are
/// there, which is how RWL attributes a Rhino nobody in this session launched. A loaded
/// plug-in assembly is a mapped image section, which holds no open handle and no
/// loader-table entry, so handle- and module-based inspection (including the Restart
/// Manager) cannot see it.
/// </summary>
internal static class FileUse
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;

    public static bool IsFileMappedByProcess(int processId, string path)
    {
        RequireWindows();

        string devicePath = ToDevicePath(Path.GetFullPath(path));
        IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        // Verification polls the process this launch started, so a process it cannot open
        // is one that holds nothing this launch can see, and the poll continues until the
        // launch times out by name. Attribution below answers a different question about a
        // process nobody here started, so it reports the refusal instead of hiding it.
        if (process == IntPtr.Zero)
            return false;
        try
        {
            foreach (string mapped in MappedDeviceNames(process))
            {
                if (string.Equals(mapped, devicePath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>
    /// Every distinct file with the given extension that the process holds mapped, as a
    /// drive path. Throws when this account may not read the process's address space: a
    /// process that cannot be inspected is not a process that holds nothing.
    /// </summary>
    public static IReadOnlyList<string> MappedFilesWithExtension(int processId, string extension)
    {
        RequireWindows();

        IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (process == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows refused to open process {processId} for reading its mapped files.");
        }
        try
        {
            IReadOnlyDictionary<string, string> drives = DriveDeviceNames();
            return MappedDeviceNames(process)
                .Where(device => device.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .Select(device => ToDrivePath(device, drives))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    // Walks the address space once, yielding the file name behind each distinct mapped
    // allocation. Enumerate it inside the caller's handle scope.
    private static IEnumerable<string> MappedDeviceNames(IntPtr process)
    {
        StringBuilder mappedName = new StringBuilder(1024);
        ulong address = 0;
        IntPtr previousAllocation = IntPtr.Zero;
        while (VirtualQueryEx(
            process,
            (IntPtr)address,
            out MEMORY_BASIC_INFORMATION region,
            (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
        {
            bool isMappedFile = region.Type is MemMapped or MemImage;
            if (isMappedFile && region.AllocationBase != previousAllocation)
            {
                previousAllocation = region.AllocationBase;
                mappedName.Clear();
                uint length = GetMappedFileNameW(process, region.BaseAddress, mappedName, (uint)mappedName.Capacity);
                if (length > 0)
                    yield return mappedName.ToString();
            }
            ulong next = (ulong)region.BaseAddress + (ulong)region.RegionSize;
            if (next <= address)
                break;
            address = next;
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("File-use inspection requires Windows.");
    }

    private static string ToDevicePath(string path)
    {
        string root = Path.GetPathRoot(path) ?? throw new InvalidDataException($"'{path}' has no drive root.");
        string drive = root.TrimEnd(Path.DirectorySeparatorChar);
        StringBuilder device = new StringBuilder(1024);
        if (QueryDosDevice(drive, device, (uint)device.Capacity) == 0)
            throw new InvalidOperationException($"The device name of drive '{drive}' could not be resolved.");
        return device + path.Substring(drive.Length);
    }

    // The reverse of ToDevicePath, for a name RWL did not start from a path. A device with
    // no drive letter, such as a mapped network path, keeps its device name rather than
    // being dropped or presented as a path that does not exist.
    private static string ToDrivePath(string devicePath, IReadOnlyDictionary<string, string> drives)
    {
        foreach ((string device, string drive) in drives)
        {
            if (devicePath.StartsWith(device + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return drive + devicePath.Substring(device.Length);
        }
        return devicePath;
    }

    private static IReadOnlyDictionary<string, string> DriveDeviceNames()
    {
        Dictionary<string, string> drives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo candidate in DriveInfo.GetDrives())
        {
            string drive = candidate.Name.TrimEnd(Path.DirectorySeparatorChar);
            StringBuilder device = new StringBuilder(1024);
            // A drive letter with no device name is one that vanished between the
            // enumeration and this call. Every path mapped from it still reports the
            // device name it was mapped under.
            if (QueryDosDevice(drive, device, (uint)device.Capacity) != 0)
                drives[device.ToString()] = drive;
        }
        return drives;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern int VirtualQueryEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer,
        uint dwLength);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetMappedFileNameW(
        IntPtr hProcess,
        IntPtr lpv,
        StringBuilder lpFilename,
        uint nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
