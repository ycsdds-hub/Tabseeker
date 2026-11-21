using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tabseeker;

/// <summary>F1 / ? help: one page with a short explanation of each feature, a table of contents and the shortcut list.</summary>
public partial class HelpWindow : Window
{
    private static readonly (string keys, string what)[] Shortcuts =
    {
        ("F1", "이 도움말"),
        ("Ctrl+T / Ctrl+W", "새 탭 / 탭 닫기"),
        ("Ctrl+Shift+T", "닫은 탭 다시 열기"),
        ("Ctrl+Tab, Ctrl+1~9", "탭 이동"),
        ("Alt+← / Alt+→", "뒤로 / 앞으로"),
        ("Backspace, Alt+↑", "상위 폴더"),
        ("Ctrl+L / Ctrl+F", "경로 입력 / 이 폴더 거르기"),
        ("Ctrl+Shift+1 / 2", "목록 보기 / 타일 보기"),
        ("Ctrl+E", "전체 검색창으로"),
        ("Ctrl+Alt+F", "어디서나 Tabseeker 불러와 검색"),
        ("Ctrl+Enter", "폴더를 새 탭으로 (검색 결과: 들어 있는 폴더)"),
        ("Ctrl+M", "선택한 항목 메모 쓰기"),
        ("Ctrl+D", "현재 폴더를 즐겨찾기에"),
        ("F2 / Delete", "이름 바꾸기 / 휴지통 (Shift+Delete 영구 삭제)"),
        ("Ctrl+C / X / V", "복사 / 잘라내기 / 붙여넣기"),
        ("Ctrl+Shift+C", "경로 복사"),
        ("Ctrl+Shift+N", "새 폴더"),
        ("F5", "새로 고침"),
    };

    private static HelpWindow? _open;

    /// <summary>Shows the single help window (brings it forward if already open).</summary>
    public static void ShowFor(Window owner, string? topic = null)
    {
        if (_open == null)
        {
            _open = new HelpWindow { Owner = owner };
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
        }
        if (topic != null) _open.GoTo(topic);
    }

    private HelpWindow()
    {
        InitializeComponent();
        foreach (var (keys, what) in Shortcuts)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var key = new Border { Style = (Style)FindResource("Key"), Child = new TextBlock { Text = keys, FontFamily = new FontFamily("Consolas, Malgun Gothic") } };
            var text = new TextBlock { Text = what, Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(key);
            row.Children.Add(text);
            KeyRows.Children.Add(row);
        }
        Version.Text = $"Tabseeker {typeof(HelpWindow).Assembly.GetName().Version?.ToString(3)} · TabSeek 1.0.0 바탕";
    }

    public void GoTo(string topic)
    {
        if (FindName(topic) is FrameworkElement target)
        {
            target.BringIntoView(new Rect(0, 0, 1, Scroller.ViewportHeight)); // heading at the top of the view
        }
    }

    private void Toc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Toc.SelectedItem is ListBoxItem { Tag: string topic }) GoTo(topic);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F1) { Close(); e.Handled = true; }
    }
}
