using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 4.3：组件页「设备电量」：已连接、能读到电量的无线外设。读不到电量的设备不显示。
/// </summary>
public sealed partial class IslandWindow
{
    public PeripheralWatcher? Devices { get; set; }

    // ───────────── 组件页：设备电量 ─────────────

    private static string DeviceGlyph(PeripheralKind k) => k switch
    {
        PeripheralKind.Mouse => "\uE962",
        PeripheralKind.Keyboard => "\uE765",
        PeripheralKind.Headset => "\uE7F6",
        PeripheralKind.Gamepad => "\uE7FC",
        PeripheralKind.Pen => "\uEDC6",
        PeripheralKind.Phone => "\uE8EA",
        _ => "\uE702",
    };

    private bool HasDevices => Features.On(Features.Peripherals) && Devices is { Devices.Count: > 0 };

    private Border DevicesWidget()
    {
        var list = Devices?.Devices ?? [];
        var card = WCard(null);
        var sp = new StackPanel();
        var shown = list.Take(4).ToList();
        sp.Children.Add(WidgetHead(Def("devices"), list.Count > shown.Count ? $"{list.Count} 个 · 低电量在前" : list.Any(d => d.Battery <= Peripherals.Low) ? "有设备该充电了" : $"{list.Count} 个设备", null));
        foreach (var d in shown)
        {
            var row = new Grid { Height = 30, Background = Brushes.Transparent, ToolTip = $"{d.Name} · {Peripherals.KindName(d.Kind)} · {d.Battery}%" };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var gl = Glyph(DeviceGlyph(d.Kind), 11, Theme.Secondary); gl.HorizontalAlignment = HorizontalAlignment.Center; gl.VerticalAlignment = VerticalAlignment.Center;
            ic.Child = gl;
            row.Children.Add(ic);
            var nm = Txt(12.5, FontWeights.SemiBold, Theme.Primary); nm.Text = Peripherals.ShortName(d.Name); nm.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(nm, 1); row.Children.Add(nm);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(BatteryPip(d.Battery));
            var pct = Txt(12, FontWeights.SemiBold, d.Battery <= Peripherals.VeryLow ? Theme.Red : d.Battery <= Peripherals.Low ? Theme.Orange : Theme.Secondary);
            pct.Text = Peripherals.Percent(d.Battery); pct.Width = 36; pct.TextAlignment = TextAlignment.Right; pct.VerticalAlignment = VerticalAlignment.Center;
            pct.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
            right.Children.Add(pct);
            Grid.SetColumn(right, 2); row.Children.Add(right);
            sp.Children.Add(row);
        }
        SetBody(card, sp);
        return card;
    }

    /// <summary>小电池：圆角外框 + 按电量填充（低电量橙 / 红），右边一个小正极。</summary>
    private static FrameworkElement BatteryPip(int pct)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var body = new Border { Width = 24, Height = 11, CornerRadius = new CornerRadius(3.2), BorderThickness = new Thickness(1), BorderBrush = Theme.Tertiary, Padding = new Thickness(1.5) };
        var color = pct <= Peripherals.VeryLow ? Theme.Red : pct <= Peripherals.Low ? Theme.Orange : Theme.Green;
        body.Child = new Border { Width = Math.Max(2, 19 * Math.Clamp(pct, 0, 100) / 100.0), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.6), Background = color };
        host.Children.Add(body);
        host.Children.Add(new Border { Width = 1.6, Height = 4, Background = Theme.Tertiary, CornerRadius = new CornerRadius(0, 1, 1, 0), Margin = new Thickness(0.6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return host;
    }

    public void OnDevicesChanged()
    {
        _pageCache.Remove(Page.Widgets);
        if (_mode == Mode.Expanded && _page == Page.Widgets && !_animating) RebuildExpanded(); else _expDirty = true;
    }
}
