using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 「翻译与 AI 设置」小窗：引擎、模型、快捷键、问 AI 去哪；API Key 填进来就存到 Windows 凭据管理器，
/// 输入框立即清空，界面上只显示「已保存」，从不回显。
/// </summary>
public sealed class AiSettingsWindow : Window
{
    private readonly AiSettings _s;
    private readonly Func<List<string>> _applyHotkeys;
    private readonly Dictionary<string, PasswordBox> _keys = [];
    private readonly Dictionary<string, TextBox> _models = [];
    private readonly Dictionary<string, TextBlock> _keyState = [];
    private readonly Brush _fg, _sub;

    public AiSettingsWindow(AiSettings s, Func<List<string>> applyHotkeys)
    {
        _s = s; _applyHotkeys = applyHotkeys;
        Title = "翻译与 AI 设置";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = true;
        var dark = Theme.Current != Theme.Look.Light;
        _fg = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        _sub = new SolidColorBrush(dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0, 0, 0));

        var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 520 };
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "翻译与 AI 设置", FontFamily = Theme.Text, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = _fg });
        var close = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = _sub, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        close.MouseLeftButtonUp += (_, _) => Close();
        head.Children.Add(close);
        sp.Children.Add(head);
        sp.Children.Add(Note("选中文字后按快捷键：翻译在岛上左右对照显示，可复制、可替换原文；没有选中时会替你按一次 Ctrl+C（之后还原剪贴板），焦点在密码框里时不读取。文字只在内存里，不记日志。"));

        // 快捷键
        var hk = Row();
        var trHk = Box(s.TranslateHotkey, 140); var askHk = Box(s.AskHotkey, 140);
        hk.Children.Add(Label("翻译快捷键")); hk.Children.Add(trHk);
        hk.Children.Add(Label("  问 AI 快捷键")); hk.Children.Add(askHk);
        sp.Children.Add(hk);

        // 翻译引擎
        var tr = Row();
        tr.Children.Add(Label("翻译用"));
        var trEngine = Combo(AiCatalog.All, s.TranslateEngine);
        tr.Children.Add(trEngine);
        sp.Children.Add(tr);
        sp.Children.Add(Note("默认 MyMemory 免费、不用 Key（每天有额度）；选了需要 Key 的引擎但没填 Key 时自动退回 MyMemory。"));

        // 问 AI
        var ask = Row();
        ask.Children.Add(Label("问 AI"));
        var toDoubao = new RadioButton { Content = "粘贴到豆包窗口（不发送）", Foreground = _fg, IsChecked = s.AskTarget == "doubao", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var onIsland = new RadioButton { Content = "岛上回答，用", Foreground = _fg, IsChecked = s.AskTarget != "doubao", VerticalAlignment = VerticalAlignment.Center };
        var askEngine = Combo(AiCatalog.All.Where(e => e.Engine != AiEngine.MyMemory), s.AskEngine);
        askEngine.Width = 150; askEngine.Margin = new Thickness(6, 0, 0, 0);
        ask.Children.Add(toDoubao); ask.Children.Add(onIsland); ask.Children.Add(askEngine);
        sp.Children.Add(ask);

        // Key 和模型
        sp.Children.Add(new TextBlock { Text = "API Key 和模型", FontFamily = Theme.Text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _fg, Margin = new Thickness(0, 14, 0, 2) });
        sp.Children.Add(Note("Key 存在 Windows 凭据管理器（名字以 Qisland/ 开头），只有这台电脑上的你能读；这里不会再显示出来。留空表示不改。"));
        foreach (var e in AiCatalog.All.Where(e => e.Engine != AiEngine.MyMemory))
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var name = Label(e.Name); name.ToolTip = e.KeyHint;
            g.Children.Add(name);
            if (e.NeedsKey)
            {
                var pb = new PasswordBox { Height = 26, Margin = new Thickness(0, 0, 6, 0), ToolTip = e.KeyHint, VerticalContentAlignment = VerticalAlignment.Center };
                Grid.SetColumn(pb, 1); g.Children.Add(pb); _keys[e.Id] = pb;
            }
            var model = Box(s.Models.TryGetValue(e.Id, out var m) ? m : "", 0);
            model.ToolTip = "模型名，留空用默认：" + e.DefaultModel;
            Grid.SetColumn(model, 2); g.Children.Add(model); _models[e.Id] = model;
            var state = new TextBlock { FontFamily = Theme.Text, FontSize = 11.5, Foreground = _sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
            Grid.SetColumn(state, 3); g.Children.Add(state); _keyState[e.Id] = state;
            if (e.NeedsKey)
                state.MouseLeftButtonUp += (_, _) =>
                {
                    if (!SecretStore.Has(AiSettings.KeyName(e))) return;
                    if (MessageBox.Show(this, $"删除已保存的 {e.Name} Key？", "Qisland", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                    SecretStore.Delete(AiSettings.KeyName(e)); RefreshStates();
                };
            sp.Children.Add(g);
        }
        RefreshStates();

        var status = new TextBlock { FontFamily = Theme.Text, FontSize = 12, Foreground = _sub, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(status);
        var ok = new Button { Content = "保存", Width = 90, Height = 28, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (!HotkeyText.TryParse(trHk.Text, out _, out _) || !HotkeyText.TryParse(askHk.Text, out _, out _))
            { status.Text = "快捷键格式不对：要带 Ctrl / Alt / Win，例如 Ctrl+Alt+T"; return; }
            if (string.Equals(trHk.Text.Trim(), askHk.Text.Trim(), StringComparison.OrdinalIgnoreCase)) { status.Text = "两个快捷键不能一样"; return; }
            foreach (var (id, pb) in _keys)
            {
                var key = pb.Password.Trim();
                pb.Clear();
                if (key.Length == 0) continue;
                if (key.Length > 400 || key.Any(c => c < 33 || c > 126)) { status.Text = "有一个 Key 里有空格或中文，没有保存它"; continue; }
                SecretStore.Save("ai/" + id, key);
            }
            _s.TranslateHotkey = HotkeyText.TryParse(trHk.Text, out var m1, out var v1) ? HotkeyText.Format(m1, v1) : _s.TranslateHotkey;
            _s.AskHotkey = HotkeyText.TryParse(askHk.Text, out var m2, out var v2) ? HotkeyText.Format(m2, v2) : _s.AskHotkey;
            _s.TranslateEngine = (trEngine.SelectedItem as ComboBoxItem)?.Tag as string ?? _s.TranslateEngine;
            _s.AskEngine = (askEngine.SelectedItem as ComboBoxItem)?.Tag as string ?? _s.AskEngine;
            _s.AskTarget = toDoubao.IsChecked == true ? "doubao" : "island";
            foreach (var (id, box) in _models) { if (box.Text.Trim().Length > 0) _s.Models[id] = box.Text.Trim(); else _s.Models.Remove(id); }
            _s.Save();
            var failed = _applyHotkeys();
            RefreshStates();
            status.Text = failed.Count > 0 ? $"已保存，但快捷键 {string.Join("、", failed)} 被别的程序占用了，换一个试试" : _s.TranslateOn ? "已保存" : "已保存（划词功能现在是关着的，托盘里可以打开）";
        };
        sp.Children.Add(ok);
        Content = new Border
        {
            Margin = new Thickness(20), CornerRadius = new CornerRadius(18), Child = sp,
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF7, 0xF7, 0xF8)),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0, 0, 0)), BorderThickness = new Thickness(1),
        };
        DialogKit.DragFromBlank(this);
        Loaded += (_, _) => Activate();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { foreach (var pb in _keys.Values) pb.Clear(); };
    }

    private void RefreshStates()
    {
        foreach (var (id, t) in _keyState)
        {
            var e = AiCatalog.ById(id)!;
            t.Text = !e.NeedsKey ? "不需要 Key" : SecretStore.Has(AiSettings.KeyName(e)) ? "已保存（点此删除）" : "未填";
        }
    }

    private StackPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private TextBlock Label(string t) => new() { Text = t, FontFamily = Theme.Text, FontSize = 12.5, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private TextBlock Note(string t) => new() { Text = t, FontFamily = Theme.Text, FontSize = 11.5, Foreground = _sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private static TextBox Box(string text, double width)
    {
        var b = new TextBox { Text = text, FontFamily = Theme.Text, FontSize = 12.5, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0, 4, 0) };
        if (width > 0) b.Width = width;
        return b;
    }
    private static ComboBox Combo(IEnumerable<AiEngineInfo> items, string selected)
    {
        var c = new ComboBox { Width = 220, Height = 26 };
        foreach (var e in items)
        {
            var it = new ComboBoxItem { Content = e.Name, Tag = e.Id };
            c.Items.Add(it);
            if (e.Id == selected) c.SelectedItem = it;
        }
        if (c.SelectedItem == null && c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }
}

/// <summary>连接豆包网页额度：粘贴 Cookie 请求头（输入框遮住），校验后 DPAPI 加密存本机。</summary>
public sealed class DoubaoConnectWindow : Window
{
    public DoubaoConnectWindow(DoubaoQuotaService svc)
    {
        Title = "连接豆包额度";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = true;
        var dark = Theme.Current != Theme.Look.Light;
        var fg = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        var sub = new SolidColorBrush(dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0, 0, 0));
        var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 440 };
        sp.Children.Add(new TextBlock { Text = "连接豆包额度（非官方接口）", FontFamily = Theme.Text, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = fg });
        sp.Children.Add(new TextBlock
        {
            Text = "1. 浏览器登录 www.doubao.com，打开「会员 → 额度」页面\n2. 按 F12 → 网络（Network）→ 点任意一个 doubao.com 请求 → 请求标头里的 Cookie，整行复制\n3. 粘贴到下面\n\n只读额度，约每 25 分钟一次；Cookie 用 DPAPI 加密存在本机，不记日志、不发给别人。豆包改接口或登录过期时会提示，不会反复重试。托盘里随时断开。",
            FontFamily = Theme.Text, FontSize = 12, Foreground = sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        });
        var box = new PasswordBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        sp.Children.Add(box);
        var status = new TextBlock { FontFamily = Theme.Text, FontSize = 12, Foreground = sub, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(status);
        var ok = new Button { Content = "连接", Width = 90, Height = 28, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            var raw = box.Password; box.Clear();
            status.Text = "正在读取额度…";
            var msg = await svc.Connect(raw);
            status.Text = msg;
            ok.IsEnabled = true;
        };
        sp.Children.Add(ok);
        Content = new Border
        {
            Margin = new Thickness(20), CornerRadius = new CornerRadius(18), Child = sp,
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF7, 0xF7, 0xF8)),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0, 0, 0)), BorderThickness = new Thickness(1),
        };
        DialogKit.DragFromBlank(this);
        Loaded += (_, _) => { Activate(); box.Focus(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => box.Clear();
    }
}
