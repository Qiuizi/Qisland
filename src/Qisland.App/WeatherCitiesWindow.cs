using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 天气城市：搜索（Open-Meteo 地理编码）、添加（最多 5 个）、删除、切换。只存在本机 %APPDATA%\Qisland\weather-cities.json。
/// 思路参考小糖岛仔（MIT）的多城市天气，C# 重写。
/// </summary>
public sealed class WeatherCitiesWindow : Window
{
    private readonly App _app;
    private readonly TextBox _box;
    private readonly TextBlock _status;
    private readonly StackPanel _saved = new();
    private readonly StackPanel _results = new();
    private readonly Brush _fg, _sub, _row, _hover;
    private CancellationTokenSource? _cts;

    public WeatherCitiesWindow(App app)
    {
        _app = app;
        Title = "天气城市";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = true;
        var dark = Theme.Current != Theme.Look.Light;
        _fg = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        _sub = new SolidColorBrush(dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0, 0, 0));
        _row = new SolidColorBrush(dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0, 0, 0));
        _hover = new SolidColorBrush(dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0));
        var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 380 };
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "天气城市", FontFamily = Theme.Text, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = _fg });
        var close = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = _sub, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        close.MouseLeftButtonUp += (_, _) => Close();
        head.Children.Add(close);
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock
        {
            Text = $"最多保存 {OpenMeteo.MaxCities} 个城市，岛上天气卡片点地名也能切换。城市只存在本机。数据来自 Open-Meteo。",
            FontFamily = Theme.Text, FontSize = 12, Foreground = _sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
        });
        sp.Children.Add(_saved);
        var search = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        search.ColumnDefinitions.Add(new ColumnDefinition());
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _box = DialogKit.Box();
        _box.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Search(); } };
        search.Children.Add(_box);
        var go = DialogKit.Button("搜索", () => _ = Search(), primary: true);
        go.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(go, 1);
        search.Children.Add(go);
        sp.Children.Add(search);
        _status = new TextBlock { FontFamily = Theme.Text, FontSize = 12, Foreground = _sub, Margin = new Thickness(0, 6, 0, 4), TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(_status);
        sp.Children.Add(_results);
        Content = new Border
        {
            Margin = new Thickness(20), CornerRadius = new CornerRadius(18), Child = sp,
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF7, 0xF7, 0xF8)),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0, 0, 0)), BorderThickness = new Thickness(1),
        };
        DialogKit.DragFromBlank(this);   // 以前按在结果行上也整窗拖动，MouseUp 被吞掉，点了没反应
        Loaded += (_, _) => { Activate(); _box.Focus(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => _cts?.Cancel();
        RenderSaved();
    }

    private Border Row(string title, string sub, bool active, (string Glyph, string Tip, Action Do)? action, Action? click)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = (active ? "✓ " : "") + title, FontFamily = Theme.Text, FontSize = 13, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, Foreground = _fg });
        if (sub.Length > 0) t.Children.Add(new TextBlock { Text = sub, FontFamily = Theme.Text, FontSize = 11, Foreground = _sub, TextTrimming = TextTrimming.CharacterEllipsis });
        g.Children.Add(t);
        if (action is { } a)
        {
            var b = new TextBlock { Text = a.Glyph, ToolTip = a.Tip, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = _sub, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Margin = new Thickness(10, 0, 2, 0) };
            b.MouseLeftButtonUp += (_, e) => { a.Do(); e.Handled = true; };
            Grid.SetColumn(b, 1);
            g.Children.Add(b);
        }
        var row = new Border { Background = _row, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4), Child = g, Cursor = click != null ? Cursors.Hand : null };
        if (click != null)
        {
            row.MouseEnter += (_, _) => row.Background = _hover;
            row.MouseLeave += (_, _) => row.Background = _row;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        }
        return row;
    }

    private void RenderSaved()
    {
        _saved.Children.Clear();
        var list = App.WeatherCityList;
        _saved.Children.Add(Row("自动定位", "按 weather.txt 或网络位置", list.Current == null, null, () => { _app.SwitchWeatherCity(-1); RenderSaved(); }));
        for (var i = 0; i < list.Saved.Count; i++)
        {
            var idx = i;
            var c = list.Saved[i];
            _saved.Children.Add(Row(c.Label, c.Region, list.Active == i,
                ("\uE74D", "删除", () => { var was = list.Active == idx; App.RemoveWeatherCity(idx); if (was) _app.SwitchWeatherCity(-1); RenderSaved(); }),
                () => { _app.SwitchWeatherCity(idx); RenderSaved(); }));
        }
    }

    private async Task Search()
    {
        var q = _box.Text.Trim();
        if (q.Length == 0) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = _cts.Token;
        _results.Children.Clear();
        _status.Text = "正在搜索…";
        try
        {
            var found = await App.SearchCities(q, ct);
            if (ct.IsCancellationRequested) return;
            _status.Text = found.Count == 0 ? "没找到，换个写法试试（如「北京」「Beijing」）"
                : CitySearch.ProvinceCities(q).Count > 1 ? $"{CitySearch.Province(q)}的省会和主要城市排在最前，点一个添加并切换：" : "点一个添加并切换：";
            foreach (var c in found.Take(8))
            {
                var city = c;
                _results.Children.Add(Row(c.Label, c.Region, false, null, () =>
                {
                    var at = App.AddWeatherCity(city);
                    if (at < 0) { _status.Text = $"最多 {OpenMeteo.MaxCities} 个，先删一个"; return; }
                    _app.SwitchWeatherCity(at);
                    _results.Children.Clear();
                    _status.Text = "已添加并切换";
                    _box.Clear();
                    RenderSaved();
                }));
            }
        }
        catch (OperationCanceledException) { _status.Text = "搜索超时"; }
        catch (Exception ex) { _status.Text = "搜索失败：" + ex.Message; }
    }
}
