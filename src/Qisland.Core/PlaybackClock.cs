namespace Qisland.Core;

/// <summary>
/// 播放进度时钟：把播放器断断续续报来的「位置 + 采样时刻」变成每一帧都能读的平滑进度。
/// - 新采样和本地推算差得不多（&lt; Tolerance）时不跳，避免歌词在两句之间来回闪；差得多（拖进度、缓冲卡住、切歌）立刻对齐；
/// - 同一份采样重复送来（播放器只在播放/暂停/拖动时更新时间轴）不会把进度拽回去，继续按本地时间往前走；
/// - 采样时刻明显不可信（早于几小时前或在未来，有的播放器不填 LastUpdatedTime）时，按收到采样的此刻算；
/// - Live：这首歌已经收到过两份不同的采样且进度在走，说明播放器真的在推时间轴，可以当主时钟。
/// </summary>
public sealed class PlaybackClock
{
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromMilliseconds(400);

    private string? _track;
    private TimeSpan _anchorPos;
    private DateTime _anchorAt;
    private bool _playing;
    private (TimeSpan pos, DateTime at, bool playing)? _lastRaw;

    public string? Track => _track;
    public bool Playing => _playing;
    /// <summary>这首歌收到过 ≥ 2 份不同的、进度在走的采样。</summary>
    public bool Live { get; private set; }
    /// <summary>最近一次是不是因为差太多而重新对齐（拖进度 / 切歌 / 暂停恢复）。</summary>
    public int Resyncs { get; private set; }

    /// <summary>送一份采样。返回 true 表示进度被重新对齐了（切歌、拖动、暂停/播放切换或漂移超出容差）。</summary>
    public bool Sample(string track, TimeSpan position, DateTime sampledAt, bool playing, DateTime now)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (sampledAt > now + TimeSpan.FromSeconds(5) || sampledAt < now - TimeSpan.FromHours(6)) sampledAt = now;

        if (track != _track)
        {
            _track = track; Live = false; _lastRaw = null;
            Anchor(position, sampledAt, playing, now);
            _lastRaw = (position, sampledAt, playing);
            return true;
        }
        if (_lastRaw is { } r && r.pos == position && r.at == sampledAt && r.playing == playing) return false; // 同一份采样
        var prevRaw = _lastRaw;
        _lastRaw = (position, sampledAt, playing);
        if (prevRaw is { } p && playing && p.playing && position > p.pos) Live = true; // 播放器自己在推进度

        var reported = playing ? position + (now - sampledAt) : position;
        if (playing != _playing) { Anchor(position, sampledAt, playing, now); return true; }
        var predicted = Position(now);
        if ((reported - predicted).Duration() > Tolerance) { Anchor(position, sampledAt, playing, now); return true; }
        return false;
    }

    private void Anchor(TimeSpan pos, DateTime at, bool playing, DateTime now)
    {
        _playing = playing;
        // 统一换算到「此刻」的位置再存，后面只用本地时钟往前推
        _anchorPos = playing ? pos + (now - at) : pos;
        if (_anchorPos < TimeSpan.Zero) _anchorPos = TimeSpan.Zero;
        _anchorAt = now;
        Resyncs++;
    }

    public TimeSpan Position(DateTime now, TimeSpan? duration = null)
    {
        if (_track == null) return TimeSpan.Zero;
        var p = _playing ? _anchorPos + (now - _anchorAt) : _anchorPos;
        if (p < TimeSpan.Zero) p = TimeSpan.Zero;
        if (duration is { } d && d > TimeSpan.Zero && p > d) p = d;
        return p;
    }

    /// <summary>手动拖动后本地先跳过去，不等播放器回报。</summary>
    public void Seek(TimeSpan position, DateTime now) { if (_track != null) { _anchorPos = position; _anchorAt = now; } }

    public void Reset() { _track = null; _lastRaw = null; Live = false; }
}
