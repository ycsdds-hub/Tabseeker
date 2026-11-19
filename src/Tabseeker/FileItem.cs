using System.IO;
using System.Windows.Media;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>One row in a folder or search list. Size/date are read lazily, so virtualized search lists only stat visible rows.</summary>
public sealed class FileItem
{
    private bool _statLoaded;
    private long _size = -1;
    private DateTime _modified;

    public FileItem(string fullPath, string name, bool isDir)
    {
        FullPath = fullPath;
        Name = name;
        IsDir = isDir;
    }

    public FileItem(FileSystemInfo fi) : this(fi.FullName, fi.Name, fi is DirectoryInfo)
    {
        try
        {
            _modified = fi.LastWriteTime;
            _size = fi is FileInfo f ? f.Length : -1;
            IsHidden = (fi.Attributes & FileAttributes.Hidden) != 0;
        }
        catch { }
        _statLoaded = true;
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDir { get; }
    public bool IsHidden { get; }
    public string? DisplayName { get; init; }
    public string ShownName => DisplayName ?? Name;
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";

    public long Size { get { EnsureStat(); return _size; } }
    public DateTime Modified { get { EnsureStat(); return _modified; } }
    public string SizeText => IsDir ? "" : Format.Size(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
    public string TypeText => ShellIcons.TypeName(FullPath, IsDir);
    public ImageSource? Icon => ShellIcons.Get(FullPath, IsDir);
    public double Opacity => IsHidden ? 0.55 : 1.0;

    private void EnsureStat()
    {
        if (_statLoaded) return;
        _statLoaded = true;
        try
        {
            if (IsDir) _modified = Directory.GetLastWriteTime(FullPath);
            else
            {
                var fi = new FileInfo(FullPath);
                if (fi.Exists) { _size = fi.Length; _modified = fi.LastWriteTime; }
            }
        }
        catch { }
    }
}
