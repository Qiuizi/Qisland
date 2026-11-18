using System.Globalization;
using System.Text.Json;

namespace Qisland.Core;

public sealed record WeatherSnapshot(
    double Temperature, int Code, double FeelsLike, double Humidity,
    double WindSpeed, double Precipitation, double High, double Low);

public static class WeatherParser
{
    public static WeatherSnapshot? ParseForecast(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryDouble(current, "temperature_2m", out var temp)) return null;
        var code = TryInt(current, "weather_code");
        var feels = TryDouble(current, "apparent_temperature", out var feel) ? feel : temp;
        var humidity = TryDouble(current, "relative_humidity_2m", out var hum) ? hum : 0;
        var wind = TryDouble(current, "wind_speed_10m", out var w) ? w : 0;
        var precip = TryDouble(current, "precipitation", out var p) ? p : 0;
        var high = temp;
        var low = temp;
        if (root.TryGetProperty("daily", out var daily))
        {
            if (daily.TryGetProperty("temperature_2m_max", out var highs) && highs.GetArrayLength() > 0)
                high = highs[0].GetDouble();
            if (daily.TryGetProperty("temperature_2m_min", out var lows) && lows.GetArrayLength() > 0)
                low = lows[0].GetDouble();
        }
        return new WeatherSnapshot(temp, code, feels, humidity, wind, precip, high, low);
    }

    public static string Describe(int code) => code switch
    {
        0 => "晴",
        1 or 2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        >= 51 and <= 67 => "雨",
        >= 71 and <= 77 => "雪",
        >= 80 and <= 82 => "阵雨",
        85 or 86 => "阵雪",
        >= 95 => "雷雨",
        _ => "多云",
    };

    public static string FormatCompact(WeatherSnapshot weather, string? city = null)
    {
        var range = weather.Low < weather.High
            ? $" {weather.Low.ToString("0", CultureInfo.InvariantCulture)}/{weather.High.ToString("0", CultureInfo.InvariantCulture)}°"
            : "";
        var place = string.IsNullOrWhiteSpace(city) ? "" : city.Trim() + " ";
        return place + weather.Temperature.ToString("0", CultureInfo.InvariantCulture) + "° " + Describe(weather.Code) + range;
    }

    private static int TryInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static bool TryDouble(JsonElement element, string name, out double number)
    {
        number = 0;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return false;
        number = value.GetDouble();
        return true;
    }
}

public sealed class WeatherProvider : IActivityProvider
{
    private IslandActivity? _current;
    public string City { get; set; } = "";
    public string Id => "weather";
    public string DisplayName => "天气";
    public bool Enabled { get; set; } = true;

    public void ApplyForecast(string json)
    {
        var snapshot = WeatherParser.ParseForecast(json);
        if (snapshot is null) return;
        _current = new IslandActivity(
            "weather:" + City,
            "weather",
            WeatherParser.FormatCompact(snapshot, City),
            $"体感 {snapshot.FeelsLike:0}° · 湿度 {snapshot.Humidity:0}%",
            ActivityKind.Info,
            12,
            DurationSeconds: 1800,
            ResetTtl: true);
    }

    public IReadOnlyList<IslandActivity> Snapshot()
        => Enabled && _current is not null ? new[] { _current } : Array.Empty<IslandActivity>();
}

public enum TimerMode { Idle, Countdown, PomodoroWork, PomodoroBreak }

/// <summary>倒计时和番茄钟。调用方传入当前时间，方便单测快进。</summary>
public sealed class TimerEngine
{
    private DateTime _end;
    private TimeSpan _remaining;
    public TimerMode Mode { get; private set; } = TimerMode.Idle;
    public bool IsPaused { get; private set; }
    public TimeSpan WorkLength { get; private set; } = TimeSpan.FromMinutes(25);
    public TimeSpan BreakLength { get; private set; } = TimeSpan.FromMinutes(5);

    public TimeSpan Remaining => _remaining < TimeSpan.Zero ? TimeSpan.Zero : _remaining;

    public string ClockText
    {
        get
        {
            var r = Remaining;
            return r.TotalHours >= 1
                ? $"{(int)r.TotalHours:00}:{r.Minutes:00}:{r.Seconds:00}"
                : $"{(int)r.TotalMinutes:00}:{r.Seconds:00}";
        }
    }

    public void StartCountdown(TimeSpan duration, DateTime now)
    {
        if (duration < TimeSpan.FromSeconds(1)) duration = TimeSpan.FromSeconds(1);
        Mode = TimerMode.Countdown;
        Arm(duration, now);
    }

    public void StartPomodoro(TimeSpan work, TimeSpan breakLength, DateTime now)
    {
        WorkLength = work < TimeSpan.FromSeconds(1) ? TimeSpan.FromMinutes(25) : work;
        BreakLength = breakLength < TimeSpan.FromSeconds(1) ? TimeSpan.FromMinutes(5) : breakLength;
        Mode = TimerMode.PomodoroWork;
        Arm(WorkLength, now);
    }

    public void Pause(DateTime now)
    {
        if (Mode == TimerMode.Idle || IsPaused) return;
        _remaining = _end - now;
        if (_remaining < TimeSpan.Zero) _remaining = TimeSpan.Zero;
        IsPaused = true;
    }

    public void Resume(DateTime now)
    {
        if (Mode == TimerMode.Idle || !IsPaused) return;
        IsPaused = false;
        _end = now + _remaining;
    }

    /// <summary>返回 true 表示这一拍刚好结束一个阶段。</summary>
    public bool Tick(DateTime now)
    {
        if (Mode == TimerMode.Idle || IsPaused) return false;
        _remaining = _end - now;
        if (_remaining > TimeSpan.Zero) return false;
        _remaining = TimeSpan.Zero;
        if (Mode == TimerMode.PomodoroWork)
        {
            Mode = TimerMode.PomodoroBreak;
            Arm(BreakLength, now);
            return true;
        }
        var finished = Mode;
        Mode = TimerMode.Idle;
        IsPaused = false;
        return finished != TimerMode.Idle;
    }

    private void Arm(TimeSpan duration, DateTime now)
    {
        IsPaused = false;
        _remaining = duration;
        _end = now + duration;
    }
}

public sealed class TimerProvider : IActivityProvider
{
    private readonly TimerEngine _engine = new();
    public string Id => "timer";
    public string DisplayName => "计时";
    public bool Enabled { get; set; } = true;
    public TimerEngine Engine => _engine;

    public IReadOnlyList<IslandActivity> Snapshot()
    {
        if (!Enabled || _engine.Mode == TimerMode.Idle) return Array.Empty<IslandActivity>();
        var title = _engine.Mode switch
        {
            TimerMode.Countdown => "倒计时",
            TimerMode.PomodoroWork => "专注",
            TimerMode.PomodoroBreak => "休息",
            _ => "计时",
        };
        if (_engine.IsPaused) title += " 已暂停";
        return new[]
        {
            new IslandActivity("timer:current", "timer", title, _engine.ClockText, ActivityKind.Running, 30,
                Icon: "\uE823", DurationSeconds: 3600, ResetTtl: true),
        };
    }
}

public sealed record TodoEntry(string Id, string Text, bool Done, DateTime CreatedAt);

public sealed class TodoStore
{
    private readonly string _file;
    private readonly List<TodoEntry> _items = new();
    public event Action? Changed;
    public TodoStore(string file)
    {
        _file = file;
        Load();
    }

    public IReadOnlyList<TodoEntry> Items => _items.ToList();

    public TodoEntry Add(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("待办不能为空。", nameof(text));
        var item = new TodoEntry(Guid.NewGuid().ToString("N"), text, false, DateTime.Now);
        _items.Insert(0, item);
        Save();
        return item;
    }

    public bool Toggle(string id)
    {
        var index = _items.FindIndex(x => x.Id == id);
        if (index < 0) return false;
        _items[index] = _items[index] with { Done = !_items[index].Done };
        Save();
        return true;
    }

    public bool Remove(string id)
    {
        var removed = _items.RemoveAll(x => x.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        var items = JsonSerializer.Deserialize<List<TodoEntry>>(File.ReadAllText(_file));
        if (items is null) return;
        _items.Clear();
        _items.AddRange(items);
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
        File.Move(tmp, _file, overwrite: true);
        Changed?.Invoke();
    }
}

public sealed class TodoProvider : IActivityProvider
{
    private readonly TodoStore _store;
    public string Id => "todo";
    public string DisplayName => "待办";
    public bool Enabled { get; set; } = true;
    public TodoStore Store => _store;

    public TodoProvider(TodoStore store) => _store = store;

    public IReadOnlyList<IslandActivity> Snapshot()
    {
        if (!Enabled) return Array.Empty<IslandActivity>();
        var pending = _store.Items.Where(i => !i.Done).Take(3).ToList();
        if (pending.Count == 0) return Array.Empty<IslandActivity>();
        return pending.Select(item => new IslandActivity(
            "todo:" + item.Id, "todo", item.Text, "", ActivityKind.Info, 22,
            Icon: "\uE73E", DurationSeconds: 600, ResetTtl: true)).ToList();
    }
}

/// <summary>媒体仍由宿主的 SMTC 绘制。这里只在需要排队时给出一条摘要。</summary>
public sealed class MediaActivityProvider : IActivityProvider
{
    private IslandActivity? _current;
    public string Id => "media";
    public string DisplayName => "媒体";
    public bool Enabled { get; set; } = true;

    public void Update(string? title, string? artist, bool playing)
    {
        if (!playing || string.IsNullOrWhiteSpace(title))
        {
            _current = null;
            return;
        }
        _current = new IslandActivity(
            "media:now", "media", title.Trim(), artist?.Trim() ?? "", ActivityKind.Running, 25,
            Icon: "\uE8D6", DurationSeconds: 30, ResetTtl: true);
    }

    public IReadOnlyList<IslandActivity> Snapshot()
        => Enabled && _current is not null ? new[] { _current } : Array.Empty<IslandActivity>();
}

/// <summary>WSL 把事件送回 Windows 时的地址顺序。镜像模式走 127.0.0.1；NAT 再试宿主地址，失败则写收件箱。</summary>
public static class WslEndpoint
{
    public static IReadOnlyList<string> CandidateBases(string? resolvConf, string? defaultGateway, int port)
    {
        var list = new List<string> { "http://127.0.0.1:" + port };
        var host = Nameserver(resolvConf);
        if (string.IsNullOrEmpty(host)) host = Gateway(defaultGateway);
        if (!string.IsNullOrEmpty(host) && host != "127.0.0.1" && host != "::1")
            list.Add("http://" + host + ":" + port);
        return list;
    }

    public static string Nameserver(string? resolvConf)
    {
        if (string.IsNullOrWhiteSpace(resolvConf)) return "";
        foreach (var raw in resolvConf.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("nameserver", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return parts[1].Trim();
        }
        return "";
    }

    public static string Gateway(string? routeLine)
    {
        if (string.IsNullOrWhiteSpace(routeLine)) return "";
        var parts = routeLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var via = Array.IndexOf(parts, "via");
        return via >= 0 && via + 1 < parts.Length ? parts[via + 1] : "";
    }
}
