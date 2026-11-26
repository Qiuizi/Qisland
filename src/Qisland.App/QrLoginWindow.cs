using QRCoder;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Qisland.App;

/// <summary>
/// 通用扫码登录小窗：显示二维码，等平台确认；二维码过期可点一下刷新。雨课堂、学习通共用。
/// issue() 返回二维码内容和一个等待函数（成功返回 true）。
/// </summary>
public sealed class QrLoginWindow : Window
{
    private readonly Func<Task<(ImageSource qr, Func<CancellationToken, Task<bool>> wait)>> _issue;
    private readonly Image _img = new() { Width = 200, Height = 200, Stretch = Stretch.Uniform };
    private readonly TextBlock _status;
    private readonly Border _qrBox;
    private CancellationTokenSource? _cts;
    private string _lastStatus = "";
    public bool Success { get; private set; }
    private readonly string _scanHint;
    public void SetStatus(string text)
    {
        _lastStatus = text;
        Dispatcher.BeginInvoke(() => _status.Text = text);
    }

    public QrLoginWindow(string title, string hint, string scanHint, Func<Task<(ImageSource, Func<CancellationToken, Task<bool>>)>> issue)
    {
        _issue = issue;
        _scanHint = scanHint;
        Title = title;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = true;

        var dark = Theme.Current != Theme.Look.Light;
        var fg = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        var sub = new SolidColorBrush(dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0, 0, 0));
        var sp = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };

        var head = new Grid();
        head.Children.Add(new TextBlock { Text = title, FontFamily = Theme.Text, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = fg });
        var close = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = sub, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        close.MouseLeftButtonUp += (_, _) => Close();
        head.Children.Add(close);
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = hint, FontFamily = Theme.Text, FontSize = 12.5, Foreground = sub, Margin = new Thickness(0, 4, 0, 16), TextWrapping = TextWrapping.Wrap, MaxWidth = 232 });

        _qrBox = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = _img, Cursor = Cursors.Hand };
        _qrBox.MouseLeftButtonUp += (_, _) => { if (_status!.Text.Contains("刷新") || _status.Text.Contains("登录未保存")) _ = Start(); };
        sp.Children.Add(_qrBox);
        _status = new TextBlock { Text = "正在获取二维码…", FontFamily = Theme.Text, FontSize = 12.5, Foreground = sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        sp.Children.Add(_status);
        sp.Children.Add(new TextBlock { Text = "只读取作业和截止时间 · 登录状态加密保存在本机", FontFamily = Theme.Text, FontSize = 11, Foreground = sub, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });

        Content = new Border
        {
            Margin = new Thickness(20), CornerRadius = new CornerRadius(22), Child = sp,
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF7, 0xF7, 0xF8)),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0, 0, 0)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.35, Color = Colors.Black },
        };
        DialogKit.DragFromBlank(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => _ = Start();
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task Start()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _lastStatus = "";
        _img.Opacity = 1;
        _status.Text = "正在获取二维码…";
        try
        {
            var (qr, wait) = await _issue();
            _img.Source = qr;
            _status.Text = _scanHint;
            var ok = await wait(ct);
            if (ct.IsCancellationRequested) return;
            if (ok)
            {
                Success = true;
                _status.Text = "登录成功";
                await Task.Delay(600);
                Close();
            }
            else if (_lastStatus.Contains("登录未保存"))
            {
                _img.Opacity = 0.25;
                _status.Text = _lastStatus;
            }
            else { _img.Opacity = 0.25; _status.Text = "二维码过期了，点二维码刷新"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _img.Opacity = 0.25; _status.Text = "出错了，点二维码刷新"; App.Log("扫码登录: " + ex.Message); }
    }

    public static BitmapImage FromBytes(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static BitmapImage Render(string content)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, [0, 0, 0], [255, 255, 255], drawQuietZones: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
