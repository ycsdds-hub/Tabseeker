using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>
/// Owns the global file index: loads the cache at startup, rebuilds it in the background,
/// and keeps it current with FileSystemWatchers on every fixed drive.
/// </summary>
public sealed class IndexService : IDisposable
{
    private static string CachePath => Path.Combine(AppSettings.Dir, "index.bin");

    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private volatile bool _rebuilding;
    private bool _dirty;
    private DateTime _lastSave = DateTime.UtcNow;
    private DateTime _lastOverflowRebuild = DateTime.MinValue;
    private CancellationTokenSource _cts = new();

    public FileIndex? Index { get; private set; }
    public string Status { get; private set; } = "색인 준비 중…";
    public bool IsReady => Index != null;

    /// <summary>Raised (on a worker thread) when the status text changes.</summary>
    public event Action? StatusChanged;
    /// <summary>Raised (on a worker thread) when index contents changed (swap or live update).</summary>
    public event Action? IndexChanged;

    public IndexService()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, 1500, 1500);
    }

    /// <summary>Local NTFS/ReFS drives, like Everything. Skips FAT volumes and virtual drives such as Google Drive.</summary>
    public static List<string> Roots()
    {
        var list = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat is "NTFS" or "ReFS")
                    list.Add(d.RootDirectory.FullName);
            }
            catch { }
        }
        return list;
    }

    /// <summary>Cache older than this is rebuilt at startup (watchers keep it current while the app runs).</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);
    private bool _uacDeclined;

    public void Start()
    {
        Task.Run(() =>
        {
            bool stale = true;
            try
            {
                if (File.Exists(CachePath))
                {
                    var sw = Stopwatch.StartNew();
                    Index = FileIndex.LoadFromFile(CachePath);
                    stale = DateTime.Now - File.GetLastWriteTime(CachePath) > StaleAfter;
                    SetStatus($"{Index.Count:N0}개 항목 (캐시 {sw.ElapsedMilliseconds}ms)" + (stale ? " · 최신 상태로 갱신 중…" : ""));
                    IndexChanged?.Invoke();
                }
            }
            catch { Index = null; }
            StartWatchers();
            if (stale) Rebuild();
        });
    }

    public void RequestRebuild()
    {
        if (_rebuilding) return;
        _uacDeclined = false;
        Task.Run(Rebuild);
    }

    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>MFT for NTFS volumes this process can open, directory walk for the rest.</summary>
    public static FileIndex BuildFast(IReadOnlyList<string> roots, IProgress<int>? progress, CancellationToken ct) =>
        FileIndex.Build(roots, progress, ct, root =>
            MftReader.CanRead(root) ? (MftReader.RootRecord, MftReader.Read(root, ct)) : null);

    /// <summary>Entry point of the elevated helper: <c>Tabseeker.exe --build-index &lt;file&gt;</c>.</summary>
    public static int BuildIndexFile(string path)
    {
        try
        {
            BuildFast(Roots(), null, CancellationToken.None).SaveToFile(path);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".error.txt", ex.ToString()); } catch { }
            return 1;
        }
    }

    private void Rebuild()
    {
        lock (_gate)
        {
            if (_rebuilding) return;
            _rebuilding = true;
        }
        try
        {
            var ct = _cts.Token;
            var roots = Roots();
            var sw = Stopwatch.StartNew();
            var fresh = IsElevated() ? BuildFast(roots, null, ct) : BuildWithHelper() ?? BuildSlow(roots, ct);
            // changes that happened while building
            var during = _pending.Keys.ToList();
            if (during.Count > 0) fresh.Refresh(during);
            Index = fresh;
            sw.Stop();
            SetStatus($"{fresh.Count:N0}개 항목 · 색인 {sw.Elapsed.TotalSeconds:0.0}초 ({string.Join(" ", roots.Select(r => r.TrimEnd('\\')))})");
            IndexChanged?.Invoke();
            Save();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("색인 오류: " + ex.Message); }
        finally { _rebuilding = false; }
    }

    /// <summary>Runs this exe elevated (one UAC prompt) to read the MFT, like Everything does. Null if declined or failed.</summary>
    private FileIndex? BuildWithHelper()
    {
        if (_uacDeclined || Environment.ProcessPath is not { } exe) return null;
        var tmp = CachePath + ".new";
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            if (File.Exists(tmp)) File.Delete(tmp);
            SetStatus("빠른 색인(MFT) 중… 관리자 권한 확인 창(UAC)에서 '예'를 눌러 주세요");
            using var p = Process.Start(new ProcessStartInfo(exe, $"--build-index \"{tmp}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null) return null;
            SetStatus("빠른 색인(MFT) 중… 모든 NTFS 드라이브를 읽고 있습니다");
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(tmp)) return null;
            var ix = FileIndex.LoadFromFile(tmp);
            File.Delete(tmp);
            return ix;
        }
        catch (System.ComponentModel.Win32Exception) // 1223: user said no to UAC
        {
            _uacDeclined = true;
            return null;
        }
        catch { return null; }
    }

    /// <summary>No admin rights: walk every folder (much slower on big hard disks).</summary>
    private FileIndex BuildSlow(IReadOnlyList<string> roots, CancellationToken ct)
    {
        string prefix = Index == null ? "색인 만드는 중 (일반 모드, 느림)" : "색인 갱신 중 (일반 모드, 느림)";
        var progress = new Progress(n => SetStatus($"{prefix}… {n:N0}개 — 설정 ▸ '색인 다시 만들기'에서 관리자 권한을 허용하면 몇 초면 끝납니다"));
        return FileIndex.Build(roots, progress, ct);
    }

    private void StartWatchers()
    {
        foreach (var root in Roots())
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => Touch(e.FullPath);
                w.Deleted += (_, e) => Touch(e.FullPath);
                w.Renamed += (_, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
                w.Error += (_, _) => OnOverflow();
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { }
        }
    }

    private static readonly string OwnDir = AppSettings.Dir;

    private void Touch(string path)
    {
        if (path.StartsWith(OwnDir, StringComparison.OrdinalIgnoreCase)) return;
        var dir = Path.GetDirectoryName(path);
        if (dir != null) _pending[dir] = 0;
    }

    private void OnOverflow()
    {
        if (DateTime.UtcNow - _lastOverflowRebuild < TimeSpan.FromMinutes(5)) return;
        _lastOverflowRebuild = DateTime.UtcNow;
        RequestRebuild();
    }

    private int _ticking;
    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var ix = Index;
            if (ix != null && !_rebuilding && !_pending.IsEmpty)
            {
                var dirs = _pending.Keys.ToList();
                foreach (var d in dirs) _pending.TryRemove(d, out _);
                ix.Refresh(dirs);
                _dirty = true;
                if (!Status.StartsWith("색인")) SetStatus($"{ix.Count:N0}개 항목 · 실시간 반영 중");
                IndexChanged?.Invoke();
            }
            else if (_rebuilding == false && _pending.IsEmpty && Status.EndsWith("실시간 반영 중") && ix != null)
            {
                SetStatus($"{ix.Count:N0}개 항목");
            }
            if (_dirty && DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(10)) Save();
        }
        catch { }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void Save()
    {
        try { Index?.SaveToFile(CachePath); _dirty = false; _lastSave = DateTime.UtcNow; } catch { }
    }

    private void SetStatus(string s) { Status = s; StatusChanged?.Invoke(); }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        foreach (var w in _watchers) w.Dispose();
        if (_dirty && !_rebuilding) Save();
    }

    private sealed class Progress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
