using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>One folder tab: a file list with history, breadcrumb, filter and live refresh.</summary>
public partial class FolderView : UserControl
{
    public const string ThisPC = "내 PC";

    private readonly IShell _shell;
    private readonly Stack<string> _back = new(), _fwd = new();
    private readonly DispatcherTimer _refreshTimer;
    private List<FileItem> _all = new();
    private string _sortKey = "Name";
    private bool _sortDesc;
    private FileSystemWatcher? _watcher;
    private int _loadVersion;

    public string CurrentPath { get; private set; } = "";

    public string Title => CurrentPath == ThisPC ? ThisPC
        : Path.GetFileName(CurrentPath.TrimEnd('\\')) is { Length: > 0 } n ? n : CurrentPath;

    public ImageSource? Icon => CurrentPath == ThisPC ? ShellIcons.Get("C:\\", true) : ShellIcons.Get(CurrentPath, true);

    public FolderView(IShell shell, string path, string? selectName = null)
    {
        _shell = shell;
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
        ListHelpers.EnableDragOut(List);
        shell.Memos.Attach(List);
        PreviewKeyDown += View_PreviewKeyDown;
        ApplyViewMode();
        Navigate(path, record: false, selectName: selectName);
    }

    // ------------------------------------------------------------ list / tile

    /// <summary>Switches between the detail list (virtualized GridView) and tiles (big icons / thumbnails, wrapping).</summary>
    public void ApplyViewMode()
    {
        bool tile = _shell.TileView;
        if (tile)
        {
            List.View = null;
            List.ItemTemplate = (DataTemplate)FindResource("TileItem");
            List.ItemContainerStyle = (Style)FindResource("TileContainer");
            List.ItemsPanel = (ItemsPanelTemplate)FindResource("TilePanel");
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
        }
        else
        {
            List.ItemTemplate = null;
            List.ItemContainerStyle = null;
            List.ItemsPanel = (ItemsPanelTemplate)FindResource("ListPanel");
            List.View = Grid;
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto);
        }
        ListModeBtn.Background = tile ? Brushes.Transparent : (Brush)FindResource("AccentSoft");
        TileModeBtn.Background = tile ? (Brush)FindResource("AccentSoft") : Brushes.Transparent;
    }

    private void ListMode_Click(object sender, RoutedEventArgs e) => _shell.TileView = false;
    private void TileMode_Click(object sender, RoutedEventArgs e) => _shell.TileView = true;

    private void SortBy(string key)
    {
        _sortDesc = key == _sortKey ? !_sortDesc : key is "Modified" or "Size";
        _sortKey = key;
        Apply();
    }

    // ------------------------------------------------------------ navigation

    public void Navigate(string path, bool record = true, string? selectName = null)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.Length == 0 || path.Equals(ThisPC, StringComparison.OrdinalIgnoreCase) || path.Equals("this pc", StringComparison.OrdinalIgnoreCase))
            path = ThisPC;
        else
        {
            try { path = Path.GetFullPath(path); } catch { FileOps.Error(new IOException($"잘못된 경로: {path}")); return; }
            if (File.Exists(path)) { FileOps.Open(path); return; }
            if (!Directory.Exists(path)) { FileOps.Error(new DirectoryNotFoundException($"폴더를 찾을 수 없습니다:\n{path}")); return; }
            if (path.Length > 3) path = path.TrimEnd('\\');
        }

        if (record && CurrentPath.Length > 0 && !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _back.Push(CurrentPath);
            _fwd.Clear();
        }
        // when going up, keep the folder we came from selected
        if (selectName == null && CurrentPath.Length > 0 && CurrentPath != ThisPC &&
            string.Equals(Path.GetDirectoryName(CurrentPath), path, StringComparison.OrdinalIgnoreCase))
            selectName = Path.GetFileName(CurrentPath);
        if (selectName == null && path == ThisPC && CurrentPath.Length is > 0 and <= 3) selectName = CurrentPath;

        CurrentPath = path;
        Filter.Text = "";
        Load(selectName, keepScroll: false);
        Watch();
        UpdateChrome();
        _shell.ViewChanged(this);
    }

    public void GoBack() { if (_back.Count > 0) { _fwd.Push(CurrentPath); Navigate(_back.Pop(), record: false); } }
    public void GoForward() { if (_fwd.Count > 0) { _back.Push(CurrentPath); Navigate(_fwd.Pop(), record: false); } }

    public void GoUp()
    {
        if (CurrentPath == ThisPC) return;
        var parent = Path.GetDirectoryName(CurrentPath);
        Navigate(parent ?? ThisPC);
    }

    public void Refresh() => Load(null, keepScroll: true);

    private void Load(string? selectName, bool keepScroll)
    {
        int version = ++_loadVersion;
        if (!keepScroll) ShellThumbnails.NewGeneration(); // new folder: drop thumbnails still queued for the old one
        var path = CurrentPath;
        bool showHidden = _shell.ShowHidden;
        var previous = keepScroll ? ListHelpers.Selected(List).Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        Task.Run(() => Read(path, showHidden)).ContinueWith(t =>
        {
            if (version != _loadVersion) return;
            if (t.Exception != null) { Status.Text = "읽기 실패: " + t.Exception.InnerException?.Message; return; }
            _all = t.Result;
            Apply();
            if (previous != null && previous.Count > 0)
            {
                foreach (var it in List.Items.Cast<FileItem>().Where(i => previous.Contains(i.FullPath))) List.SelectedItems.Add(it);
            }
            else if (selectName != null)
            {
                var it = List.Items.Cast<FileItem>().FirstOrDefault(i => string.Equals(i.Name, selectName, StringComparison.OrdinalIgnoreCase) || string.Equals(i.FullPath, selectName, StringComparison.OrdinalIgnoreCase));
                if (it != null) { List.SelectedItem = it; List.ScrollIntoView(it); FocusItem(it); }
            }
            else if (!keepScroll && List.Items.Count > 0) List.ScrollIntoView(List.Items[0]);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static List<FileItem> Read(string path, bool showHidden)
    {
        if (path == ThisPC)
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d =>
            {
                string label;
                try { label = string.IsNullOrEmpty(d.VolumeLabel) ? "로컬 디스크" : d.VolumeLabel; } catch { label = "드라이브"; }
                return new FileItem(d.RootDirectory.FullName, d.Name, true) { DisplayName = $"{label} ({d.Name.TrimEnd('\\')})" };
            }).ToList();
        }
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };
        return new DirectoryInfo(path).EnumerateFileSystemInfos("*", options).Select(fi => new FileItem(fi)).ToList();
    }

    private void Apply()
    {
        IEnumerable<FileItem> items = _all;
        var q = SearchQuery.Parse(Filter.Text);
        if (!q.IsEmpty) items = items.Where(i => q.Matches(i.Name, i.IsDir, () => i.FullPath));
        List.ItemsSource = ListHelpers.Sort(items, _sortKey, _sortDesc, foldersFirst: _sortKey != "Size" || true);
        ListHelpers.MarkSortHeader(Grid, _sortKey, _sortDesc);
        UpdateStatus();
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (CurrentPath == ThisPC) return;
        try
        {
            _watcher = new FileSystemWatcher(CurrentPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
    }

    public void Close() { _watcher?.Dispose(); _refreshTimer.Stop(); }

    // ------------------------------------------------------------ chrome

    private void UpdateChrome()
    {
        BackBtn.IsEnabled = _back.Count > 0;
        FwdBtn.IsEnabled = _fwd.Count > 0;
        UpBtn.IsEnabled = CurrentPath != ThisPC;
        Address.Text = CurrentPath;
        BuildCrumbs();
    }

    private void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var parts = new List<(string label, string path)> { (ThisPC, ThisPC) };
        if (CurrentPath != ThisPC)
        {
            var root = Path.GetPathRoot(CurrentPath)!;
            parts.Add((root.TrimEnd('\\'), root));
            var acc = root;
            foreach (var seg in CurrentPath[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = Path.Combine(acc, seg);
                parts.Add((seg, acc));
            }
        }
        for (int i = 0; i < parts.Count; i++)
        {
            var (label, target) = parts[i];
            var b = new Button
            {
                Content = label,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = target + "\n(가운데 클릭: 새 탭)",
                FontWeight = i == parts.Count - 1 ? FontWeights.SemiBold : FontWeights.Normal,
            };
            b.Click += (_, _) => Navigate(target);
            b.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { _shell.OpenFolderTab(target, false); e.Handled = true; } };
            Crumbs.Children.Add(b);
            if (i < parts.Count - 1)
                Crumbs.Children.Add(new TextBlock { Text = "›", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 1) });
        }
        CrumbScroll.ScrollToRightEnd();
    }

    private void UpdateStatus()
    {
        int shown = List.Items.Count;
        var sel = ListHelpers.Selected(List);
        var text = shown == _all.Count ? $"{shown:N0}개 항목" : $"{shown:N0} / {_all.Count:N0}개 항목 (거르기)";
        if (sel.Count > 0)
        {
            long size = sel.Where(s => !s.IsDir).Sum(s => Math.Max(0, s.Size));
            text += $"   ·   {sel.Count:N0}개 선택" + (size > 0 ? $" ({Format.Size(size)})" : "");
        }
        Status.Text = text;
    }

    public void EditAddress()
    {
        CrumbHost.Visibility = Visibility.Collapsed;
        Address.Text = CurrentPath;
        Address.Focus();
        Address.SelectAll();
    }

    public void FocusFilter() { Filter.Focus(); Filter.SelectAll(); }
    public void FocusList()
    {
        if (List.SelectedItem is FileItem it) FocusItem(it);
        else if (List.Items.Count > 0) { List.SelectedIndex = 0; FocusItem((FileItem)List.Items[0]); }
        else List.Focus();
    }

    private void FocusItem(FileItem it)
    {
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromItem(it) is ListViewItem c) c.Focus(); else List.Focus();
    }

    // ------------------------------------------------------------ actions

    private void OpenSelected(bool newTab)
    {
        var sel = ListHelpers.Selected(List);
        if (sel.Count == 0) return;
        var dirs = sel.Where(s => s.IsDir).ToList();
        foreach (var f in sel.Where(s => !s.IsDir)) FileOps.Open(f.FullPath);
        if (dirs.Count == 1 && !newTab) Navigate(dirs[0].FullPath);
        else foreach (var d in dirs) _shell.OpenFolderTab(d.FullPath, activate: dirs.Count == 1);
    }

    private void Rename()
    {
        if (List.SelectedItem is not FileItem it || CurrentPath == ThisPC) return;
        var name = Prompt.Ask("이름 바꾸기", "새 이름:", it.Name);
        if (name != null && FileOps.Rename(it.FullPath, name)) Load(name, keepScroll: false);
    }

    private void DeleteSelected(bool permanent)
    {
        var paths = ListHelpers.Selected(List).Select(s => s.FullPath).Where(p => p.Length > 3).ToList();
        FileOps.Delete(paths, permanent);
        Refresh();
    }

    private void NewFolder()
    {
        if (CurrentPath == ThisPC) return;
        var path = FileOps.NewFolder(CurrentPath);
        if (path == null) return;
        var name = Prompt.Ask("새 폴더", "폴더 이름:", Path.GetFileName(path));
        if (name != null && name != Path.GetFileName(path) && FileOps.Rename(path, name)) path = Path.Combine(CurrentPath, name);
        Load(Path.GetFileName(path), keepScroll: false);
    }

    private void Paste()
    {
        if (CurrentPath == ThisPC) return;
        FileOps.Paste(CurrentPath);
        Refresh();
    }

    // ------------------------------------------------------------ events

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (mods == ModifierKeys.Alt && key == Key.Left) { GoBack(); e.Handled = true; }
        else if (mods == ModifierKeys.Alt && key == Key.Right) { GoForward(); e.Handled = true; }
        else if (mods == ModifierKeys.Alt && key == Key.Up) { GoUp(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && key == Key.L) { EditAddress(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && key == Key.F) { FocusFilter(); e.Handled = true; }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key is Key.D1 or Key.NumPad1) { _shell.TileView = false; e.Handled = true; }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key is Key.D2 or Key.NumPad2) { _shell.TileView = true; e.Handled = true; }
        else if (key == Key.F5) { Refresh(); e.Handled = true; }
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter: OpenSelected(newTab: mods == ModifierKeys.Control); e.Handled = true; break;
            case Key.Back: GoUp(); e.Handled = true; break;
            case Key.Delete: DeleteSelected(mods == ModifierKeys.Shift); e.Handled = true; break;
            case Key.F2: Rename(); e.Handled = true; break;
            case Key.M when mods == ModifierKeys.Control: _shell.Memos.OpenForSelected(List); e.Handled = true; break;
            case Key.C when mods == ModifierKeys.Control: FileOps.ToClipboard(ListHelpers.Selected(List).Select(s => s.FullPath).ToList(), false); e.Handled = true; break;
            case Key.X when mods == ModifierKeys.Control: FileOps.ToClipboard(ListHelpers.Selected(List).Select(s => s.FullPath).ToList(), true); e.Handled = true; break;
            case Key.V when mods == ModifierKeys.Control: Paste(); e.Handled = true; break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift): FileOps.CopyPaths(ListHelpers.Selected(List).Select(s => s.FullPath)); e.Handled = true; break;
            case Key.N when mods == (ModifierKeys.Control | ModifierKeys.Shift): NewFolder(); e.Handled = true; break;
        }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ListHelpers.ItemAt(List, e.GetPosition(List)) == null) { if (CurrentPath != ThisPC) GoUp(); return; } // QTTabBar: double-click empty space = up
        OpenSelected(newTab: Keyboard.Modifiers == ModifierKeys.Control);
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            var it = ListHelpers.ItemAt(List, e.GetPosition(List));
            if (it != null && it.IsDir) { _shell.OpenFolderTab(it.FullPath, activate: false); e.Handled = true; }
        }
    }

    private void List_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // click on empty space clears selection
        if (e.ChangedButton == MouseButton.Left && ListHelpers.ItemAt(List, e.GetPosition(List)) == null) List.SelectedItems.Clear();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        var sel = ListHelpers.Selected(List);
        bool pc = CurrentPath == ThisPC;
        if (sel.Count > 0)
        {
            var first = sel[0];
            menu.Items.Add(ListHelpers.Item("열기", () => OpenSelected(false), "Enter"));
            if (sel.Any(s => s.IsDir))
            {
                menu.Items.Add(ListHelpers.Item("새 탭에서 열기", () => OpenSelected(true), "Ctrl+Enter / 가운데 클릭"));
                menu.Items.Add(ListHelpers.Item("즐겨찾기에 추가", () => { foreach (var d in sel.Where(s => s.IsDir)) _shell.AddFavorite(d.FullPath); }));
            }
            menu.Items.Add(ListHelpers.Item("탐색기에서 보기", () => FileOps.ShowInExplorer(first.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(ListHelpers.Item("잘라내기", () => FileOps.ToClipboard(sel.Select(s => s.FullPath).ToList(), true), "Ctrl+X", !pc));
            menu.Items.Add(ListHelpers.Item("복사", () => FileOps.ToClipboard(sel.Select(s => s.FullPath).ToList(), false), "Ctrl+C", !pc));
            menu.Items.Add(ListHelpers.Item("경로 복사", () => FileOps.CopyPaths(sel.Select(s => s.FullPath)), "Ctrl+Shift+C"));
            menu.Items.Add(new Separator());
            menu.Items.Add(ListHelpers.Item(first.HasMemo ? "메모 보기/고치기" : "메모 쓰기", () => _shell.Memos.OpenForSelected(List), "Ctrl+M", sel.Count == 1));
            menu.Items.Add(ListHelpers.Item("이름 바꾸기", Rename, "F2", !pc && sel.Count == 1));
            menu.Items.Add(ListHelpers.Item("삭제 (휴지통)", () => DeleteSelected(false), "Delete", !pc));
            menu.Items.Add(new Separator());
            menu.Items.Add(ListHelpers.Item("속성", () => FileOps.Properties(first.FullPath)));
        }
        else
        {
            menu.Items.Add(ListHelpers.Item("새로 고침", Refresh, "F5"));
            var view = new MenuItem { Header = "보기" };
            view.Items.Add(new MenuItem { Header = "목록", IsCheckable = true, IsChecked = !_shell.TileView, InputGestureText = "Ctrl+Shift+1" });
            view.Items.Add(new MenuItem { Header = "타일", IsCheckable = true, IsChecked = _shell.TileView, InputGestureText = "Ctrl+Shift+2" });
            ((MenuItem)view.Items[0]).Click += (_, _) => _shell.TileView = false;
            ((MenuItem)view.Items[1]).Click += (_, _) => _shell.TileView = true;
            menu.Items.Add(view);
            var sort = new MenuItem { Header = "정렬 기준" };
            foreach (var (label, key) in new[] { ("이름", "Name"), ("수정한 날짜", "Modified"), ("유형", "Type"), ("크기", "Size") })
            {
                var mi = new MenuItem { Header = label + (key == _sortKey ? (_sortDesc ? "  ▼" : "  ▲") : "") };
                mi.Click += (_, _) => SortBy(key);
                sort.Items.Add(mi);
            }
            menu.Items.Add(sort);
            menu.Items.Add(new Separator());
            menu.Items.Add(ListHelpers.Item("새 폴더", NewFolder, "Ctrl+Shift+N", !pc));
            menu.Items.Add(ListHelpers.Item("붙여넣기", Paste, "Ctrl+V", !pc && Clipboard.ContainsFileDropList()));
            menu.Items.Add(new Separator());
            menu.Items.Add(ListHelpers.Item("터미널 열기", () => FileOps.OpenTerminal(CurrentPath), null, !pc));
            menu.Items.Add(ListHelpers.Item("탐색기에서 열기", () => FileOps.OpenFolderInExplorer(pc ? "shell:MyComputerFolder" : CurrentPath)));
            menu.Items.Add(ListHelpers.Item("현재 폴더를 즐겨찾기에 추가", () => _shell.AddFavorite(CurrentPath), "Ctrl+D", !pc));
            menu.Items.Add(ListHelpers.Item("속성", () => FileOps.Properties(CurrentPath), null, !pc));
        }
    }

    private string DropTarget(DragEventArgs e)
    {
        var it = ListHelpers.ItemAt(List, e.GetPosition(List));
        if (it != null && it.IsDir) return it.FullPath;
        return CurrentPath == ThisPC ? "" : CurrentPath;
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ListHelpers.DropEffect(e, DropTarget(e));
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        var effect = ListHelpers.DropEffect(e, target);
        if (effect == DragDropEffects.None || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        // dropping items onto themselves is a no-op
        var sources = files.Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase)).ToList();
        FileOps.Transfer(sources, target, effect == DragDropEffects.Move);
        Refresh();
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader h || h.Tag is not string key) return;
        _sortDesc = key == _sortKey && !_sortDesc;
        _sortKey = key;
        Apply();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void Fwd_Click(object sender, RoutedEventArgs e) => GoForward();
    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Crumbs_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
        EditAddress();
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var c = d; c != null; c = VisualTreeHelper.GetParent(c)) if (c is T t) return t;
        return null;
    }

    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { var p = Address.Text; CrumbHost.Visibility = Visibility.Visible; Navigate(p); FocusList(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CrumbHost.Visibility = Visibility.Visible; FocusList(); e.Handled = true; }
    }

    private void Address_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Address.Text = CurrentPath;
        CrumbHost.Visibility = Visibility.Visible;
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => Apply();

    private void Filter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Filter.Text = ""; FocusList(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Enter) { FocusList(); e.Handled = true; }
    }
}
