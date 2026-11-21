using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Tabseeker.Core;

/// <summary>One item of Explorer's classic right-click menu.</summary>
public sealed class ShellMenuEntry
{
    /// <summary>Registry class it belongs to: "*" (files), "Directory", "Directory\Background", "Drive", ...</summary>
    public string Class { get; init; } = "";
    /// <summary>Verb or handler key name.</summary>
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>A COM context-menu handler (7-Zip, ALZip, ...): Windows cannot nest these, only hide them.</summary>
    public bool IsHandler { get; init; }
    /// <summary>Command line (verbs) or handler DLL (handlers), for display.</summary>
    public string Detail { get; init; } = "";
    /// <summary>Part of Windows itself: left alone.</summary>
    public bool IsSystem { get; init; }
    /// <summary>Currently grouped (verb) / hidden (handler) by Tabseeker.</summary>
    public bool ByTabseeker { get; init; }

    public string ClassLabel => Class switch
    {
        "*" => "파일",
        "Directory" => "폴더",
        "Directory\\Background" => "폴더 빈 곳",
        "Drive" => "드라이브",
        "AllFilesystemObjects" => "파일·폴더",
        "Folder" => "폴더(모든 종류)",
        _ => Class,
    };
}

/// <summary>
/// Tidies Explorer's classic right-click menu for the current user only (HKCU\Software\Classes, no admin rights):
/// app commands ("Open with PyCharm", "VLC로 재생" ...) move into one cascading submenu that unfolds like a tree,
/// and app extensions that Windows cannot nest (7-Zip, ALZip ...) can be hidden.
/// Every change is written to a manifest so <see cref="Undo"/> puts everything back exactly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellMenuOrganizer
{
    public const string GroupKey = "Tabseeker.AppMenus";
    public const string GroupLabel = "앱 메뉴 모음";
    private const string HideValue = "LegacyDisable";

    /// <summary>Classes whose verbs can be grouped.</summary>
    public static readonly string[] VerbClasses = { "*", "Directory", "Directory\\Background", "Drive" };
    /// <summary>Classes whose handlers can be hidden.</summary>
    public static readonly string[] HandlerClasses = { "*", "AllFilesystemObjects", "Directory", "Directory\\Background", "Folder", "Drive" };

    private static readonly HashSet<string> SystemVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennew", "explore", "opennewprocess", "opennewtab", "opennewwindow", "pintohome", "pintohomefile", "find", "cmd",
        "Powershell", "WSL", "runas", "runasuser", "edit", "print", "printto", "removeproperties", "properties", "format",
        "UpdateEncryptionSettings", "UpdateEncryptionSettingsWork", "change-passphrase", "change-pin", "encrypt-bde", "encrypt-bde-elev",
        "manage-bde", "resume-bde", "resume-bde-elev", "unlock-bde", "LaunchWorkfoldersControl", "ModernProperties",
        "OfflineFilesLaunchSyncCenter", "Eject", "MapNetworkDrive", "DisconnectNetworkDrive", "Share", "pin", "play", "preview",
    };

    private static readonly HashSet<string> SystemHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Open With", "Open With EncryptionMenu", "EncryptionMenu", "Sharing", "SendTo", "ModernSharing", "Library Location",
        "Offline Files", "WorkFolders", "EnhancedStorageShell", "New", "CopyAsPathMenu", "PintoStartScreen", "EPP", "FileSyncEx",
    };

    private readonly RegistryKey _read;     // merged view (HKEY_CLASSES_ROOT) — or a test key
    private readonly RegistryKey _write;    // HKCU\Software\Classes — or a test key
    private readonly RegistryKey _blocked;  // HKCU\...\Shell Extensions\Blocked — or a test key
    private readonly string _manifestPath;

    public ShellMenuOrganizer(RegistryKey read, RegistryKey write, RegistryKey blocked, string manifestPath)
    {
        _read = read;
        _write = write;
        _blocked = blocked;
        _manifestPath = manifestPath;
    }

    /// <summary>
    /// The real thing: HKCR read, HKCU\Software\Classes write (submenu), and the current user's
    /// "blocked shell extensions" list for hiding extensions (Windows' own per-user switch; works for every handler).
    /// </summary>
    public static ShellMenuOrganizer ForCurrentUser(string appDataDir) =>
        new(Registry.ClassesRoot,
            Registry.CurrentUser.CreateSubKey(@"Software\Classes"),
            Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked"),
            Path.Combine(appDataDir, "shellmenu-undo.json"));

    public bool IsApplied => File.Exists(_manifestPath);

    // ---------------------------------------------------------------- scan

    public List<ShellMenuEntry> Scan()
    {
        var manifest = LoadManifest();
        var grouped = manifest?.Grouped.Select(k => (k.Class, k.Name)).ToHashSet() ?? new();
        var hidden = manifest?.Hidden.ToDictionary(k => (k.Class, k.Name), k => k.Original) ?? new();
        var list = new List<ShellMenuEntry>();

        foreach (var cls in VerbClasses)
        {
            using var shell = _read.OpenSubKey(cls + "\\shell");
            if (shell == null) continue;
            foreach (var name in shell.GetSubKeyNames())
            {
                if (name == GroupKey) continue;
                bool ours = grouped.Contains((cls, name));
                // grouped by us: describe it from our copy inside the submenu
                using var verb = ours ? _read.OpenSubKey($"{cls}\\shell\\{GroupKey}\\shell\\{name}") ?? shell.OpenSubKey(name) : shell.OpenSubKey(name);
                if (verb == null) continue;
                var values = verb.GetValueNames();
                // already invisible in the normal menu (Shift-only, disabled, programmatic) — not clutter
                if (!ours && values.Any(v => v is "Extended" or HideValue or "ProgrammaticAccessOnly")) continue;
                if (values.Contains("SubCommands") || values.Contains("ExtendedSubCommandsKey")) continue; // menus of their own
                var label = (verb.GetValue("MUIVerb") as string) ?? (verb.GetValue("") as string) ?? name;
                string command = "";
                using (var cmd = verb.OpenSubKey("command")) command = cmd?.GetValue("") as string ?? "";
                bool system = SystemVerbs.Contains(name) || (command.Length == 0 && verb.GetValue("ExplorerCommandHandler") != null && !ours)
                              || IsWindowsResource(label) || IsWindowsPath(command);
                list.Add(new ShellMenuEntry
                {
                    Class = cls, Name = name, Label = CleanLabel(label, name), Detail = command,
                    IsSystem = system && !ours, ByTabseeker = ours,
                });
            }
        }

        foreach (var cls in HandlerClasses)
        {
            using var handlers = _read.OpenSubKey(cls + "\\shellex\\ContextMenuHandlers");
            if (handlers == null) continue;
            foreach (var name in handlers.GetSubKeyNames())
            {
                using var h = handlers.OpenSubKey(name);
                var clsid = h?.GetValue("") as string;
                bool ours = hidden.TryGetValue((cls, name), out var original);
                if (ours) clsid = original;
                else if (clsid != null && clsid.StartsWith('-')) continue; // disabled by someone else
                if (string.IsNullOrWhiteSpace(clsid)) clsid = name;
                if (!clsid.StartsWith('{')) continue;
                if (!ours && _blocked.GetValue(clsid) != null) continue;     // already blocked by someone else
                var (title, dll) = DescribeClsid(clsid);
                // the DLL's product name ("7-Zip", "Ashampoo UnInstaller 16") reads better than class names like "UIContextHandler"
                string label = ProductName(dll) ?? title ?? (name.StartsWith('{') ? "" : name);
                if (label.Length == 0) label = Path.GetFileNameWithoutExtension(dll) is { Length: > 0 } f ? f : clsid;
                bool system = SystemHandlers.Contains(name) || IsWindowsPath(dll) || dll.Length == 0;
                list.Add(new ShellMenuEntry
                {
                    Class = cls, Name = name, Label = label, Detail = dll, IsHandler = true,
                    IsSystem = system && !ours, ByTabseeker = ours,
                });
            }
        }
        return list;
    }

    private (string? title, string dll) DescribeClsid(string clsid)
    {
        using var key = _read.OpenSubKey("CLSID\\" + clsid);
        var title = key?.GetValue("") as string;
        using var server = key?.OpenSubKey("InprocServer32");
        var dll = Environment.ExpandEnvironmentVariables(server?.GetValue("") as string ?? "");
        return (string.IsNullOrWhiteSpace(title) ? null : title, dll);
    }

    /// <summary>
    /// A readable name for an extension DLL: the app's install folder when it sits right under Program Files
    /// ("7-Zip", "Bandizip", "Ashampoo UnInstaller 16" — DLL product names can be shared or cryptic), else its product name.
    /// </summary>
    private static string? ProductName(string dll)
    {
        try
        {
            if (dll.Length == 0) return null;
            // folders under Program Files: "7-Zip\x.dll" → 7-Zip, "Ashampoo\Ashampoo UnInstaller 10\x.dll" → the product folder,
            // "ESTsoft\ALZip\x.dll" → ALZip; version folders ("26.204.1019") and Common Files fall back to the product name
            var parts = (Path.GetDirectoryName(dll) ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries);
            int pf = Array.FindIndex(parts, p => p.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) || p.StartsWith("PROGRA~", StringComparison.OrdinalIgnoreCase));
            if (pf >= 0 && pf + 1 < parts.Length && !parts[pf + 1].Equals("Common Files", StringComparison.OrdinalIgnoreCase))
            {
                var vendor = parts[pf + 1];
                if (pf + 2 >= parts.Length) return vendor;
                var product = parts[pf + 2];
                bool technical = char.IsDigit(product[0]) || product is "WinUI3Apps" or "bin" or "x64" or "x86" or "lib" or "modules";
                return technical ? vendor : product;
            }
            if (!File.Exists(dll)) return null;
            var name = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductName?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    private static bool IsWindowsResource(string label) =>
        label.StartsWith('@') && (label.Contains("shell32", StringComparison.OrdinalIgnoreCase) || label.Contains("windows.storage", StringComparison.OrdinalIgnoreCase)
            || label.Contains("%SystemRoot%", StringComparison.OrdinalIgnoreCase) || label.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)
            || label.Contains("efscore", StringComparison.OrdinalIgnoreCase) || label.Contains("fvewiz", StringComparison.OrdinalIgnoreCase)
            || label.Contains("wsl.exe", StringComparison.OrdinalIgnoreCase));

    private static bool IsWindowsPath(string s) =>
        s.Contains(@"\Windows\System32", StringComparison.OrdinalIgnoreCase) || s.Contains(@"\Windows\SysWOW64", StringComparison.OrdinalIgnoreCase)
        || s.Contains("%SystemRoot%", StringComparison.OrdinalIgnoreCase) || s.Contains("%windir%", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith(@"C:\Windows\explorer", StringComparison.OrdinalIgnoreCase);

    private static string CleanLabel(string label, string name)
    {
        if (label.StartsWith('@')) return name; // resource string we cannot resolve here
        return label.Replace("&", "");
    }

    // ---------------------------------------------------------------- apply / undo

    /// <summary>
    /// Moves <paramref name="group"/> verbs into the "앱 메뉴 모음" submenu (per class) and hides <paramref name="hide"/> handlers.
    /// Any earlier Tabseeker change is undone first, so the result is exactly this selection.
    /// </summary>
    public void Apply(IEnumerable<ShellMenuEntry> group, IEnumerable<ShellMenuEntry> hide, string? iconPath)
    {
        if (IsApplied) Undo();
        var m = new Manifest { Applied = DateTime.Now };
        try
        {
            foreach (var byClass in group.Where(e => !e.IsHandler).GroupBy(e => e.Class))
            {
                var groupPath = $"{byClass.Key}\\shell\\{GroupKey}";
                CreateKey(m, groupPath);
                using (var g = _write.OpenSubKey(groupPath, writable: true)!)
                {
                    g.SetValue("MUIVerb", GroupLabel);
                    g.SetValue("SubCommands", "");
                    if (!string.IsNullOrEmpty(iconPath)) g.SetValue("Icon", iconPath);
                    g.SetValue("Position", "Bottom");
                }
                foreach (var e in byClass)
                {
                    using var src = _read.OpenSubKey($"{e.Class}\\shell\\{e.Name}");
                    if (src == null) continue;
                    using (var dst = _write.CreateSubKey($"{groupPath}\\shell\\{e.Name}")) CopyKey(src, dst);
                    SetValue(m, $"{e.Class}\\shell\\{e.Name}", HideValue, "");   // hide the original
                    m.Grouped.Add(new EntryRef { Class = e.Class, Name = e.Name });
                }
            }
            var blockedDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in hide.Where(e => e.IsHandler))
            {
                var path = $"{e.Class}\\shellex\\ContextMenuHandlers\\{e.Name}";
                using var src = _read.OpenSubKey(path);
                var clsid = src?.GetValue("") as string;
                if (string.IsNullOrWhiteSpace(clsid)) clsid = e.Name;
                if (!clsid.StartsWith('{')) continue;
                // Windows' per-user "blocked shell extensions" list: Explorer skips the extension everywhere.
                // (Changing the handler key's value does not work for keys named by their CLSID.)
                if (blockedDone.Add(clsid))
                {
                    var old = _blocked.GetValue(clsid);
                    m.Ops.Add(new Op { Kind = "SetValue", Root = "blocked", Path = "", Name = clsid, HadOld = old != null, Old = old?.ToString() });
                    _blocked.SetValue(clsid, $"{e.Label} (Tabseeker 우클릭 메뉴 정리)");
                }
                m.Hidden.Add(new EntryRef { Class = e.Class, Name = e.Name, Original = clsid });
            }
        }
        finally
        {
            if (m.Ops.Count > 0) SaveManifest(m);
        }
    }

    /// <summary>Puts every registry change back (newest first) and deletes the manifest.</summary>
    public void Undo()
    {
        var m = LoadManifest();
        if (m == null) return;
        for (int i = m.Ops.Count - 1; i >= 0; i--)
        {
            var op = m.Ops[i];
            try
            {
                if (op.Kind == "CreatedKey") _write.DeleteSubKeyTree(op.Path, throwOnMissingSubKey: false);
                else if (op.Kind == "SetValue" && op.Root == "blocked")
                {
                    if (op.HadOld) _blocked.SetValue(op.Name, op.Old ?? "");
                    else _blocked.DeleteValue(op.Name, throwOnMissingValue: false);
                }
                else if (op.Kind == "SetValue")
                {
                    using var k = _write.OpenSubKey(op.Path, writable: true);
                    if (k == null) continue;
                    if (op.HadOld) k.SetValue(op.Name, op.Old ?? "");
                    else k.DeleteValue(op.Name, throwOnMissingValue: false);
                }
            }
            catch { /* keep undoing the rest */ }
        }
        File.Delete(_manifestPath);
    }

    /// <summary>
    /// Creates the key if missing and records only that key for undo. Empty parent keys ("*\shell") are left behind on
    /// purpose: other programs may add their own entries there later, and undo must never delete those.
    /// </summary>
    private void CreateKey(Manifest m, string path)
    {
        using (var existing = _write.OpenSubKey(path)) if (existing != null) return;
        _write.CreateSubKey(path)!.Dispose();
        m.Ops.Add(new Op { Kind = "CreatedKey", Path = path });
    }

    private void SetValue(Manifest m, string path, string name, string value)
    {
        CreateKey(m, path);
        using var k = _write.OpenSubKey(path, writable: true)!;
        var old = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        // a key we just created goes away as a whole on undo; only pre-existing keys need value-level undo
        if (!m.Ops.Any(o => o.Kind == "CreatedKey" && (path == o.Path || path.StartsWith(o.Path + "\\"))))
            m.Ops.Add(new Op { Kind = "SetValue", Path = path, Name = name, HadOld = old != null, Old = old?.ToString() });
        k.SetValue(name, value);
    }

    private static void CopyKey(RegistryKey src, RegistryKey dst)
    {
        foreach (var v in src.GetValueNames())
            dst.SetValue(v, src.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, src.GetValueKind(v));
        foreach (var sub in src.GetSubKeyNames())
        {
            using var s = src.OpenSubKey(sub);
            if (s == null) continue;
            using var d = dst.CreateSubKey(sub);
            CopyKey(s, d);
        }
    }

    // ---------------------------------------------------------------- manifest

    public sealed class EntryRef
    {
        public string Class { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Original { get; set; }
    }

    public sealed class Op
    {
        public string Kind { get; set; } = "";
        /// <summary>"classes" (HKCU\Software\Classes, default) or "blocked" (the per-user blocked-extensions list).</summary>
        public string Root { get; set; } = "classes";
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool HadOld { get; set; }
        public string? Old { get; set; }
    }

    public sealed class Manifest
    {
        public DateTime Applied { get; set; }
        public List<EntryRef> Grouped { get; set; } = new();
        public List<EntryRef> Hidden { get; set; } = new();
        public List<Op> Ops { get; set; } = new();
    }

    public Manifest? LoadManifest()
    {
        try { return File.Exists(_manifestPath) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(_manifestPath)) : null; }
        catch { return null; }
    }

    private void SaveManifest(Manifest m)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        File.WriteAllText(_manifestPath, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
    }
}
