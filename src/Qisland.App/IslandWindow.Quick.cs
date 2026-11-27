using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Qisland.Core;
using ActivityKind = Qisland.Core.ActivityKind;

namespace Qisland.App;

/// <summary>
/// 5.0 自定义快捷按钮：组件页里一张紧凑的按钮卡（一排最多 4 个），一个都没配时整张卡不出现。
/// 动作只有四种：开网页（http/https）、打开本机程序 / 文件 / 文件夹（先查路径存在，直接交给系统打开、不拼命令行）、
/// 在岛上弹一条提醒、向本机回环地址发一个请求。点之前都会再校验一遍。
/// </summary>
public sealed partial class IslandWindow
{
    private static readonly HttpClient QuickHttp = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };
    private bool _quickHooked;

    private FrameworkElement QuickWidget()
    {
        if (!_quickHooked)
        {
            _quickHooked = true;
            QuickStore.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                _pageCache.Remove(Page.Widgets);
                if (_mode == Mode.Expanded && _page == Page.Widgets && !_animating && !_widgetEdit) RebuildExpanded(); else _expDirty = true;
            });
        }
        var list = QuickStore.Current;
        var sp = new StackPanel();
        sp.Children.Add(WidgetHead(Def("quick"), list.Count + " 个", null));
        var cols = Math.Clamp(list.Count, 1, 4);
        var grid = new UniformGrid { Columns = cols, Margin = new Thickness(-4, 0, -4, 0) };
        foreach (var a in list) grid.Children.Add(QuickButton(a));
        sp.Children.Add(grid);
        return WCard(sp);
    }

    private FrameworkElement QuickButton(QuickAction a)
    {
        var icon = new TextBlock
        {
            Text = a.Icon, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe Fluent Icons"), FontSize = 19,
            Foreground = Theme.Primary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 3),
        };
        var title = Txt(11.5, FontWeights.Medium, Theme.Secondary);
        title.Text = a.Title; title.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon); stack.Children.Add(title);
        var check = Glyph("\uE73E", 15, Theme.Blue);
        check.HorizontalAlignment = HorizontalAlignment.Center; check.VerticalAlignment = VerticalAlignment.Center; check.Opacity = 0;
        var host = new Grid();
        host.Children.Add(stack); host.Children.Add(check);
        var b = new Border
        {
            Height = 62, Margin = new Thickness(4, 0, 4, 8), CornerRadius = new CornerRadius(Math.Max(10, Theme.CardRadius - 8)),
            Background = Theme.Fill, Child = host, Cursor = Cursors.Hand, ToolTip = Tip(a),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        Theme.ChipSurface(b);
        b.MouseEnter += (_, _) => b.Background = Theme.CardHover;
        b.MouseLeave += (_, _) => { b.Background = Theme.Fill; Press(b, 1); };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; Press(b, 0.94); };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Press(b, 1);
            if (RunQuick(a))
            {
                // 成功：内容淡出、对勾淡入再还原，告诉你「点到了」
                var dur = TimeSpan.FromMilliseconds(ReducedMotion ? 1 : 160);
                stack.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.15, dur) { AutoReverse = true, BeginTime = TimeSpan.Zero, FillBehavior = FillBehavior.Stop });
                check.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
                {
                    KeyFrames = { new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(dur)), new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(dur * 4)), new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(dur * 5)) },
                });
            }
        };
        return b;
    }

    private static void Press(Border b, double to)
    {
        var st = (ScaleTransform)b.RenderTransform;
        if (ReducedMotion) { st.ScaleX = st.ScaleY = to; return; }
        var anim = SpringEase.Animation(to, 0.28, 0.7);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, SpringEase.Animation(to, 0.28, 0.7));
    }

    private static string Tip(QuickAction a) => a.Kind switch
    {
        QuickKind.Url => "打开 " + a.Target,
        QuickKind.Open => "打开 " + Path.GetFileName(a.Target.TrimEnd('\\', '/')),
        QuickKind.Notify => "在岛上提醒：" + a.Target,
        _ => "发送到 " + a.Target,
    };

    private static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>执行一个快捷按钮。返回 true 表示已经开始执行（网络请求的结果稍后在岛上提示）。</summary>
    private bool RunQuick(QuickAction a)
    {
        if (QuickActions.Validate(a, a.Kind == QuickKind.Open ? PathExists : null) is { } err) { QuickNotice(a.Title, err, error: true); return false; }
        try
        {
            switch (a.Kind)
            {
                case QuickKind.Url:
                    Process.Start(new ProcessStartInfo(QuickActions.SafeWebUrl(a.Target)!.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                    return true;
                case QuickKind.Open:
                {
                    var path = a.Target.Trim();
                    var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                    // 直接交给系统打开：FileName 就是这个路径本身，没有参数，不经过 cmd
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = dir ?? "" })?.Dispose();
                    return true;
                }
                case QuickKind.Notify:
                    _store.Upsert(new IslandActivity("quick:notify", "quick", a.Title, a.Target, ActivityKind.Info, 30, DurationSeconds: 8, ResetTtl: true, Replace: true));
                    return true;
                case QuickKind.Local:
                    _ = SendLocal(a);
                    return true;
            }
        }
        catch (Exception ex) { App.Log("快捷按钮「" + a.Title + "」: " + ex.Message); QuickNotice(a.Title, "没能执行：" + ex.Message, error: true); }
        return false;
    }

    private async Task SendLocal(QuickAction a)
    {
        var uri = QuickActions.SafeLoopbackUrl(a.Target)!;
        try
        {
            using var req = new HttpRequestMessage(a.Body.Length > 0 ? HttpMethod.Post : HttpMethod.Get, uri);
            if (a.Body.Length > 0) req.Content = new StringContent(a.Body, Encoding.UTF8, a.Body.TrimStart().StartsWith('{') || a.Body.TrimStart().StartsWith('[') ? "application/json" : "text/plain");
            // 发给 Qisland 自己的推送 API 时自动带上本机 Token（设了的话）；发给别的本机服务不带
            if (uri.Port == App.Port && ApiTokenFile.Read(App.DataDir) is { } tok) req.Headers.TryAddWithoutValidation("X-Qisland-Token", tok);
            using var res = await QuickHttp.SendAsync(req).ConfigureAwait(true);
            if (!res.IsSuccessStatusCode) QuickNotice(a.Title, $"本机接口返回 {(int)res.StatusCode}", error: true);
        }
        catch (Exception ex) { App.Log("快捷按钮「" + a.Title + "」请求失败: " + ex.GetBaseException().Message); QuickNotice(a.Title, "本机接口没有响应", error: true); }
    }

    private void QuickNotice(string title, string body, bool error) =>
        _store.Upsert(new IslandActivity("quick:result", "quick", title, body, error ? ActivityKind.Warning : ActivityKind.Done, 30, DurationSeconds: 5, ResetTtl: true, Replace: true));
}
