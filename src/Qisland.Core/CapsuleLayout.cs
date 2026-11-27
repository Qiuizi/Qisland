using System.Text;

namespace Qisland.Core;

/// <summary>收起胶囊的两种状态：没事时（空闲）和放音乐时（播放）。两种状态分开编排。</summary>
public enum CapsuleState { Idle, Playing }

/// <summary>胶囊里的三个槽位：左、中、右。中间槽位占满剩余宽度、内容居中。</summary>
public enum CapsuleSlot { Left, Center, Right }

/// <summary>一项：放在哪个槽位、显不显示。同一槽位里按列表顺序从左到右排。</summary>
public sealed record CapsuleEntry(string Id, CapsuleSlot Slot, bool On);

/// <summary>
/// 5.0 零代码 DIY：收起胶囊的左 / 中 / 右槽位编排，空闲和播放各一份。
/// 存成 capsule-layout.txt（每行「状态.槽位 = 项目, 项目」，前面加 - 表示隐藏），手改也看得懂。
/// 没有这个文件时就是 4.3 原来的样子：左时钟、中间倒计时 / 系统状态、右额度环 + 天气；播放时左封面、中间歌名 / 歌词、右频谱。
/// </summary>
public sealed class CapsuleLayout
{
    public sealed record Item(string Id, string Name, string Hint);

    /// <summary>每种状态能放哪些项（id 固定，名字给设置页用）。</summary>
    public static IReadOnlyList<Item> Catalog(CapsuleState s) => s == CapsuleState.Idle ? IdleItems : PlayingItems;

    private static readonly Item[] IdleItems =
    [
        new("clock", "时间", "当前时间，跟随 12 / 24 小时制"),
        new("weather", "天气", "天气符号 + 气温；没拿到天气时显示星期"),
        new("countdown", "课程倒计时", "快上课 / 上课中才出现，平时不占位"),
        new("stats", "精简系统状态", "CPU / 内存 / 网速，要在「设备与状态」里打开采样"),
        new("quota", "最紧 AI 额度环", "要在「AI 额度」里打开「收起时显示小环」"),
    ];

    private static readonly Item[] PlayingItems =
    [
        new("cover", "封面", "当前歌曲的专辑封面"),
        new("title", "歌名 / 歌词", "有歌词时滚动显示当前一句"),
        new("bars", "迷你频谱", "三根跳动的频谱条"),
        new("clock", "时间", "放歌时也能看一眼时间"),
        new("weather", "天气", "天气符号 + 气温"),
        new("quota", "最紧 AI 额度环", "要在「AI 额度」里打开「收起时显示小环」"),
    ];

    private static readonly CapsuleEntry[] IdleDefault =
    [
        new("clock", CapsuleSlot.Left, true),
        new("countdown", CapsuleSlot.Center, true),
        new("stats", CapsuleSlot.Center, true),
        new("quota", CapsuleSlot.Right, true),
        new("weather", CapsuleSlot.Right, true),
    ];

    private static readonly CapsuleEntry[] PlayingDefault =
    [
        new("cover", CapsuleSlot.Left, true),
        new("title", CapsuleSlot.Center, true),
        new("bars", CapsuleSlot.Right, true),
        new("clock", CapsuleSlot.Right, false),
        new("weather", CapsuleSlot.Right, false),
        new("quota", CapsuleSlot.Right, false),
    ];

    public List<CapsuleEntry> Idle { get; } = [];
    public List<CapsuleEntry> Playing { get; } = [];

    public List<CapsuleEntry> For(CapsuleState s) => s == CapsuleState.Idle ? Idle : Playing;

    public static CapsuleLayout Default()
    {
        var l = new CapsuleLayout();
        l.Idle.AddRange(IdleDefault);
        l.Playing.AddRange(PlayingDefault);
        return l;
    }

    public static IReadOnlyList<CapsuleEntry> DefaultFor(CapsuleState s) => s == CapsuleState.Idle ? IdleDefault : PlayingDefault;

    /// <summary>某槽位里显示的项，按顺序。</summary>
    public IReadOnlyList<string> Shown(CapsuleState s, CapsuleSlot slot) =>
        For(s).Where(e => e.On && e.Slot == slot).Select(e => e.Id).ToList();

    public bool IsOn(CapsuleState s, string id) => For(s).Any(e => e.Id == id && e.On);

    public CapsuleLayout Clone()
    {
        var c = new CapsuleLayout();
        c.Idle.AddRange(Idle);
        c.Playing.AddRange(Playing);
        return c;
    }

    /// <summary>歌名 / 歌词是播放胶囊里唯一会伸缩的一项，固定在中间。</summary>
    public static bool SlotLocked(CapsuleState s, string id) => s == CapsuleState.Playing && id == "title";

    public void Set(CapsuleState s, string id, bool? on = null, CapsuleSlot? slot = null)
    {
        if (SlotLocked(s, id) && slot is not null and not CapsuleSlot.Center) slot = CapsuleSlot.Center;
        var list = For(s);
        var i = list.FindIndex(e => e.Id == id);
        if (i < 0) return;
        var was = list[i].Slot;
        var e = list[i] with { On = on ?? list[i].On, Slot = slot ?? list[i].Slot };
        if (e.Slot == was) { list[i] = e; return; }
        // 换了槽位：挪到新槽位那一组的末尾，列表始终按 左 / 中 / 右 分组
        list.RemoveAt(i);
        var at = list.FindLastIndex(x => x.Slot <= e.Slot) + 1;
        list.Insert(at, e);
    }

    /// <summary>把第 from 项挪到第 to 位（拖拽排序 / 上下移动都走这里）。</summary>
    public void Move(CapsuleState s, int from, int to)
    {
        var list = For(s);
        if (from < 0 || from >= list.Count) return;
        to = Math.Clamp(to, 0, list.Count - 1);
        if (from == to) return;
        var e = list[from];
        list.RemoveAt(from);
        list.Insert(to, e);
        // 拖进了别的槽位那一组：跟着换槽位（歌名固定在中间，拖不出去）
        var target = e.Slot;
        if (from < to && list[to - 1].Slot > e.Slot) target = list[to - 1].Slot;          // 往下拖过了组界
        if (from > to && list[to + 1].Slot < e.Slot) target = list[to + 1].Slot;          // 往上拖过了组界
        if (target != e.Slot)
        {
            if (SlotLocked(s, e.Id)) { list.RemoveAt(to); list.Insert(from, e); return; }
            list[to] = e with { Slot = target };
        }
        Group(list);
    }

    /// <summary>按 左 / 中 / 右 稳定排序（同一槽位里的先后不变）。</summary>
    private static void Group(List<CapsuleEntry> list)
    {
        var sorted = list.OrderBy(x => x.Slot).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    // ───────────── 存取 ─────────────

    /// <summary>
    /// 解析 capsule-layout.txt。认不出的行 / 项目直接忽略；文件里漏掉的项按默认位置补上（新版本加的项也这样进来）；
    /// 一种状态一项都不显示时整份退回默认，免得胶囊变成一条空壳。
    /// </summary>
    public static CapsuleLayout Parse(string? text)
    {
        var l = new CapsuleLayout();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var dot = key.IndexOf('.');
            if (dot <= 0) continue;
            CapsuleState? st = key[..dot] switch { "idle" => CapsuleState.Idle, "playing" => CapsuleState.Playing, _ => null };
            CapsuleSlot? slot = key[(dot + 1)..] switch { "left" => CapsuleSlot.Left, "center" => CapsuleSlot.Center, "right" => CapsuleSlot.Right, _ => null };
            if (st is not { } s || slot is not { } sl) continue;
            var list = l.For(s);
            foreach (var part in line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var on = !part.StartsWith('-');
                var id = part.TrimStart('-').Trim().ToLowerInvariant();
                if (Catalog(s).All(c => c.Id != id) || list.Any(e => e.Id == id)) continue;
                list.Add(new CapsuleEntry(id, sl, on));
            }
        }
        foreach (var s in new[] { CapsuleState.Idle, CapsuleState.Playing }) Normalize(l.For(s), s);
        l.Set(CapsuleState.Playing, "title", slot: CapsuleSlot.Center);
        return l;
    }

    private static void Normalize(List<CapsuleEntry> list, CapsuleState s)
    {
        var defs = DefaultFor(s);
        if (list.Count == 0 || list.All(e => !e.On)) { list.Clear(); list.AddRange(defs); return; }
        for (var k = 0; k < defs.Count; k++)
        {
            var d = defs[k];
            if (list.Any(e => e.Id == d.Id)) continue;
            // 新项插在默认顺序里它前一项的后面
            var at = k == 0 ? 0 : list.FindIndex(e => e.Id == defs[k - 1].Id) + 1;
            list.Insert(Math.Clamp(at, 0, list.Count), d);
        }
        Group(list);
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("# Qisland 收起胶囊编排：状态.槽位 = 项目（逗号分隔、从左到右）；前面加 - 表示隐藏\n");
        sb.Append("# 空闲可选：").Append(string.Join(" ", IdleItems.Select(i => i.Id))).Append('\n');
        sb.Append("# 播放可选：").Append(string.Join(" ", PlayingItems.Select(i => i.Id))).Append('\n');
        foreach (var (s, name) in new[] { (CapsuleState.Idle, "idle"), (CapsuleState.Playing, "playing") })
            foreach (var (slot, sname) in new[] { (CapsuleSlot.Left, "left"), (CapsuleSlot.Center, "center"), (CapsuleSlot.Right, "right") })
            {
                var items = For(s).Where(e => e.Slot == slot).Select(e => (e.On ? "" : "-") + e.Id);
                sb.Append(name).Append('.').Append(sname).Append(" = ").Append(string.Join(", ", items)).Append('\n');
            }
        return sb.ToString();
    }

    /// <summary>和默认一样（设置页「恢复默认」按钮置灰用）。</summary>
    public bool IsDefault(CapsuleState s) => For(s).SequenceEqual(DefaultFor(s));
}

/// <summary>
/// 展开后组件页的顺序与显隐（widgets.txt：一行一个，前面加 - 表示隐藏）。从岛的代码里抽出来，方便自检旧文件的迁移。
/// </summary>
public static class WidgetOrder
{
    /// <summary>
    /// 解析 widgets.txt。<paramref name="known"/> 是当前版本认识的全部组件（默认顺序）。
    /// 认不出的行忽略；文件里没有的新组件插在默认顺序里前一个组件的后面、默认显示。
    /// </summary>
    public static List<(string Id, bool On)> Parse(IEnumerable<string> lines, IReadOnlyList<string> known)
    {
        var list = new List<(string Id, bool On)>();
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            var on = !l.StartsWith('-');
            var id = l.TrimStart('-').Trim().ToLowerInvariant();
            if (known.Contains(id) && list.All(x => x.Id != id)) list.Add((id, on));
        }
        for (var k = 0; k < known.Count; k++)
        {
            var id = known[k];
            if (list.Any(x => x.Id == id)) continue;
            var at = k == 0 ? 0 : list.FindIndex(x => x.Id == known[k - 1]) + 1;
            list.Insert(Math.Clamp(at, 0, list.Count), (id, true));
        }
        return list;
    }

    public static List<string> Serialize(IEnumerable<(string Id, bool On)> list, IEnumerable<string> known)
    {
        var lines = new List<string> { "# Qisland 组件页：一行一个，从上到下就是显示顺序；前面加 - 表示隐藏", "# 可选：" + string.Join(" ", known) };
        lines.AddRange(list.Select(w => (w.On ? "" : "-") + w.Id));
        return lines;
    }

    public static void Move<T>(IList<T> list, int from, int to)
    {
        if (from < 0 || from >= list.Count) return;
        to = Math.Clamp(to, 0, list.Count - 1);
        if (from == to) return;
        var e = list[from];
        list.RemoveAt(from);
        list.Insert(to, e);
    }
}
