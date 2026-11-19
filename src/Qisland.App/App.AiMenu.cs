using System.Windows;
using Qisland.Core;
using Forms = System.Windows.Forms;

namespace Qisland.App;

// 托盘里的「翻译与 AI」和「接入其他 AI 工具」
public sealed partial class App
{
    private Forms.ToolStripMenuItem AiMenu()
    {
        var m = new Forms.ToolStripMenuItem("翻译与 AI");
        var on = new Forms.ToolStripMenuItem("划词翻译 / 问 AI 快捷键") { CheckOnClick = true, Checked = _ai.TranslateOn };
        on.CheckedChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _ai.TranslateOn = on.Checked; _ai.Save();
            var failed = _window?.ApplyAiHotkeys() ?? [];
            if (failed.Count > 0) MessageBox.Show($"快捷键 {string.Join("、", failed)} 被别的程序占用了，去「翻译与 AI 设置」里换一个。", "Qisland");
        });
        var settings = new Forms.ToolStripMenuItem("翻译与 AI 设置…");
        settings.Click += (_, _) => Dispatcher.BeginInvoke(OpenAiSettings);
        var doubao = new Forms.ToolStripMenuItem("豆包额度：连接…") { ToolTipText = "非官方接口：只读额度，约 25 分钟一次，Cookie 加密存在本机。默认关闭，你点了才连接" };
        doubao.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_doubao == null) return;
            if (_doubao.Connected && !_doubao.LoginExpired)
            {
                if (MessageBox.Show("断开豆包额度？会删除本机保存的 Cookie。", "Qisland", MessageBoxButton.OKCancel) == MessageBoxResult.OK) _doubao.Disconnect();
            }
            else new DoubaoConnectWindow(_doubao).Show();
        });
        var refresh = new Forms.ToolStripMenuItem("立即刷新额度和余额");
        refresh.Click += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            _quota?.Refresh();
            _doubao?.RefreshNow();
            if (_window != null && _ai.BalancesOn) { _window.Balances = await AiClient.Balances(CancellationToken.None); _window.OnAiQuotaChanged(); }
        });
        var bal = new Forms.ToolStripMenuItem("读取 DeepSeek / Kimi 余额") { CheckOnClick = true, Checked = _ai.BalancesOn, ToolTipText = "用你在设置里填的 Key 读官方余额接口，1 小时一次；没填 Key 的不读" };
        bal.CheckedChanged += (_, _) => { _ai.BalancesOn = bal.Checked; _ai.Save(); if (!bal.Checked && _window != null) { _window.Balances = []; _window.OnAiQuotaChanged(); } };
        m.DropDownItems.Add(on);
        m.DropDownItems.Add(settings);
        m.DropDownItems.Add(new Forms.ToolStripSeparator());
        m.DropDownItems.Add(doubao);
        m.DropDownItems.Add(bal);
        m.DropDownItems.Add(refresh);
        m.DropDownOpening += (_, _) =>
        {
            on.Text = $"划词翻译 / 问 AI（{_ai.TranslateHotkey} / {_ai.AskHotkey}）";
            doubao.Text = _doubao switch
            {
                { Connected: true, LoginExpired: true } => "豆包额度：登录失效，重新连接…",
                { Connected: true, Fresh: true } d => $"豆包额度：已连接 · {string.Join(" · ", d.Last!.Windows.Select(w => $"{w.Label} {w.UsedPercent:0}%"))}（点此断开）",
                { Connected: true } d => $"豆包额度：已连接{(d.Error != null ? "（" + d.Error + "）" : "")}（点此断开）",
                _ => "豆包额度：连接…（非官方接口）",
            };
        };
        return m;
    }

    private Forms.ToolStripMenuItem IntegrationsMenu()
    {
        var m = new Forms.ToolStripMenuItem("接入其他 AI 工具") { ToolTipText = "每一项都会先弹窗告诉你改哪个文件、改什么，你同意了才改；改前备份、记进改动账本、可还原" };
        m.DropDownItems.Add("…");
        m.DropDownOpening += (_, _) =>
        {
            m.DropDownItems.Clear();
            foreach (var t in HookPlans.All)
            {
                var installed = Integrations.Installed(t);
                var present = Integrations.AppPresent(t);
                var it = new Forms.ToolStripMenuItem(installed ? $"{t.App}：已接入（点此移除）" : present ? $"{t.App}：接入…" + (t.Experimental ? "（实验）" : "") : $"{t.App}：没检测到")
                {
                    Checked = installed, Enabled = installed || present, ToolTipText = t.Note,
                };
                it.Click += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (installed) { var r = Integrations.Remove(t, ask: true); if (r != null) MessageBox.Show(r, "Qisland"); }
                    else Integrations.Install(t);
                });
                m.DropDownItems.Add(it);
            }
            var kimi = new Forms.ToolStripMenuItem("Kimi CLI：手动接入说明…");
            kimi.Click += (_, _) => Dispatcher.BeginInvoke(() => MessageBox.Show(HookPlans.ManualHowTo(Environment.ProcessPath ?? "Qisland.exe"), "Qisland · Kimi CLI"));
            m.DropDownItems.Add(new Forms.ToolStripSeparator());
            m.DropDownItems.Add(kimi);
            m.DropDownItems.Add(new Forms.ToolStripMenuItem("Claude Code：只显示状态，岛上不审批（不改它的设置）") { Enabled = false });
        };
        return m;
    }
}
