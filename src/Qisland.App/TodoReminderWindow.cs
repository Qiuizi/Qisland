using System.Windows;
using System.Windows.Controls;
using Qisland.Core;

namespace Qisland.App;

/// <summary>给一条待办设提醒时间和每周重复（写回 todo.md 那一行的 ⏰ / 🔁）。</summary>
public sealed class TodoReminderWindow : Window
{
    public TodoReminderWindow(TodoFile todo, TodoFileItem item)
    {
        var meta = TodoMeta.Parse(item.Text);
        var now = DateTime.Now;
        var due = meta.Due ?? (now.Hour < 20 ? now.Date.AddHours(Math.Max(now.Hour + 1, 9)) : now.Date.AddDays(1).AddHours(9));
        var days = new HashSet<DayOfWeek>(meta.Repeat);

        var sp = new StackPanel();
        sp.Children.Add(DialogKit.Text(meta.Title, 14, FontWeights.SemiBold, DialogKit.Fg));
        sp.Children.Add(DialogKit.Label("提醒时间"));
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var date = DialogKit.Date(due.Date);
        var time = DialogKit.Box(due.ToString("HH:mm"), 80); time.Margin = new Thickness(8, 0, 0, 0);
        line.Children.Add(date); line.Children.Add(time);
        sp.Children.Add(line);
        sp.Children.Add(DialogKit.Label("每周重复（可不选）"));
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var day = d;
            chips.Children.Add(DialogKit.Chip("日一二三四五六"[(int)d].ToString(), days.Contains(d), on => { if (on) days.Add(day); else days.Remove(day); }));
        }
        sp.Children.Add(chips);
        var hint = DialogKit.Text("到点时岛上弹一下提醒；重复的待办勾掉后会自动挪到下一次。", 11.5, FontWeights.Normal, DialogKit.Sub);
        hint.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(hint);
        var err = DialogKit.Text("", 12, FontWeights.SemiBold, DialogKit.Danger);
        sp.Children.Add(err);

        // 没写进去时窗口留着、输入不动，告诉用户为什么
        bool Saved(TodoEditResult r)
        {
            err.Text = r switch
            {
                TodoEditResult.Conflict => "todo.md 刚被改过，找不到这一条（或有两条一样的），没有保存。可以关掉窗口重新打开再设。",
                TodoEditResult.WriteFailed => "写入 todo.md 失败（文件可能被占用），再点一次保存试试。",
                _ => "",
            };
            return r == TodoEditResult.Ok;
        }

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (meta.Due != null || meta.Repeats)
            btns.Children.Add(DialogKit.Button("清除提醒", () => { if (Saved(todo.SetText(item, (meta with { Due = null, Repeat = [] }).ToText()))) Close(); }, danger: true));
        btns.Children.Add(DialogKit.Button("取消", Close));
        btns.Children.Add(DialogKit.Button("保存", () =>
        {
            if (date.SelectedDate is not { } d) { err.Text = "选一个日期"; return; }
            if (!DialogKit.TryTime(time.Text, out var t)) { err.Text = "时间写成 20:00 这样"; return; }
            var at = d.Date + t;
            var rep = days.OrderBy(x => x == DayOfWeek.Sunday ? 7 : (int)x).ToList();
            // 选了星期但日期不在其中：挪到这一天起最近的那次
            if (rep.Count > 0 && !rep.Contains(at.DayOfWeek)) at = TodoMeta.NextWeekly(at.AddSeconds(-1), rep, t);
            if (Saved(todo.SetText(item, (meta with { Due = at, Repeat = rep }).ToText()))) Close();
        }, primary: true));
        sp.Children.Add(btns);
        DialogKit.Shell(this, "待办提醒", sp, 330);
        Loaded += (_, _) => Activate();
    }
}
