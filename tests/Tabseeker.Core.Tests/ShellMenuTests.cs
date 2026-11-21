using Microsoft.Win32;
using Tabseeker.Core;

namespace Tabseeker.Core.Tests;

/// <summary>
/// Runs the right-click menu organizer against a throwaway registry key (HKCU\Software\TabseekerTest_*),
/// never against the real Explorer classes.
/// </summary>
public sealed class ShellMenuTests : IDisposable
{
    private readonly string _rootName = @"Software\TabseekerTest_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly RegistryKey _read;   // plays HKEY_CLASSES_ROOT
    private readonly RegistryKey _write;  // plays HKCU\Software\Classes
    private readonly RegistryKey _blocked; // plays HKCU\...\Shell Extensions\Blocked
    private readonly string _manifest = Path.Combine(Path.GetTempPath(), "tsk_shellmenu_" + Guid.NewGuid().ToString("N") + ".json");

    public ShellMenuTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName);
        _read = _root.CreateSubKey("read");
        _write = _root.CreateSubKey("write");
        _blocked = _root.CreateSubKey("blocked");

        Verb("*", "Notepad++", "Edit with Notepad++", @"""C:\Program Files\Notepad++\notepad++.exe"" ""%1""");
        Verb("*", "Grammarly", "Import to Grammarly", @"C:\Apps\grammarly.exe ""%1""");
        Verb("*", "pintohomefile", "@shell32.dll,-51608", "");                         // Windows
        Verb("Directory", "git_shell", "Open Git Ba&sh here", @"C:\Program Files\Git\git-bash.exe ""--cd=%1""");
        Verb("Directory", "cmd", "@shell32.dll,-8506", "cmd.exe", extended: true);   // Shift-only: not clutter
        using (var dropTarget = _read.CreateSubKey(@"Directory\shell\git_shell\DropTarget")) dropTarget.SetValue("CLSID", "{dummy}");

        Handler("*", "7-Zip", "{23170F69-40C1-278A-1000-000100020000}", "7-Zip Shell Extension", @"C:\Program Files\7-Zip\7-zip.dll");
        Handler("*", "Open With", "{09799AFB-AD67-11d1-ABCD-00C04FC30936}", null, @"C:\Windows\System32\shell32.dll");
        // a handler whose key is named by its CLSID with an empty value (like Ashampoo's)
        Handler("*", "{595F4AF2-DEAA-427D-8F93-C9790265967E}", "", "UIContextHandler", @"C:\Apps\Ashampoo\UIContextHandler64.dll");
        Handler("Folder", "{595F4AF2-DEAA-427D-8F93-C9790265967E}", "", "UIContextHandler", @"C:\Apps\Ashampoo\UIContextHandler64.dll");
    }

    public void Dispose()
    {
        _read.Dispose(); _write.Dispose(); _blocked.Dispose(); _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, false);
        File.Delete(_manifest);
    }

    private void Verb(string cls, string name, string label, string command, bool extended = false)
    {
        using var k = _read.CreateSubKey($@"{cls}\shell\{name}");
        k.SetValue("MUIVerb", label);
        if (extended) k.SetValue("Extended", "");
        if (command.Length > 0) using (var c = k.CreateSubKey("command")) c.SetValue("", command);
    }

    private void Handler(string cls, string name, string clsid, string? title, string dll)
    {
        using (var h = _read.CreateSubKey($@"{cls}\shellex\ContextMenuHandlers\{name}")) h.SetValue("", clsid);
        if (clsid.Length == 0) clsid = name;
        using var c = _read.CreateSubKey($@"CLSID\{clsid}");
        if (title != null) c.SetValue("", title);
        using var s = c.CreateSubKey("InprocServer32");
        s.SetValue("", dll);
    }

    private ShellMenuOrganizer New() => new(_read, _write, _blocked, _manifest);

    private bool Blocked(string clsid) => _blocked.GetValue(clsid) != null;

    /// <summary>Emulates HKCR's merge: our HKCU-side values win over the machine-side ones.</summary>
    private object? Merged(string path, string value)
    {
        using var w = _write.OpenSubKey(path);
        var v = w?.GetValue(value);
        if (v != null) return v;
        using var r = _read.OpenSubKey(path);
        return r?.GetValue(value);
    }

    [Fact]
    public void ScanFindsAppItemsAndMarksWindowsOnes()
    {
        var items = New().Scan();
        Assert.Contains(items, e => e.Name == "Notepad++" && !e.IsSystem && e.Label == "Edit with Notepad++");
        Assert.Contains(items, e => e.Name == "git_shell" && !e.IsSystem && e.Label == "Open Git Bash here");
        Assert.Contains(items, e => e.Name == "pintohomefile" && e.IsSystem);
        Assert.DoesNotContain(items, e => e.Name == "cmd");                       // Shift-only items are left out
        // product name of the DLL when it exists ("7-Zip"), otherwise the class title
        Assert.Contains(items, e => e.Name == "7-Zip" && e.IsHandler && !e.IsSystem && e.Label.StartsWith("7-Zip"));
        Assert.Contains(items, e => e.Name == "Open With" && e.IsHandler && e.IsSystem);
    }

    [Fact]
    public void ApplyGroupsVerbsIntoSubmenuAndHidesOriginals()
    {
        var org = New();
        var items = org.Scan();
        org.Apply(items.Where(e => !e.IsHandler && !e.IsSystem), items.Where(e => e.Name == "7-Zip"), @"C:\x\Tabseeker.exe,0");

        using (var g = _write.OpenSubKey($@"*\shell\{ShellMenuOrganizer.GroupKey}")!)
        {
            Assert.Equal(ShellMenuOrganizer.GroupLabel, g.GetValue("MUIVerb"));
            Assert.Equal("", g.GetValue("SubCommands"));
        }
        using (var copied = _write.OpenSubKey($@"*\shell\{ShellMenuOrganizer.GroupKey}\shell\Notepad++\command")!)
            Assert.Contains("notepad++.exe", (string)copied.GetValue("")!);
        using (var dt = _write.OpenSubKey($@"Directory\shell\{ShellMenuOrganizer.GroupKey}\shell\git_shell\DropTarget")!)
            Assert.Equal("{dummy}", dt.GetValue("CLSID"));                       // sub-keys copied too
        Assert.Equal("", Merged(@"*\shell\Notepad++", "LegacyDisable"));           // original hidden
        Assert.Null(Merged(@"*\shell\pintohomefile", "LegacyDisable"));            // Windows item untouched
        Assert.True(Blocked("{23170F69-40C1-278A-1000-000100020000}"));            // 7-Zip blocked for this user
        Assert.False(Blocked("{595F4AF2-DEAA-427D-8F93-C9790265967E}"));           // not selected
        Assert.Null(_write.OpenSubKey(@"*\shellex\ContextMenuHandlers\7-Zip"));     // handler keys themselves untouched
        Assert.True(org.IsApplied);

        // the machine-side ("read") keys were never written
        using var src = _read.OpenSubKey(@"*\shell\Notepad++")!;
        Assert.Null(src.GetValue("LegacyDisable"));
    }

    [Fact]
    public void ScanAfterApplyShowsTabseekerChanges()
    {
        var org = New();
        var items = org.Scan();
        org.Apply(items.Where(e => e.Name == "Grammarly"), items.Where(e => e.Name == "7-Zip"), null);
        var again = New().Scan();
        Assert.Contains(again, e => e.Name == "Grammarly" && e.ByTabseeker && e.Label == "Import to Grammarly");
        Assert.Contains(again, e => e.Name == "7-Zip" && e.ByTabseeker);
        Assert.Contains(again, e => e.Name == "Notepad++" && !e.ByTabseeker);
        Assert.DoesNotContain(again, e => e.Name == ShellMenuOrganizer.GroupKey);
    }

    [Fact]
    public void UndoRestoresEverythingExactly()
    {
        // a pre-existing per-user verb with its own value must come back unchanged
        using (var mine = _write.CreateSubKey(@"*\shell\Grammarly")) mine.SetValue("Icon", "g.ico");
        string Snapshot() => Dump(_write);
        var before = Snapshot();

        var org = New();
        var items = org.Scan();
        org.Apply(items.Where(e => !e.IsHandler && !e.IsSystem), items.Where(e => e.IsHandler && !e.IsSystem), null);
        Assert.NotEqual(before, Snapshot());
        org.Undo();

        Assert.False(org.IsApplied);
        using (var mine = _write.OpenSubKey(@"*\shell\Grammarly")!)
        {
            Assert.Equal("g.ico", mine.GetValue("Icon"));
            Assert.Null(mine.GetValue("LegacyDisable"));
        }
        Assert.Null(_write.OpenSubKey($@"*\shell\{ShellMenuOrganizer.GroupKey}"));
        Assert.Null(_write.OpenSubKey(@"*\shell\Notepad++"));
        Assert.Null(_write.OpenSubKey(@"*\shellex\ContextMenuHandlers\7-Zip"));
    }

    [Fact]
    public void ClsidNamedHandlerIsBlockedOnceAndUndone()
    {
        var org = New();
        var items = org.Scan();
        var ash = items.Where(e => e.Name.StartsWith("{595F4AF2")).ToList();
        Assert.Equal(2, ash.Count);                                                  // files + folders
        Assert.All(ash, e => Assert.False(e.IsSystem));
        // a value someone else already put in the blocked list must survive our undo
        _blocked.SetValue("{11111111-2222-3333-4444-555555555555}", "other tool");
        org.Apply(Array.Empty<ShellMenuEntry>(), ash, null);
        Assert.True(Blocked("{595F4AF2-DEAA-427D-8F93-C9790265967E}"));
        Assert.Single(org.LoadManifest()!.Ops);                                     // one blocked-list value for both places
        Assert.Contains(New().Scan(), e => e.Name.StartsWith("{595F4AF2") && e.ByTabseeker);
        org.Undo();
        Assert.False(Blocked("{595F4AF2-DEAA-427D-8F93-C9790265967E}"));
        Assert.True(Blocked("{11111111-2222-3333-4444-555555555555}"));
    }

    [Fact]
    public void HandlerBlockedBySomeoneElseIsNotListed()
    {
        _blocked.SetValue("{23170F69-40C1-278A-1000-000100020000}", "other tool");
        Assert.DoesNotContain(New().Scan(), e => e.Name == "7-Zip");
    }

    [Fact]
    public void ReapplyReplacesPreviousSelection()
    {
        var org = New();
        var items = org.Scan();
        org.Apply(items.Where(e => e.Name is "Notepad++" or "Grammarly"), Array.Empty<ShellMenuEntry>(), null);
        org.Apply(items.Where(e => e.Name == "Grammarly"), Array.Empty<ShellMenuEntry>(), null);
        Assert.Null(Merged(@"*\shell\Notepad++", "LegacyDisable"));                 // un-grouped again
        Assert.Null(_write.OpenSubKey($@"*\shell\{ShellMenuOrganizer.GroupKey}\shell\Notepad++"));
        Assert.Equal("", Merged(@"*\shell\Grammarly", "LegacyDisable"));
    }

    private static string Dump(RegistryKey k, string prefix = "")
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in k.GetValueNames().OrderBy(x => x)) sb.Append(prefix).Append('|').Append(v).Append('=').Append(k.GetValue(v)).Append('\n');
        foreach (var s in k.GetSubKeyNames().OrderBy(x => x))
        {
            using var sub = k.OpenSubKey(s)!;
            var inner = Dump(sub, prefix + "\\" + s);
            if (inner.Length > 0 || sub.GetSubKeyNames().Length == 0) sb.Append(prefix).Append("\\").Append(s).Append('\n').Append(inner);
        }
        return sb.ToString();
    }
}
