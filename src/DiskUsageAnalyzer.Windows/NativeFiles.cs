using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DiskUsageAnalyzer.Core;

namespace DiskUsageAnalyzer.Windows;

public static class NativeFiles
{
    public const uint ReadData = 0x80000000, ReadAttributes = 0x80, DeleteAccess = 0x10000;
    public const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
    private const uint OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparse = 0x00200000, OpenNoRecall = 0x00100000;
    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending, Directory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, System.Text.StringBuilder path, uint size, uint flags);

    public static string FinalPath(SafeFileHandle handle)
    {
        var path = new System.Text.StringBuilder(32768);
        uint n = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (n == 0 || n >= path.Capacity) throw new IOException("Cannot verify the handle's current path.");
        string result = path.ToString(); return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    public static SafeFileHandle Open(string path, uint access = ReadAttributes, uint share = ShareRead | ShareWrite | ShareDelete)
    {
        var h = CreateFileW(PathPolicy.Canonical(path), access, share, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse | OpenNoRecall, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        int error = Marshal.GetLastWin32Error(); h.Dispose(); throw new IOException(new Win32Exception(error).Message, error);
    }
    public static FileIdentity Identity(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var i)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        long? allocated = GetFileInformationByHandleEx(h, 1, out var s, (uint)Marshal.SizeOf<StandardInfo>()) ? s.AllocationSize : null;
        return new(i.Volume, ((ulong)i.IndexHigh << 32) | i.IndexLow, ((long)i.SizeHigh << 32) | i.SizeLow,
            ((long)i.WriteHigh << 32) | i.WriteLow, ((long)i.CreationHigh << 32) | i.CreationLow, i.Attributes, i.Links, allocated);
    }
    public static void DeleteByHandle(SafeFileHandle h)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, 1);
            if (!SetFileInformationByHandle(h, 4, buffer, 4)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static void RenameByHandle(SafeFileHandle h, string target)
    {
        // FILE_RENAME_INFO: BOOLEAN/union at 0, HANDLE at aligned pointer boundary,
        // DWORD byte length after HANDLE, then UTF-16 filename with a trailing NUL.
        // The length excludes NUL; include it in the buffer for the Win32 rename wrapper.
        string full = PathPolicy.Canonical(target);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + 4;
        byte[] name = System.Text.Encoding.Unicode.GetBytes(full);
        int size = nameOffset + name.Length + 2;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(h, 3, buffer, (uint)size)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (!string.Equals(FinalPath(h), full, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Rename outcome mismatch: {FinalPath(h)}");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

public sealed class WindowsMetadata : IFileMetadata
{
    public FileIdentity Read(string path) { using var h = NativeFiles.Open(path); return NativeFiles.Identity(h); }
    public string? Exclusion(string path, FileIdentity i)
    {
        if (PathPolicy.ProtectedReason(path) is { } reason) return reason;
        const uint unsafeAttributes = 0x400 | 0x1000 | 0x400000 | 0x40000 | 0x4 | 0x1;
        if ((i.Attributes & unsafeAttributes) != 0) return "Link, system, read-only or cloud placeholder: analysis only.";
        if (i.Links != 1) return "Hardlinked or unknown link count: analysis only.";
        if (i.FileId == 0) return "No stable filesystem identity.";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return "Cleanup supports fixed local NTFS volumes only.";
        }
        catch (IOException) { return "Volume metadata unavailable."; }
        return null;
    }
}

public sealed class AncestorGuard : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    public AncestorGuard(string path)
    {
        try
        {
            string full = PathPolicy.Canonical(path);
            var parts = new Stack<string>();
            for (var p = Path.GetDirectoryName(full); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p)) parts.Push(p);
            while (parts.TryPop(out var p))
            {
                // Metadata-only handles do not enforce sharing restrictions. Request real directory read access.
                var h = NativeFiles.Open(p, NativeFiles.ReadData | NativeFiles.ReadAttributes, NativeFiles.ShareRead | NativeFiles.ShareWrite);
                _handles.Add(h);
                if ((NativeFiles.Identity(h).Attributes & 0x400) != 0) throw new IOException("An ancestor is a reparse point. Operation blocked.");
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); _handles.Clear(); }
}
