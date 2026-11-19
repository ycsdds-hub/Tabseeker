using Tabseeker.Core;

namespace Tabseeker.Core.Tests;

public class SearchQueryTests
{
    private static bool M(string q, string name, bool dir = false, string? path = null) =>
        SearchQuery.Parse(q).Matches(name, dir, () => path ?? @"C:\x\" + name);

    [Fact] public void Empty() => Assert.True(SearchQuery.Parse("   ").IsEmpty);
    [Fact] public void Substring_CaseInsensitive() => Assert.True(M("REPort", "Annual report.pdf"));
    [Fact] public void And() { Assert.True(M("ann pdf", "annual.pdf")); Assert.False(M("ann doc", "annual.pdf")); }
    [Fact] public void Or() { Assert.True(M("doc|pdf", "a.pdf")); Assert.False(M("doc|xls", "a.pdf")); }
    [Fact] public void Not() { Assert.False(M("!pdf", "a.pdf")); Assert.True(M("a !doc", "a.pdf")); }
    [Fact] public void Phrase() { Assert.True(M("\"my file\"", "this is my file.txt")); Assert.False(M("\"my file\"", "my other file.txt")); }
    [Fact] public void Wildcard_WholeName() { Assert.True(M("*.pdf", "x.pdf")); Assert.False(M("*.pdf", "x.pdf.bak")); Assert.True(M("re?ort*", "report.doc")); }
    [Fact] public void Ext() { Assert.True(M("ext:pdf;docx", "a.DOCX")); Assert.False(M("ext:pdf", "pdf")); Assert.False(M("ext:pdf", "x.pdf", dir: true)); }
    [Fact] public void Kind() { Assert.True(M("folder:", "src", dir: true)); Assert.False(M("folder:", "src")); Assert.True(M("file: src", "src.cs")); }
    [Fact] public void PathTerm() { Assert.True(M(@"proj\src", "a.cs", path: @"C:\proj\src\a.cs")); Assert.True(M("proj/src", "a.cs", path: @"C:\proj\src\a.cs")); Assert.False(M(@"proj\lib", "a.cs", path: @"C:\proj\src\a.cs")); }
    [Fact] public void Regex() { Assert.True(M("regex:^a\\d+$", "a123")); Assert.False(M("regex:^a\\d+$", "a12b")); }
    [Fact] public void BadRegex_Ignored() => Assert.True(SearchQuery.Parse("regex:(").IsEmpty);
    [Fact] public void Korean() => Assert.True(M("보고서", "2026 연간 보고서.hwp"));
    [Fact] public void PathWildcard() { Assert.True(M(@"src\*.cs", "a.cs", path: @"C:\proj\src\a.cs")); Assert.False(M(@"src\*.cs", "a.cs", path: @"C:\proj\lib\a.cs")); }
    [Theory]
    [InlineData("abc", "abc", true)] [InlineData("ABC", "a?c", true)] [InlineData("abc", "a*", true)] [InlineData("abc", "*c", true)]
    [InlineData("abc", "*b*", true)] [InlineData("abc", "*d*", false)] [InlineData("abc", "a?", false)] [InlineData("", "*", true)]
    [InlineData("aXbXc", "a*b*c", true)] [InlineData("abcabd", "*abd", true)] [InlineData("ab", "a*b*c", false)]
    public void Glob(string s, string p, bool expected) => Assert.Equal(expected, SearchQuery.Glob(s, p));
}

public sealed class FileIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TabseekerTest_" + Guid.NewGuid().ToString("N"));

    public FileIndexTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "2026"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "docs", "report.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "docs", "2026", "plan.docx"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "main.cs"), "x");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private List<string> Find(FileIndex ix, string q) => ix.Search(SearchQuery.Parse(q)).Select(ix.GetFullPath).ToList();

    [Fact]
    public void Build_IndexesEverything()
    {
        var ix = FileIndex.Build(new[] { _root });
        Assert.Equal(7, ix.Count); // root + docs, 2026, src + 3 files
        Assert.Equal(new[] { Path.Combine(_root, "docs", "2026", "plan.docx") }, Find(ix, "plan"));
        Assert.Equal(2, Find(ix, "ext:pdf;cs").Count);
        Assert.Equal(3, Find(ix, "folder: !TabseekerTest").Count);
    }

    [Fact]
    public void FindDirectory_ResolvesPaths()
    {
        var ix = FileIndex.Build(new[] { _root });
        int i = ix.FindDirectory(Path.Combine(_root, "DOCS", "2026") + "\\");
        Assert.True(i >= 0);
        Assert.Equal(Path.Combine(_root, "docs", "2026"), ix.GetFullPath(i));
        Assert.Equal(-1, ix.FindDirectory(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void Refresh_AddsAndRemoves()
    {
        var ix = FileIndex.Build(new[] { _root });
        File.Delete(Path.Combine(_root, "src", "main.cs"));
        File.WriteAllText(Path.Combine(_root, "src", "util.cs"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "new", "deep"));
        File.WriteAllText(Path.Combine(_root, "new", "deep", "inner.txt"), "x");
        Directory.Delete(Path.Combine(_root, "docs"), true);

        ix.Refresh(new[] { Path.Combine(_root, "src"), Path.Combine(_root, "new", "deep"), Path.Combine(_root, "docs") });

        Assert.Empty(Find(ix, "main"));
        Assert.Single(Find(ix, "util"));
        Assert.Equal(new[] { Path.Combine(_root, "new", "deep", "inner.txt") }, Find(ix, "inner"));
        Assert.Empty(Find(ix, "report"));
        Assert.Empty(Find(ix, "plan"));
        Assert.Equal(-1, ix.FindDirectory(Path.Combine(_root, "docs")));
        Assert.Equal(6, ix.Count); // root, src, util.cs, new, deep, inner.txt
    }

    [Fact]
    public void SaveLoad_RoundTripCompacts()
    {
        var ix = FileIndex.Build(new[] { _root });
        Directory.Delete(Path.Combine(_root, "docs"), true);
        ix.Refresh(new[] { _root });
        using var ms = new MemoryStream();
        ix.Save(ms);
        ms.Position = 0;
        var loaded = FileIndex.Load(ms);
        Assert.Equal(ix.Count, loaded.Count);
        Assert.Equal(new[] { Path.Combine(_root, "src", "main.cs") }, Find(loaded, "main"));
        Assert.True(loaded.FindDirectory(Path.Combine(_root, "src")) >= 0);
    }

    [Fact]
    public void Build_FromTreeRecords()
    {
        // fake MFT: root=5, metafile $MFT (0), folder docs(40) with a.txt(41), orphan(99) whose parent is unknown
        var recs = new List<TreeRecord>
        {
            new(0, 5, "$MFT", false),
            new(41, 40, "a.txt", false),   // child listed before its parent
            new(40, 5, "docs", true),
            new(42, 5, "top.md", false),
            new(99, 1234, "orphan", false),
        };
        var ix = FileIndex.Build(new[] { _root }, treeSource: _ => (5UL, recs));
        Assert.Equal(4, ix.Count); // root, docs, a.txt, top.md
        Assert.Equal(new[] { Path.Combine(_root, "docs", "a.txt") }, Find(ix, "a.txt"));
        Assert.Empty(Find(ix, "$MFT"));
        Assert.Empty(Find(ix, "orphan"));
        Assert.True(ix.FindDirectory(Path.Combine(_root, "docs")) >= 0);
    }

    [Fact]
    public void Build_TreeSourceFailureFallsBackToScan()
    {
        var ix = FileIndex.Build(new[] { _root }, treeSource: _ => throw new UnauthorizedAccessException());
        Assert.Equal(7, ix.Count);
    }

    [Fact]
    public void Search_LargeIndexIsFast()
    {
        var big = Path.Combine(_root, "big");
        for (int d = 0; d < 20; d++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(big, "d" + d)).FullName;
            for (int f = 0; f < 200; f++) File.WriteAllText(Path.Combine(dir, $"file_{d}_{f}.txt"), "");
        }
        var ix = FileIndex.Build(new[] { _root });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = ix.Search(SearchQuery.Parse("file_7_ ext:txt"));
        sw.Stop();
        Assert.Equal(200, hits.Count);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }
}

public class FormatTests
{
    [Fact] public void Sizes() { Assert.Equal("512 B", Format.Size(512)); Assert.Equal("1.5 KB", Format.Size(1536)); Assert.Equal("10 MB", Format.Size(10L << 20)); }
    [Fact] public void Natural() => Assert.True(NaturalComparer.Instance.Compare("file2", "file10") < 0);
}
