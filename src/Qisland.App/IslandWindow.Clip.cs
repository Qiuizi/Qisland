using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Qisland.App;

/// <summary>
/// 剪贴板历史：文字、文件和图片（图片最多留 4 张），最多 30 条，只放在内存里（退出即清空，不落盘）。
/// 密码管理器复制的内容会带 ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory=0 标记，直接跳过。
/// </summary>
public sealed partial class IslandWindow
{
    private sealed record ClipItem(string Text, string[]? Files, DateTime At, System.Windows.Media.Imaging.BitmapSource? Image = null);
    private readonly Dictionary<ClipItem, string> _translated = new();
    private readonly HashSet<ClipItem> _translating = new();
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
            else if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is { } img && (long)img.PixelWidth * img.PixelHeight <= 40_000_000)
            {
                img.Freeze();
                // 同一张图连着复制两次不重复记；用尺寸 + 采样像素粗判
                var key = $"图片 {img.PixelWidth}×{img.PixelHeight}";
                item = new ClipItem(key, null, DateTime.Now, img);
                if (_clips.FirstOrDefault() is { Image: { } last } && SameImage(last, img)) return;
            }
            if (item == null) return;
            if (item.Files == null && item.Text == _selfCopy) { _selfCopy = null; return; }
            if (item.Image == null) _clips.RemoveAll(c => c.Image == null && c.Text == item.Text);
            _clips.Insert(0, item);
            foreach (var old in _clips.Where(c => c.Image != null).Skip(4).ToList()) _clips.Remove(old);
            if (_clips.Count > 30) _clips.RemoveRange(30, _clips.Count - 30);
            if (_mode == Mode.Expanded && _page == Page.Clip && !_animating) RebuildExpanded(); else _expDirty = true;
        }
        catch { /* 剪贴板被别的程序占着，忽略这一次 */ }
    }

    private void CopyBack(ClipItem c)
    {
        try
        {
            if (c.Image != null) Clipboard.SetImage(c.Image);
            else if (c.Files != null)
            {
                var sc = new System.Collections.Specialized.StringCollection();
                sc.AddRange(c.Files.Where(File.Exists).ToArray());
                if (sc.Count > 0) Clipboard.SetFileDropList(sc);
            }
            else { _selfCopy = c.Text; Clipboard.SetText(c.Text); }
            _clips.Remove(c);
            var moved = c with { At = DateTime.Now };
            if (_translated.Remove(c, out var tr)) _translated[moved] = tr;
            _clips.Insert(0, moved);
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
            clear.MouseLeftButtonUp += (_, e) => { e.Handled = true; _clips.Clear(); _translated.Clear(); RebuildExpanded(); };
            head.Children.Add(clear);
        }
        stack.Children.Add(head);

        if (_clips.Count == 0)
        {
            var empty = Txt(12.5, FontWeights.Normal, Theme.Tertiary);
            empty.Text = "复制过的文字、文件和图片会出现在这里\n只保存在内存里，退出就清空";
            empty.TextAlignment = TextAlignment.Center;
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 18, 0, 16);
            stack.Children.Add(empty);
            return;
        }
        var list = new StackPanel();
        foreach (var c in _clips.Take(8))
        {
            list.Children.Add(ClipRow(c));
            if (_translated.TryGetValue(c, out var tr)) list.Children.Add(TranslationRow(tr));
        }
        stack.Children.Add(list);
    }

    private FrameworkElement ClipRow(ClipItem c)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand, MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (c.Image != null)
        {
            g.ColumnDefinitions[0].Width = new GridLength(58);
            g.MinHeight = 40;
            var thumb = new Border
            {
                Width = 48, Height = 32, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new ImageBrush(c.Image) { Stretch = Stretch.UniformToFill }, BorderBrush = Theme.Fill, BorderThickness = new Thickness(1),
            };
            g.Children.Add(thumb);
        }
        else
        {
            var icon = Glyph(c.Files != null ? "\uE8B7" : LooksLikeUrl(c.Text) ? "\uE71B" : "\uE8C4", 13, Theme.Secondary);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            g.Children.Add(icon);
        }
        var oneLine = string.Join(" ", c.Text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        var tx = Txt(13, FontWeights.Normal, Theme.Primary);
        tx.Text = oneLine.Length > 140 ? oneLine[..140] : oneLine;
        tx.TextTrimming = TextTrimming.CharacterEllipsis; tx.VerticalAlignment = VerticalAlignment.Center;
        tx.ToolTip = c.Text.Length > 600 ? c.Text[..600] + "…" : c.Text;
        Grid.SetColumn(tx, 1);
        g.Children.Add(tx);
        var ago = Txt(11.5, FontWeights.Normal, Theme.Tertiary);
        ago.Text = Ago(c.At); ago.VerticalAlignment = VerticalAlignment.Center; ago.Margin = new Thickness(10, 0, 8, 0);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(ago);
        if (c.Image == null && c.Files == null && !LooksLikeUrl(c.Text) && c.Text.Trim().Length is > 0 and <= 2000)
        {
            var busy = _translating.Contains(c);
            var tb = RoundIcon(busy ? "\uE895" : "\uE8C1", 22, () => _ = TranslateClip(c), Theme.Blue, QuickTranslate.HasCjk(c.Text) ? "译成英文" : "译成中文");
            tb.Margin = new Thickness(0, 0, 4, 0);
            tb.Opacity = _translated.ContainsKey(c) || busy ? 1 : 0;
            g.MouseEnter += (_, _) => tb.Opacity = 1;
            g.MouseLeave += (_, _) => { if (!_translated.ContainsKey(c) && !_translating.Contains(c)) tb.Opacity = 0; };
            right.Children.Add(tb);
        }
        else right.Children.Add(new Border { Width = 26 });   // 占住「译」的位置，时间一列对齐
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        AddHover(g);
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyBack(c); };
        g.MouseRightButtonUp += (_, e) => { e.Handled = true; _clips.Remove(c); _translated.Remove(c); RebuildExpanded(); };
        return g;
    }

    private async Task TranslateClip(ClipItem c)
    {
        if (_translating.Contains(c)) return;
        if (_translated.Remove(c)) { RebuildExpanded(); return; }   // 再点一次收起
        _translating.Add(c);
        RebuildExpanded();
        try { _translated[c] = await QuickTranslate.Run(c.Text); }
        catch (Exception ex) { _translated[c] = "⚠ 翻译失败：" + ex.Message; }
        finally { _translating.Remove(c); }
        if (_mode == Mode.Expanded && _page == Page.Clip && !_animating) RebuildExpanded(); else _expDirty = true;
    }

    private FrameworkElement TranslationRow(string text)
    {
        var b = new Border
        {
            Background = Theme.Fill, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 7), Margin = new Thickness(30, 0, 8, 4), Cursor = Cursors.Hand,
            ToolTip = "点一下复制译文",
        };
        var t = Txt(12.5, FontWeights.Normal, Theme.Primary);
        t.Text = text; t.TextWrapping = TextWrapping.Wrap; t.MaxHeight = 56; t.TextTrimming = TextTrimming.CharacterEllipsis;
        b.Child = t;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (text.StartsWith("⚠")) return;
            try { _selfCopy = text; Clipboard.SetText(text); _copiedFlash = DateTime.Now; RebuildExpanded(); } catch { }
        };
        return b;
    }

    private static bool SameImage(System.Windows.Media.Imaging.BitmapSource a, System.Windows.Media.Imaging.BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        try
        {
            int w = Math.Min(64, a.PixelWidth), stride = w * 4;
            var pa = new byte[stride]; var pb = new byte[stride];
            var r = new Int32Rect(0, a.PixelHeight / 2, w, 1);
            new System.Windows.Media.Imaging.FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0).CopyPixels(r, pa, stride, 0);
            new System.Windows.Media.Imaging.FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0).CopyPixels(r, pb, stride, 0);
            return pa.AsSpan().SequenceEqual(pb);
        }
        catch { return false; }
    }

    private static bool LooksLikeUrl(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalSeconds < 60 ? "刚刚" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} 分钟前" : d.TotalHours < 24 ? $"{(int)d.TotalHours} 小时前" : t.ToString("M/d");
    }

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr h);
}
