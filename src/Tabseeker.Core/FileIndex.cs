using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text;

namespace Tabseeker.Core;

/// <summary>
/// In-memory index of every file and folder name under a set of roots (Everything-style).
/// Entries are stored as name + parent index, so a child always has a larger index than its parent.
/// Deleted entries are kept as tombstones until the index is saved (save compacts).
/// </summary>
public sealed class FileIndex
{
    private const byte FlagDir = 1, FlagDeleted = 2;
    private const int FileMagic = 0x4B535354; // "TSSK"
    private const int FileVersion = 1;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly List<string> _names = new();
    private readonly List<int> _parents = new();
    private readonly List<byte> _flags = new();
    private readonly Dictionary<DirKey, int> _dirs = new(DirKeyComparer.Instance);
    private int _liveCount;

    public int Count { get { _lock.EnterReadLock(); try { return _liveCount; } finally { _lock.ExitReadLock(); } } }
    public IReadOnlyList<string> Roots { get; private set; } = Array.Empty<string>();

    private static readonly EnumerationOptions EnumOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    // ---------------------------------------------------------------- build

    /// <param name="treeSource">
    /// Optional fast source for a root (e.g. the NTFS MFT): returns the root's record id and every record on the volume,
    /// or null / throws to fall back to walking directories.
    /// </param>
    public static FileIndex Build(IEnumerable<string> roots, IProgress<int>? progress = null, CancellationToken ct = default,
        Func<string, (ulong RootId, IReadOnlyList<TreeRecord> Records)?>? treeSource = null)
    {
        var index = new FileIndex();
        var rootList = new List<string>();
        foreach (var r in roots)
        {
            var root = NormalizeDir(r);
            if (!Directory.Exists(root)) continue;
            rootList.Add(root);
            int idx = index.AddEntry(-1, root, true);
            (ulong RootId, IReadOnlyList<TreeRecord> Records)? tree = null;
            try { tree = treeSource?.Invoke(root); } catch (OperationCanceledException) { throw; } catch { tree = null; }
            if (tree is { } t) index.AddTree(idx, t.RootId, t.Records);
            else index.ScanSubtree(idx, root, progress, ct);
            progress?.Report(index._names.Count);
        }
        index.Roots = rootList;
        return index;
    }

    /// <summary>Adds a flat id/parent-id record list under an indexed root, parents before children.</summary>
    private void AddTree(int rootIdx, ulong rootId, IReadOnlyList<TreeRecord> records)
    {
        var children = new Dictionary<ulong, List<int>>(records.Count / 4);
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (rec.Id == rootId) continue;
            if (rec.ParentId == rootId && rec.Id < 16 && rec.Name.StartsWith('$')) continue; // NTFS metafiles ($MFT, $Extend, ...)
            if (!children.TryGetValue(rec.ParentId, out var list)) children[rec.ParentId] = list = new List<int>();
            list.Add(i);
        }
        var stack = new Stack<(ulong id, int idx)>();
        stack.Push((rootId, rootIdx));
        while (stack.Count > 0)
        {
            var (id, idx) = stack.Pop();
            if (!children.Remove(id, out var kids)) continue; // Remove: each folder is expanded once
            foreach (var k in kids)
            {
                var rec = records[k];
                int child = AddEntry(idx, rec.Name, rec.IsDirectory);
                if (rec.IsDirectory) stack.Push((rec.Id, child));
            }
        }
    }

    /// <summary>Recursively adds the contents of an already-indexed directory. Caller holds the write lock (or owns the index).</summary>
    private void ScanSubtree(int dirIdx, string dirPath, IProgress<int>? progress, CancellationToken ct)
    {
        var stack = new Stack<(int idx, string path)>();
        stack.Push((dirIdx, dirPath));
        int lastReport = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (parent, path) = stack.Pop();
            foreach (var e in EnumerateDir(path))
            {
                int idx = AddEntry(parent, e.Name, e.IsDir);
                if (e.IsDir && !e.IsReparse) stack.Push((idx, Path.Combine(path, e.Name)));
            }
            if (progress != null && _names.Count - lastReport > 20000)
            {
                lastReport = _names.Count;
                progress.Report(lastReport);
            }
        }
    }

    private readonly record struct DiskEntry(string Name, bool IsDir, bool IsReparse);

    private static List<DiskEntry> EnumerateDir(string path)
    {
        var list = new List<DiskEntry>();
        try
        {
            var en = new FileSystemEnumerable<DiskEntry>(path,
                (ref FileSystemEntry e) => new DiskEntry(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                EnumOptions);
            foreach (var e in en) list.Add(e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return list;
    }

    private int AddEntry(int parent, string name, bool isDir)
    {
        int idx = _names.Count;
        _names.Add(name);
        _parents.Add(parent);
        _flags.Add(isDir ? FlagDir : (byte)0);
        if (isDir) _dirs[new DirKey(parent, name)] = idx;
        _liveCount++;
        return idx;
    }

    // ---------------------------------------------------------------- lookups

    public string GetName(int idx) { _lock.EnterReadLock(); try { return _names[idx]; } finally { _lock.ExitReadLock(); } }
    public bool IsDirectory(int idx) { _lock.EnterReadLock(); try { return (_flags[idx] & FlagDir) != 0; } finally { _lock.ExitReadLock(); } }

    public string GetFullPath(int idx)
    {
        _lock.EnterReadLock();
        try { return FullPathNoLock(idx); }
        finally { _lock.ExitReadLock(); }
    }

    private string FullPathNoLock(int idx)
    {
        Span<int> chain = stackalloc int[256];
        int n = 0;
        for (int i = idx; i >= 0 && n < chain.Length; i = _parents[i]) chain[n++] = i;
        var sb = new StringBuilder(128);
        for (int k = n - 1; k >= 0; k--)
        {
            var part = _names[chain[k]];
            if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
            sb.Append(part);
        }
        return sb.ToString();
    }

    /// <summary>Index of a directory by full path, or -1.</summary>
    public int FindDirectory(string path)
    {
        _lock.EnterReadLock();
        try { return FindDirectoryNoLock(path); }
        finally { _lock.ExitReadLock(); }
    }

    private int FindDirectoryNoLock(string path)
    {
        path = NormalizeDir(path);
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return -1;
        int cur = -1;
        foreach (var r in Roots)
        {
            if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && (path.Length == r.Length || r.EndsWith('\\') || path[r.Length] == '\\')
                && _dirs.TryGetValue(new DirKey(-1, r), out var ri) && (cur < 0 || r.Length > _names[cur].Length))
                cur = ri;
        }
        if (cur < 0) return -1;
        var rest = path[_names[cur].Length..].Trim('\\');
        if (rest.Length == 0) return cur;
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (!_dirs.TryGetValue(new DirKey(cur, part), out cur)) return -1;
        return cur;
    }

    public static string NormalizeDir(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Length > 3) path = path.TrimEnd('\\');
        return path;
    }

    // ---------------------------------------------------------------- search

    public List<int> Search(SearchQuery query, CancellationToken ct = default)
    {
        var result = new List<int>();
        if (query.IsEmpty) return result;
        _lock.EnterReadLock();
        try
        {
            int n = _names.Count;
            var bag = new ConcurrentBag<List<int>>();
            Parallel.ForEach(Partitioner.Create(0, n, Math.Max(4096, n / (Environment.ProcessorCount * 8) + 1)),
                new ParallelOptions { CancellationToken = ct },
                range =>
                {
                    var local = new List<int>();
                    var pc = new PathCache();
                    // siblings are stored next to each other, so the parent's path is computed once per folder
                    int cur = 0, lastParent = int.MinValue;
                    string parentPath = "";
                    Func<string> pathOf = () =>
                    {
                        int p = _parents[cur];
                        if (p < 0) return _names[cur];
                        if (p != lastParent) { lastParent = p; parentPath = FullPathNoLock(p); }
                        return parentPath.EndsWith('\\') ? parentPath + _names[cur] : parentPath + "\\" + _names[cur];
                    };
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        byte f = _flags[i];
                        if ((f & FlagDeleted) != 0) continue;
                        cur = i;
                        var name = _names[i];
                        pc.Reset(name, pathOf);
                        if (query.Matches(name, (f & FlagDir) != 0, pc)) local.Add(i);
                    }
                    if (local.Count > 0) bag.Add(local);
                });
            foreach (var l in bag) result.AddRange(l);
        }
        finally { _lock.ExitReadLock(); }
        result.Sort();
        return result;
    }

    // ---------------------------------------------------------------- live updates

    /// <summary>
    /// Re-reads the given directories from disk and reconciles their direct children with the index
    /// (new folders are scanned recursively, removed folders drop their whole subtree).
    /// Directories that are not yet indexed are resolved to their nearest indexed ancestor.
    /// </summary>
    public void Refresh(IEnumerable<string> directories)
    {
        _lock.EnterWriteLock();
        try
        {
            var targets = new Dictionary<int, string>();
            foreach (var d in directories)
            {
                string? path;
                try { path = NormalizeDir(d); } catch { continue; }
                while (path != null)
                {
                    int idx = FindDirectoryNoLock(path);
                    if (idx >= 0) { targets[idx] = path; break; }
                    path = Path.GetDirectoryName(path);
                }
            }
            if (targets.Count == 0) return;

            // existing children of each target, one pass over the index
            var children = targets.Keys.ToDictionary(k => k, _ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            for (int i = 0; i < _parents.Count; i++)
                if ((_flags[i] & FlagDeleted) == 0 && children.TryGetValue(_parents[i], out var map))
                    map[_names[i]] = i;

            bool anyDeleted = false;
            foreach (var (dirIdx, dirPath) in targets)
            {
                if ((_flags[dirIdx] & FlagDeleted) != 0) continue;
                var existing = children[dirIdx];
                if (!Directory.Exists(dirPath))
                {
                    MarkDeleted(dirIdx);
                    anyDeleted = true;
                    continue;
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in EnumerateDir(dirPath))
                {
                    seen.Add(e.Name);
                    if (existing.TryGetValue(e.Name, out var oldIdx))
                    {
                        bool wasDir = (_flags[oldIdx] & FlagDir) != 0;
                        if (wasDir == e.IsDir) continue;
                        MarkDeleted(oldIdx); anyDeleted = true;   // type changed: replace
                    }
                    int idx = AddEntry(dirIdx, e.Name, e.IsDir);
                    if (e.IsDir && !e.IsReparse) ScanSubtree(idx, Path.Combine(dirPath, e.Name), null, CancellationToken.None);
                }
                foreach (var (name, idx) in existing)
                    if (!seen.Contains(name)) { MarkDeleted(idx); anyDeleted = true; }
            }

            if (anyDeleted) PropagateDeletes();
        }
        finally { _lock.ExitWriteLock(); }
    }

    private void MarkDeleted(int idx)
    {
        if ((_flags[idx] & FlagDeleted) != 0) return;
        _flags[idx] |= FlagDeleted;
        _liveCount--;
        if ((_flags[idx] & FlagDir) != 0) _dirs.Remove(new DirKey(_parents[idx], _names[idx]));
    }

    /// <summary>Children always come after parents, so a single forward pass removes whole subtrees.</summary>
    private void PropagateDeletes()
    {
        for (int i = 0; i < _parents.Count; i++)
        {
            int p = _parents[i];
            if (p >= 0 && (_flags[p] & FlagDeleted) != 0) MarkDeleted(i);
        }
    }

    // ---------------------------------------------------------------- persistence

    public void Save(Stream stream)
    {
        _lock.EnterReadLock();
        try
        {
            using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            w.Write(FileMagic);
            w.Write(FileVersion);
            w.Write(Roots.Count);
            foreach (var r in Roots) w.Write(r);
            var map = new int[_names.Count];
            int live = 0;
            for (int i = 0; i < _names.Count; i++) map[i] = (_flags[i] & FlagDeleted) != 0 ? -1 : live++;
            w.Write(live);
            for (int i = 0; i < _names.Count; i++)
            {
                if (map[i] < 0) continue;
                w.Write(_parents[i] < 0 ? -1 : map[_parents[i]]);
                w.Write((byte)(_flags[i] & FlagDir));
                w.Write(_names[i]);
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    public static FileIndex Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadInt32() != FileMagic || r.ReadInt32() != FileVersion) throw new InvalidDataException("Not a Tabseeker index file.");
        var index = new FileIndex();
        int rootCount = r.ReadInt32();
        var roots = new List<string>(rootCount);
        for (int i = 0; i < rootCount; i++) roots.Add(r.ReadString());
        int count = r.ReadInt32();
        index._names.Capacity = index._parents.Capacity = index._flags.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            int parent = r.ReadInt32();
            bool isDir = r.ReadByte() != 0;
            index.AddEntry(parent, r.ReadString(), isDir);
        }
        index.Roots = roots;
        return index;
    }

    public void SaveToFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            Save(fs);
        File.Move(tmp, path, overwrite: true);
    }

    public static FileIndex LoadFromFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Load(fs);
    }

    // ---------------------------------------------------------------- helpers

    private readonly record struct DirKey(int Parent, string Name);

    private sealed class DirKeyComparer : IEqualityComparer<DirKey>
    {
        public static readonly DirKeyComparer Instance = new();
        public bool Equals(DirKey a, DirKey b) => a.Parent == b.Parent && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(DirKey k) => HashCode.Combine(k.Parent, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Name));
    }
}
