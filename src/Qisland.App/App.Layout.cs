using System.IO;
using Qisland.Core;
using Forms = System.Windows.Forms;

namespace Qisland.App;

/// <summary>停靠位置 / 极简模式（layout.txt）和托盘里的「停靠」菜单。</summary>
public sealed partial class App
{
    private static string LayoutFile => Path.Combine(DataDir, "layout.txt");

    private static void LoadLayout()
    {
        var pref = IslandLayout.Parse(File.Exists(LayoutFile) ? SafeRead(LayoutFile) : null);
        IslandWindow.LoadLayout(pref);
        if (pref.Dock != DockSide.Top || pref.Minimal) Log($"停靠：{DockName(pref.Dock)}{(pref.Minimal ? " · 极简" : "")}");
    }

    internal static void SaveLayout(DockSide dock, bool minimal)
    {
        try { File.WriteAllText(LayoutFile, IslandLayout.Serialize(dock, minimal)); }
        catch (Exception ex) { Log("保存 layout.txt: " + ex.Message); }
    }

    internal static string DockName(DockSide d) => d switch { DockSide.Left => "左侧", DockSide.Right => "右侧", _ => "顶部居中" };

    public void SetLayout(DockSide dock, bool minimal) => _window?.ApplyLayout(dock, minimal);

    private Forms.ToolStripMenuItem DockMenu()
    {
        var m = new Forms.ToolStripMenuItem("停靠");
        foreach (var d in new[] { DockSide.Top, DockSide.Left, DockSide.Right })
        {
            var item = new Forms.ToolStripMenuItem(DockName(d)) { Tag = d, ToolTipText = d == DockSide.Top ? "屏幕顶部正中（默认）" : $"贴在屏幕{DockName(d)}边缘，收起是竖向小胶囊，展开从边上滑出" };
            item.Click += (_, _) => Dispatcher.BeginInvoke(() => SetLayout(d, IslandWindow.Minimal));
            m.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(new Forms.ToolStripSeparator());
        var mini = new Forms.ToolStripMenuItem("极简模式") { ToolTipText = "没事时只留一小段线，指针靠近变大、停一下展开；有通知、放音乐时照常显示" };
        mini.Click += (_, _) => Dispatcher.BeginInvoke(() => SetLayout(IslandWindow.Dock, !IslandWindow.Minimal));
        m.DropDownItems.Add(mini);
        m.DropDownOpening += (_, _) =>
        {
            foreach (var it in m.DropDownItems.OfType<Forms.ToolStripMenuItem>())
                it.Checked = it.Tag is DockSide d ? d == IslandWindow.Dock : IslandWindow.Minimal;
        };
        return m;
    }
}

public sealed partial class App
{
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    internal CalendarWatcher? CalendarService => _cal;
    internal IslandWindow? Island => _window;

    /// <summary>打开（或切到）统一设置窗。</summary>
    public void OpenSettings(int category = 0)
    {
        if (_trayMenu == null) return;
        foreach (System.Windows.Window w in Windows) if (w is SettingsWindow sw) { sw.Show(category); return; }
        new SettingsWindow(this, _trayMenu, category).Show();
    }
}
