using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Tabseeker;

public sealed class TabInfo : INotifyPropertyChanged
{
    private string _title = "";
    private ImageSource? _icon;
    private bool _isActive;
    private string _toolTip = "";

    public TabInfo(FrameworkElement view) => View = view;
    public FrameworkElement View { get; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string ToolTip { get => _toolTip; set => Set(ref _toolTip, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class MainWindow : Window, IShell
{
    private const int HotkeyId = 0x5A11;
    private const string SearchPrefix = "search:";

    private readonly ObservableCollection<TabInfo> _tabs = new();
    private readonly Stack<string> _closed = new();
    private readonly AppSettings _settings;
    private readonly IndexService _index = new();
    private readonly DispatcherTimer _searchDelay;
    private TabInfo? _active;
    private bool _suppressSearchBox;
    private Point? _tabDragStart;

    public bool ShowHidden => _settings.ShowHidden;
    public MemoController Memos { get; }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        TabsHost.ItemsSource = _tabs;
        HiddenItem.IsChecked = _settings.ShowHidden;
        MemoEmptyItem.IsChecked = _settings.MemoHoverForEmpty;

        var memoStore = Core.MemoStore.OpenDefault(AppSettings.Dir);
        Memos = new MemoController(this, memoStore) { HoverForEmpty = _settings.MemoHoverForEmpty };
        if (memoStore.LoadResult is Core.MemoLoadResult.RecoveredFromBackup or Core.MemoLoadResult.Corrupt)
            Dispatcher.BeginInvoke(() => MessageBox.Show(this,
                memoStore.LoadResult == Core.MemoLoadResult.RecoveredFromBackup
                    ? "메모 데이터베이스가 Tabseeker 밖에서 바뀌었거나 손상되어, 바로 전 백업으로 되돌렸습니다."
                    : "메모 데이터베이스를 읽을 수 없어 빈 상태로 시작합니다. 손상된 파일은 따로 보관해 두었습니다.",
                "Tabseeker 메모", MessageBoxButton.OK, MessageBoxImage.Warning), DispatcherPriority.Loaded);
        NavColumn.Width = new GridLength(Math.Clamp(_settings.NavWidth, 120, 480));
        RestorePlacement();

        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); PushSearch(); };

        _index.StatusChanged += () => Dispatcher.BeginInvoke(() =>
        {
            IndexStatus.Text = "색인: " + _index.Status;
            foreach (var s in _tabs.Select(t => t.View).OfType<SearchView>()) s.IndexStatusChanged();
        });
        _index.IndexChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (_active?.View is SearchView sv) sv.Rerun();
        }, DispatcherPriority.Background);
        _index.Start();

        BuildNav();
        RestoreTabs(args);
    }

    // ------------------------------------------------------------ tabs

    private void RestoreTabs(string[] args)
    {
        foreach (var entry in _settings.Tabs)
        {
            if (entry.StartsWith(SearchPrefix)) AddSearchTab(entry[SearchPrefix.Length..], activate: false);
            else if (entry == FolderView.ThisPC || Directory.Exists(entry)) AddFolderTab(entry, activate: false);
        }
        foreach (var a in args.Where(a => Directory.Exists(a) || File.Exists(a)))
        {
            if (File.Exists(a)) AddFolderTab(Path.GetDirectoryName(Path.GetFullPath(a))!, true, Path.GetFileName(a));
            else AddFolderTab(a, true);
        }
        if (_tabs.Count == 0) AddFolderTab(FolderView.ThisPC, true);
        if (_active == null) Activate(_tabs[Math.Clamp(_settings.ActiveTab, 0, _tabs.Count - 1)]);
    }

    private TabInfo AddTab(FrameworkElement view, bool activate, int? at = null)
    {
        var tab = new TabInfo(view);
        int index = at ?? (_active != null ? _tabs.IndexOf(_active) + 1 : _tabs.Count);
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        UpdateHeader(tab);
        if (activate || _active == null) Activate(tab);
        return tab;
    }

    private TabInfo AddFolderTab(string path, bool activate, string? selectName = null)
    {
        return AddTab(new FolderView(this, path, selectName), activate);
    }

    private TabInfo AddSearchTab(string query, bool activate)
    {
        var view = new SearchView(this, _index);
        view.SetQuery(query); // before Activate, which copies the tab's query into the search box
        return AddTab(view, activate);
    }

    private void Activate(TabInfo tab)
    {
        if (_active != null) _active.IsActive = false;
        _active = tab;
        tab.IsActive = true;
        Memos.Hide();
        Host.Content = tab.View;
        Title = $"{tab.Title} — Tabseeker";
        if (tab.View is SearchView sv)
        {
            SetSearchBox(sv.Query);
            sv.Rerun();
        }
        // bring the header into view
        Dispatcher.BeginInvoke(() =>
        {
            if (TabsHost.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement fe) fe.BringIntoView();
            if (tab.View is FolderView fv && !SearchBox.IsKeyboardFocusWithin) fv.FocusList();
        }, DispatcherPriority.Loaded);
    }

    private void CloseTab(TabInfo tab)
    {
        int i = _tabs.IndexOf(tab);
        if (i < 0) return;
        if (tab.View is FolderView fv) { _closed.Push(fv.CurrentPath); fv.Close(); }
        _tabs.RemoveAt(i);
        if (_tabs.Count == 0) { AddFolderTab(FolderView.ThisPC, true); return; }
        if (tab == _active) { _active = null; Activate(_tabs[Math.Min(i, _tabs.Count - 1)]); }
    }

    private void UpdateHeader(TabInfo tab)
    {
        switch (tab.View)
        {
            case FolderView fv:
                tab.Title = fv.Title;
                tab.Icon = fv.Icon;
                tab.ToolTip = fv.CurrentPath;
                break;
            case SearchView sv:
                tab.Title = "🔍 " + sv.Title;
                tab.Icon = null;
                tab.ToolTip = sv.Query.Length == 0 ? "검색" : "검색: " + sv.Query;
                break;
        }
        if (tab == _active) Title = $"{tab.Title} — Tabseeker";
    }

    // IShell
    public void ViewChanged(FrameworkElement view)
    {
        var tab = _tabs.FirstOrDefault(t => t.View == view);
        if (tab != null) UpdateHeader(tab);
    }

    public void OpenFolderTab(string path, bool activate = true, string? selectName = null) => AddFolderTab(path, activate, selectName);

    public void NavigateActive(string path)
    {
        if (_active?.View is FolderView fv) fv.Navigate(path);
        else AddFolderTab(path, true);
    }

    public void AddFavorite(string path)
    {
        if (path == FolderView.ThisPC || _settings.Favorites.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _settings.Favorites.Add(path);
        _settings.Save();
        BuildNav();
    }

    // ------------------------------------------------------------ search box

    private void SetSearchBox(string text)
    {
        _suppressSearchBox = true;
        SearchBox.Text = text;
        SearchBox.CaretIndex = text.Length;
        _suppressSearchBox = false;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchBox) return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void PushSearch()
    {
        var text = SearchBox.Text;
        SearchView? sv = _active?.View as SearchView;
        if (sv == null)
        {
            if (text.Trim().Length == 0) return;
            var tab = _tabs.FirstOrDefault(t => t.View is SearchView);
            if (tab != null) { Activate(tab); sv = (SearchView)tab.View; SetSearchBox(text); }
            else { AddSearchTab(text, activate: true); return; }
        }
        sv.SetQuery(text);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            else if (_tabs.FirstOrDefault(t => t.View is FolderView) is { } ft) Activate(ft);
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            _searchDelay.Stop();
            PushSearch();
            if (_active?.View is SearchView sv) Dispatcher.BeginInvoke(sv.FocusList, DispatcherPriority.Background);
            e.Handled = true;
        }
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // ------------------------------------------------------------ navigation pane

    private void BuildNav()
    {
        Nav.Children.Clear();
        AddSection("즐겨찾기");
        if (_settings.Favorites.Count == 0)
            Nav.Children.Add(new TextBlock { Text = "폴더를 여기로 끌어오거나\nCtrl+D 로 추가", Foreground = Brushes.Gray, FontSize = 11.5, Margin = new Thickness(10, 2, 0, 6) });
        foreach (var f in _settings.Favorites) AddEntry(Path.GetFileName(f.TrimEnd('\\')) is { Length: > 0 } n ? n : f, f, favorite: true);

        AddSection("빠른 접근");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddEntry("바탕 화면", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddEntry("문서", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddEntry("다운로드", Path.Combine(home, "Downloads"));
        AddEntry("사진", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddEntry("내 폴더", home);

        AddSection("내 PC");
        AddEntry(FolderView.ThisPC, FolderView.ThisPC);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            string label;
            try { label = string.IsNullOrEmpty(d.VolumeLabel) ? "로컬 디스크" : d.VolumeLabel; } catch { label = "드라이브"; }
            AddEntry($"{label} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName);
        }
    }

    private void AddSection(string title) =>
        Nav.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(6, 10, 0, 3) });

    private void AddEntry(string label, string path, bool favorite = false)
    {
        if (path != FolderView.ThisPC && !Directory.Exists(path) && !favorite) return;
        var row = new Border { Padding = new Thickness(8, 4, 6, 4), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path + "\n(가운데 클릭: 새 탭)" };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Image { Source = path == FolderView.ThisPC ? ShellIcons.Get("C:\\", true) : ShellIcons.Get(path, true), Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) });
        sp.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = Directory.Exists(path) || path == FolderView.ThisPC ? 1 : 0.45 });
        row.Child = sp;
        row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) NavigateActive(path);
            else if (e.ChangedButton == MouseButton.Middle) AddFolderTab(path, false);
        };
        row.AllowDrop = path != FolderView.ThisPC;
        row.DragOver += (_, e) => { e.Effects = ListHelpers.DropEffect(e, path); e.Handled = true; };
        row.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                FileOps.Transfer(files, path, ListHelpers.DropEffect(e, path) == DragDropEffects.Move);
            e.Handled = true;
        };
        var menu = new ContextMenu();
        menu.Items.Add(ListHelpers.Item("새 탭에서 열기", () => AddFolderTab(path, true)));
        menu.Items.Add(ListHelpers.Item("탐색기에서 열기", () => FileOps.OpenFolderInExplorer(path == FolderView.ThisPC ? "shell:MyComputerFolder" : path)));
        if (favorite)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ListHelpers.Item("위로 이동", () => MoveFavorite(path, -1)));
            menu.Items.Add(ListHelpers.Item("아래로 이동", () => MoveFavorite(path, +1)));
            menu.Items.Add(ListHelpers.Item("즐겨찾기에서 제거", () => { _settings.Favorites.Remove(path); _settings.Save(); BuildNav(); }));
        }
        else if (path != FolderView.ThisPC) menu.Items.Add(ListHelpers.Item("즐겨찾기에 추가", () => AddFavorite(path)));
        row.ContextMenu = menu;
        Nav.Children.Add(row);
    }

    private void MoveFavorite(string path, int delta)
    {
        int i = _settings.Favorites.IndexOf(path);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _settings.Favorites.Count) return;
        (_settings.Favorites[i], _settings.Favorites[j]) = (_settings.Favorites[j], _settings.Favorites[i]);
        _settings.Save();
        BuildNav();
    }

    private void Nav_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Any(Directory.Exists) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Nav_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files.Where(Directory.Exists)) AddFavorite(f);
    }

    // ------------------------------------------------------------ tab header events

    private static TabInfo? TabOf(object sender) => (sender as FrameworkElement)?.Tag as TabInfo;

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TabOf(sender) is { } tab)
        {
            if (e.ClickCount == 2 && tab.View is FolderView fv) { AddFolderTab(fv.CurrentPath, true); e.Handled = true; return; } // double-click = duplicate
            Activate(tab);
            _tabDragStart = e.GetPosition(this);
        }
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragStart == null || e.LeftButton != MouseButtonState.Pressed || TabOf(sender) is not { } tab) return;
        var d = e.GetPosition(this) - _tabDragStart.Value;
        if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;
        _tabDragStart = null;
        var data = new DataObject("Tabseeker.Tab", tab);
        if (tab.View is FolderView fv && fv.CurrentPath != FolderView.ThisPC) data.SetData(DataFormats.FileDrop, new[] { fv.CurrentPath });
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
    }

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = null;
        if (e.ChangedButton == MouseButton.Middle && TabOf(sender) is { } tab) { CloseTab(tab); e.Handled = true; }
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        var target = TabOf(sender);
        if (target == null) return;
        if (e.Data.GetData("Tabseeker.Tab") is TabInfo dragged)
        {
            e.Effects = DragDropEffects.Move;
            int from = _tabs.IndexOf(dragged), to = _tabs.IndexOf(target);
            if (from >= 0 && to >= 0 && from != to) _tabs.Move(from, to);
        }
        else if (target.View is FolderView fv)
        {
            e.Effects = ListHelpers.DropEffect(e, fv.CurrentPath == FolderView.ThisPC ? "" : fv.CurrentPath);
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        // files dropped on a folder tab are copied/moved into that folder (QTTabBar behaviour)
        if (e.Data.GetDataPresent("Tabseeker.Tab")) { e.Handled = true; return; }
        if (TabOf(sender)?.View is FolderView fv && fv.CurrentPath != FolderView.ThisPC && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            FileOps.Transfer(files, fv.CurrentPath, ListHelpers.DropEffect(e, fv.CurrentPath) == DragDropEffects.Move);
            fv.Refresh();
        }
        e.Handled = true;
    }

    private void Tab_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border b || TabOf(sender) is not { } tab) return;
        var menu = b.ContextMenu!;
        menu.Items.Clear();
        var fv = tab.View as FolderView;
        menu.Items.Add(ListHelpers.Item("새 탭", () => NewTab(), "Ctrl+T"));
        menu.Items.Add(ListHelpers.Item("탭 복제", () => AddFolderTab(fv!.CurrentPath, true), "더블클릭", fv != null));
        menu.Items.Add(new Separator());
        menu.Items.Add(ListHelpers.Item("탭 닫기", () => CloseTab(tab), "Ctrl+W"));
        menu.Items.Add(ListHelpers.Item("다른 탭 모두 닫기", () => { foreach (var t in _tabs.Where(t => t != tab).ToList()) CloseTab(t); }));
        menu.Items.Add(ListHelpers.Item("오른쪽 탭 닫기", () => { foreach (var t in _tabs.Skip(_tabs.IndexOf(tab) + 1).ToList()) CloseTab(t); }));
        menu.Items.Add(ListHelpers.Item("닫은 탭 다시 열기", ReopenClosed, "Ctrl+Shift+T", _closed.Count > 0));
        if (fv != null && fv.CurrentPath != FolderView.ThisPC)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ListHelpers.Item("경로 복사", () => FileOps.CopyPaths(new[] { fv.CurrentPath })));
            menu.Items.Add(ListHelpers.Item("탐색기에서 열기", () => FileOps.OpenFolderInExplorer(fv.CurrentPath)));
            menu.Items.Add(ListHelpers.Item("즐겨찾기에 추가", () => AddFavorite(fv.CurrentPath), "Ctrl+D"));
        }
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) CloseTab(tab);
        e.Handled = true;
    }

    private void Strip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && e.OriginalSource == Strip) NewTab();
    }

    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();

    private void NewTab()
    {
        var path = _active?.View is FolderView fv ? fv.CurrentPath : FolderView.ThisPC;
        AddFolderTab(path, true);
    }

    private void ReopenClosed()
    {
        while (_closed.Count > 0)
        {
            var p = _closed.Pop();
            if (p == FolderView.ThisPC || Directory.Exists(p)) { AddFolderTab(p, true); return; }
        }
    }

    // ------------------------------------------------------------ keyboard

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = mods == ModifierKeys.Control, ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);

        if (ctrl && key == Key.T) NewTab();
        else if (ctrl && key == Key.W) { if (_active != null) CloseTab(_active); }
        else if (ctrlShift && key == Key.T) ReopenClosed();
        else if ((ctrl || ctrlShift) && key == Key.Tab) Cycle(ctrlShift ? -1 : 1);
        else if (ctrl && key == Key.PageDown) Cycle(1);
        else if (ctrl && key == Key.PageUp) Cycle(-1);
        else if (ctrl && key >= Key.D1 && key <= Key.D9)
        {
            int i = key == Key.D9 ? _tabs.Count - 1 : key - Key.D1;
            if (i < _tabs.Count) Activate(_tabs[i]);
        }
        else if (ctrl && (key == Key.E || key == Key.K)) FocusSearch();
        else if (ctrlShift && key == Key.F) { AddSearchTab("", true); FocusSearch(); }
        else if (ctrl && key == Key.D) { if (_active?.View is FolderView fv) AddFavorite(fv.CurrentPath); }
        else return;
        e.Handled = true;
    }

    private void Cycle(int delta)
    {
        if (_active == null || _tabs.Count < 2) return;
        int i = (_tabs.IndexOf(_active) + delta + _tabs.Count) % _tabs.Count;
        Activate(_tabs[i]);
    }

    // ------------------------------------------------------------ menu

    private void MenuBtn_Click(object sender, RoutedEventArgs e)
    {
        MenuBtn.ContextMenu!.PlacementTarget = MenuBtn;
        MenuBtn.ContextMenu.IsOpen = true;
    }

    private void Hidden_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowHidden = HiddenItem.IsChecked;
        _settings.Save();
        foreach (var fv in _tabs.Select(t => t.View).OfType<FolderView>()) fv.Refresh();
    }

    private void Reindex_Click(object sender, RoutedEventArgs e) => _index.RequestRebuild();

    private void MemoEmpty_Click(object sender, RoutedEventArgs e)
    {
        _settings.MemoHoverForEmpty = MemoEmptyItem.IsChecked;
        Memos.HoverForEmpty = MemoEmptyItem.IsChecked;
        _settings.Save();
    }

    private void MemoList_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = SearchView.MemoPrefix;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
@"[탭]
Ctrl+T 새 탭 · Ctrl+W 닫기 · Ctrl+Shift+T 닫은 탭 다시 열기
Ctrl+Tab / Ctrl+1~9 탭 이동 · 탭 더블클릭 = 복제 · 탭 가운데 클릭 = 닫기
탭을 끌어서 순서 변경 · 파일을 탭에 끌어 놓으면 그 폴더로 복사/이동
폴더 가운데 클릭 / Ctrl+Enter = 새 탭에서 열기

[폴더]
Alt+←/→ 뒤로/앞으로 · Backspace·Alt+↑ 위로 · 빈 곳 더블클릭 = 위로
Ctrl+L 경로 입력 · Ctrl+F 이 폴더 거르기 · F5 새로 고침 · Ctrl+D 즐겨찾기
F2 이름 바꾸기 · Delete 휴지통 · Ctrl+C/X/V · Ctrl+Shift+C 경로 복사 · Ctrl+Shift+N 새 폴더

[검색]  Ctrl+E (창 안) · Ctrl+Alt+F (어디서나)
단어 여러 개 = AND   a|b = OR   !단어 = 제외   ""띄어 쓴 말""
*.pdf  보고?  = 와일드카드      ext:pdf;hwp  file:  folder:
폴더\이름 = 경로로 찾기      regex:^IMG_\d+
결과 Enter = 열기 · Ctrl+Enter = 들어있는 폴더를 새 탭으로

[메모]
이름 위에 마우스를 잠시 올리면 이름 옆에 메모 말풍선이 뜸 — 바로 입력, 자동 저장
Ctrl+M = 선택한 항목 메모 쓰기 · 오른쪽 아래 모서리를 끌어 크기 조절 · Esc 닫기
메모가 있는 이름 옆에는 노트 표시 · 검색창에 memo: 단어 = 메모 내용으로 찾기",
        "단축키 / 검색 문법", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"Tabseeker {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n탭 탐색기 + 전체 드라이브 즉시 검색\n\n설정/색인 위치: {AppSettings.Dir}",
        "Tabseeker 정보", MessageBoxButton.OK, MessageBoxImage.Information);

    // ------------------------------------------------------------ window lifetime

    private void RestorePlacement()
    {
        Width = Math.Max(640, _settings.Width);
        Height = Math.Max(400, _settings.Height);
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) &&
            _settings.Left > SystemParameters.VirtualScreenLeft - 50 && _settings.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            _settings.Top > SystemParameters.VirtualScreenTop - 50 && _settings.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.Left;
            Top = _settings.Top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _settings.Tabs = _tabs.Select(t => t.View switch
        {
            FolderView fv => fv.CurrentPath,
            SearchView sv => SearchPrefix + sv.Query,
            _ => "",
        }).Where(s => s.Length > 0).ToList();
        _settings.ActiveTab = _active != null ? _tabs.IndexOf(_active) : 0;
        _settings.Maximized = WindowState == WindowState.Maximized;
        var r = RestoreBounds;
        if (!r.IsEmpty) { _settings.Left = r.Left; _settings.Top = r.Top; _settings.Width = r.Width; _settings.Height = r.Height; }
        _settings.NavWidth = NavColumn.ActualWidth;
        _settings.Save();
        foreach (var fv in _tabs.Select(t => t.View).OfType<FolderView>()) fv.Close();
        _index.Dispose();
        var handle = new WindowInteropHelper(this).Handle;
        Native.UnregisterHotKey(handle, HotkeyId);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Ctrl+Alt+F: bring Tabseeker forward and focus the search box from anywhere
        Native.RegisterHotKey(handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.F));
        HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show();
                Activate();
                Topmost = true; Topmost = false;
                FocusSearch();
                handled = true;
            }
            return IntPtr.Zero;
        });
    }
}
