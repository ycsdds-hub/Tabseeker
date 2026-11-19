using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Tabseeker;

/// <summary>What a view needs from the main window.</summary>
public interface IShell
{
    void OpenFolderTab(string path, bool activate = true, string? selectName = null);
    void NavigateActive(string path);
    void AddFavorite(string path);
    void ViewChanged(FrameworkElement view);
    bool ShowHidden { get; }
    MemoController Memos { get; }
}

internal static class ListHelpers
{
    public static List<FileItem> Selected(ListView list) => list.SelectedItems.Cast<FileItem>().ToList();

    public static FileItem? ItemAt(ListView list, Point p)
    {
        var hit = list.InputHitTest(p) as DependencyObject;
        while (hit != null && hit is not ListViewItem && hit != list) hit = VisualTreeHelper.GetParent(hit);
        return (hit as ListViewItem)?.DataContext as FileItem;
    }

    /// <summary>Lets the user drag selected rows out to Explorer, the desktop or any app.</summary>
    public static void EnableDragOut(ListView list)
    {
        Point? start = null;
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = ItemAt(list, e.GetPosition(list)) != null && !IsOnScrollBar(e.OriginalSource) ? e.GetPosition(list) : null;
        };
        list.PreviewMouseLeftButtonUp += (_, _) => start = null;
        list.PreviewMouseMove += (_, e) =>
        {
            if (start == null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(list) - start.Value;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            var paths = Selected(list).Select(i => i.FullPath).Where(p => p.Length > 3).ToArray();
            if (paths.Length == 0) return;
            var data = new DataObject(DataFormats.FileDrop, paths);
            try { DragDrop.DoDragDrop(list, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); } catch { }
        };
    }

    private static bool IsOnScrollBar(object src)
    {
        for (var d = src as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollBar) return true;
        return false;
    }

    /// <summary>Explorer's rule: Ctrl = copy, Shift = move, otherwise move on the same drive and copy across drives.</summary>
    public static DragDropEffects DropEffect(DragEventArgs e, string target)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || string.IsNullOrEmpty(target)) return DragDropEffects.None;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var src = files?.FirstOrDefault();
        return src != null && string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase)
            ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    public static MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Sorts folders first, then by key.</summary>
    public static List<FileItem> Sort(IEnumerable<FileItem> items, string key, bool desc, bool foldersFirst = true)
    {
        IComparer<FileItem> cmp = key switch
        {
            "Size" => Comparer<FileItem>.Create((a, b) => a.Size.CompareTo(b.Size)),
            "Modified" => Comparer<FileItem>.Create((a, b) => a.Modified.CompareTo(b.Modified)),
            "Type" => Comparer<FileItem>.Create((a, b) => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase)),
            "Folder" => Comparer<FileItem>.Create((a, b) => Core.NaturalComparer.Instance.Compare(a.Folder, b.Folder)),
            _ => Comparer<FileItem>.Create((a, b) => Core.NaturalComparer.Instance.Compare(a.ShownName, b.ShownName)),
        };
        var list = items.ToList();
        list.Sort((a, b) =>
        {
            if (foldersFirst && a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
            int c = cmp.Compare(a, b);
            if (c == 0 && key != "Name") c = Core.NaturalComparer.Instance.Compare(a.Name, b.Name);
            return desc ? -c : c;
        });
        return list;
    }

    public static void MarkSortHeader(GridView view, string key, bool desc)
    {
        foreach (var col in view.Columns)
        {
            if (col.Header is not GridViewColumnHeader h) continue;
            var label = (h.Content as string ?? "").TrimEnd(' ', '▲', '▼');
            h.Content = (string?)h.Tag == key ? label + (desc ? " ▼" : " ▲") : label;
        }
    }
}

/// <summary>Tiny text-input dialog.</summary>
internal sealed class Prompt : Window
{
    private readonly TextBox _box;

    private Prompt(string title, string label, string value)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        _box = new TextBox { Text = value, Padding = new Thickness(4) };
        panel.Children.Add(_box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "확인", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "취소", Width = 80, IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _box.Focus();
            // select the name without its extension, like Explorer
            int dot = value.LastIndexOf('.');
            if (dot > 0) _box.Select(0, dot); else _box.SelectAll();
        };
    }

    public static string? Ask(string title, string label, string value)
    {
        var p = new Prompt(title, label, value) { Owner = Application.Current.MainWindow };
        return p.ShowDialog() == true ? p._box.Text.Trim() : null;
    }
}
