using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>A search tab: instant whole-disk filename search over the shared index.</summary>
public partial class SearchView : UserControl
{
    private const int MaxShown = 50_000;
    /// <summary>"memo: words" searches memo text (and memo paths) instead of file names.</summary>
    public const string MemoPrefix = "memo:";

    private readonly IShell _shell;
    private readonly IndexService _index;
    private CancellationTokenSource? _cts;
    private int _version;
    private string _sortKey = "Name";
    private bool _sortDesc;
    private List<FileItem> _items = new();
    private int _total;
    private long _elapsedMs;

    public string Query { get; private set; } = "";
    public string Title => Query.Length == 0 ? "검색" : Query;

    public SearchView(IShell shell, IndexService index)
    {
        _shell = shell;
        _index = index;
        InitializeComponent();
        ListHelpers.EnableDragOut(List);
        shell.Memos.Attach(List);
        UpdateStatus();
    }

    private bool IsMemoQuery => Query.TrimStart().StartsWith(MemoPrefix, StringComparison.OrdinalIgnoreCase);

    public void SetQuery(string text)
    {
        if (text == Query) return;
        Query = text;
        Run(quiet: false);
        _shell.ViewChanged(this);
    }

    /// <summary>Index changed (finished building or live update): refresh results without disturbing the user.</summary>
    public void Rerun() => Run(quiet: true);

    private void Run(bool quiet)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int version = ++_version;
        if (IsMemoQuery && MemoController.Store is { } memos)
        {
            // newest memos first; folders/files that no longer exist are still listed (dimmed by the shell icon) so their memo can be read
            var sw = Stopwatch.StartNew();
            var words = Query.TrimStart()[MemoPrefix.Length..];
            var found = memos.Search(words).Select(m =>
                new FileItem(m.Path, Path.GetFileName(m.Path) is { Length: > 0 } n ? n : m.Path, Directory.Exists(m.Path))).ToList();
            Show(found, found.Count, sw.ElapsedMilliseconds, quiet);
            return;
        }
        var ix = _index.Index;
        var q = SearchQuery.Parse(Query);
        if (q.IsEmpty || ix == null)
        {
            Show(new List<FileItem>(), 0, 0, quiet: false);
            return;
        }
        string key = _sortKey; bool desc = _sortDesc;
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var hits = ix.Search(q, cts.Token);
            int total = hits.Count;
            // huge result sets (e.g. a single letter): show the first MaxShown in disk order and ask for a narrower query
            if (total > MaxShown) hits = hits.GetRange(0, MaxShown);
            var items = hits.Select(i => new FileItem(ix.GetFullPath(i), ix.GetName(i), ix.IsDirectory(i))).ToList();
            items = ListHelpers.Sort(items, key, desc, foldersFirst: false);
            sw.Stop();
            return (items, total, sw.ElapsedMilliseconds);
        }, cts.Token).ContinueWith(t =>
        {
            if (version != _version || t.IsCanceled || t.IsFaulted) return;
            var (items, total, ms) = t.Result;
            Show(items, total, ms, quiet);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Show(List<FileItem> items, int total, long ms, bool quiet)
    {
        if (quiet && items.Count == _items.Count && items.Select(i => i.FullPath).SequenceEqual(_items.Select(i => i.FullPath)))
        {
            _total = total;
            UpdateStatus();
            return;
        }
        var selected = quiet ? ListHelpers.Selected(List).Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        _items = items;
        _total = total;
        _elapsedMs = ms;
        List.ItemsSource = items;
        ListHelpers.MarkSortHeader(Grid, _sortKey, _sortDesc);
        if (selected != null && selected.Count > 0)
        {
            foreach (var it in items.Where(i => selected.Contains(i.FullPath))) List.SelectedItems.Add(it);
            if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
        }
        else if (items.Count > 0) List.ScrollIntoView(items[0]);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (IsMemoQuery)
        {
            Status.Text = $"메모 {_total:N0}개 (최근 수정 순) — 이름에 마우스를 올리면 메모가 보입니다";
            return;
        }
        if (!_index.IsReady) { Status.Text = "색인을 만드는 중입니다… 처음 한 번만 시간이 걸리고, 다음부터는 바로 시작합니다."; return; }
        if (Query.Trim().Length == 0) { Status.Text = $"전체 {(_index.Index?.Count ?? 0):N0}개 항목에서 검색"; return; }
        var s = $"{_total:N0}개 결과 ({_elapsedMs}ms)";
        if (_total > _items.Count) s += $" · 처음 {_items.Count:N0}개만 표시 — 검색어를 더 구체적으로 입력하세요";
        int sel = List.SelectedItems.Count;
        if (sel > 0) s += $"   ·   {sel:N0}개 선택";
        Status.Text = s;
    }

    public void IndexStatusChanged() { if (_items.Count == 0) UpdateStatus(); }

    public void FocusList()
    {
        if (List.Items.Count == 0) return;
        if (List.SelectedIndex < 0) List.SelectedIndex = 0;
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) is ListViewItem c) c.Focus(); else List.Focus();
    }

    // ------------------------------------------------------------ actions

    private void OpenSelected(bool containingFolder)
    {
        foreach (var it in ListHelpers.Selected(List).Take(20))
        {
            if (containingFolder) _shell.OpenFolderTab(it.Folder, activate: true, selectName: it.Name);
            else if (it.IsDir) _shell.OpenFolderTab(it.FullPath);
            else FileOps.Open(it.FullPath);
        }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ListHelpers.ItemAt(List, e.GetPosition(List)) != null)
            OpenSelected(containingFolder: Keyboard.Modifiers == ModifierKeys.Control);
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var it = ListHelpers.ItemAt(List, e.GetPosition(List));
        if (it == null) return;
        _shell.OpenFolderTab(it.IsDir ? it.FullPath : it.Folder, activate: false, selectName: it.IsDir ? null : it.Name);
        e.Handled = true;
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var sel = ListHelpers.Selected(List);
        switch (e.Key)
        {
            case Key.Enter: OpenSelected(containingFolder: mods == ModifierKeys.Control); e.Handled = true; break;
            case Key.Delete: FileOps.Delete(sel.Select(s => s.FullPath).ToList(), mods == ModifierKeys.Shift); e.Handled = true; break;
            case Key.F2 when sel.Count == 1:
                var name = Prompt.Ask("이름 바꾸기", "새 이름:", sel[0].Name);
                if (name != null) FileOps.Rename(sel[0].FullPath, name);
                e.Handled = true; break;
            case Key.M when mods == ModifierKeys.Control: _shell.Memos.OpenForSelected(List); e.Handled = true; break;
            case Key.C when mods == ModifierKeys.Control: FileOps.ToClipboard(sel.Select(s => s.FullPath).ToList(), false); e.Handled = true; break;
            case Key.X when mods == ModifierKeys.Control: FileOps.ToClipboard(sel.Select(s => s.FullPath).ToList(), true); e.Handled = true; break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift): FileOps.CopyPaths(sel.Select(s => s.FullPath)); e.Handled = true; break;
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        var sel = ListHelpers.Selected(List);
        if (sel.Count == 0) { e.Handled = true; return; }
        var first = sel[0];
        menu.Items.Add(ListHelpers.Item("열기", () => OpenSelected(false), "Enter"));
        menu.Items.Add(ListHelpers.Item("들어있는 폴더를 새 탭에서 열기", () => OpenSelected(true), "Ctrl+Enter"));
        menu.Items.Add(ListHelpers.Item("탐색기에서 보기", () => FileOps.ShowInExplorer(first.FullPath)));
        if (first.IsDir) menu.Items.Add(ListHelpers.Item("즐겨찾기에 추가", () => _shell.AddFavorite(first.FullPath)));
        menu.Items.Add(new Separator());
        menu.Items.Add(ListHelpers.Item("잘라내기", () => FileOps.ToClipboard(sel.Select(s => s.FullPath).ToList(), true), "Ctrl+X"));
        menu.Items.Add(ListHelpers.Item("복사", () => FileOps.ToClipboard(sel.Select(s => s.FullPath).ToList(), false), "Ctrl+C"));
        menu.Items.Add(ListHelpers.Item("경로 복사", () => FileOps.CopyPaths(sel.Select(s => s.FullPath)), "Ctrl+Shift+C"));
        menu.Items.Add(new Separator());
        menu.Items.Add(ListHelpers.Item(first.HasMemo ? "메모 보기/고치기" : "메모 쓰기", () => _shell.Memos.OpenForSelected(List), "Ctrl+M", sel.Count == 1));
        menu.Items.Add(ListHelpers.Item("삭제 (휴지통)", () => FileOps.Delete(sel.Select(s => s.FullPath).ToList(), false), "Delete"));
        menu.Items.Add(ListHelpers.Item("속성", () => FileOps.Properties(first.FullPath)));
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader h || h.Tag is not string key) return;
        _sortDesc = key == _sortKey && !_sortDesc;
        _sortKey = key;
        var items = _items;
        string k = _sortKey; bool d = _sortDesc;
        Status.Text = "정렬 중…";
        Task.Run(() => ListHelpers.Sort(items, k, d, foldersFirst: false)).ContinueWith(t =>
        {
            if (!ReferenceEquals(items, _items)) return;
            _items = t.Result;
            List.ItemsSource = _items;
            ListHelpers.MarkSortHeader(Grid, _sortKey, _sortDesc);
            UpdateStatus();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
