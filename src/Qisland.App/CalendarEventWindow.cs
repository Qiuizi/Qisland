using System.Windows;
using System.Windows.Controls;
using Qisland.Core;

namespace Qisland.App;

/// <summary>新建 / 修改 / 删除一个本机日程（和 .ics 订阅一起显示、一起提前 10 分钟提醒）。</summary>
public sealed class CalendarEventWindow : Window
{
    public CalendarEventWindow(CalendarWatcher cal, LocalEvent? existing = null, DateTime? day = null)
    {
        var now = DateTime.Now;
        var startDefault = (day ?? now.Date) + (day == null || day == now.Date ? TimeSpan.FromHours(Math.Clamp(now.Hour + 1, 8, 22)) : TimeSpan.FromHours(9));
        var e = existing ?? new LocalEvent(LocalCalendar.NewId(), "", "", startDefault, startDefault.AddHours(1), false);
        var allDay = e.AllDay; var weekly = e.Weekly;

        var sp = new StackPanel();
        sp.Children.Add(DialogKit.Label("标题"));
        var title = DialogKit.Box(e.Title);
        sp.Children.Add(title);
        sp.Children.Add(DialogKit.Label("日期和时间"));
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var date = DialogKit.Date(e.Start.Date);
        var start = DialogKit.Box(e.Start.ToString("HH:mm"), 70); start.Margin = new Thickness(8, 0, 0, 0);
        var dash = DialogKit.Text("–", 13, FontWeights.Normal, DialogKit.Sub); dash.Margin = new Thickness(6, 5, 6, 0);
        var end = DialogKit.Box(e.End.ToString("HH:mm"), 70);
        line.Children.Add(date); line.Children.Add(start); line.Children.Add(dash); line.Children.Add(end);
        sp.Children.Add(line);
        void SyncTimes() { start.IsEnabled = end.IsEnabled = !allDay; start.Opacity = end.Opacity = allDay ? 0.4 : 1; }
        SyncTimes();
        sp.Children.Add(new Border { Height = 10 });
        sp.Children.Add(DialogKit.Row("全天", null, DialogKit.Switch(allDay, v => { allDay = v; SyncTimes(); })));
        sp.Children.Add(DialogKit.Row("每周重复", "比如每周的组会、固定的课外活动", DialogKit.Switch(weekly, v => weekly = v)));
        sp.Children.Add(DialogKit.Label("地点（可不填）"));
        var loc = DialogKit.Box(e.Location);
        sp.Children.Add(loc);
        var err = DialogKit.Text("", 12, FontWeights.SemiBold, DialogKit.Danger); err.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(err);
        var note = DialogKit.Text("本机日程加密保存在这台电脑上，不会上传。开始前 10 分钟岛上提醒。", 11.5, FontWeights.Normal, DialogKit.Sub);
        note.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(note);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (existing != null) btns.Children.Add(DialogKit.Button("删除", () => { cal.DeleteLocal(existing.Id); Close(); }, danger: true));
        btns.Children.Add(DialogKit.Button("取消", Close));
        btns.Children.Add(DialogKit.Button("保存", () =>
        {
            if (title.Text.Trim().Length == 0) { err.Text = "写个标题"; return; }
            if (date.SelectedDate is not { } d) { err.Text = "选一个日期"; return; }
            DateTime s, en;
            if (allDay) { s = d.Date; en = d.Date.AddDays(1); }
            else
            {
                if (!DialogKit.TryTime(start.Text, out var ts) || !DialogKit.TryTime(end.Text, out var te)) { err.Text = "时间写成 14:00 这样"; return; }
                s = d.Date + ts; en = d.Date + te;
                if (en <= s) en = en.AddDays(1);   // 跨过零点
            }
            var ev = e with { Title = title.Text.Trim(), Location = loc.Text.Trim(), Start = s, End = en, AllDay = allDay, Weekly = weekly };
            if (!cal.SaveLocal(ev)) { err.Text = $"没保存上（最多 {LocalCalendar.MaxEvents} 个日程）"; return; }
            Close();
        }, primary: true));
        sp.Children.Add(btns);
        DialogKit.Shell(this, existing == null ? "新建日程" : "编辑日程", sp, 360);
        Loaded += (_, _) => { Activate(); title.Focus(); };
    }
}
