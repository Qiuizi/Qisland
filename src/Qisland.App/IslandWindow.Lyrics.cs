using Qisland.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Qisland.App;

/// <summary>
/// 歌词：胶囊里把「歌名 歌手」换成当前这句；首页音乐卡片下面显示当前句 + 翻译（或下一句）。
/// 只在有歌词且正在播放时开一个 5 Hz 的计时器，换句才改文字，平时不耗资源。
/// </summary>
public sealed partial class IslandWindow
{
    public LyricsService? Lyrics { get; set; }
    private const double LyricCompactW = 330;
    private readonly DispatcherTimer _lyricTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _lyricIdx = -2;
    private TextBlock? _compactLyric, _cardLyric, _cardLyric2;
    private bool _lyricInit;

    private IReadOnlyList<LyricLine>? LiveLyrics => Lyrics?.Lines is { Count: > 0 } l && Media?.Current is { Video: false } ? l : null;

    public void OnLyricsChanged()
    {
        _lyricIdx = -2;
        RebuildCompact();
        if (_mode == Mode.Expanded && _page == Page.Home && !_animating) RebuildExpanded(); else _expDirty = true;
        UpdateLyricTimer();
    }

    /// <summary>播放器校准改了（设置页 / Ctrl 滚轮 / 开关）：下一拍按新时间重新找句子，不重建界面。</summary>
    public void OnLyricShiftChanged() { _lyricIdx = -2; LyricTick(); }

    /// <summary>播放 / 暂停 / 换会话时调用：只开关计时器，不重建界面。</summary>
    public void UpdateLyricTimer()
    {
        if (!_lyricInit) { _lyricInit = true; _lyricTimer.Tick += (_, _) => LyricTick(); }
        var on = LiveLyrics != null && MusicPlaying;
        if (on && !_lyricTimer.IsEnabled) _lyricTimer.Start();
        else if (!on && _lyricTimer.IsEnabled) _lyricTimer.Stop();
        LyricTick();
    }

    private (string cur, string? sub) LyricAt(int idx, IReadOnlyList<LyricLine> l)
    {
        // 空行（间奏）继续显示上一句的位置留白，用「♪」代替
        if (idx < 0) return ("♪", l.FirstOrDefault(x => x.Text.Length > 0)?.Text);
        var c = l[idx];
        var next = l.Skip(idx + 1).FirstOrDefault(x => x.Text.Length > 0)?.Text;
        return (c.Text.Length > 0 ? c.Text : "♪", c.Translation ?? next);
    }

    /// <summary>查歌词用的时间：当前进度 + 0.25 秒（换句略提前一点，读起来不拖）+ 这首歌用户调过的偏移 + 这个播放器的整体校准。</summary>
    private TimeSpan LyricClock() =>
        (Media?.LivePosition() ?? TimeSpan.Zero) + TimeSpan.FromMilliseconds(250) + (Lyrics?.UserOffset ?? TimeSpan.Zero) + (Lyrics?.PlayerShift ?? TimeSpan.Zero);

    /// <summary>
    /// 在歌词上滚滚轮：往上 = 歌词提前 0.3 秒，往下 = 推后 0.3 秒；每首歌单独记住。
    /// 按住 Ctrl 滚：整个播放器 ±0.1 秒（这个播放器放的歌都跟着走），设置里关掉「按播放器校准」时 Ctrl 滚轮照旧按歌调。
    /// </summary>
    private void HookLyricWheel(FrameworkElement el)
    {
        el.ToolTip = Features.On(Features.PlayerLyricOffset)
            ? "滚轮微调这首歌的歌词快慢（每格 0.3 秒）\n按住 Ctrl 滚：校准整个播放器（每格 0.1 秒）"
            : "滚轮微调歌词快慢（每格 0.3 秒，按歌记住）";
        el.MouseWheel += (_, e) =>
        {
            if (Lyrics == null) return;
            e.Handled = true;
            var up = e.Delta > 0;
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && Features.On(Features.PlayerLyricOffset) && Lyrics.Player.Length > 0)
            {
                Lyrics.NudgePlayer(up ? 0.1 : -0.1);
                var po = Lyrics.PlayerShift.TotalSeconds;
                if (_cardLyric2 != null) _cardLyric2.Text = Math.Abs(po) < 0.05 ? $"{Lyrics.PlayerName} 校准已复位" : $"{Lyrics.PlayerName} 整体{(po > 0 ? "提前" : "推后")} {Math.Abs(po):0.0} 秒";
                _lyricIdx = -2;
                return;
            }
            Lyrics.Nudge(up ? 0.3 : -0.3);
            var o = Lyrics.UserOffset.TotalSeconds;
            App.Log($"歌词偏移 {o:+0.0;-0.0;0} 秒");
            if (_cardLyric2 != null) _cardLyric2.Text = Math.Abs(o) < 0.05 ? "歌词偏移已复位" : $"歌词{(o > 0 ? "提前" : "推后")} {Math.Abs(o):0.0} 秒";
            _lyricIdx = -2;
        };
    }

    private void LyricTick()
    {
        var l = LiveLyrics;
        if (l == null || Media == null) return;
        var idx = Lrc.IndexAt(l, LyricClock());
        if (idx == _lyricIdx) return;
        _lyricIdx = idx;
        var (cur, sub) = LyricAt(idx, l);
        Swap(_compactLyric, cur);
        Swap(_cardLyric, cur);
        Swap(_cardLyric2, sub ?? "");
    }

    private static void Swap(TextBlock? tb, string text)
    {
        if (tb == null || tb.Text == text) return;
        tb.Text = text;
        tb.ToolTip = text;
        var a = new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        tb.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>首页音乐卡片下方的两行歌词；没有歌词时返回 null。</summary>
    private FrameworkElement? CardLyrics()
    {
        var l = LiveLyrics;
        _cardLyric = _cardLyric2 = null;
        if (l == null) return null;
        var (cur, sub) = LyricAt(Media is { } md ? Lrc.IndexAt(l, LyricClock()) : -1, l);
        var sp = new StackPanel { Margin = new Thickness(60, -2, 8, 8) };
        var a = Txt(14, FontWeights.SemiBold, Theme.Primary);
        a.Text = cur; a.TextTrimming = TextTrimming.CharacterEllipsis;
        var b = Txt(12, FontWeights.Normal, Theme.Secondary);
        b.Text = sub ?? ""; b.TextTrimming = TextTrimming.CharacterEllipsis; b.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(a); sp.Children.Add(b);
        sp.Background = System.Windows.Media.Brushes.Transparent; // 让空白处也能收到滚轮
        HookLyricWheel(sp);
        _cardLyric = a; _cardLyric2 = b;
        return sp;
    }
}
