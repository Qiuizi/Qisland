using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 通知的第一性原理：通知花的是注意力，打扰的代价要和信息的紧急程度相配。
/// 参照 iOS 的四档中断级别：
///   · 关键（等你批准、出错）——立刻展开，多停一会儿，专注中也打断；
///   · 时效（完成、重要提醒）——展开偷看几秒，但两次偷看至少隔 6 秒，专注中先攒着；
///   · 主动（一般消息）——不展开，胶囊轻轻弹一下，换上新内容；
///   · 被动（进度更新、媒体）——静默替换。
/// 专注计时期间攒下的非关键通知，计时结束时合成一条摘要。
/// </summary>
public sealed partial class IslandWindow
{
    private enum Level { Passive, Active, TimeSensitive, Critical }

    private DateTime _lastPeek = DateTime.MinValue;
    private readonly List<string> _held = [];
    private bool _wasFocusing;

    private static Level LevelOf(LiveActivity a)
    {
        if (a.Source is "media") return Level.Passive;
        if (a.Source is "call" && a.Kind == ActivityKind.Warning) return Level.Critical;   // 来电：立刻展开
        // 微信 / QQ 消息只是“主动”档：胶囊轻弹、换上新内容，不展开（要看就鼠标移上去）
        if (a.Source is "wechat" or "qq") return Level.Active;
        return a.Kind switch
        {
            ActivityKind.WaitingApproval or ActivityKind.Error => Level.Critical,
            ActivityKind.Done or ActivityKind.Warning => a.Priority <= 20 ? Level.Active : Level.TimeSensitive,
            ActivityKind.Running => a.Priority >= 85 ? Level.Active : Level.Passive,
            _ => a.Priority >= 70 ? Level.TimeSensitive : a.Priority <= 20 ? Level.Passive : Level.Active,
        };
    }

    private bool OwnFocus => Timer is { Active: true, Paused: false, Label: "专注" };
    private bool Focusing => OwnFocus || FocusMode.Active;

    /// <summary>专注联动开着时，被挡在胶囊外的新消息也记进摘要（它们不会成为头条，Attend 看不到）。</summary>
    private void NoteHeld()
    {
        if (!FocusMode.Active) return;
        var now = DateTime.Now;
        foreach (var a in _store.Ranked())
        {
            if (a.Quiet || FocusMode.Passes(a) || (now - a.Updated).TotalSeconds >= 2 || LevelOf(a) < Level.Active) continue;
            if (!_held.Contains(a.Title) && _held.Count < 99) _held.Add(a.Title);
        }
    }

    /// <summary>专注开始 / 结束（含 Windows 专注会话）：胶囊上放行的东西变了，重排一次。</summary>
    public void OnFocusModeChanged() => OnStoreChanged();

    /// <summary>新头条到达时决定打扰到什么程度。返回 true 表示已经展开偷看。</summary>
    private bool Attend(LiveActivity top)
    {
        if (_hidden) return false;
        var level = LevelOf(top);
        var now = DateTime.Now;
        if (top.Source != "timer" && Focusing && level < Level.Critical && !FocusMode.Passes(top))
        {
            if (level >= Level.Active && !_held.Contains(top.Title)) _held.Add(top.Title);
            return false;
        }
        switch (level)
        {
            case Level.Critical:
                Peek(6);
                return true;
            case Level.TimeSensitive when (now - _lastPeek).TotalSeconds >= 6:
                Peek(3.5);
                return true;
            case Level.TimeSensitive:
            case Level.Active:
                Pulse();
                return false;
            default:
                return false;
        }
    }

    /// <summary>性能测试：展开 1.2 秒再收起，重复 3 次（日志里看 anim → Expanded 的帧数据）。</summary>
    public void RunBench()
    {
        Perf.Load();
        App.Log("bench flags: " + Perf.Text);
        var i = 0;
        if (Perf.Has("pages"))
        {
            // 翻页测速：展开后左右各翻两次
            Peek(8);
            var dirs = new[] { 1, 1, -1, -1 };
            var pt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
            pt.Tick += (_, _) => { if (i >= dirs.Length) { pt.Stop(); return; } App.Log($"bench page {i}"); this.Step(dirs[i++]); };
            pt.Start();
            return;
        }
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        void Step() { if (i++ >= 3) { t.Stop(); return; } App.Log($"bench {i}"); Peek(1.2); }
        t.Tick += (_, _) => Step();
        Step();
        t.Start();
    }

    /// <summary>调试：展开几秒（测展开状态下的占用）。</summary>
    public void PeekFor(double seconds) => Peek(seconds);

    /// <summary>性能护栏升降级：重建用到动画的部分（频谱条），歌词计时器换档。</summary>
    public void OnPerfChanged()
    {
        _lyricTimer.Interval = TimeSpan.FromMilliseconds(PerfGuard.Degraded ? 450 : 200);
        RebuildCompact();
        if (_mode == Mode.Expanded && !_animating) RebuildExpanded(); else _expDirty = true;
    }

    private void Peek(double seconds)
    {
        _lastPeek = DateTime.Now;
        _peekUntil = DateTime.Now.AddSeconds(seconds);
        SetMode(Mode.Expanded);
    }

    /// <summary>胶囊轻弹一下：给尺寸弹簧一个初速度，弹簧自己回位。</summary>
    private void Pulse()
    {
        SetMode(DesiredRestMode());
        if (_mode != Mode.Compact) return;
        _w.Velocity += 220;
        _h.Velocity += 26;
        Kick();
    }

    /// <summary>每秒检查一次：专注刚结束时，把攒下的通知合成一条摘要。</summary>
    private void CheckFocusDigest()
    {
        var f = Focusing;
        if (_wasFocusing && !f && _held.Count > 0)
        {
            var body = string.Join(" · ", _held.Take(3)) + (_held.Count > 3 ? $" 等" : "");
            _store.Upsert(new IslandActivity("focus-digest", "timer", $"专注期间来了 {_held.Count} 条消息", body, ActivityKind.Info, 60, "\uE7E7", "", 40));
            _held.Clear();
        }
        _wasFocusing = f;
    }
}

/// <summary>调试用开关（%APPDATA%\Qisland\perf.txt，空格分隔），只在测速时读一次，方便对比不同渲染做法。</summary>
internal static class Perf
{
    public static string Text = "";
    public static bool Has(string f) => (" " + Text + " ").Contains(" " + f + " ");
    public static void Load()
    {
        try { var p = System.IO.Path.Combine(App.DataDir, "perf.txt"); Text = System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p).Trim() : ""; } catch { Text = ""; }
    }
}
