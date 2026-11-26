using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>4.3 设置「设备与状态」页：无线外设电量。</summary>
public sealed partial class SettingsWindow
{
    // ───────────── 设备与状态 ─────────────

    private void DevicesStatus(StackPanel p)
    {
        var dev = _app.DeviceService;
        if (dev != null) RefreshOn("设备与状态", h => dev.Changed += h, h => dev.Changed -= h);
        p.Children.Add(Section("无线外设电量"));
        var a = new StackPanel();
        a.Children.Add(FeatureRow(Features.Peripherals, "读取外设电量", "已连接的蓝牙鼠标、键盘、手柄、触控笔、耳机，读到电量就在组件页「设备电量」里显示；读不到电量的设备不显示"));
        a.Children.Add(FeatureRow(Features.PeripheralAlerts, "低电量提醒", "掉到 20% 和 10% 各提醒一次，充上电后重新计；耳机的提醒在「岛上提示 → 蓝牙耳机」里"));
        if (Features.On(Features.Peripherals))
        {
            var list = dev?.Devices ?? [];
            if (list.Count == 0)
                a.Children.Add(Note(dev?.LastScan == DateTime.MinValue || dev == null ? "正在扫描…" : "现在没有读到电量的已连接设备（有的设备不向系统报电量）"));
            foreach (var d in list)
            {
                var pct = DialogKit.Text(Peripherals.Percent(d.Battery), 13, FontWeights.SemiBold, d.Battery <= Peripherals.Low ? Theme.Orange : DialogKit.Fg);
                a.Children.Add(DialogKit.Row(d.Name, Peripherals.KindName(d.Kind) + " · " + (d.Via == "BLE" ? "BLE 电池服务" : d.Via == "手柄" ? "Xbox 手柄" : "系统蓝牙电量"), pct));
            }
            var rescan = DialogKit.Button("重新扫描", () => { dev?.Scan(); RenderSoon(); });
            rescan.HorizontalAlignment = HorizontalAlignment.Left; rescan.Margin = new Thickness(0, 0, 0, 10);
            a.Children.Add(rescan);
        }
        p.Children.Add(Card(a));

        CapsuleSection(p);
    }
}
