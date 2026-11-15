using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Qisland.App;

/// <summary>Qisland.exe --snapshot &lt;目录&gt;：用演示数据把各个状态渲染成透明 PNG（README / 宣传片素材）。</summary>
public sealed partial class IslandWindow
{
    public async Task Snapshot(string dir, Action demo)
    {
        Directory.CreateDirectory(dir);
        await Shot(dir, "01-idle", Mode.Idle);
        demo();
        await Task.Delay(300);
        await Shot(dir, "02-compact", Mode.Compact);
        _peekUntil = DateTime.MinValue;
        _page = Page.Home; _expDirty = true;
        await Shot(dir, "03-home", Mode.Expanded);
        foreach (var (p, name) in new[] { (Page.Widgets, "04-widgets"), (Page.Clip, "05-clipboard") })
        {
            if (p == Page.Clip)
            {
                _clips.Clear();
                _clips.Add(new ClipItem("https://github.com/Qiuizi/Qisland", null, DateTime.Now.AddMinutes(-1)));
                _clips.Add(new ClipItem("dotnet publish -c Release -r win-x64", null, DateTime.Now.AddMinutes(-6)));
                _clips.Add(new ClipItem("实验报告-离散数学.docx", [@"C:\demo\实验报告-离散数学.docx"], DateTime.Now.AddMinutes(-18)));
                _clips.Add(new ClipItem("The quick brown fox jumps over the lazy dog.", null, DateTime.Now.AddHours(-2)));
            }
            _page = p; _expDirty = true;
            await Shot(dir, name, Mode.Expanded);
        }
        _page = Page.Home;
        SetMode(Mode.Compact);
        foreach (var s in Springs) s.Snap(s.Target);
        foreach (var a in _store.Ranked().ToList()) _store.Remove(a.Id);
        _osd = OsdKind.Volume; _osdLevel = 0.64; _osdMuted = false;
        await Shot(dir, "06-volume", Mode.Compact, osd: true);
        App.Log("snapshot 完成: " + dir);
    }

    private async Task Shot(string dir, string name, Mode m, bool osd = false)
    {
        if (osd) RebuildCompact();
        if (m == Mode.Expanded) RebuildExpanded();
        SetMode(m);
        _pinned = m == Mode.Expanded;
        await Task.Delay(1100);                       // 让入场动画走完
        foreach (var s in Springs) s.Snap(s.Target);
        ApplyFrame();
        _root.UpdateLayout();
        await Task.Delay(120);
        const double scale = 2;
        var rtb = new RenderTargetBitmap((int)(WinW * scale), (int)(WinH * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(_root);
        var w = _w.Value; var h = _h.Value;
        var pad = 28.0;
        var extra = _bubbleOp.Value > 0.05 ? CompactH + 12 : 0;   // 右侧的小气泡也要拍进去
        var x = Math.Max(0, (WinW - w) / 2 - pad - extra);
        var cw = Math.Min(WinW - x, w + (pad + extra) * 2);
        var ch = Math.Min(WinH, TopGap + h + pad + 6);
        var crop = new CroppedBitmap(rtb, new Int32Rect((int)(x * scale), 0, (int)(cw * scale), (int)(ch * scale)));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(crop));
        await using var f = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(f);
    }
}
