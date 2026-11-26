using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>4.3 设置「AI 额度」页，以及 4.3 几页共用的小件。</summary>
public sealed partial class SettingsWindow
{
    private readonly HashSet<string> _watched = [];

    /// <summary>后台状态变了（额度读到了、邮箱连上了、扫到设备了）：正开着对应那一页就刷新；窗口关了就退订。</summary>
    private void RefreshOn(string cat, Action<Action> add, Action<Action> remove)
    {
        if (!_watched.Add(cat)) return;
        Action h = () => Dispatcher.BeginInvoke(() => { if (IsLoaded && _cats[_cat].Name == cat) RenderSoon(); });
        add(h);
        Closed += (_, _) => remove(h);
    }

    private static TextBlock Note(string s)
    {
        var t = DialogKit.Text(s, 11.5, FontWeights.Normal, DialogKit.Sub);
        t.Margin = new Thickness(0, 0, 0, 10);
        return t;
    }

    private static TextBlock StatusLine(string s, bool good)
    {
        var t = DialogKit.Text(s, 12, FontWeights.SemiBold, good ? Theme.Green : DialogKit.Sub);
        t.Margin = new Thickness(0, 0, 0, 10);
        return t;
    }

    // ───────────── AI 额度 ─────────────

    private void AiQuota(StackPanel p)
    {
        var svc = _app.UsageService;
        if (svc != null) RefreshOn("AI 额度", h => svc.Changed += h, h => svc.Changed -= h);
        var on = Features.On(Features.AiUsage);
        var top = new StackPanel();
        top.Children.Add(DialogKit.Row("读取本机 AI 编程工具额度",
            "只读打开 Claude Code / Cursor / Gemini CLI / Kimi Code 登录后存在本机的令牌，去它们自己的用量接口查百分比，约 10 分钟一次",
            DialogKit.Switch(on, v => { Features.Set(Features.AiUsage, v); if (v) svc?.RefreshNow(); RenderSoon(); })));
        top.Children.Add(Note("不改、不锁它们的文件，不刷新令牌（令牌过期就等你下次用那个工具时它自己续上），令牌和接口原文不写日志、不落盘。这些用量接口没有公开文档，改版后可能暂时读不到；读不到就不显示，不估算。"));
        p.Children.Add(Card(top));

        if (on)
        {
            p.Children.Add(Section("工具"));
            var list = new StackPanel();
            foreach (var tool in AiUsage.Tools)
            {
                var t = tool;
                var st = svc?.Get(t);
                var desc = !AiUsageService.ToolOn(t) ? "已关闭" : st == null ? "" : st.State switch
                {
                    AiUsageService.State.Ok => st.Message + $"（{st.At:HH:mm} 读取）",
                    AiUsageService.State.Off => "正在读取…",
                    _ => st.Message,
                };
                var row = DialogKit.Row(AiUsage.Name(t), desc, DialogKit.Switch(AiUsageService.ToolOn(t), v => { Features.Set("ai-usage-" + t, v); if (v) svc?.RefreshNow(); RenderSoon(); }));
                if (st?.State == AiUsageService.State.Ok && row.Children[0] is StackPanel left && left.Children.Count > 1 && left.Children[1] is TextBlock d) d.Foreground = Theme.Green;
                list.Children.Add(row);
            }
            var refresh = DialogKit.Button("立即刷新", () => { svc?.RefreshNow(); RenderSoon(); });
            refresh.HorizontalAlignment = HorizontalAlignment.Left; refresh.Margin = new Thickness(0, 0, 0, 10);
            list.Children.Add(refresh);
            p.Children.Add(Card(list));
        }

        p.Children.Add(Section("提醒与显示"));
        var more = new StackPanel();
        more.Children.Add(FeatureRow(Features.QuotaAlerts, "用到 70% / 90% 时提醒", "Codex、豆包和上面这些工具，任何一个额度窗口过 70% 和 90% 各提醒一次；同一个重置周期里不重复，重启也不重复"));
        more.Children.Add(DialogKit.Row("收起时显示额度小环", "岛收起时天气左边一个小圆环，表示最紧的那个额度；过 70% 变黄、过 90% 变红",
            DialogKit.Switch(Features.On(Features.QuotaCapsule), v => Features.Set(Features.QuotaCapsule, v))));
        p.Children.Add(Card(more));
    }
}
