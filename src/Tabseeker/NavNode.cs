using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>
/// One folder in the left pane. Clicking it opens the folder and unfolds its subfolders underneath
/// (loaded in the background on first open), so you can step straight into a subfolder.
/// The small arrow only folds/unfolds.
/// </summary>
internal sealed class NavNode
{
    private const int MaxChildren = 400;
    private const double Indent = 14;

    private readonly MainWindow _host;
    private readonly int _depth;
    private readonly StackPanel _children = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _arrow;
    private readonly Border _row;
    private bool _loaded;
    private bool _loading;

    public string Path { get; }
    public UIElement Element { get; }
    public bool IsExpanded => _children.Visibility == Visibility.Visible;

    public NavNode(MainWindow host, string label, string path, int depth, ContextMenu? menu = null)
    {
        _host = host;
        Path = path;
        _depth = depth;
        bool exists = path == FolderView.ThisPC || Directory.Exists(path);

        _arrow = new TextBlock
        {
            Text = "\uE76C", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, Width = 14,
            Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            Cursor = Cursors.Arrow, Background = Brushes.Transparent, Visibility = exists ? Visibility.Visible : Visibility.Hidden,
        };
        _arrow.MouseLeftButtonUp += (_, e) => { Toggle(); e.Handled = true; };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(_arrow);
        line.Children.Add(new Image
        {
            Source = path == FolderView.ThisPC ? ShellIcons.Get("C:\\", true) : ShellIcons.Get(path, true),
            Width = 16, Height = 16, Margin = new Thickness(2, 0, 6, 0),
        });
        line.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = exists ? 1 : 0.45, VerticalAlignment = VerticalAlignment.Center });
        if (MemoController.Store?.HasMemo(path) == true)
            line.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = (Brush)host.FindResource("MemoDot"), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        _row = new Border
        {
            Padding = new Thickness(2 + depth * Indent, 3, 6, 3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = path == FolderView.ThisPC ? null : path + "\n클릭: 열고 하위 폴더 펼치기 · 가운데 클릭: 새 탭",
            Child = line,
            ContextMenu = menu,
            AllowDrop = path != FolderView.ThisPC,
        };
        _row.MouseEnter += (_, _) => _row.Background = (Brush)host.FindResource("Hover");
        _row.MouseLeave += (_, _) => _row.Background = Brushes.Transparent;
        _row.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                host.NavigateActive(path);
                if (!IsExpanded) Expand();
            }
            else if (e.ChangedButton == MouseButton.Middle) host.OpenFolderTab(path, activate: false);
        };
        _row.DragOver += (_, e) => { e.Effects = ListHelpers.DropEffect(e, path); e.Handled = true; };
        _row.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                FileOps.Transfer(files, path, ListHelpers.DropEffect(e, path) == DragDropEffects.Move);
            e.Handled = true;
        };
        if (menu == null && path != FolderView.ThisPC)
        {
            _row.ContextMenu = new ContextMenu();
            _row.ContextMenu.Items.Add(ListHelpers.Item("새 탭에서 열기", () => host.OpenFolderTab(path)));
            _row.ContextMenu.Items.Add(ListHelpers.Item("탐색기에서 열기", () => FileOps.OpenFolderInExplorer(path)));
            _row.ContextMenu.Items.Add(ListHelpers.Item("즐겨찾기에 추가", () => host.AddFavorite(path)));
        }

        var panel = new StackPanel();
        panel.Children.Add(_row);
        panel.Children.Add(_children);
        Element = panel;
    }

    public void Toggle()
    {
        if (IsExpanded) Collapse(); else Expand();
    }

    public void Collapse()
    {
        _children.Visibility = Visibility.Collapsed;
        _arrow.Text = "\uE76C"; // ›
    }

    public void Expand()
    {
        _children.Visibility = Visibility.Visible;
        _arrow.Text = "\uE70D"; // ⌄
        if (_loaded || _loading) return;
        _loading = true;
        _children.Children.Add(new TextBlock { Text = "읽는 중…", Foreground = Brushes.Gray, FontSize = 11.5, Margin = new Thickness(18 + (_depth + 1) * Indent, 2, 0, 2) });
        bool showHidden = _host.ShowHidden;
        var path = Path;
        Task.Run(() => ReadChildren(path, showHidden)).ContinueWith(t =>
        {
            _loading = false;
            _loaded = true;
            _children.Children.Clear();
            var list = t.Exception == null ? t.Result : new List<(string, string)>();
            if (list.Count == 0)
            {
                _arrow.Visibility = Visibility.Hidden; // nothing below
                _children.Visibility = Visibility.Collapsed;
                return;
            }
            foreach (var (name, full) in list.Take(MaxChildren))
                _children.Children.Add(new NavNode(_host, name, full, _depth + 1).Element);
            if (list.Count > MaxChildren)
                _children.Children.Add(new TextBlock { Text = $"… {list.Count - MaxChildren:N0}개 더 (폴더를 열어서 보기)", Foreground = Brushes.Gray, FontSize = 11.5, Margin = new Thickness(18 + (_depth + 1) * Indent, 2, 0, 2) });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Re-read the subfolders next time it is opened (after "show hidden" changes, etc.).</summary>
    public void Reset()
    {
        _loaded = false;
        _children.Children.Clear();
        _arrow.Visibility = Visibility.Visible;
        Collapse();
    }

    private static List<(string name, string full)> ReadChildren(string path, bool showHidden)
    {
        if (path == FolderView.ThisPC)
        {
            var drives = new List<(string, string)>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    var label = string.IsNullOrEmpty(d.VolumeLabel) ? "로컬 디스크" : d.VolumeLabel;
                    drives.Add(($"{label} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName));
                }
                catch { }
            }
            return drives;
        }
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories("*", options)
                .Select(d => (d.Name, d.FullName))
                .OrderBy(x => x.Name, NaturalComparer.Instance)
                .ToList();
        }
        catch { return new List<(string, string)>(); }
    }
}
