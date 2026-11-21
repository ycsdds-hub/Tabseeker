using System.IO;
using System.Windows.Media;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>One row in a folder or search list. Size/date are read lazily, so virtualized search lists only stat visible rows.</summary>
public sealed class FileItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Shows the memo mark next to the name.</summary>
    public bool HasMemo => MemoController.Store?.HasMemo(FullPath) ?? false;

    public void RefreshMemo() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasMemo)));

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

    // ---- tile view
    private ImageSource? _thumb;
    private int _thumbGen = -1;

    /// <summary>Big icon, or a picture/video thumbnail, loaded in the background (small icon until then).</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumb != null) return _thumb;
            var key = ShellThumbnails.KeyFor(FullPath, IsDir);
            var cached = ShellThumbnails.Cached(key);
            if (cached != null) return _thumb = cached;
            if (_thumbGen != ShellThumbnails.Generation) // not asked yet, or the request was dropped by a reload
            {
                _thumbGen = ShellThumbnails.Generation;
                ShellThumbnails.Request(FullPath, key, img =>
                {
                    _thumb = img;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail)));
                });
            }
            return Icon;
        }
    }

    public string TileDetail => IsDir ? ModifiedText : $"{SizeText}  ·  {ModifiedText}";

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
