using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tabseeker;

/// <summary>Small shell icons and type names, cached by extension (or by path for exe/lnk/ico).</summary>
internal static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PerFile = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".cur", ".scr" };

    public static ImageSource? Get(string path, bool isDir)
    {
        string key;
        bool useAttrs = true;
        if (isDir)
        {
            bool isRoot = path.Length <= 3;
            key = isRoot ? path : "<dir>";
            useAttrs = !isRoot;
        }
        else
        {
            var ext = Path.GetExtension(path);
            if (PerFile.Contains(ext)) { key = path; useAttrs = false; }
            else key = ext.Length == 0 ? "<noext>" : ext;
        }
        if (Icons.TryGetValue(key, out var cached)) return cached;
        var img = Load(path, isDir, useAttrs);
        if (img == null && !useAttrs) img = Load(path, isDir, true);
        Icons[key] = img;
        return img;
    }

    private static ImageSource? Load(string path, bool isDir, bool useAttrs)
    {
        var info = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_ICON | Native.SHGFI_SMALLICON | (useAttrs ? Native.SHGFI_USEFILEATTRIBUTES : 0);
        uint attrs = isDir ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
        Native.SHGetFileInfo(useAttrs && !isDir ? "x" + Path.GetExtension(path) : path, attrs, ref info, (uint)Marshal.SizeOf(info), flags);
        if (info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { Native.DestroyIcon(info.hIcon); }
    }

    public static string TypeName(string path, bool isDir)
    {
        if (isDir) return path.Length <= 3 ? "로컬 디스크" : "파일 폴더";
        var ext = Path.GetExtension(path);
        if (TypeNames.TryGetValue(ext, out var name)) return name;
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo("x" + ext, Native.FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
        name = string.IsNullOrEmpty(info.szTypeName) ? (ext.Length > 1 ? ext[1..].ToUpperInvariant() + " 파일" : "파일") : info.szTypeName;
        TypeNames[ext] = name;
        return name;
    }
}
