using System.Runtime.InteropServices;
using System.Text;

namespace RhinoWorktreeLauncher;

/// <summary>
/// Answers whether a process holds a file mapped into its address space. A loaded plug-in
/// assembly is a mapped image section, which holds no open handle and no loader-table entry,
/// so handle- and module-based inspection (including the Restart Manager) cannot see it.
/// </summary>
internal static class FileUse
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;

    public static bool IsFileMappedByProcess(int processId, string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("File-use inspection requires Windows.");

        string devicePath = ToDevicePath(Path.GetFullPath(path));
        IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (process == IntPtr.Zero)
            return false;
        try
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
                    if (length > 0 && string.Equals(mappedName.ToString(), devicePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                ulong next = (ulong)region.BaseAddress + (ulong)region.RegionSize;
                if (next <= address)
                    break;
                address = next;
            }
            return false;
        }
        finally
        {
            _ = CloseHandle(process);
        }
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
