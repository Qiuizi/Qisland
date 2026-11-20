using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 健康组件：今天喝了多少水（圆环 + 按小时的柱子）、一键记一杯 / 撤销、连续用电脑多久。
/// 只在数据变化或每分钟一次时改文字和一个依赖属性，没有动画循环。
/// </summary>
public sealed partial class IslandWindow
{
    private (RingArc ring, TextBlock total, HourBars bars, TextBlock sub, TextBlock sit)? _healthT;
    private static readonly Brush WaterBlue = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x32, 0xAD, 0xE6)));

    private Border HealthWidget()
    {
        var card = WCard(null);
        var sp = new StackPanel();
        var water = Features.On(Features.Water);
        var sit = Features.On(Features.Sit);
        var tip = (water, sit) switch { (true, true) => "喝水 · 久坐提醒开", (true, false) => "喝水提醒开", (false, true) => "久坐提醒开", _ => "提醒关（设置里开）" };
        sp.Children.Add(WidgetHead(Def("health"), tip, null));

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var ringHost = new Grid { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Left };
        var ring = new RingArc { Thickness = 5, Gradient = (Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x0A, 0x84, 0xFF)), Track = Theme.Fill };
        ringHost.Children.Add(ring);
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var total = Txt(13, FontWeights.Bold, Theme.Primary); total.HorizontalAlignment = HorizontalAlignment.Center;
        var unit = Txt(9, FontWeights.Medium, Theme.Tertiary); unit.Text = "ml"; unit.HorizontalAlignment = HorizontalAlignment.Center; unit.Margin = new Thickness(0, -3, 0, 0);
        center.Children.Add(total); center.Children.Add(unit);
        ringHost.Children.Add(center);
        g.Children.Add(ringHost);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
        var bars = new HourBars { Height = 30, Fill = WaterBlue, Track = Theme.Fill, From = App.Health.StartHour, To = App.Health.EndHour };
        mid.Children.Add(bars);
        var sub = Txt(10.5, FontWeights.Medium, Theme.Tertiary); sub.Margin = new Thickness(0, 4, 0, 0);
        mid.Children.Add(sub);
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        var btns = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Border Btn(string text, Brush fg, Action act)
        {
            var t = Txt(11.5, FontWeights.SemiBold, fg); t.Text = text; t.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border { Background = Theme.Fill, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 5), Cursor = Cursors.Hand, Child = t };
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
            Pressable(b);
            return b;
        }
        btns.Children.Add(Btn($"＋ {App.Health.ServingMl} ml", Theme.Blue, () => { App.DrinkWater(App.Health.ServingMl); _store.Remove("health:water"); OnHealthChanged(); }));
        btns.Children.Add(Btn("撤销", Theme.Secondary, () => { if (App.Water.TotalMl > 0) { App.DrinkWater(-Math.Min(App.Health.ServingMl, App.Water.TotalMl)); OnHealthChanged(); } }));
        Grid.SetColumn(btns, 2);
        g.Children.Add(btns);
        sp.Children.Add(g);

        var sitT = Txt(11, FontWeights.Medium, Theme.Secondary); sitT.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(sitT);
        SetBody(card, sp);
        _healthT = (ring, total, bars, sub, sitT);
        OnHealthChanged();
        return card;
    }

    /// <summary>喝水数据或时间变了：只改文字和圆环。</summary>
    public void OnHealthChanged()
    {
        if (_healthT is not { } h) return;
        var w = App.Water;
        w.Roll(DateTime.Now);
        var s = App.Health;
        h.ring.AnimateTo(w.Progress(s.GoalMl));
        h.total.Text = w.TotalMl.ToString(CultureInfo.InvariantCulture);
        h.bars.Values = (int[])w.Hours.Clone();
        var cups = (int)Math.Ceiling(Math.Max(0, s.GoalMl - w.TotalMl) / (double)s.ServingMl);
        h.sub.Text = (w.TotalMl >= s.GoalMl ? "今天喝够了" : $"目标 {s.GoalMl} ml · 还差 {cups} 杯")
                     + (w.LastDrink > DateTime.Today ? $" · 上一杯 {w.LastDrink:HH:mm}" : "");
        var mins = (int)(DateTime.Now - ((Application.Current as App)?.ActiveSince ?? DateTime.Now)).TotalMinutes;
        h.sit.Text = mins < 1 ? "刚休息过" : mins >= s.SitMinutes ? $"已经连续用电脑 {mins} 分钟，起来活动一下吧" : $"已经连续用电脑 {mins} 分钟";
        h.sit.Foreground = mins >= s.SitMinutes ? Theme.Orange : Theme.Secondary;
    }
}

/// <summary>按小时的小柱状图（喝水量）。</summary>
public sealed class HourBars : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(int[]), typeof(HourBars),
        new FrameworkPropertyMetadata(Array.Empty<int>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public int[] Values { get => (int[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Fill { get; set; } = Brushes.DeepSkyBlue;
    public Brush? Track { get; set; }
    public int From { get; set; } = 9;
    public int To { get; set; } = 22;

    protected override void OnRender(DrawingContext dc)
    {
        var n = Math.Max(1, To - From);
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var v = Values;
        var max = Math.Max(250, v.Length == 0 ? 0 : v.Max());
        var slot = w / n; var bw = Math.Max(2, slot * 0.58);
        var nowH = DateTime.Now.Hour;
        for (var i = 0; i < n; i++)
        {
            var hr = From + i;
            var x = i * slot + (slot - bw) / 2;
            if (Track != null) dc.DrawRoundedRectangle(Track, null, new Rect(x, h - 3, bw, 3), 1.5, 1.5);
            var val = hr < v.Length ? v[hr] : 0;
            if (val <= 0) continue;
            var bh = Math.Max(3, h * Math.Min(1, val / (double)max));
            dc.PushOpacity(hr == nowH ? 1 : 0.75);
            dc.DrawRoundedRectangle(Fill, null, new Rect(x, h - bh, bw, bh), Math.Min(2, bw / 2), Math.Min(2, bw / 2));
            dc.Pop();
        }
    }
}
