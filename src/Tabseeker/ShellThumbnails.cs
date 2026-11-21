using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tabseeker;

/// <summary>
/// Big icons and picture/video thumbnails for the tile view, made by the Windows shell (IShellItemImageFactory)
/// on one background STA thread. Requests from a previous folder are dropped when the folder changes.
/// </summary>
internal static class ShellThumbnails
{
    public const int Size = 64;

    private static readonly HashSet<string> ThumbExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".ico", ".jfif",
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".webm", ".pdf", ".psd", ".svg",
    };

    private static readonly ConcurrentStack<(string path, string key, int gen, Action<ImageSource> done)> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static int _generation;
    private static Thread? _worker;

    /// <summary>Does this file get a real picture thumbnail (otherwise the big icon is enough)?</summary>
    public static bool WantsThumbnail(string path) => ThumbExts.Contains(Path.GetExtension(path));

    /// <summary>Call when a list is reloaded: pending thumbnails for the old contents are skipped.</summary>
    public static void NewGeneration() => Interlocked.Increment(ref _generation);
    public static int Generation => Volatile.Read(ref _generation);

    /// <summary>Cache key: the file itself for pictures/videos/exe/shortcuts, otherwise one big icon per file type.</summary>
    public static string KeyFor(string path, bool isDir)
    {
        if (isDir) return path.Length <= 3 ? path : "<dir>";
        var ext = Path.GetExtension(path);
        if (WantsThumbnail(path) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) return path;
        return ext.Length == 0 ? "<noext>" : ext;
    }

    public static ImageSource? Cached(string key) => Cache.TryGetValue(key, out var img) ? img : null;

    public static void Request(string path, string key, Action<ImageSource> done)
    {
        EnsureWorker();
        Queue.Push((path, key, Volatile.Read(ref _generation), done)); // newest first: what was just shown
        Signal.Release();
    }

    private static void EnsureWorker()
    {
        if (_worker != null) return;
        lock (Queue)
        {
            if (_worker != null) return;
            _worker = new Thread(Work) { IsBackground = true, Name = "Thumbnails", Priority = ThreadPriority.BelowNormal };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    private static void Work()
    {
        while (true)
        {
            Signal.Wait();
            if (!Queue.TryPop(out var job)) continue;
            if (job.gen != Volatile.Read(ref _generation)) continue;
            if (Cache.TryGetValue(job.key, out var hit)) { Deliver(job.done, hit); continue; }
            // a key shared by many files (".docx", "<dir>") must be a plain icon, not this one file's preview
            bool shared = !string.Equals(job.key, job.path, StringComparison.OrdinalIgnoreCase) || !WantsThumbnail(job.path);
            var img = Load(job.path, iconOnly: shared);
            if (img == null) continue;
            if (Cache.Count > 4000) Cache.Clear();
            Cache[job.key] = img;
            Deliver(job.done, img);
        }
    }

    private static void Deliver(Action<ImageSource> done, ImageSource img) =>
        Application.Current?.Dispatcher.BeginInvoke(() => done(img));

    // ------------------------------------------------------------ shell interop

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    /// <summary>Thumbnail (pictures/videos) or the big shell icon, with transparency kept.</summary>
    private static ImageSource? Load(string path, bool iconOnly)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            try
            {
                int flags = SIIGBF_BIGGERSIZEOK | (iconOnly ? SIIGBF_ICONONLY : 0);
                if (factory.GetImage(new SIZE { cx = Size, cy = Size }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            }
            finally { Marshal.ReleaseComObject(factory); }

            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;
            int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
            var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * h * 4];
            var hdc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bmi, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, hdc); }

            // pictures come back without alpha (all zero): make them opaque
            bool anyAlpha = false;
            for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;

            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }
}
