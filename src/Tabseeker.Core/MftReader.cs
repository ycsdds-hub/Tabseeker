using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Tabseeker.Core;

/// <summary>A file or folder record from a volume's master file table.</summary>
public readonly record struct TreeRecord(ulong Id, ulong ParentId, string Name, bool IsDirectory);

/// <summary>
/// Reads every file name on an NTFS volume straight from the MFT with FSCTL_ENUM_USN_DATA,
/// the same trick Everything uses. Millions of names per few seconds, but it needs administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MftReader
{
    /// <summary>NTFS root directory is always MFT record 5.</summary>
    public const ulong RootRecord = 5;
    private const ulong RecordMask = 0x0000FFFFFFFFFFFF;

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    private const int ERROR_HANDLE_EOF = 38;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref MFT_ENUM_DATA_V0 input, int inSize, IntPtr output, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr input, int inSize, out USN_JOURNAL_DATA_V0 output, int outSize, out int returned, IntPtr overlapped);

    /// <summary>Can this process open the volume for MFT reading?</summary>
    public static bool CanRead(string root)
    {
        using var h = OpenVolume(root);
        return !h.IsInvalid;
    }

    private static SafeFileHandle OpenVolume(string root)
    {
        var letter = root.TrimEnd('\\', ':');
        return CreateFile($@"\\.\{letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    }

    /// <summary>All records of the volume; Id/ParentId are MFT record numbers (sequence bits stripped). Root is <see cref="RootRecord"/>.</summary>
    public static List<TreeRecord> Read(string root, CancellationToken ct = default)
    {
        using var h = OpenVolume(root);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"{root} 볼륨을 열 수 없습니다 (관리자 권한 필요)");

        long highUsn = long.MaxValue;
        if (DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, out USN_JOURNAL_DATA_V0 journal, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero))
            highUsn = journal.NextUsn;

        const int BufSize = 1 << 20;
        var buf = Marshal.AllocHGlobal(BufSize);
        var list = new List<TreeRecord>(1 << 20);
        try
        {
            var input = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = highUsn };
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!DeviceIoControl(h, FSCTL_ENUM_USN_DATA, ref input, Marshal.SizeOf<MFT_ENUM_DATA_V0>(), buf, BufSize, out int bytes, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_HANDLE_EOF) break;
                    throw new Win32Exception(err);
                }
                if (bytes <= 8) break;
                int offset = 8;
                while (offset < bytes)
                {
                    int len = Marshal.ReadInt32(buf, offset);
                    if (len <= 0) break;
                    short major = Marshal.ReadInt16(buf, offset + 4);
                    if (major == 2)
                    {
                        ulong frn = (ulong)Marshal.ReadInt64(buf, offset + 8) & RecordMask;
                        ulong parent = (ulong)Marshal.ReadInt64(buf, offset + 16) & RecordMask;
                        uint attrs = (uint)Marshal.ReadInt32(buf, offset + 52);
                        int nameLen = Marshal.ReadInt16(buf, offset + 56);
                        int nameOff = Marshal.ReadInt16(buf, offset + 58);
                        var name = Marshal.PtrToStringUni(buf + offset + nameOff, nameLen / 2);
                        list.Add(new TreeRecord(frn, parent, name, (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0));
                    }
                    offset += len;
                }
                input.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(buf, 0);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }
}
