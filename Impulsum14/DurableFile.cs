using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Impulsum14;

internal static class DurableFile
{
    // Call only after flushing the complete temporary file. Both names must be in
    // directory; never copy/delete across volumes. published tracks an ambiguous
    // failure after the rename so callers can stop further conflicting writes.
    internal static void Replace(string temporary, string target, string directory, ref bool published)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(temporary, target, 0x1 | 0x8))
                throw new IOException("Unable to replace durable snapshot.", new Win32Exception(Marshal.GetLastWin32Error()));
            published = true;
            return;
        }
        File.Move(temporary, target, overwrite: true);
        published = true;
        int descriptor = OpenDirectory(directory, 0);
        if (descriptor < 0) throw new IOException("Unable to open snapshot directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            if (SyncDescriptor(descriptor) != 0)
                throw new IOException("Unable to persist snapshot directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { CloseDescriptor(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncDescriptor(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseDescriptor(int descriptor);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFile, string replacementFile, uint flags);
}
