using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>
/// Sticky-note bubble attached to a file name. It is a small owned window (not a WPF Popup) so Korean IME input works.
/// The arrow always points at the name it belongs to; the window follows the name when the list scrolls or the window moves.
/// </summary>
public partial class MemoBubble : Window
{
    private const double ArrowCenter = 26;   // arrow tip y inside the window (grid margin 4 + arrow margin 14 + half height 8)
    private const double DefaultWidth = 300, DefaultHeight = 170;

    private readonly MemoStore _store;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;
    private bool _dirty;
    private bool _onLeft;

    public FileItem? Item { get; private set; }
    public FrameworkElement? Anchor { get; private set; }
    public bool IsEditing => Editor.IsKeyboardFocusWithin;
    public bool IsOpen => IsVisible && Item != null;

    /// <summary>Raised after the memo of <see cref="Item"/> was saved or deleted.</summary>
    public event Action<FileItem>? MemoChanged;

    private readonly Window _ownerWindow;

    public MemoBubble(MemoStore store, Window owner)
    {
        _store = store;
        InitializeComponent();
        _ownerWindow = owner; // assigned to Owner on first show: WPF refuses an owner that has not been shown yet
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
    }

    // ------------------------------------------------------------ show / hide

    public void ShowFor(FileItem item, FrameworkElement anchor, bool focusEditor)
    {
        if (Item != null && !ReferenceEquals(Item, item)) SaveNow();
        if (Owner == null && _ownerWindow.IsLoaded) Owner = _ownerWindow;
        Item = item;
        Anchor = anchor;
        var memo = _store.Find(item.FullPath);
        _loading = true;
        Editor.Text = memo?.Text ?? "";
        Editor.CaretIndex = Editor.Text.Length;
        _loading = false;
        _dirty = false;
        Width = Math.Max(MinWidth, memo?.Width ?? DefaultWidth);
        Height = Math.Max(MinHeight, memo?.Height ?? DefaultHeight);
        ItemName.Text = item.ShownName;
        ItemName.ToolTip = item.FullPath;
        ItemIcon.Source = item.Icon;
        var winComment = WindowsComment(item.FullPath);
        WinComment.Text = winComment == null ? "" : "Windows 설명 (파일 자체 정보): " + winComment;
        WinCommentBox.Visibility = winComment == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateChrome(memo);
        if (!Reposition()) { HideBubble(); return; }
        if (!IsVisible) Show();
        if (focusEditor)
        {
            Activate();
            Editor.Focus();
        }
    }

    public void HideBubble()
    {
        SaveNow();
        if (IsVisible) Hide();
        Item = null;
        Anchor = null;
    }

    /// <summary>Places the bubble beside its name (right side, or left when there is no room). False if the name is not visible.</summary>
    public bool Reposition()
    {
        if (Anchor == null || !Anchor.IsVisible) return false;
        var source = PresentationSource.FromVisual(Anchor);
        if (source?.CompositionTarget == null) return false;
        if (!VisibleInScroller(Anchor, out double visibleWidth)) return false;

        var toDip = source.CompositionTarget.TransformFromDevice;
        var topLeft = toDip.Transform(Anchor.PointToScreen(new Point(0, 0)));
        var bottomRight = toDip.Transform(Anchor.PointToScreen(new Point(visibleWidth, Anchor.ActualHeight)));
        double centerY = (topLeft.Y + bottomRight.Y) / 2;
        var work = WorkArea(Anchor.PointToScreen(new Point(0, 0)), toDip);

        _onLeft = bottomRight.X + 2 + Width > work.Right && topLeft.X - 2 - Width >= work.Left;
        LeftArrow.Visibility = _onLeft ? Visibility.Collapsed : Visibility.Visible;
        RightArrow.Visibility = _onLeft ? Visibility.Visible : Visibility.Collapsed;
        // with the arrow on the right, the window's right edge is the arrow tip (grid has a 6px right margin for the shadow)
        Left = _onLeft ? topLeft.X - 2 - Width + 6 : bottomRight.X + 2;

        double top = centerY - ArrowCenter;
        top = Math.Max(work.Top, Math.Min(top, work.Bottom - Height));
        Top = top;
        double arrowTop = Math.Clamp(centerY - top - ArrowCenter + 14, 4, Height - 30);
        LeftArrow.Margin = new Thickness(0, arrowTop, -1, 0);
        RightArrow.Margin = new Thickness(-1, arrowTop, 0, 0);
        return true;
    }

    /// <summary>Is the name inside the list's visible area? Also returns how much of it is visible (name column clipping).</summary>
    private static bool VisibleInScroller(FrameworkElement anchor, out double visibleWidth)
    {
        visibleWidth = anchor.ActualWidth;
        DependencyObject? d = anchor;
        GridViewRowPresenter? row = null;
        ScrollContentPresenter? scp = null;
        while (d != null && scp == null)
        {
            if (d is GridViewRowPresenter r) row = r;
            if (d is ScrollContentPresenter s) scp = s;
            d = VisualTreeHelper.GetParent(d);
        }
        if (row != null && row.Columns is { Count: > 0 } cols)
        {
            double x = anchor.TranslatePoint(new Point(0, 0), row).X;
            visibleWidth = Math.Max(8, Math.Min(anchor.ActualWidth, cols[0].ActualWidth - x - 6));
        }
        if (scp == null) return true;
        var p = anchor.TranslatePoint(new Point(0, anchor.ActualHeight / 2), scp);
        return p.Y >= 0 && p.Y <= scp.ActualHeight && p.X < scp.ActualWidth;
    }

    // ------------------------------------------------------------ saving

    public void SaveNow()
    {
        _saveTimer.Stop();
        if (!_dirty || Item == null) return;
        _dirty = false;
        try
        {
            var memo = _store.Save(Item.FullPath, Editor.Text, Width, Height);
            UpdateChrome(memo);
            Item.RefreshMemo();
            MemoChanged?.Invoke(Item);
        }
        catch (Exception ex) { Status.Text = "저장 실패: " + ex.Message; }
    }

    private void UpdateChrome(Memo? memo)
    {
        Hint.Visibility = Editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.IsEnabled = memo != null;
        Status.Text = memo == null ? "Tabseeker 안에만 암호화해서 보관됩니다" : $"저장됨 · {memo.Updated:yyyy-MM-dd HH:mm}";
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        Hint.Visibility = Editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loading) return;
        _dirty = true;
        Status.Text = "입력 중…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Item == null) return;
        _loading = true;
        Editor.Text = "";
        _loading = false;
        _dirty = false;
        _store.Delete(Item.FullPath);
        Item.RefreshMemo();
        MemoChanged?.Invoke(Item);
        UpdateChrome(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) { HideBubble(); Owner?.Activate(); }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideBubble(); Owner?.Activate(); e.Handled = true; }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // clicked somewhere else after editing: save and step away, unless the mouse is still over the bubble or its name
        SaveNow();
        if (!IsMouseOver && !(Anchor?.IsMouseOver ?? false)) HideBubble();
    }

    // ------------------------------------------------------------ resize

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double w = Math.Max(MinWidth, Math.Min(900, Width + e.HorizontalChange));
        double h = Math.Max(MinHeight, Math.Min(700, Height + e.VerticalChange));
        if (_onLeft) Left -= w - Width; // keep the arrow glued to the name when the bubble sits on the left
        Width = w;
        Height = h;
    }

    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Reposition();
        if (Item != null && (Editor.Text.Length > 0)) { _dirty = true; SaveNow(); }
    }

    // ------------------------------------------------------------ Windows' own description

    private static readonly Type? ShellType = Type.GetTypeFromProgID("Shell.Application");

    /// <summary>
    /// What Explorer shows as the file's description/comment (System.Comment): a shortcut's "설명", an installer's or
    /// document's comment. Written by whoever made the file — never by Tabseeker. Null when empty.
    /// </summary>
    private static string? WindowsComment(string path)
    {
        if (ShellType == null || path.Length <= 3) return null;
        try
        {
            dynamic shell = Activator.CreateInstance(ShellType)!;
            dynamic? folder = shell.NameSpace(System.IO.Path.GetDirectoryName(path));
            dynamic? item = folder?.ParseName(System.IO.Path.GetFileName(path));
            string? value = item?.ExtendedProperty("System.Comment") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ monitor work area

    private static Rect WorkArea(Point devicePoint, Matrix toDip)
    {
        var mon = MonitorFromPoint(new POINT { X = (int)devicePoint.X, Y = (int)devicePoint.Y }, 2 /* nearest */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info)) return SystemParameters.WorkArea;
        var tl = toDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        var br = toDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
        return new Rect(tl, br);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
}
