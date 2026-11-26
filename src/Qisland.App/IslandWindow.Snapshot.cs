using Qisland.Core;
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
        if (Countdown == null) try { Countdown = new Countdowns(); } catch { }
        if (_weather == null)
        {
            var now = DateTime.Now; var h0 = now.Date.AddHours(now.Hour + 1);
            _weather = new WeatherSnapshot(18, 2, 17, 64, 9, 0, 21, 12);
            _wx = new WeatherDetail(_weather, 20, 3, "东北", 10, "07:31", "18:06",
                Enumerable.Range(0, 8).Select(i => new HourSlot(h0.AddHours(i), 18 - i * 0.8, i is 3 or 4 ? 61 : 2, i is 3 or 4 ? 45 : 10)).ToList(),
                20, 11, 61, 60)
            {
                Days = [new(now.Date, 2, 21, 12, 20), new(now.Date.AddDays(1), 61, 20, 11, 60), new(now.Date.AddDays(2), 1, 23, 13, 0)],
                Pressure = 1018, MoonPhase = "盈凸月", MoonIllum = 72,
            };
        }
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
                var fox = new ClipItem("The quick brown fox jumps over the lazy dog.", null, DateTime.Now.AddHours(-2));
                _clips.Add(fox);
                _translated[fox] = "敏捷的棕色狐狸跳过了那只懒狗。";
                _clips.Insert(0, new ClipItem("图片 1920×1080", null, DateTime.Now.AddSeconds(-20), DemoImage()));
            }
            _page = p; _expDirty = true;
            await Shot(dir, name, Mode.Expanded);
            if (p == Page.Widgets)
            {
                _wxView = 1; _pageCache.Clear(); await Shot(dir, "07-widgets-detail", Mode.Expanded);
                _wxView = 2; _pageCache.Clear(); await Shot(dir, "08-widgets-days", Mode.Expanded);
                _wxView = 0; _pageCache.Clear();
                if (Timer != null) { Timer.Start(25, "专注"); _pageCache.Clear(); await Shot(dir, "09-widgets-focus", Mode.Expanded); Timer.Cancel(); _pageCache.Clear(); }
            }
        }
        _page = Page.Home;
        SetMode(Mode.Compact);
        foreach (var s in Springs) s.Snap(s.Target);
        foreach (var a in _store.Ranked().ToList()) _store.Remove(a.Id);
        _osd = OsdKind.Volume; _osdLevel = 0.64; _osdMuted = false;
        await Shot(dir, "06-volume", Mode.Compact, osd: true);
        _osd = OsdKind.None;
        // 课表：上课中 + 下一节
        var realClasses = Classes;
        Classes = new ClassWatcher(_store, ClassWatcher.DemoIcs());
        OnCountdownChanged();
        await Task.Delay(300);
        await Shot(dir, "10-class-compact", Mode.Compact);
        _page = Page.Home; _expDirty = true; _pageCache.Clear();
        await Shot(dir, "11-class-home", Mode.Expanded);
        foreach (var a in _store.Ranked().ToList()) _store.Remove(a.Id);
        SetMode(Mode.Idle);
        await Shot(dir, "12-class-idle", Mode.Idle);
        Classes = realClasses;
        // 歌词：演示用自编歌词
        Media ??= new MediaWatcher(Dispatcher);
        Lyrics ??= new LyricsService();
        var t0 = TimeSpan.FromSeconds(40);
        Lyrics.ShowDemo([
            new(t0 - TimeSpan.FromSeconds(6), "风从教学楼的走廊穿过", "The wind runs through the hallway"),
            new(t0 - TimeSpan.FromSeconds(1), "把没写完的作业吹成了诗", "Turning unfinished homework into poems"),
            new(t0 + TimeSpan.FromSeconds(5), "灯一盏一盏亮起来", "Lights come on one by one"),
        ]);
        Media.ShowDemo(new MediaState("晚自习", "示例歌手", "网易云音乐", "netease", true, DemoCover(), t0, TimeSpan.FromSeconds(214), DateTime.Now));
        OnLyricsChanged();
        await Task.Delay(300);
        await Shot(dir, "13-lyrics-compact", Mode.Compact);
        _page = Page.Home; _expDirty = true; _pageCache.Clear();
        await Shot(dir, "14-lyrics-home", Mode.Expanded);
        // 作业 DDL：演示数据
        Media = null;
        foreach (var a in _store.Ranked().ToList()) _store.Remove(a.Id);
        var realHw = Homework;
        Homework = HomeworkHub.Demo(_store);
        OnHomeworkChanged();
        _page = Page.Home; _expDirty = true; _pageCache.Clear();
        await Shot(dir, "15-homework-home", Mode.Expanded);
        SetMode(Mode.Idle);
        await Shot(dir, "16-homework-idle", Mode.Idle);
        Homework = realHw;
        // Codex 审批 + 额度环：演示数据
        foreach (var a in _store.Ranked().ToList()) _store.Remove(a.Id);
        Quota = new CodexQuota(a => a());
        Quota.Demo(34, 62);
        _store.Upsert(new Qisland.Core.IslandActivity("agent:codex:demo", "codex", "Codex 进行中", "重构 HomeworkHub · Qisland", Qisland.Core.ActivityKind.Running, 55, DurationSeconds: 600));
        Approvals = new ApprovalBroker(a => a());
        Approvals.Changed += OnApprovalsChanged;
        _ = Approvals.Request("codex", "{\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"dotnet publish src/Qisland.App -c Release -o app && git commit -am 'v2 step 5'\"},\"cwd\":\"C:\\\\src\\\\Qisland\"}");
        await Task.Delay(200);
        _page = Page.Home; _expDirty = true; _pageCache.Clear();
        await Shot(dir, "17-codex-approval", Mode.Expanded);
        Approvals.CancelAll();
        await Task.Delay(200);
        Quota.Demo(91, 74);
        _page = Page.Home; _expDirty = true; _pageCache.Clear();
        await Shot(dir, "18-codex-quota", Mode.Expanded);
        // 4.3：本机 AI 额度（演示数据，不读真实令牌）
        Usage = new AiUsageService(a => a()); Usage.Demo();
        _page = Page.Agents; _expDirty = true; _pageCache.Clear();
        await Shot(dir, "19-ai-usage", Mode.Expanded);
        // 设备电量（演示数据，不读真实设备）
        Devices = new PeripheralWatcher(_store, a => a()); Devices.Demo();
        _widgetList = Widgets().OrderBy(w => w.Id == "devices" ? 0 : 1).ToList();   // 只在快照进程里把设备卡挪到最上面，不写 widgets.txt
        _page = Page.Widgets; _expDirty = true; _pageCache.Clear();
        await Shot(dir, "20-devices", Mode.Expanded);
        _widgetList = null;
        _page = Page.Home;
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

    private static System.Windows.Media.Imaging.BitmapSource DemoImage()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x5E, 0x5C, 0xE6), Color.FromRgb(0xFF, 0x9F, 0x0A), 30), null, new Rect(0, 0, 192, 108));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), null, new Point(140, 34), 18, 18);
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(192, 108, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv); bmp.Freeze();
        return bmp;
    }

    /// <summary>演示封面（132 px）：暮色渐变 + 一轮月亮 + 几扇亮着的窗，用来展示封面取色。全部代码画的，没有真实专辑图。</summary>
    private static System.Windows.Media.Imaging.BitmapSource DemoCover()
    {
        const int n = 132;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x2B, 0x2A, 0x5C), 0));
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xC8, 0x4E, 0x5A), 0.62));
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0x9A, 0x5C), 1));
            dc.DrawRectangle(sky, null, new Rect(0, 0, n, n));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xEF, 0xD6)), null, new Point(96, 34), 13, 13);
            var bldg = new SolidColorBrush(Color.FromRgb(0x1C, 0x17, 0x2E));
            dc.DrawRectangle(bldg, null, new Rect(10, 78, 70, 54));
            var lit = new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x8A));
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 4; c++)
                    if ((r * 4 + c) % 3 != 1) dc.DrawRectangle(lit, null, new Rect(17 + c * 15, 86 + r * 13, 8, 6));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(n, n, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv); bmp.Freeze();
        return bmp;
    }
}
