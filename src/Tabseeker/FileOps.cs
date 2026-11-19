using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Tabseeker;

internal static class FileOps
{
    private static IntPtr Owner => Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" }); }
        catch (Exception ex) { Error(ex); }
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path) && !File.Exists(path) && path.Length <= 3) Process.Start("explorer.exe", $"\"{path}\"");
            else Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) { Error(ex); }
    }

    public static void OpenFolderInExplorer(string folder)
    {
        try { Process.Start("explorer.exe", $"\"{folder}\""); } catch (Exception ex) { Error(ex); }
    }

    public static void CopyPaths(IEnumerable<string> paths) => Clipboard.SetText(string.Join(Environment.NewLine, paths));

    public static void ToClipboard(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        // Preferred DropEffect: 2 = move, 5 = copy|link
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        Clipboard.SetDataObject(data, true);
    }

    public static void Paste(string targetDir)
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            bool cut = false;
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
                cut = (BitConverter.ToInt32(ms.ToArray(), 0) & 2) != 0;
            Transfer(files, targetDir, cut);
            if (cut) Clipboard.Clear();
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>Copy or move through the shell, with its progress / conflict dialogs and undo.</summary>
    public static void Transfer(IReadOnlyList<string> sources, string targetDir, bool move)
    {
        var src = sources.Where(s => !string.Equals(Path.GetDirectoryName(s.TrimEnd('\\')), targetDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !move).ToList();
        if (src.Count == 0) return;
        ushort flags = Native.FOF_ALLOWUNDO;
        // copying into the same folder: make "name - 복사본"
        if (!move && src.All(s => string.Equals(Path.GetDirectoryName(s.TrimEnd('\\')), targetDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            flags |= Native.FOF_RENAMEONCOLLISION;
        Run(move ? Native.FO_MOVE : Native.FO_COPY, src, targetDir, flags);
    }

    public static void Delete(IReadOnlyList<string> paths, bool permanent)
    {
        if (paths.Count == 0) return;
        Run(Native.FO_DELETE, paths, null, permanent ? (ushort)0 : Native.FOF_ALLOWUNDO);
    }

    private static void Run(uint func, IEnumerable<string> from, string? to, ushort flags)
    {
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = Owner,
            wFunc = func,
            pFrom = string.Join("\0", from) + "\0\0",
            pTo = to == null ? null : to + "\0\0",
            fFlags = flags,
        };
        int rc = Native.SHFileOperation(ref op);
        if (rc != 0 && !op.fAnyOperationsAborted && rc != 0x4C7 /* cancelled */)
            MessageBox.Show($"파일 작업 실패 (코드 0x{rc:X})", "Tabseeker", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static bool Rename(string path, string newName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("사용할 수 없는 이름입니다.");
            var dir = Path.GetDirectoryName(path.TrimEnd('\\'))!;
            var target = Path.Combine(dir, newName);
            if (string.Equals(target, path, StringComparison.Ordinal)) return false;
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            MemoController.Store?.Renamed(path, target); // memos (and a folder's child memos) move with the name
            return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }

    public static string? NewFolder(string parent)
    {
        try
        {
            var name = "새 폴더";
            var path = Path.Combine(parent, name);
            for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(parent, $"{name} ({i})");
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) { Error(ex); return null; }
    }

    public static void Properties(string path)
    {
        var info = new Native.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
            fMask = Native.SEE_MASK_INVOKEIDLIST,
            hwnd = Owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = 1,
        };
        Native.ShellExecuteEx(ref info);
    }

    public static void OpenTerminal(string folder)
    {
        try
        {
            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(wt)) Process.Start(new ProcessStartInfo(wt, $"-d \"{folder}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
        catch (Exception ex) { Error(ex); }
    }

    public static void Error(Exception ex) =>
        MessageBox.Show(ex.Message, "Tabseeker", MessageBoxButton.OK, MessageBoxImage.Warning);
}
