using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Qisland.App;

/// <summary>
/// 剪贴板历史：只记文字和文件，最多 30 条，只放在内存里（退出即清空，不落盘）。
/// 密码管理器复制的内容会带 ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory=0 标记，直接跳过。
/// </summary>
public sealed partial class IslandWindow
{
    private sealed record ClipItem(string Text, string[]? Files, DateTime At);
    private readonly List<ClipItem> _clips = new();
    private string? _selfCopy;
    private DateTime _copiedFlash = DateTime.MinValue;

    private void SetupClipboard()
    {
        if (SnapshotMode) return;
        if (!AddClipboardFormatListener(_hwnd)) App.Log("剪贴板监听失败");
    }

    private void OnClipboardUpdate() =>
        Dispatcher.BeginInvoke(async () => { await Task.Delay(80); ReadClipboard(); });

    private void ReadClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4]; ms.Read(b, 0, 4);
                if (BitConverter.ToInt32(b) == 0) return;
            }
            ClipItem? item = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                item = new ClipItem(string.Join("  ", files.Select(Path.GetFileName)), files, DateTime.Now);
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string t && !string.IsNullOrWhiteSpace(t) && t.Length < 100_000)
                item = new ClipItem(t, null, DateTime.Now);
            if (item == null) return;
            if (item.Files == null && item.Text == _selfCopy) { _selfCopy = null; return; }
            _clips.RemoveAll(c => c.Text == item.Text);
            _clips.Insert(0, item);
            if (_clips.Count > 30) _clips.RemoveRange(30, _clips.Count - 30);
            if (_mode == Mode.Expanded && _page == Page.Clip && !_animating) RebuildExpanded(); else _expDirty = true;
        }
        catch { /* 剪贴板被别的程序占着，忽略这一次 */ }
    }

    private void CopyBack(ClipItem c)
    {
        try
        {
            if (c.Files != null)
            {
                var sc = new System.Collections.Specialized.StringCollection();
                sc.AddRange(c.Files.Where(File.Exists).ToArray());
                if (sc.Count > 0) Clipboard.SetFileDropList(sc);
            }
            else { _selfCopy = c.Text; Clipboard.SetText(c.Text); }
            _clips.Remove(c);
            _clips.Insert(0, c with { At = DateTime.Now });
            _copiedFlash = DateTime.Now;
            RebuildExpanded();
        }
        catch (Exception ex) { App.Log("写剪贴板失败: " + ex.Message); }
    }

    private void BuildClip(StackPanel stack)
    {
        var head = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Glyph("\uE77F", 14, Theme.Blue));
        var tt = Txt(13, FontWeights.SemiBold, Theme.Primary); tt.Text = "剪贴板"; tt.Margin = new Thickness(7, 0, 0, 0);
        title.Children.Add(tt);
        var n = Txt(12, FontWeights.Normal, Theme.Tertiary);
        n.Text = DateTime.Now - _copiedFlash < TimeSpan.FromSeconds(2) ? "  已复制，去粘贴吧" : _clips.Count > 0 ? $"  {_clips.Count} 条 · 点一下重新复制" : "";
        title.Children.Add(n);
        head.Children.Add(title);
        if (_clips.Count > 0)
        {
            var clear = Txt(12, FontWeights.Medium, Theme.Secondary); clear.Text = "清空"; clear.Cursor = Cursors.Hand;
            clear.HorizontalAlignment = HorizontalAlignment.Right; clear.VerticalAlignment = VerticalAlignment.Center;
            clear.MouseLeftButtonUp += (_, e) => { e.Handled = true; _clips.Clear(); RebuildExpanded(); };
            head.Children.Add(clear);
        }
        stack.Children.Add(head);

        if (_clips.Count == 0)
        {
            var empty = Txt(12.5, FontWeights.Normal, Theme.Tertiary);
            empty.Text = "复制过的文字和文件会出现在这里\n只保存在内存里，退出就清空";
            empty.TextAlignment = TextAlignment.Center;
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 18, 0, 16);
            stack.Children.Add(empty);
            return;
        }
        var list = new StackPanel();
        foreach (var c in _clips.Take(8)) list.Children.Add(ClipRow(c));
        stack.Children.Add(list);
    }

    private FrameworkElement ClipRow(ClipItem c)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand, MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Glyph(c.Files != null ? "\uE8B7" : LooksLikeUrl(c.Text) ? "\uE71B" : "\uE8C4", 13, Theme.Secondary);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        g.Children.Add(icon);
        var oneLine = string.Join(" ", c.Text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        var tx = Txt(13, FontWeights.Normal, Theme.Primary);
        tx.Text = oneLine.Length > 140 ? oneLine[..140] : oneLine;
        tx.TextTrimming = TextTrimming.CharacterEllipsis; tx.VerticalAlignment = VerticalAlignment.Center;
        tx.ToolTip = c.Text.Length > 600 ? c.Text[..600] + "…" : c.Text;
        Grid.SetColumn(tx, 1);
        g.Children.Add(tx);
        var ago = Txt(11.5, FontWeights.Normal, Theme.Tertiary);
        ago.Text = Ago(c.At); ago.VerticalAlignment = VerticalAlignment.Center; ago.Margin = new Thickness(10, 0, 8, 0);
        Grid.SetColumn(ago, 2);
        g.Children.Add(ago);
        AddHover(g);
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyBack(c); };
        g.MouseRightButtonUp += (_, e) => { e.Handled = true; _clips.Remove(c); RebuildExpanded(); };
        return g;
    }

    private static bool LooksLikeUrl(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalSeconds < 60 ? "刚刚" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} 分钟前" : d.TotalHours < 24 ? $"{(int)d.TotalHours} 小时前" : t.ToString("M/d");
    }

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr h);
}
