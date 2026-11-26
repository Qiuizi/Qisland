using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 剪贴板历史：文字、文件和图片（图片最多留 3 张、每张不超过 1600 万像素），最多 30 条，只放在内存里（退出即清空，不落盘）。
/// 图片不再整张解码常驻：列表只留一张小缩略图，原图压成 PNG 字节放在内存里，点回去复制时才解开。
/// 密码管理器复制的内容会带 ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory=0 标记，直接跳过。
/// </summary>
public sealed partial class IslandWindow
{
    /// <summary>Image 是显示用的小缩略图；Full 是原图的 PNG 字节（后台压缩，复制回去时才解码）；Sig 用来判断连着复制的是不是同一张。</summary>
    private sealed record ClipItem(string Text, string[]? Files, DateTime At, System.Windows.Media.Imaging.BitmapSource? Image = null, bool Pinned = false,
        Task<byte[]?>? Full = null, ulong Sig = 0);
    // 置顶的文字：用户主动钉住才落盘，DPAPI 加密（只有本机本用户能解开）
    private ClipPins? _pins;
    private static string PinFile => Path.Combine(App.DataDir, "clip-pins.dat");
    private ClipPins Pins => _pins ??= ClipPins.Parse(Dpapi.Load(PinFile));

    private void SavePins()
    {
        try
        {
            if (Pins.Count == 0) { if (File.Exists(PinFile)) File.Delete(PinFile); }
            else Dpapi.Save(PinFile, Pins.Serialize());
        }
        catch (Exception ex) { App.Log("保存剪贴板置顶: " + ex.Message); }
    }

    private void TogglePin(ClipItem c)
    {
        if (c.Pinned || Pins.Contains(c.Text)) Pins.Unpin(c.Text);
        else if (!Pins.Pin(c.Text)) return;
        SavePins();
        RebuildExpanded();
    }
    private readonly Dictionary<ClipItem, string> _translated = new();
    private readonly HashSet<ClipItem> _translating = new();
    private readonly List<ClipItem> _clips = new();
    private string? _selfCopy;
    private DateTime _copiedFlash = DateTime.MinValue;
    // 图片很占内存（4K 截图解码后 ≈ 33 MB，以前三张原图常驻、显示时还要整张传进显卡）：现在只留缩略图 + PNG 字节，
    // 截图类图片压完通常几百 KB 到几 MB。最多留 3 张，超过 1600 万像素的不进历史
    private const int MaxClipImages = 3;
    private const long MaxClipPixels = 16_000_000;

    private void SetupClipboard()
    {
        if (SnapshotMode) return;
        if (!AddClipboardFormatListener(_hwnd)) App.Log("剪贴板监听失败");
    }

    private void OnClipboardUpdate() =>
        Dispatcher.BeginInvoke(async () => { await Task.Delay(80); ReadClipboard(); });

    private void ReadClipboard()
    {
        if (DateTime.Now < SelectionGrabber.SuppressClipUntil) return;   // 划词时的临时复制 / 还原，不进历史
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
            else if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is { } img && (long)img.PixelWidth * img.PixelHeight <= MaxClipPixels)
            {
                img.Freeze();
                // 同一张图连着复制两次不重复记；用尺寸 + 采样像素粗判
                var key = $"图片 {img.PixelWidth}×{img.PixelHeight}";
                var sig = ImageSig(img);
                if (_clips.FirstOrDefault() is { Image: not null, Sig: var lastSig } last && last.Text == key && lastSig == sig && sig != 0) return;
                var full = Task.Run(() => EncodePng(img));   // 压完 img 就没人引用了，原图跟着释放
                item = new ClipItem(key, null, DateTime.Now, Thumb(img), Full: full, Sig: sig);
            }
            if (item == null) return;
            if (item.Files == null && item.Text == _selfCopy) { _selfCopy = null; return; }
            if (item.Image == null) _clips.RemoveAll(c => c.Image == null && c.Text == item.Text);
            _clips.Insert(0, item);
            foreach (var old in _clips.Where(c => c.Image != null).Skip(MaxClipImages).ToList()) _clips.Remove(old);
            if (_clips.Count > 30) _clips.RemoveRange(30, _clips.Count - 30);
            if (_mode == Mode.Expanded && _page is Page.Clip or Page.Widgets && !_animating && !_widgetEdit) RebuildExpanded(); else _expDirty = true;
        }
        catch { /* 剪贴板被别的程序占着，忽略这一次 */ }
    }

    private void CopyBack(ClipItem c)
    {
        try
        {
            if (c.Image != null) { _ = CopyBackImage(c); return; }
            else if (c.Files != null)
            {
                var sc = new System.Collections.Specialized.StringCollection();
                sc.AddRange(c.Files.Where(File.Exists).ToArray());
                if (sc.Count > 0) Clipboard.SetFileDropList(sc);
            }
            else { _selfCopy = c.Text; Clipboard.SetText(c.Text); }
            if (c.Pinned) { _copiedFlash = DateTime.Now; RebuildExpanded(); return; }
            _clips.Remove(c);
            var moved = c with { At = DateTime.Now };
            if (_translated.Remove(c, out var tr)) _translated[moved] = tr;
            _clips.Insert(0, moved);
            _copiedFlash = DateTime.Now;
            RebuildExpanded();
        }
        catch (Exception ex) { App.Log("写剪贴板失败: " + ex.Message); }
    }

    private async Task CopyBackImage(ClipItem c)
    {
        try
        {
            System.Windows.Media.Imaging.BitmapSource img = c.Image!;
            if (c.Full != null && await c.Full is { } png)
            {
                var dec = new System.Windows.Media.Imaging.PngBitmapDecoder(new MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                img = dec.Frames[0];
            }
            Clipboard.SetImage(img);   // 系统剪贴板接手后，这份解码出来的原图随即可以回收
            _clips.Remove(c);
            _clips.Insert(0, c with { At = DateTime.Now });
            _copiedFlash = DateTime.Now;
            RebuildExpanded();
        }
        catch (Exception ex) { App.Log("写剪贴板失败: " + ex.Message); }
    }

    private static byte[]? EncodePng(System.Windows.Media.Imaging.BitmapSource img)
    {
        try
        {
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex) { App.Log("剪贴板图片压缩失败: " + ex.GetType().Name); return null; }
    }

    /// <summary>
    /// 列表里的缩略图（48×32 DIP，按 300% 缩放算 144×96 像素，铺满时短边也够）：
    /// 像素拷进一块小数组再建位图，不留对原图的引用。
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapSource? Thumb(System.Windows.Media.Imaging.BitmapSource img)
    {
        try
        {
            var k = Math.Min(1, Math.Max(144.0 / img.PixelWidth, 96.0 / img.PixelHeight));
            System.Windows.Media.Imaging.BitmapSource src = new System.Windows.Media.Imaging.FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
            if (k < 1) src = new System.Windows.Media.Imaging.TransformedBitmap(src, new ScaleTransform(k, k));
            int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            src.CopyPixels(px, stride, 0);
            var t = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            t.Freeze();
            return t;
        }
        catch { return null; }
    }

    private void BuildClip(StackPanel stack)
    {
        var head = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(PageIcon("clip"));
        var tt = Txt(13, FontWeights.SemiBold, Theme.Primary); tt.Text = "剪贴板"; tt.Margin = new Thickness(7, 0, 0, 0);
        title.Children.Add(tt);
        var n = Txt(12, FontWeights.Normal, Theme.Tertiary);
        n.Text = DateTime.Now - _copiedFlash < TimeSpan.FromSeconds(2) ? "  已复制，去粘贴吧" : _clips.Count + Pins.Count > 0 ? $"  {_clips.Count} 条{(Pins.Count > 0 ? $" · 置顶 {Pins.Count}" : "")} · 点一下重新复制" : "";
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

        if (_clips.Count == 0 && Pins.Count == 0)
        {
            var empty = Txt(12.5, FontWeights.Normal, Theme.Tertiary);
            empty.Text = "复制过的文字、文件和图片会出现在这里\n只保存在内存里，退出就清空；钉住的文字加密保存在本机";
            empty.TextAlignment = TextAlignment.Center;
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 18, 0, 16);
            stack.Children.Add(empty);
            return;
        }
        var list = new StackPanel();
        var rows = Pins.Items.Select(t => new ClipItem(t, null, DateTime.MinValue, null, true))
            .Concat(_clips.Where(c => c.Image != null || c.Files != null || !Pins.Contains(c.Text)).Take(8));
        foreach (var c in rows)
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
        ago.Text = c.Pinned ? "" : Ago(c.At); ago.VerticalAlignment = VerticalAlignment.Center; ago.Margin = new Thickness(10, 0, 8, 0);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(ago);
        if (c.Image == null && c.Files == null && ClipPins.CanPin(c.Text))
        {
            var pb = RoundIcon(c.Pinned ? "\uE77A" : "\uE718", 22, () => TogglePin(c), c.Pinned ? Theme.Orange : Theme.Secondary, c.Pinned ? "取消置顶" : "置顶（加密保存在本机）");
            pb.Margin = new Thickness(0, 0, 4, 0);
            pb.Opacity = c.Pinned ? 1 : 0;
            if (!c.Pinned) { g.MouseEnter += (_, _) => pb.Opacity = 1; g.MouseLeave += (_, _) => pb.Opacity = 0; }
            right.Children.Add(pb);
        }
        if (c.Image == null && c.Files == null && !LooksLikeUrl(c.Text) && c.Text.Trim().Length is > 0 and <= 2000)
        {
            var busy = _translating.Contains(c);
            var tb = RoundIcon(busy ? "\uE895" : "\uE8C1", 22, () => _ = TranslateClip(c), Theme.Blue, AiCatalog.HasCjk(c.Text) ? "译成英文" : "译成中文");
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
        g.MouseRightButtonUp += (_, e) => { e.Handled = true; if (c.Pinned) { TogglePin(c); return; } _clips.Remove(c); _translated.Remove(c); RebuildExpanded(); };
        return g;
    }

    private async Task TranslateClip(ClipItem c)
    {
        if (_translating.Contains(c)) return;
        if (_translated.Remove(c)) { RebuildExpanded(); return; }   // 再点一次收起
        _translating.Add(c);
        RebuildExpanded();
        try { _translated[c] = await AiClient.Translate(Ai, c.Text, CancellationToken.None); }
        catch (AiException ex) { _translated[c] = "⚠ 翻译失败：" + ex.Message; }
        catch (Exception ex) { _translated[c] = "⚠ 翻译失败：" + ex.GetType().Name; }
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

    /// <summary>同一张图的粗判：取中间一行的前 64 个像素算个指纹（和以前 SameImage 比的是同一段像素）。</summary>
    private static ulong ImageSig(System.Windows.Media.Imaging.BitmapSource a)
    {
        try
        {
            int w = Math.Min(64, a.PixelWidth), stride = w * 4;
            var pa = new byte[stride];
            new System.Windows.Media.Imaging.FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, a.PixelHeight / 2, w, 1), pa, stride, 0);
            var h = 14695981039346656037UL;
            foreach (var x in pa) { h ^= x; h *= 1099511628211UL; }
            return h == 0 ? 1 : h;
        }
        catch { return 0; }
    }

    private static bool LooksLikeUrl(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalSeconds < 60 ? "刚刚" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} 分钟前" : d.TotalHours < 24 ? $"{(int)d.TotalHours} 小时前" : t.ToString("M/d");
    }

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr h);
}
