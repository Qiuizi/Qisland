using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 健康：喝水记录（water.txt，只存今天的杯数）、喝水提醒、久坐提醒。
/// 两个提醒默认关（托盘 → 岛上提示里打开）；判断“在用电脑”只看系统的最后输入时间（GetLastInputInfo），不看按了什么。
/// 思路参考小糖岛仔（MIT）的健康页，C# 重写。
/// </summary>
public sealed partial class App
{
    private static string HealthFile => Path.Combine(DataDir, "health.txt");
    private static HealthSettings? _health;
    /// <summary>健康提醒设置（health.txt；设置窗里改）。</summary>
    public static HealthSettings Health
    {
        get => _health ??= HealthSettings.Parse(File.Exists(HealthFile) ? SafeRead(HealthFile) : null);
        set
        {
            _health = value.Clamp();
            try { File.WriteAllText(HealthFile, _health.Serialize()); } catch (Exception ex) { Log("保存 health.txt: " + ex.Message); }
        }
    }
    private DateTime _todoSince = DateTime.Now;
    private int _healthMinute = -1;
    private static string WaterFile => Path.Combine(DataDir, "water.txt");
    private static WaterLog? _water;
    public static WaterLog Water => _water ??= WaterLog.Parse(File.Exists(WaterFile) ? SafeRead(WaterFile) : null, DateTime.Now);
    private DateTime _waterNudge = DateTime.MinValue, _sitNudge = DateTime.MinValue, _activeSince = DateTime.Now;
    private DispatcherTimer? _healthTimer;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>距离最后一次键盘 / 鼠标输入多久了。</summary>
    public static TimeSpan IdleTime()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
        var ms = unchecked((uint)Environment.TickCount - li.dwTime);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>连续使用电脑的起点（离开超过 5 分钟就重新算）。</summary>
    public DateTime ActiveSince => _activeSince;

    private void StartHealth()
    {
        // 30 秒看一次（待办提醒按分钟准；健康每分钟一次），几乎不耗电
        _healthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _healthTimer.Tick += (_, _) =>
        {
            TodoTick();
            if (DateTime.Now.Minute == _healthMinute) return;
            _healthMinute = DateTime.Now.Minute;
            HealthTick();
        };
        _healthTimer.Start();
    }

    private void HealthTick()
    {
        var now = DateTime.Now;
        var idle = IdleTime();
        _activeSince = HealthClock.TrackActivity(Health, _activeSince, idle, now);
        var w = Water;
        w.Roll(now);
        if (Features.On(Features.Water) && HealthClock.WaterDue(Health, w, _waterNudge, now) && idle < TimeSpan.FromMinutes(2))
        {
            _waterNudge = now;
            _store.Upsert(new IslandActivity("health:water", "health", "该喝水了",
                $"今天 {w.TotalMl}/{Health.GoalMl} ml · 点一下记一杯 {Health.ServingMl} ml", ActivityKind.Info, 30, DurationSeconds: 12, ResetTtl: true));
        }
        if (Features.On(Features.Sit) && HealthClock.SitDue(Health, _activeSince, _sitNudge, idle, now))
        {
            _sitNudge = now;
            var mins = (int)(now - _activeSince).TotalMinutes;
            _store.Upsert(new IslandActivity("health:sit", "health", $"已经连续坐了 {mins} 分钟",
                "起来走走、看看远处，离开 5 分钟就重新计时", ActivityKind.Info, 30, DurationSeconds: 12, ResetTtl: true));
        }
        _window?.OnHealthChanged();
    }

    /// <summary>记一杯（ml 可以是负数：撤销）。</summary>
    public static void DrinkWater(int ml)
    {
        Water.Drink(ml, DateTime.Now);
        try { File.WriteAllText(WaterFile, Water.Serialize()); }
        catch (Exception ex) { Log("保存 water.txt: " + ex.Message); }
    }

    /// <summary>待办到点提醒：上次检查之后到点的弹一下（启动前已过的不补弹，免得一开机一串）。</summary>
    private void TodoTick()
    {
        var now = DateTime.Now;
        var since = _todoSince;
        _todoSince = now;
        if (_todo == null) return;
        foreach (var it in _todo.Items)
        {
            if (it.Done) continue;
            var m = TodoMeta.Parse(it.Text);
            if (!m.DueBetween(since, now)) continue;
            _store.Upsert(new IslandActivity("todo:remind:" + m.Title.GetHashCode().ToString("x8"), "todo", m.Title,
                m.Repeats ? $"待办提醒 · {m.When(now)} · 点一下去待办" : "待办提醒 · 点一下去待办", ActivityKind.Warning, 70, DurationSeconds: 90, ResetTtl: true));
            Log("待办提醒");
        }
    }

    private bool OpenHealth(LiveActivity a)
    {
        if (a.Id.StartsWith("todo:remind:", StringComparison.Ordinal)) { _store.Remove(a.Id); _window?.OpenTodoPage(); return true; }
        if (a.Id == "health:water")
        {
            DrinkWater(Health.ServingMl);
            _store.Remove(a.Id);
            _window?.OnHealthChanged();
            return true;
        }
        if (a.Id == "health:sit") { _store.Remove(a.Id); return true; }
        return false;
    }
}
