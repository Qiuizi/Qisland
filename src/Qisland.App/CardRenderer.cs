using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 声明式卡片渲染：把 <see cref="CardView"/> 画成和岛上其他组件同一套风格的卡片内容（标题行 20 px 图标、12.5 半粗标题、
/// 大号数值用 Display 字体、徽章是带色的胶囊、按钮是浅底圆角块）。只画字面文字，不解析任何标记，不加载外部图片。
/// </summary>
internal static class CardRenderer
{
    private static readonly FontFamily Emoji = new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");

    public static Color ToneColor(string tone) => tone switch
    {
        "green" => Color.FromRgb(0x30, 0xD1, 0x58),
        "amber" => Color.FromRgb(0xFF, 0x9F, 0x0A),
        "red" => Color.FromRgb(0xFF, 0x45, 0x3A),
        "blue" => Color.FromRgb(0x0A, 0x84, 0xFF),
        "purple" => Color.FromRgb(0xBF, 0x5A, 0xF2),
        "teal" => Color.FromRgb(0x40, 0xC8, 0xE0),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    private static Color StatusColor(string s) => s switch
    {
        "ok" => Color.FromRgb(0x30, 0xD1, 0x58),
        "live" => Color.FromRgb(0x40, 0xC8, 0xE0),
        "warn" => Color.FromRgb(0xFF, 0x9F, 0x0A),
        "error" => Color.FromRgb(0xFF, 0x45, 0x3A),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    public static Color Accent(string hex, Color fallback)
    {
        if (hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return fallback;
    }

    private static Brush B(Color c, byte a = 0xFF) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }

    private static TextBlock T(string s, double size, FontWeight w, Brush fg, bool display = false) => new()
    {
        Text = s, FontSize = size, FontWeight = w, Foreground = fg, FontFamily = display ? Theme.Display : Theme.Text,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>插件图标：强调色渐变圆角方块 + 一个字符（emoji / 符号 / 字母）。和内置组件图标同尺寸同圆角。</summary>
    public static Border Tile(string icon, string accent, double size)
    {
        var c = Accent(accent, Color.FromRgb(0x40, 0xC8, 0xE0));
        Color Lift(Color x, double k) => Color.FromRgb((byte)(x.R + (255 - x.R) * k), (byte)(x.G + (255 - x.G) * k), (byte)(x.B + (255 - x.B) * k));
        Color Sink(Color x, double k) => Color.FromRgb((byte)(x.R * (1 - k)), (byte)(x.G * (1 - k)), (byte)(x.B * (1 - k)));
        var bg = new LinearGradientBrush(Lift(c, 0.22), Sink(c, 0.12), 90); bg.Freeze();
        var t = new TextBlock
        {
            Text = string.IsNullOrEmpty(icon) ? "◆" : icon, FontFamily = Emoji, FontSize = size * 0.52, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -size * 0.03, 0, 0),
        };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.29), Background = bg, Child = t,
            BorderThickness = new Thickness(0.6), BorderBrush = B(Colors.White, 0x38), SnapsToDevicePixels = true,
        };
    }

    /// <summary>整张卡片的内容（外框由调用方给：岛上是 WCard，设置页预览是同色的圆角块）。</summary>
    public static FrameworkElement Build(CardView v, Action<CardAction>? act)
    {
        var sp = new StackPanel();
        var first = true;
        foreach (var n in v.Nodes)
        {
            FrameworkElement? el = n switch
            {
                HeaderNode h => Header(h, v),
                MetricNode m => Metric(m),
                ProgressNode p => Progress(p),
                SparkNode s => Spark(s),
                BadgeRowNode b => Badges(b),
                ActionRowNode a => Actions(a, act),
                _ => null,
            };
            if (el == null) continue;
            if (!first) el.Margin = new Thickness(0, n is HeaderNode ? 10 : 8, 0, 0);
            first = false;
            sp.Children.Add(el);
        }
        if (v.Notice is { Length: > 0 } notice)
        {
            var err = !v.Pending;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(new TextBlock { Text = err ? "\uE7BA" : "\uE916", FontFamily = Theme.Icons, FontSize = 11, Foreground = err ? Theme.Orange : Theme.Tertiary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
            row.Children.Add(T(notice, 11, FontWeights.Normal, Theme.Tertiary));
            sp.Children.Add(row);
        }
        return sp;
    }

    private static FrameworkElement Header(HeaderNode h, CardView v)
    {
        var g = new Grid { Height = 20 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = Tile(h.Icon, v.Accent, 20);
        tile.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(tile);
        var names = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = T(h.Title, 12.5, FontWeights.SemiBold, Theme.Primary);
        title.MaxWidth = 210;
        names.Children.Add(title);
        if (h.Subtitle.Length > 0 && h.Subtitle != "—")
        {
            var sub = T(h.Subtitle, 11, FontWeights.Medium, Theme.Tertiary);
            sub.Margin = new Thickness(7, 1, 0, 0); sub.MaxWidth = 170;
            names.Children.Add(sub);
        }
        Grid.SetColumn(names, 1);
        g.Children.Add(names);
        // 右侧：状态点（带一圈柔光）+ 更新时间
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (v.UpdatedAt is { } u && h.Status is not "idle")
        {
            var t = T(u.ToString("HH:mm", CultureInfo.InvariantCulture), 10.5, FontWeights.Medium, Theme.Tertiary);
            t.Margin = new Thickness(0, 0, 7, 0);
            right.Children.Add(t);
        }
        var sc = StatusColor(h.Status);
        var dot = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        dot.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = B(sc, 0x33) });
        dot.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = B(sc), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        dot.ToolTip = h.Status switch { "ok" => "数据正常", "live" => "本地实时计算", "warn" => "刷新失败，显示的是旧数据", "error" => "拿不到数据", _ => "等待数据" };
        right.Children.Add(dot);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        return g;
    }

    private static FrameworkElement Metric(MetricNode m)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var value = T(m.Value, 28, FontWeights.SemiBold, Theme.Primary, display: true);
        value.MaxWidth = 250;
        TextOptions.SetTextFormattingMode(value, TextFormattingMode.Ideal);
        line.Children.Add(value);
        if (m.Unit.Length > 0)
        {
            var unit = T(m.Unit, 12.5, FontWeights.Medium, Theme.Secondary);
            unit.VerticalAlignment = VerticalAlignment.Bottom; unit.Margin = new Thickness(5, 0, 0, 6);
            line.Children.Add(unit);
        }
        left.Children.Add(line);
        if (m.Label.Length > 0)
        {
            var label = T(m.Label, 11.5, FontWeights.Normal, Theme.Secondary);
            label.Margin = new Thickness(1, -1, 0, 0);
            left.Children.Add(label);
        }
        g.Children.Add(left);
        if (m.Delta.Length > 0)
        {
            var c = ToneColor(m.Tone);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 3, 8, 3.5), Background = B(c, 0x26),
                BorderBrush = B(c, 0x40), BorderThickness = new Thickness(0.8),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 7, 0, 0),
                Child = T((m.Tone == "green" ? "▲ " : m.Tone == "red" ? "▼ " : "") + m.Delta.TrimStart('+', '-', '−'), 11, FontWeights.SemiBold, B(c)),
            };
            Grid.SetColumn(pill, 1);
            g.Children.Add(pill);
        }
        return g;
    }

    private static FrameworkElement Progress(ProgressNode p)
    {
        var sp = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        head.Children.Add(T(p.Label, 11.5, FontWeights.Normal, Theme.Secondary));
        var right = T(p.Text, 11.5, FontWeights.SemiBold, Theme.Primary);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(right);
        sp.Children.Add(head);
        var c = ToneColor(p.Tone);
        var track = new Grid { Height = 6 };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Theme.Fill });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Theme.Freeze(new LinearGradientBrush(Color.FromArgb(0xC0, c.R, c.G, c.B), c, 0)),
        };
        track.Children.Add(fill);
        track.SizeChanged += (_, e) =>
        {
            var w = Math.Max(p.Fraction > 0 ? 6 : 0, e.NewSize.Width * p.Fraction);
            if (IslandWindow.ReducedMotion || fill.ActualWidth > 0) { fill.BeginAnimation(FrameworkElement.WidthProperty, null); fill.Width = w; return; }
            fill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(0, w, TimeSpan.FromMilliseconds(620)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        sp.Children.Add(track);
        return sp;
    }

    private static FrameworkElement Spark(SparkNode s)
    {
        var sp = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(T(s.Label, 11.5, FontWeights.Normal, Theme.Secondary));
        var cap = T(s.Caption, 11.5, FontWeights.SemiBold, Theme.Primary);
        cap.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(cap);
        sp.Children.Add(head);
        sp.Children.Add(new CardSpark { Points = s.Points, Color = ToneColor(s.Tone), Height = 40 });
        return sp;
    }

    private static FrameworkElement Badges(BadgeRowNode b)
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 0, -6, -6) };
        foreach (var x in b.Badges)
        {
            var neutral = x.Tone is "neutral" or "";
            var c = ToneColor(x.Tone);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 3.5), Margin = new Thickness(0, 0, 6, 6),
                Background = neutral ? Theme.Fill : B(c, 0x22),
                Child = T(x.Text, 11, FontWeights.Medium, neutral ? Theme.Secondary : B(c)),
            };
            ((TextBlock)pill.Child).MaxWidth = 200;
            wp.Children.Add(pill);
        }
        return wp;
    }

    private static FrameworkElement Actions(ActionRowNode a, Action<CardAction>? act)
    {
        var grid = new UniformGrid { Columns = a.Actions.Count, Rows = 1, Margin = new Thickness(-3, 0, -3, 0) };
        foreach (var x in a.Actions)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (x.Icon.Length > 0)
            {
                var ic = T(x.Icon, 12, FontWeights.SemiBold, Theme.Secondary);
                ic.FontFamily = Emoji; ic.Margin = new Thickness(0, 0, 5, 0);
                row.Children.Add(ic);
            }
            row.Children.Add(T(x.Label, 11.5, FontWeights.Medium, Theme.Primary));
            var enabled = x.Kind == "refresh" || x.Url != null;
            var btn = new Border
            {
                Height = 30, CornerRadius = new CornerRadius(10), Margin = new Thickness(3, 0, 3, 0), Background = Theme.Fill, Child = row,
                Cursor = enabled ? Cursors.Hand : Cursors.Arrow, Opacity = enabled ? 1 : 0.45,
                ToolTip = x.Kind == "refresh" ? "立即刷新这张卡片" : x.Url ?? "链接参数不完整",
            };
            if (enabled)
            {
                btn.MouseEnter += (_, _) => btn.Background = Theme.Hover;
                btn.MouseLeave += (_, _) => btn.Background = Theme.Fill;
                btn.MouseLeftButtonDown += (_, e) => { e.Handled = true; btn.Background = Theme.Press; };
                btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; btn.Background = Theme.Hover; act?.Invoke(x); };
            }
            grid.Children.Add(btn);
        }
        return grid;
    }

    /// <summary>按钮动作：只有「用默认浏览器开 https 链接」（开之前再查一遍）和「刷新」。</summary>
    public static void Open(CardAction a)
    {
        if (a.Kind != "open_url" || a.Url == null || !UrlPolicy.CheckOpen(a.Url, out var u)) return;
        try { Process.Start(new ProcessStartInfo(u!.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("插件链接: " + ex.Message); }
    }
}

/// <summary>迷你折线：平滑曲线 + 自上而下渐隐的面积 + 末端一个亮点。数据按最小 / 最大值归一化。</summary>
internal sealed class CardSpark : FrameworkElement
{
    public IReadOnlyList<double> Points { get; init; } = [];
    public Color Color { get; init; } = Colors.DeepSkyBlue;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var pts = Points;
        if (w < 10 || h < 10) return;
        if (pts.Count < 2)
        {
            var dash = new Pen(new SolidColorBrush(Color.FromArgb(0x50, Color.R, Color.G, Color.B)), 1.2) { DashStyle = new DashStyle([2, 3], 0) };
            dc.DrawLine(dash, new Point(0, h / 2), new Point(w, h / 2));
            return;
        }
        double min = pts.Min(), max = pts.Max();
        if (max - min < 1e-9) { min -= 1; max += 1; }
        const double padTop = 4, padBottom = 3;
        Point At(int i) => new(i * (w - 4) / (pts.Count - 1) + 2, padTop + (h - padTop - padBottom) * (1 - (pts[i] - min) / (max - min)));
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var c = line.Open())
        using (var a = area.Open())
        {
            var p0 = At(0);
            c.BeginFigure(p0, false, false);
            a.BeginFigure(new Point(p0.X, h), true, true);
            a.LineTo(p0, false, false);
            for (var i = 1; i < pts.Count; i++)
            {
                // 单调的三次贝塞尔：控制点取相邻两点水平中点，曲线不会冲出数据范围
                var prev = At(i - 1); var cur = At(i);
                var mx = (prev.X + cur.X) / 2;
                c.BezierTo(new Point(mx, prev.Y), new Point(mx, cur.Y), cur, true, true);
                a.BezierTo(new Point(mx, prev.Y), new Point(mx, cur.Y), cur, false, false);
            }
            a.LineTo(new Point(At(pts.Count - 1).X, h), false, false);
        }
        line.Freeze(); area.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, Color.R, Color.G, Color.B), Color.FromArgb(0x00, Color.R, Color.G, Color.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        var last = At(pts.Count - 1);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, Color.R, Color.G, Color.B)), null, last, 6, 6);
        dc.DrawEllipse(new SolidColorBrush(Color), new Pen(Brushes.White, 1.2), last, 3, 3);
    }
}
