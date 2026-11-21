using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tabseeker.Core;

namespace Tabseeker;

/// <summary>Lets the user choose which Explorer right-click items go into "앱 메뉴 모음 ▸" (or get hidden), and undo it.</summary>
public partial class ShellMenuWindow : Window
{
    private readonly ShellMenuOrganizer _org = ShellMenuOrganizer.ForCurrentUser(AppSettings.Dir);
    private readonly List<(CheckBox box, List<ShellMenuEntry> entries)> _group = new();
    private readonly List<(CheckBox box, List<ShellMenuEntry> entries)> _hide = new();

    public ShellMenuWindow()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        _group.Clear(); _hide.Clear();
        GroupList.Items.Clear(); HideList.Items.Clear();
        List<ShellMenuEntry> items;
        try { items = _org.Scan(); }
        catch (Exception ex) { Status.Text = "메뉴를 읽지 못했습니다: " + ex.Message; return; }
        bool applied = _org.IsApplied;

        // one checkbox per app item, even when it appears for files, folders and drives
        foreach (var g in items.Where(e => !e.IsHandler && !e.IsSystem).GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            Add(GroupList, _group, g.ToList(), isChecked: applied ? g.Any(e => e.ByTabseeker) : true);
        foreach (var g in items.Where(e => e.IsHandler && !e.IsSystem).GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            Add(HideList, _hide, g.ToList(), isChecked: g.Any(e => e.ByTabseeker));

        var m = _org.LoadManifest();
        Status.Text = applied && m != null
            ? $"적용됨 ({m.Applied:yyyy-MM-dd HH:mm}) · 묶은 항목 {m.Grouped.Count}개 · 숨긴 항목 {m.Hidden.Count}개"
            : $"아직 적용 안 됨 · 묶을 수 있는 항목 {_group.Count}개 · 숨길 수 있는 항목 {_hide.Count}개";
        UndoBtn.IsEnabled = applied;
    }

    private static void Add(ItemsControl host, List<(CheckBox, List<ShellMenuEntry>)> list, List<ShellMenuEntry> entries, bool isChecked)
    {
        var first = entries[0];
        var where = string.Join(", ", entries.Select(e => e.ClassLabel).Distinct());
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = first.Label, FontWeight = FontWeights.Normal });
        content.Children.Add(new TextBlock
        {
            Text = $"{where}  ·  {first.Detail}",
            FontSize = 11.5, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 600,
        });
        var box = new CheckBox { Content = content, IsChecked = isChecked, Margin = new Thickness(4, 3, 0, 3), ToolTip = first.Detail };
        host.Items.Add(box);
        list.Add((box, entries));
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var group = _group.Where(x => x.box.IsChecked == true).SelectMany(x => x.entries).ToList();
        var hide = _hide.Where(x => x.box.IsChecked == true).SelectMany(x => x.entries).ToList();
        int g = _group.Count(x => x.box.IsChecked == true), h = _hide.Count(x => x.box.IsChecked == true);
        if (g == 0 && h == 0)
        {
            if (_org.IsApplied) Undo_Click(sender, e);
            else Status.Text = "선택한 항목이 없습니다.";
            return;
        }
        var answer = MessageBox.Show(this,
            $"탐색기 우클릭 메뉴를 바꿉니다.\n\n· ‘앱 메뉴 모음 ▸’으로 묶기: {g}개\n· 숨기기: {h}개\n\n현재 Windows 사용자에게만 적용되며, 이 창의 ‘원래대로 되돌리기’로 언제든 원래 메뉴로 돌아옵니다.\n적용할까요?",
            "탐색기 우클릭 메뉴 정리", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            var icon = Environment.ProcessPath is { } exe ? exe + ",0" : null;
            _org.Apply(group, hide, icon);
            NotifyShell();
            Load();
            Status.Text += " — 탐색기에서 파일을 오른쪽 클릭 ▸ ‘더 많은 옵션 표시’에서 확인하세요.";
        }
        catch (Exception ex) { Status.Text = "적용 실패: " + ex.Message; }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _org.Undo();
            NotifyShell();
            Load();
            Status.Text = "원래 메뉴로 되돌렸습니다. " + Status.Text;
        }
        catch (Exception ex) { Status.Text = "되돌리기 실패: " + ex.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Tell Explorer that file associations/menus changed.</summary>
    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
}
