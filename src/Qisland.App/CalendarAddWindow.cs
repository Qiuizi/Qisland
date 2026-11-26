using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Qisland.App;

/// <summary>添加日历订阅的小窗：粘贴 .ics / webcal 链接，先拉一次确认是日历再保存。</summary>
public sealed class CalendarAddWindow : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _status;
    private readonly Button _ok;

    public CalendarAddWindow(CalendarWatcher cal)
    {
        Title = "添加日历订阅";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = true;
        var dark = Theme.Current != Theme.Look.Light;
        var fg = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        var sub = new SolidColorBrush(dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0, 0, 0));
        var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 420 };
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "添加日历订阅", FontFamily = Theme.Text, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = fg });
        var close = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = sub, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        close.MouseLeftButtonUp += (_, _) => Close();
        head.Children.Add(close);
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock
        {
            Text = "粘贴日历的 .ics 订阅链接（Outlook：设置 → 日历 → 共享日历 → 发布 → ICS；Google：日历设置 → 私密地址 iCal 格式；也支持 webcal://）。链接加密保存在本机，只读取日程。",
            FontFamily = Theme.Text, FontSize = 12, Foreground = sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        });
        _box = new TextBox { FontFamily = Theme.Text, FontSize = 13, Padding = new Thickness(6, 5, 6, 5), Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        try
        {
            var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (CalendarWatcher.Normalize(clip) != null && (clip.Contains("ics", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("webcal", StringComparison.OrdinalIgnoreCase) || clip.Contains("calendar", StringComparison.OrdinalIgnoreCase)))
                _box.Text = clip;
        }
        catch { }
        sp.Children.Add(_box);
        _status = new TextBlock { FontFamily = Theme.Text, FontSize = 12, Foreground = sub, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(_status);
        _ok = new Button { Content = "添加", Width = 90, Height = 28, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var done = false;
        _ok.Click += async (_, _) =>
        {
            if (done) { Close(); return; }
            _ok.IsEnabled = false;
            _status.Text = "正在读取…";
            var (ok, msg) = await cal.Add(_box.Text);
            _status.Text = msg;
            _ok.IsEnabled = true;
            if (ok) { done = true; _ok.Content = "完成"; }
        };
        sp.Children.Add(_ok);
        Content = new Border
        {
            Margin = new Thickness(20), CornerRadius = new CornerRadius(18), Child = sp,
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF7, 0xF7, 0xF8)),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0, 0, 0)), BorderThickness = new Thickness(1),
        };
        DialogKit.DragFromBlank(this);
        Loaded += (_, _) => { Activate(); _box.Focus(); _box.SelectAll(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
