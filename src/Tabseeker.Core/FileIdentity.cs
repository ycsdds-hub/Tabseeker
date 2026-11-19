using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Tabseeker.Core;

/// <summary>
/// Stable identity of a file or folder: volume serial + NTFS file id.
/// It survives renames and moves on the same drive, so a memo follows its file even when it is moved outside Tabseeker.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileIdentity
{
    private const uint FILE_SHARE_ALL = 1 | 2 | 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // needed to open folders

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        // three FILETIMEs (two DWORDs each, 4-byte aligned — not longs, which would add padding)
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    /// <summary>"VVVVVVVV-IIIIIIIIIIIIIIII", or null when the file is missing or the drive has no stable ids.</summary>
    public static string? Get(string path)
    {
        try
        {
            using var h = CreateFile(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
            if (info.FileIndexHigh == 0 && info.FileIndexLow == 0) return null; // FAT etc.
            return $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        }
        catch { return null; }
    }
}
