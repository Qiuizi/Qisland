using System.Text.Json;

namespace Qisland.Core;

/// <summary>本机日程（不依赖任何订阅）：岛上 / 设置里增删改，存在本机（App 侧 DPAPI 加密）。可选每周重复。</summary>
public sealed record LocalEvent(string Id, string Title, string Location, DateTime Start, DateTime End, bool AllDay, bool Weekly = false, DateTime? Until = null)
{
    public bool Valid => Title.Trim().Length > 0 && End >= Start && (AllDay || End > Start);
}

public enum LocalSaveResult { Saved, Invalid, WriteFailed }

/// <summary>
/// 本机日程表：增删改、序列化、按时间窗展开成 CalEvent（和 .ics 订阅一起显示、一起提醒）。
/// 思路参考小糖岛仔（MIT）的本地日历编辑器，C# 重写。
/// </summary>
public sealed class LocalCalendar
{
    public const int MaxEvents = 500;
    public const string UidPrefix = "local:";
    private readonly List<LocalEvent> _items = [];
    public IReadOnlyList<LocalEvent> Items => _items;
    public int Count => _items.Count;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>新增或按 Id 覆盖；不合法（没标题、结束早于开始）返回 false。</summary>
    public bool Upsert(LocalEvent e)
    {
        if (!e.Valid) return false;
        e = e with { Title = e.Title.Trim(), Location = e.Location.Trim() };
        var i = _items.FindIndex(x => x.Id == e.Id);
        if (i >= 0) _items[i] = e;
        else
        {
            if (_items.Count >= MaxEvents) return false;
            _items.Add(e);
        }
        _items.Sort((a, b) => a.Start.CompareTo(b.Start));
        return true;
    }

    public bool Delete(string id) => _items.RemoveAll(x => x.Id == id) > 0;

    /// <summary>
    /// 改内存 + 写盘，写盘失败就把内存退回原样（界面不会显示一个其实没存上的日程）。
    /// persist 收到序列化后的 JSON，返回是否真的写进了磁盘。
    /// </summary>
    public LocalSaveResult TrySave(LocalEvent e, Func<string, bool> persist)
    {
        var before = _items.ToList();
        if (!Upsert(e)) return LocalSaveResult.Invalid;
        if (persist(ToJson())) return LocalSaveResult.Saved;
        _items.Clear(); _items.AddRange(before);
        return LocalSaveResult.WriteFailed;
    }

    public LocalSaveResult TryDelete(string id, Func<string, bool> persist)
    {
        var before = _items.ToList();
        if (!Delete(id)) return LocalSaveResult.Invalid;
        if (persist(ToJson())) return LocalSaveResult.Saved;
        _items.Clear(); _items.AddRange(before);
        return LocalSaveResult.WriteFailed;
    }

    public LocalEvent? Find(string id) => _items.FirstOrDefault(x => x.Id == id);

    /// <summary>CalEvent.Uid 是 local:&lt;Id&gt;@&lt;日期&gt;（重复的每次一个），从这里取回 Id。</summary>
    public static string? IdFromUid(string uid)
    {
        if (!uid.StartsWith(UidPrefix, StringComparison.Ordinal)) return null;
        var s = uid[UidPrefix.Length..];
        var at = s.IndexOf('@');
        return at >= 0 ? s[..at] : s;
    }

    /// <summary>把 [from, to) 里发生的日程展开（每周重复的按次展开）。</summary>
    public IEnumerable<CalEvent> Expand(DateTime from, DateTime to)
    {
        foreach (var e in _items)
        {
            var len = e.End - e.Start;
            if (!e.Weekly)
            {
                if (e.End > from && e.Start < to) yield return Make(e, e.Start, e.End);
                continue;
            }
            // 从窗口开始前一周找起，跨天的重复日程也不会漏
            var first = e.Start;
            if (first < from.AddDays(-7))
            {
                var weeks = (int)Math.Floor((from.AddDays(-7) - first).TotalDays / 7);
                first = first.AddDays(weeks * 7);
            }
            for (var s = first; s < to; s = s.AddDays(7))
            {
                if (e.Until is { } u && s.Date > u.Date) break;
                if (s + len > from) yield return Make(e, s, s + len);
            }
        }
    }

    private static CalEvent Make(LocalEvent e, DateTime s, DateTime end) =>
        new(UidPrefix + e.Id + "@" + s.ToString("yyyyMMdd"), e.Title, e.Location, s, end, e.AllDay, null, null)
        {
            SourceId = "local",
            OriginalStart = s,
        };

    public string ToJson() => JsonSerializer.Serialize(_items);

    public static LocalCalendar FromJson(string? json)
    {
        var c = new LocalCalendar();
        if (string.IsNullOrWhiteSpace(json)) return c;
        try
        {
            foreach (var e in JsonSerializer.Deserialize<List<LocalEvent>>(json) ?? [])
                if (e is { Id.Length: > 0 }) c.Upsert(e);
        }
        catch (JsonException) { }
        return c;
    }
}
