using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>
/// Shows the memo bubble when the mouse rests on a file name (the TextBlock tagged "MemoAnchor" in the name cell),
/// keeps it glued to that name while scrolling or moving the window, and hides it when the mouse leaves both.
/// </summary>
public sealed class MemoController
{
    public const string AnchorTag = "MemoAnchor";
    private static readonly TimeSpan ShowDelayWithMemo = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ShowDelayEmpty = TimeSpan.FromMilliseconds(900);

    /// <summary>The memo database, shared with list rows (for the memo mark next to names).</summary>
    public static MemoStore? Store { get; private set; }

    private readonly Window _owner;
    private readonly MemoBubble _bubble;
    private readonly DispatcherTimer _showTimer = new();
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private FrameworkElement? _pending;

    /// <summary>Also pop up (after a longer pause) on names that have no memo yet, so one can be written.</summary>
    public bool HoverForEmpty { get; set; } = true;

    public MemoController(Window owner, MemoStore store)
    {
        Store = store;
        _owner = owner;
        _bubble = new MemoBubble(store, owner);
        _showTimer.Tick += (_, _) => ShowPending();
        _hideTimer.Tick += (_, _) => TryHide();
        _bubble.MouseEnter += (_, _) => _hideTimer.Stop();
        _bubble.MouseLeave += (_, _) => ScheduleHide();
        owner.LocationChanged += (_, _) => Follow();
        owner.SizeChanged += (_, _) => Follow();
        owner.StateChanged += (_, _) => { if (owner.WindowState == WindowState.Minimized) Hide(); else Follow(); };
        owner.Closing += (_, _) => { _bubble.HideBubble(); _bubble.Close(); };
    }

    public void Attach(ListView list)
    {
        list.MouseMove += (_, e) => OnMouseMove(e);
        list.MouseLeave += (_, _) => { _showTimer.Stop(); _pending = null; ScheduleHide(); };
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Follow()));
        list.IsVisibleChanged += (_, _) => { if (!list.IsVisible && IsBubbleIn(list)) Hide(); };
    }

    /// <summary>Ctrl+M / context menu: open the bubble for the selected row and start typing.</summary>
    public void OpenForSelected(ListView list)
    {
        if (list.SelectedItem is not FileItem item) return;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem row) return;
        var anchor = FindAnchor(row);
        if (anchor == null) return;
        _showTimer.Stop();
        _hideTimer.Stop();
        _bubble.ShowFor(item, anchor, focusEditor: true);
    }

    public void Hide()
    {
        _showTimer.Stop();
        _hideTimer.Stop();
        _pending = null;
        _bubble.HideBubble();
    }

    private void OnMouseMove(MouseEventArgs e)
    {
        var anchor = AnchorFrom(e.OriginalSource as DependencyObject);
        if (anchor == null)
        {
            _showTimer.Stop();
            _pending = null;
            ScheduleHide();
            return;
        }
        if (ReferenceEquals(anchor, _bubble.Anchor) && _bubble.IsOpen) { _hideTimer.Stop(); return; }
        if (ReferenceEquals(anchor, _pending)) return;
        if (anchor.DataContext is not FileItem item) return;
        if (!item.HasMemo && !HoverForEmpty) return;
        _pending = anchor;
        _showTimer.Stop();
        _showTimer.Interval = item.HasMemo ? ShowDelayWithMemo : ShowDelayEmpty;
        _showTimer.Start();
    }

    private void ShowPending()
    {
        _showTimer.Stop();
        var anchor = _pending;
        _pending = null;
        if (anchor == null || !anchor.IsMouseOver || anchor.DataContext is not FileItem item) return;
        if (_bubble.IsEditing && _bubble.IsOpen) return; // don't yank the bubble away while the user is typing
        if (!_owner.IsActive && !_bubble.IsActive) return; // only for the window the user is working in
        _hideTimer.Stop();
        _bubble.ShowFor(item, anchor, focusEditor: false);
    }

    private void ScheduleHide()
    {
        if (!_bubble.IsOpen) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void TryHide()
    {
        _hideTimer.Stop();
        if (!_bubble.IsOpen) return;
        if (_bubble.IsMouseOver || _bubble.IsEditing || (_bubble.Anchor?.IsMouseOver ?? false)) return;
        _bubble.HideBubble();
    }

    /// <summary>Keep the bubble beside its name; if the name scrolled out of view (or its tab closed), hide it.</summary>
    private void Follow()
    {
        if (!_bubble.IsOpen) return;
        if (!_bubble.Reposition()) _bubble.HideBubble();
    }

    private bool IsBubbleIn(ListView list)
    {
        for (DependencyObject? d = _bubble.Anchor; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, list)) return true;
        return false;
    }

    private static FrameworkElement? AnchorFrom(DependencyObject? d)
    {
        for (; d != null && d is not ListViewItem; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement fe && Equals(fe.Tag, AnchorTag)) return fe;
        return null;
    }

    private static FrameworkElement? FindAnchor(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is FrameworkElement fe && Equals(fe.Tag, AnchorTag)) return fe;
            var found = FindAnchor(c);
            if (found != null) return found;
        }
        return null;
    }
}
