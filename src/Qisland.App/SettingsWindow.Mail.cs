using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>4.3 设置「邮箱验证码」页。</summary>
public sealed partial class SettingsWindow
{
    // ───────────── 邮箱验证码 ─────────────

    private void MailOtpPage(StackPanel p)
    {
        var cfg = MailOtpSettings.Current;
        var svc = _app.MailService;
        if (svc != null) RefreshOn("邮箱验证码", h => svc.Changed += h, h => svc.Changed -= h);

        var head = new StackPanel();
        head.Children.Add(DialogKit.Row("开启邮箱验证码", "新邮件里有验证码时岛上弹出，点一下复制；默认关",
            DialogKit.Switch(cfg.Enabled, v => { cfg.Enabled = v; cfg.Save(); svc?.Restart(); RenderSoon(); })));
        if (cfg.Enabled) head.Children.Add(StatusLine(svc?.StatusText ?? "", svc?.Healthy == true));
        head.Children.Add(Note("只读：用 EXAMINE 只读打开收件箱，只看连上之后新来的邮件的标题和正文开头（不标已读、不删不移），认出验证码就丢掉邮件内容。授权码存在 Windows 凭据管理器（Qisland/mail/imap），配置文件里只有邮箱地址和服务器。"));
        p.Children.Add(Card(head));

        p.Children.Add(Section("账号"));
        var form = new StackPanel();
        var presets = MailPreset.All;
        var presetIdx = Math.Max(0, Array.FindIndex(presets, x => x.Id == cfg.Preset));
        var hint = DialogKit.Text(presets[presetIdx].Hint, 11.5, FontWeights.Normal, DialogKit.Sub);
        hint.Margin = new Thickness(0, -2, 0, 12);
        var host = DialogKit.Box(cfg.Host, 210);
        var port = DialogKit.Box(cfg.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), 64);
        var user = DialogKit.Box(cfg.User, 210);
        var pwd = new PasswordBox
        {
            Width = 210, FontFamily = Theme.Text, FontSize = 13, Padding = new Thickness(6, 4, 6, 4), Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center, Background = DialogKit.Faint, Foreground = DialogKit.Fg, BorderBrush = DialogKit.Line, CaretBrush = DialogKit.Fg,
        };
        var saved = cfg.HasPassword;
        var pick = presetIdx;
        form.Children.Add(DialogKit.Segmented(presets.Select(x => x.Name).ToArray(), presetIdx, i =>
        {
            pick = i;
            hint.Text = presets[i].Hint;
            if (presets[i].Host.Length > 0) { host.Text = presets[i].Host; port.Text = presets[i].Port.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        }));
        form.Children.Add(new Border { Height = 10 });
        form.Children.Add(hint);
        form.Children.Add(DialogKit.Row("邮箱地址", null, user));
        form.Children.Add(DialogKit.Row("授权码", saved ? "已保存在凭据管理器里；要换就重新填，留空保持不变" : "邮箱设置里生成的授权码 / 应用专用密码，不是登录密码", pwd));
        var hp = new StackPanel { Orientation = Orientation.Horizontal };
        hp.Children.Add(host);
        var colon = DialogKit.Text(":", 13, FontWeights.Normal, DialogKit.Sub); colon.Margin = new Thickness(6, 5, 6, 0);
        hp.Children.Add(colon);
        hp.Children.Add(port);
        form.Children.Add(DialogKit.Row("IMAP 服务器", "993 走 SSL；143 必须支持 STARTTLS，否则不会登录", hp));
        user.LostFocus += (_, _) =>
        {
            // 填了邮箱地址、还停在「其他」时：认得出的域名自动换成对应预设
            if (presets[pick].Id == "custom" && MailPreset.Guess(user.Text) is { } g && host.Text.Trim().Length == 0)
            { host.Text = g.Host; port.Text = g.Port.ToString(System.Globalization.CultureInfo.InvariantCulture); pick = Array.IndexOf(presets, g); hint.Text = g.Hint; }
        };
        var intervals = new[] { 15, 20, 30 };
        var iv = Math.Max(0, Array.IndexOf(intervals, cfg.IntervalSeconds));
        if (Array.IndexOf(intervals, cfg.IntervalSeconds) < 0) iv = 1;
        form.Children.Add(DialogKit.Row("检查间隔", "保持一条只读连接，隔一会儿问一次有没有新邮件", DialogKit.Segmented(["15 秒", "20 秒", "30 秒"], iv, i => iv = i)));
        var cards = new[] { 60, 180, 300 };
        var cv = Array.IndexOf(cards, cfg.CardSeconds) is var ci && ci >= 0 ? ci : 1;
        form.Children.Add(DialogKit.Row("验证码停留", "到时自动收起，也可以在列表里点「关闭」；通知里的验证码也按这个", DialogKit.Segmented(["1 分钟", "3 分钟", "5 分钟"], cv, i => cv = i)));

        var msg = DialogKit.Text("", 12, FontWeights.SemiBold, DialogKit.Sub);
        msg.Margin = new Thickness(0, 2, 0, 10);
        int Port() => int.TryParse(port.Text.Trim(), out var n) && n is > 0 and < 65536 ? n : 993;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 6) };
        Border? test = null;
        test = DialogKit.Button("测试连接", async () =>
        {
            var pw = pwd.Password.Length > 0 ? pwd.Password : SecretStore.Load(MailOtpSettings.SecretName) ?? "";
            if (user.Text.Trim().Length == 0 || host.Text.Trim().Length == 0 || pw.Length == 0) { msg.Foreground = Theme.Orange; msg.Text = "先填邮箱地址、服务器和授权码"; return; }
            test!.IsEnabled = false; msg.Foreground = DialogKit.Sub; msg.Text = "正在连接…";
            var (ok, text) = await MailOtpService.TestAsync(host.Text.Trim(), Port(), user.Text.Trim(), pw);
            test.IsEnabled = true; msg.Foreground = ok ? Theme.Green : Theme.Orange; msg.Text = text;
        });
        buttons.Children.Add(test);
        if (saved)
            buttons.Children.Add(DialogKit.Button("清除授权码", () =>
            {
                SecretStore.Delete(MailOtpSettings.SecretName);
                cfg.Enabled = false; cfg.Save(); svc?.Restart();
                App.Log("邮箱验证码: 已清除授权码");
                RenderSoon();
            }, danger: true));
        buttons.Children.Add(DialogKit.Button("保存", () =>
        {
            cfg.Preset = presets[pick].Id;
            cfg.Host = host.Text.Trim();
            cfg.Port = Port();
            cfg.User = user.Text.Trim();
            cfg.IntervalSeconds = intervals[iv];
            cfg.CardSeconds = cards[cv];
            if (pwd.Password.Length > 0 && !SecretStore.Save(MailOtpSettings.SecretName, pwd.Password)) { msg.Foreground = Theme.Orange; msg.Text = "授权码没存进凭据管理器"; return; }
            pwd.Clear();
            cfg.Save();
            svc?.Restart();
            App.Log("邮箱验证码: 设置已保存");
            msg.Foreground = Theme.Green; msg.Text = cfg.Enabled ? "已保存，正在重新连接" : "已保存（上面打开开关后生效）";
            RenderSoon();
        }, primary: true));
        form.Children.Add(buttons);
        form.Children.Add(msg);
        p.Children.Add(Card(form));
    }
}
