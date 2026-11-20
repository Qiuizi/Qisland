using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 停靠位置和极简模式：
/// · 顶部居中（默认）/ 左侧 / 右侧：侧边时收起是贴边的竖向胶囊（时 / 分两行 + 天气），展开面板从边缘滑出，弹簧和顶部一样；
/// · 极简：收起时只剩一小段线（顶部横线、侧边竖线），指针靠近先胀大，再停一下照常展开；有活动、放音乐时照常显示胶囊。
/// 思路参考小糖岛仔（MIT）的 side_dock / minimal，WPF 重写。
/// </summary>
public sealed partial class IslandWindow
{
    internal static DockSide Dock { get; private set; } = DockSide.Top;
    internal static bool Minimal { get; private set; }
    private static bool Side => Dock != DockSide.Top;

    private readonly TranslateTransform _dockShift = new();
    private readonly Grid _idleV = new() { Width = IslandLayout.SideW, Height = IslandLayout.SideH };
    private readonly TextBlock _vHour = Txt(15, FontWeights.SemiBold, Theme.Primary), _vMin = Txt(15, FontWeights.SemiBold, Theme.Primary);
    private readonly TextBlock _vGlyph = WxGlyph("", 12, Theme.Secondary), _vTemp = Txt(11.5, FontWeights.Medium, Theme.Secondary);

    internal static void LoadLayout((DockSide Dock, bool Minimal) pref) { Dock = pref.Dock; Minimal = pref.Minimal; }

    private (double X, double Y) Origin(double w, double h) => IslandLayout.Origin(Dock, WinW, WinH, TopGap, w, h);

    private (double W, double H, double R) RestSize() => IslandLayout.Rest(Dock, Minimal, IdleW, IdleH);

    private FrameworkElement IdleLayer => Side ? _idleV : _idle;

    private void BuildSideIdle()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var t in new[] { _vHour, _vMin, _vTemp }) { t.HorizontalAlignment = HorizontalAlignment.Center; t.FontFamily = Theme.Display; }
        _vMin.Margin = new Thickness(0, -4, 0, 0);
        _vGlyph.HorizontalAlignment = HorizontalAlignment.Center; _vGlyph.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(_vHour); sp.Children.Add(_vMin); sp.Children.Add(_vGlyph); sp.Children.Add(_vTemp);
        _idleV.Children.Add(sp);
        Canvas.SetTop(_idleV, TopGap);
        Place(_idleV, IslandLayout.SideW);
        _idleV.RenderTransformOrigin = new Point(0.5, 0);
        _idleV.RenderTransform = new ScaleTransform(1, 1);
        _idleV.Opacity = 0; _idleV.Visibility = Visibility.Hidden;
        _content.Children.Add(_idleV);
        _content.RenderTransform = _dockShift;
    }

    private void RebuildSideIdle()
    {
        var parts = _clockText.Split(':');
        _vHour.Text = parts.Length == 2 ? parts[0] : _clockText;
        _vMin.Text = parts.Length == 2 ? parts[1] : "";
        _vGlyph.Text = _idleWeatherGlyph.Text;
        _vTemp.Text = _weather != null ? _idleTemp.Text : "";
        _vHour.Foreground = _vMin.Foreground = Theme.Primary;
        _vGlyph.Foreground = _vTemp.Foreground = Theme.Secondary;
    }

    /// <summary>换停靠位置 / 极简：存盘、挪窗口、弹簧过去（不重启）。</summary>
    public void ApplyLayout(DockSide dock, bool minimal)
    {
        if (dock == Dock && minimal == Minimal) return;
        var sideChanged = (dock != DockSide.Top) != Side || dock != Dock;
        Dock = dock; Minimal = minimal;
        App.SaveLayout(dock, minimal);
        App.Log($"停靠：{dock switch { DockSide.Left => "左侧", DockSide.Right => "右侧", _ => "顶部" }}{(minimal ? " · 极简" : "")}");
        foreach (var l in new FrameworkElement[] { _idle, _idleV }) if (l != IdleLayer) { l.Opacity = 0; l.Visibility = Visibility.Hidden; }
        if (sideChanged)
        {
            if (!SnapshotMode && !_layered) Height = WinH;   // 顶部收起时窗口高度会被收到岛那么高，侧边要整高
            _lastRgn = (-1, -1, -1, -1, -1, -1);
            PlaceOnScreen();
        }
        RebuildIdle();
        SetMode(_mode);
        Kick();
    }

    /// <summary>侧边停靠时「贴边呼出」：指针顶到屏幕左 / 右边、在岛的竖直范围里。</summary>
    private bool OnSideEdge(Native.POINT p, double pillTop, double h, DpiScale dpi)
    {
        if (!Side) return false;
        var m = _monRect;
        var top = (int)Math.Round((Top + pillTop - EdgeMarginDip) * dpi.DpiScaleY);
        var bottom = (int)Math.Round((Top + pillTop + h + EdgeMarginDip) * dpi.DpiScaleY);
        if (p.Y < top || p.Y > bottom) return false;
        return Dock == DockSide.Left ? p.X <= m.Left + 1 : p.X >= m.Right - 2;
    }
}
