namespace Qisland.Core;

/// <summary>
/// 按城市缓存的结果 + 当前选中的城市。请求发出时记下城市（<see cref="Current"/>），结果回来按那个城市入缓存；
/// 只有那个城市仍是当前选中时才返回 true（才更新界面 / 发提醒）。切到没缓存的城市时 <see cref="Shown"/> 为空，不沿用别的城市的数据。
/// </summary>
public sealed class CityResults<T> where T : class
{
    private readonly Dictionary<string, T> _cache = new(StringComparer.Ordinal);
    public string Current { get; private set; } = "";

    public void Select(string key) => Current = key ?? "";

    public bool TryGet(string key, out T value) => _cache.TryGetValue(key, out value!);

    /// <summary>当前城市该显示的结果（没有就是 null）。</summary>
    public T? Shown => _cache.GetValueOrDefault(Current);

    /// <summary>请求（发出时城市为 requestKey）拿到了结果：缓存到它自己名下；返回它是否仍是当前城市。</summary>
    public bool Commit(string requestKey, T value)
    {
        _cache[requestKey] = value;
        return requestKey == Current;
    }
}
