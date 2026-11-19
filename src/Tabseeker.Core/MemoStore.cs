using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Tabseeker.Core;

/// <summary>A note attached to one file or folder.</summary>
public sealed class Memo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Volume serial + NTFS file id (<see cref="FileIdentity"/>); follows the file across renames/moves.</summary>
    public string? FileId { get; set; }
    /// <summary>Last known full path.</summary>
    public string Path { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Bubble size the user chose for this memo.</summary>
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 150;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>Encrypts/decrypts the whole database. Decryption must fail if the data was altered.</summary>
public interface IMemoProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Windows DPAPI bound to the current Windows user plus a Tabseeker-specific secret:
/// other users cannot read it at all, and any change made outside Tabseeker makes decryption fail.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiMemoProtector : IMemoProtector
{
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("Tabseeker/MemoStore/v1/8f3c1e2a-5b7d-4c90-a1e6-d2f4b8c0e917"));
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

public enum MemoLoadResult { New, Loaded, RecoveredFromBackup, Corrupt }

/// <summary>
/// The memo database: one encrypted file (plus one encrypted backup) in a folder only the current user may open.
/// Only this class reads or writes it, so memos can be created, read, changed and deleted only from inside Tabseeker.
/// Memos are found by file id first (survives moves/renames) and by path second.
/// </summary>
public sealed class MemoStore
{
    private static readonly byte[] Magic = "TSKMEMO1"u8.ToArray();

    private readonly object _lock = new();
    private readonly string _file;
    private readonly IMemoProtector _protector;
    private readonly Func<string, string?> _fileId;
    private readonly List<Memo> _memos = new();
    private readonly Dictionary<string, Memo> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Memo> _byFileId = new(StringComparer.Ordinal);

    public string FilePath => _file;
    public string BackupPath => _file + ".bak";
    public MemoLoadResult LoadResult { get; private set; }
    public int Count { get { lock (_lock) return _memos.Count; } }

    /// <summary>Raised after a memo was created, changed or deleted (argument: its path).</summary>
    public event Action<string>? Changed;

    public MemoStore(string file, IMemoProtector protector, Func<string, string?>? fileId = null)
    {
        _file = file;
        _protector = protector;
        _fileId = fileId ?? (_ => null);
    }

    [SupportedOSPlatform("windows")]
    public static MemoStore OpenDefault(string appDataDir)
    {
        var dir = Path.Combine(appDataDir, "memo");
        SecureDirectory(dir);
        var store = new MemoStore(Path.Combine(dir, "memos.tsdb"), new DpapiMemoProtector(), FileIdentity.Get);
        store.Load();
        return store;
    }

    /// <summary>Creates the folder (hidden) and limits access to the current Windows user only.</summary>
    [SupportedOSPlatform("windows")]
    public static void SecureDirectory(string dir)
    {
        var di = Directory.CreateDirectory(dir);
        try
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var sec = new DirectorySecurity();
            sec.SetOwner(me);
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
            di.Attributes |= FileAttributes.Hidden;
        }
        catch { /* keep working even if the ACL cannot be applied */ }
    }

    // ---------------------------------------------------------------- load / save

    public MemoLoadResult Load()
    {
        lock (_lock)
        {
            _memos.Clear(); _byPath.Clear(); _byFileId.Clear();
            if (!File.Exists(_file) && !File.Exists(BackupPath)) return LoadResult = MemoLoadResult.New;
            if (TryRead(_file, out var list)) { Index(list); return LoadResult = MemoLoadResult.Loaded; }
            // main file damaged or edited outside Tabseeker: keep it aside, fall back to the backup
            try { if (File.Exists(_file)) File.Move(_file, $"{_file}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}"); } catch { }
            if (TryRead(BackupPath, out list)) { Index(list); WriteNoLock(); return LoadResult = MemoLoadResult.RecoveredFromBackup; }
            return LoadResult = MemoLoadResult.Corrupt;
        }
    }

    private bool TryRead(string file, out List<Memo> list)
    {
        list = new List<Memo>();
        try
        {
            if (!File.Exists(file)) return false;
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return false;
            var json = _protector.Unprotect(bytes[Magic.Length..]);
            list = JsonSerializer.Deserialize<List<Memo>>(json) ?? new List<Memo>();
            return true;
        }
        catch { return false; }
    }

    private void Index(List<Memo> list)
    {
        foreach (var m in list)
        {
            if (string.IsNullOrWhiteSpace(m.Text)) continue;
            _memos.Add(m);
            _byPath[m.Path] = m;
            if (m.FileId != null) _byFileId[m.FileId] = m;
        }
    }

    /// <summary>Atomic write: new file → replace current, previous version kept as the backup.</summary>
    private void WriteNoLock()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(_memos);
        var data = Magic.Concat(_protector.Protect(json)).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(_file)) File.Replace(tmp, _file, BackupPath, ignoreMetadataErrors: true);
        else { File.Move(tmp, _file); File.Copy(_file, BackupPath, overwrite: true); }
    }

    // ---------------------------------------------------------------- queries

    public bool HasMemo(string path) { lock (_lock) return _byPath.ContainsKey(Norm(path)); }

    /// <summary>The memo for this file: by file id first (fixes a stale path after a move), then by path.</summary>
    public Memo? Find(string path)
    {
        path = Norm(path);
        var id = _fileId(path);
        lock (_lock)
        {
            if (id != null && _byFileId.TryGetValue(id, out var byId))
            {
                if (!string.Equals(byId.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    _byPath.Remove(byId.Path);
                    byId.Path = path;
                    _byPath[path] = byId;
                    WriteNoLock();
                }
                return Clone(byId);
            }
            if (_byPath.TryGetValue(path, out var byPath))
            {
                // same path but a different file now lives there: the memo belonged to the old one
                if (id != null && byPath.FileId != null && byPath.FileId != id) return null;
                return Clone(byPath);
            }
            return null;
        }
    }

    public IReadOnlyList<Memo> All() { lock (_lock) return _memos.Select(Clone).ToList(); }

    /// <summary>Memos whose text or path contains every word.</summary>
    public List<Memo> Search(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_lock)
            return _memos.Where(m => words.All(w => m.Text.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Path.Contains(w, StringComparison.OrdinalIgnoreCase)))
                         .OrderByDescending(m => m.Updated).Select(Clone).ToList();
    }

    // ---------------------------------------------------------------- changes

    /// <summary>Creates or updates the memo; empty text deletes it. Returns the saved memo, or null when deleted.</summary>
    public Memo? Save(string path, string text, double width, double height)
    {
        path = Norm(path);
        if (string.IsNullOrWhiteSpace(text)) { Delete(path); return null; }
        var id = _fileId(path);
        Memo result;
        lock (_lock)
        {
            var m = (id != null && _byFileId.TryGetValue(id, out var a) ? a : null)
                    ?? (_byPath.TryGetValue(path, out var b) && (id == null || b.FileId == null || b.FileId == id) ? b : null);
            if (m == null)
            {
                m = new Memo { FileId = id, Path = path };
                _memos.Add(m);
            }
            else _byPath.Remove(m.Path);
            m.Path = path;
            m.FileId = id ?? m.FileId;
            m.Text = text.TrimEnd();
            m.Width = width;
            m.Height = height;
            m.Updated = DateTime.Now;
            _byPath[path] = m;
            if (m.FileId != null) _byFileId[m.FileId] = m;
            WriteNoLock();
            result = Clone(m);
        }
        Changed?.Invoke(path);
        return result;
    }

    public bool Delete(string path)
    {
        path = Norm(path);
        lock (_lock)
        {
            if (!_byPath.TryGetValue(path, out var m))
            {
                var id = _fileId(path);
                if (id == null || !_byFileId.TryGetValue(id, out m)) return false;
            }
            _memos.Remove(m);
            _byPath.Remove(m.Path);
            if (m.FileId != null) _byFileId.Remove(m.FileId);
            WriteNoLock();
        }
        Changed?.Invoke(path);
        return true;
    }

    /// <summary>Keeps memos attached after a rename/move done in Tabseeker (a folder carries its children's memos).</summary>
    public void Renamed(string oldPath, string newPath)
    {
        oldPath = Norm(oldPath); newPath = Norm(newPath);
        bool any = false;
        lock (_lock)
        {
            foreach (var m in _memos)
            {
                string? updated = null;
                if (string.Equals(m.Path, oldPath, StringComparison.OrdinalIgnoreCase)) updated = newPath;
                else if (m.Path.StartsWith(oldPath + "\\", StringComparison.OrdinalIgnoreCase)) updated = newPath + m.Path[oldPath.Length..];
                if (updated == null) continue;
                _byPath.Remove(m.Path);
                m.Path = updated;
                _byPath[updated] = m;
                any = true;
            }
            if (any) WriteNoLock();
        }
        if (any) { Changed?.Invoke(oldPath); Changed?.Invoke(newPath); }
    }

    private static string Norm(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    private static Memo Clone(Memo m) => new()
    {
        Id = m.Id, FileId = m.FileId, Path = m.Path, Text = m.Text,
        Width = m.Width, Height = m.Height, Created = m.Created, Updated = m.Updated,
    };
}
