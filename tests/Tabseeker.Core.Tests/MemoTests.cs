using System.Text;
using Tabseeker.Core;

namespace Tabseeker.Core.Tests;

public sealed class MemoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TabseekerMemo_" + Guid.NewGuid().ToString("N"));
    private readonly string _db;
    private readonly string _files;

    public MemoTests()
    {
        _db = Path.Combine(_root, "db", "memos.tsdb");
        _files = Directory.CreateDirectory(Path.Combine(_root, "files")).FullName;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private MemoStore Open() { var s = new MemoStore(_db, new DpapiMemoProtector(), FileIdentity.Get); s.Load(); return s; }

    private string NewFile(string name)
    {
        var p = Path.Combine(_files, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void FileIdentityIsStableAndDistinct()
    {
        var a = NewFile("a.txt"); var b = NewFile("b.txt");
        var ida = FileIdentity.Get(a)!;
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{16}$", ida);
        Assert.DoesNotMatch("^00000000-", ida);              // volume serial read correctly
        Assert.NotEqual(ida, FileIdentity.Get(b));
        var moved = Path.Combine(_files, "moved.txt");
        File.Move(a, moved);
        Assert.Equal(ida, FileIdentity.Get(moved));            // survives rename
        Assert.Null(FileIdentity.Get(Path.Combine(_files, "missing.txt")));
        Assert.NotNull(FileIdentity.Get(_files));              // folders too
    }

    [Fact]
    public void SaveFindReload()
    {
        var f = NewFile("setup.exe");
        var s = Open();
        Assert.Equal(MemoLoadResult.New, s.LoadResult);
        s.Save(f, "프린터 드라이버 설치 파일", 300, 120);
        Assert.True(s.HasMemo(f));

        var again = Open();
        Assert.Equal(MemoLoadResult.Loaded, again.LoadResult);
        var m = again.Find(f)!;
        Assert.Equal("프린터 드라이버 설치 파일", m.Text);
        Assert.Equal(300, m.Width);
        Assert.Equal(120, m.Height);
    }

    [Fact]
    public void FileOnDiskIsEncrypted()
    {
        var f = NewFile("a.txt");
        Open().Save(f, "secret-memo-text-12345", 280, 150);
        var raw = File.ReadAllBytes(_db);
        Assert.DoesNotContain("secret-memo-text-12345", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("a.txt", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("secret-memo-text-12345", Encoding.Unicode.GetString(raw));
    }

    [Fact]
    public void EditedOutsideIsRejectedAndBackupRestored()
    {
        var f = NewFile("a.txt");
        var s = Open();
        s.Save(f, "first", 280, 150);
        s.Save(f, "second", 280, 150);   // backup now holds "first"
        var bytes = File.ReadAllBytes(_db);
        bytes[^5] ^= 0xFF;                // tamper with the main file
        File.WriteAllBytes(_db, bytes);

        var reopened = Open();
        Assert.Equal(MemoLoadResult.RecoveredFromBackup, reopened.LoadResult);
        Assert.Equal("first", reopened.Find(f)!.Text);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_db)!, "*.damaged-*"));
    }

    [Fact]
    public void BothFilesDamagedStartsEmptyWithoutCrashing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_db)!);
        File.WriteAllText(_db, "not a memo db");
        var s = Open();
        Assert.Equal(MemoLoadResult.Corrupt, s.LoadResult);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void MemoFollowsFileMovedOutsideTabseeker()
    {
        var f = NewFile("report.pdf");
        Open().Save(f, "2026 연간 보고서 최종본", 280, 150);
        var moved = Path.Combine(Directory.CreateDirectory(Path.Combine(_files, "archive")).FullName, "renamed.pdf");
        File.Move(f, moved);

        var s = Open();
        Assert.Equal("2026 연간 보고서 최종본", s.Find(moved)!.Text);
        Assert.True(s.HasMemo(moved));      // path index updated by the lookup
        Assert.False(s.HasMemo(f));
    }

    [Fact]
    public void NewFileAtSamePathDoesNotInheritOldMemo()
    {
        var f = NewFile("a.txt");
        Open().Save(f, "old file", 280, 150);
        File.Delete(f);
        File.WriteAllText(f, "new");
        Assert.Null(Open().Find(f));
    }

    [Fact]
    public void EmptyTextDeletes()
    {
        var f = NewFile("a.txt");
        var s = Open();
        s.Save(f, "memo", 280, 150);
        Assert.Null(s.Save(f, "   ", 280, 150));
        Assert.False(s.HasMemo(f));
        Assert.Equal(0, Open().Count);
    }

    [Fact]
    public void FolderRenameCarriesChildMemos()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_files, "proj")).FullName;
        var child = Path.Combine(dir, "readme.md");
        File.WriteAllText(child, "x");
        var s = Open();
        s.Save(dir, "프로젝트 폴더", 280, 150);
        s.Save(child, "설명서", 280, 150);
        var newDir = Path.Combine(_files, "project2");
        Directory.Move(dir, newDir);
        s.Renamed(dir, newDir);
        Assert.True(s.HasMemo(newDir));
        Assert.True(s.HasMemo(Path.Combine(newDir, "readme.md")));
        Assert.Equal("설명서", Open().Find(Path.Combine(newDir, "readme.md"))!.Text);
    }

    [Fact]
    public void SearchMatchesTextAndPath()
    {
        var a = NewFile("a.txt"); var b = NewFile("b.txt");
        var s = Open();
        s.Save(a, "세금 보고 서류", 280, 150);
        s.Save(b, "사진 백업", 280, 150);
        Assert.Single(s.Search("세금"));
        Assert.Equal(2, s.Search("files").Count);
        Assert.Empty(s.Search("세금 사진"));
    }

    [Fact]
    public void SecureDirectoryAllowsOnlyCurrentUser()
    {
        var dir = Path.Combine(_root, "secure");
        MemoStore.SecureDirectory(dir);
        var rules = new DirectoryInfo(dir).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.Equal(me, r.IdentityReference));
        Assert.True(new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden));
    }
}
