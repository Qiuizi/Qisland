using System.IO;
using Qisland.Core;
using Forms = System.Windows.Forms;

namespace Qisland.App;

/// <summary>停靠位置 / 极简模式（layout.txt）。托盘里不再放停靠菜单，在设置 → 外观与停靠里改。</summary>
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

    /// <summary>托盘「锁定点击穿透」：勾上后整座岛不接鼠标（快捷键同一开关）。打开菜单时按岛的实际状态勾选。</summary>
    private Forms.ToolStripMenuItem ClickLockMenu(Forms.ContextMenuStrip host)
    {
        var it = new Forms.ToolStripMenuItem("锁定点击穿透") { ToolTipText = "整座岛不再接鼠标，点击、悬停、滚轮都落到下面的窗口；通知和音乐照常显示。再点一次或按快捷键解除，重启后自动解除" };
        it.Click += (_, _) => Dispatcher.BeginInvoke(() => _window?.ToggleClickLock("托盘"));
        host.Opening += (_, _) =>
        {
            it.Checked = _window?.ClickLocked == true;
            it.ShortcutKeyDisplayString = _window?.ClickLockHotkeyActive == true ? IslandWindow.ClickLockHotkeyText : null;
        };
        return it;
    }
}

public sealed partial class App
{
    /// <summary>「功能菜单」：不显示在托盘上，设置窗把它的每一项画成设置行（直接调用同一个菜单项，所以同意弹窗、备份、改动账本这些流程不变）。托盘右键只显示 SetupTray 里另建的精简菜单。</summary>
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private Action? _refreshTray;

    /// <summary>设置窗打开 / 重画前调用：把「开机自启」「检查更新」「跳过这个新版本」的文字和勾选刷成当前状态。</summary>
    internal void RefreshTrayStates() => _refreshTray?.Invoke();
    internal CalendarWatcher? CalendarService => _cal;
    internal AiUsageService? UsageService => _usage;
    internal MailOtpService? MailService => _mail;
    internal PeripheralWatcher? DeviceService => _devices;
    internal IslandWindow? Island => _window;

    /// <summary>--snapshot：把精简后的托盘右键菜单离屏画成 PNG（不弹菜单、不建托盘图标）：没有新版本 / 有新版本两张。</summary>
    internal void SnapshotTray(string dir)
    {
        var auto = new System.Windows.Forms.ToolStripMenuItem("开机自启") { Checked = true };
        var check = new System.Windows.Forms.ToolStripMenuItem("");
        var skip = new System.Windows.Forms.ToolStripMenuItem("");
        var found = false;
        void Fill()
        {
            check.Text = found ? $"检查更新（当前 v{Updater.Current} · 新版 v9.9.9 可用）" : $"检查更新（当前 v{Updater.Current}）";
            skip.Enabled = found;
            skip.Text = found ? "跳过这个新版本（v9.9.9）" : "跳过这个新版本";
        }
        var old = _refreshTray;
        _refreshTray = Fill;
        try
        {
            foreach (var (name, f) in new[] { ("50-tray-menu", false), ("51-tray-menu-update", true) })
            {
                found = f; Fill();
                var menu = BuildSlimTray(auto, check, skip);
                ((Action)menu.Tag!)();
                foreach (var it in menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>())
                    if (it.Text.StartsWith("锁定", StringComparison.Ordinal)) it.ShortcutKeyDisplayString = IslandWindow.ClickLockHotkeyText;
                menu.PerformLayout();
                menu.Size = menu.GetPreferredSize(System.Drawing.Size.Empty);
                _ = menu.Handle;
                using var bmp = new System.Drawing.Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(bmp, new System.Drawing.Rectangle(System.Drawing.Point.Empty, menu.Size));
                bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                menu.Dispose();
            }
        }
        finally { _refreshTray = old; }
    }
    internal LyricsService? LyricsSvc => _lyrics;

    /// <summary>打开（或切到）统一设置窗。</summary>
    public void OpenSettings(int category = 0)
    {
        if (_trayMenu == null) return;
        foreach (System.Windows.Window w in Windows) if (w is SettingsWindow sw) { sw.Show(category); return; }
        new SettingsWindow(this, _trayMenu, category).Show();
    }
}
