using System.Text.Json;

namespace Qisland.Core;

/// <summary>
/// 剪贴板置顶：用户主动钉住的几段文字（最多 10 条、每条最长 4000 字），新钉的在最前。
/// 只有这部分会落盘（App 侧用 DPAPI 加密），普通历史仍然只在内存里。思路参考小糖岛仔（MIT）的剪贴板置顶。
/// </summary>
public sealed class ClipPins
{
    public const int Max = 10, MaxLength = 4000;
    private readonly List<string> _items = [];
    public IReadOnlyList<string> Items => _items;
    public int Count => _items.Count;

    public static bool CanPin(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxLength;

    public bool Contains(string text) => _items.Contains(text);

    /// <summary>钉住（已钉的挪到最前）；超过上限时挤掉最早钉的。不能钉返回 false。</summary>
    public bool Pin(string text)
    {
        if (!CanPin(text)) return false;
        _items.Remove(text);
        _items.Insert(0, text);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        return true;
    }

    public bool Unpin(string text) => _items.Remove(text);

    public string Serialize() => JsonSerializer.Serialize(_items);

    public static ClipPins Parse(string? json)
    {
        var p = new ClipPins();
        if (string.IsNullOrWhiteSpace(json)) return p;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            foreach (var t in list.Where(CanPin).Distinct().Take(Max)) p._items.Add(t);
        }
        catch (JsonException) { }
        return p;
    }
}
