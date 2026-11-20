using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 划词翻译 / 问 AI：按快捷键（默认 Ctrl+Alt+T / Ctrl+Alt+K）取选中的文字，在岛的首页顶部弹出卡片。
/// 翻译左右对照，可复制、可一键替换原文（岛不抢焦点，直接粘回原窗口）；问 AI 可以用自己的 Key 在岛上回答，
/// 或者粘贴到豆包窗口（只粘贴，不发送）。内容只在内存里，关掉卡片就丢。
/// </summary>
public sealed partial class IslandWindow
{
    public AiSettings Ai { get; set; } = new();
    private const int TranslateHotkeyId = 0x5150, AskHotkeyId = 0x5151;

    private sealed class AiCard
    {
        public bool Ask;
        public string Source = "";
        public string? Result, Error, Note;
        public string Engine = "";
        public bool Busy;
        public IntPtr Target;
        public DateTime At = DateTime.Now;
        public CancellationTokenSource Cts = new();
    }
    private AiCard? _ai;

    /// <summary>实际注册上的组合（被占用时可能是备用的那个）；没注册上为 null。</summary>
    public string? ActiveTranslateHotkey { get; private set; }
    public string? ActiveAskHotkey { get; private set; }

    /// <summary>
    /// 按设置注册 / 注销两个快捷键；设置里的被别的程序占了，就按顺序换备用组合（加 Shift、Ctrl+Alt+Y / J…），
    /// 用第一个注册得上的，并在岛上提示一次实际生效的组合。返回一个都注册不上的那些。
    /// （Windows 没有公开接口能查出是哪个程序占了某个热键，所以日志里只记「被占用」。）
    /// </summary>
    public List<string> ApplyAiHotkeys()
    {
        var failed = new List<string>();
        if (SnapshotMode || _hwnd == IntPtr.Zero) return failed;
        UnregHotKey(_hwnd, TranslateHotkeyId);
        UnregHotKey(_hwnd, AskHotkeyId);
        ActiveTranslateHotkey = ActiveAskHotkey = null;
        if (!Ai.TranslateOn) return failed;
        var taken = new List<string>();
        foreach (var (id, text, extras, label) in new[]
                 {
                     (TranslateHotkeyId, Ai.TranslateHotkey, HotkeyFallback.TranslateExtras, "划词翻译"),
                     (AskHotkeyId, Ai.AskHotkey, HotkeyFallback.AskExtras, "问 AI"),
                 })
        {
            string? got = null;
            foreach (var c in HotkeyFallback.Candidates(text, extras, taken))
            {
                if (!HotkeyText.TryParse(c, out var mods, out var vk)) continue;
                if (RegHotKey(_hwnd, id, mods | 0x4000 /*NOREPEAT*/, vk)) { got = c; break; }
                App.Log($"热键 {c} 被占用（{label}）");
            }
            if (got == null) { failed.Add(string.IsNullOrWhiteSpace(text) ? label : text); App.Log($"热键: {label} 所有候选都被占用，没有注册"); continue; }
            taken.Add(got);
            if (id == TranslateHotkeyId) ActiveTranslateHotkey = got; else ActiveAskHotkey = got;
            var configuredOk = HotkeyText.TryParse(text, out var cm, out var cv) && HotkeyText.Format(cm, cv) == got;
            App.Log(configuredOk ? $"热键 {got} 已注册（{label}）" : $"热键 {label}: {text} 用不了，改用备用 {got}");
            if (!configuredOk) NoteHotkeyFallback(label, text, got);
        }
        return failed;
    }

    /// <summary>备用组合生效时在岛上提示一次；同一组合以后不再提示（记在 %APPDATA%\Qisland\hotkey-notice.txt）。</summary>
    private void NoteHotkeyFallback(string label, string configured, string active)
    {
        var key = $"{label}={active}";
        var file = System.IO.Path.Combine(App.DataDir, "hotkey-notice.txt");
        try
        {
            var seen = System.IO.File.Exists(file) ? System.IO.File.ReadAllLines(file) : [];
            if (seen.Contains(key)) return;
            System.IO.File.AppendAllLines(file, [key]);
        }
        catch { }
        _store.Upsert(new IslandActivity("ai:hotkey:" + label, "translate", $"{label}快捷键：{active}",
            $"{configured} 被别的程序占用了，已自动换成 {active}", ActivityKind.Info, 45, DurationSeconds: 8, ResetTtl: true));
    }

    private async void OnAiHotkey(bool ask)
    {
        try
        {
            _ai?.Cts.Cancel();
            var g = await SelectionGrabber.Get();
            if (g.Refused != null || string.IsNullOrWhiteSpace(g.Text))
            {
                ShowAiCard(new AiCard { Ask = ask, Error = g.Refused ?? "没有选中文字", Target = g.Window }, 4);
                return;
            }
            var card = new AiCard { Ask = ask, Source = g.Text!.Trim(), Target = g.Window };
            if (ask && Ai.AskTarget == "doubao") { await PasteToDoubao(card); return; }
            await RunAi(card);
        }
        catch (Exception ex) { App.Log("划词: " + ex.Message); }
    }

    private async Task RunAi(AiCard card)
    {
        card.Busy = true;
        var e = card.Ask ? AiCatalog.ById(Ai.AskEngine) : AiCatalog.ById(Ai.TranslateEngine);
        if (e != null && !AiSettings.Ready(e) && !card.Ask) e = AiCatalog.Info(AiEngine.MyMemory);
        card.Engine = e?.Name ?? "";
        ShowAiCard(card, 90);
        try { card.Result = card.Ask ? await AiClient.Ask(Ai, card.Source, card.Cts.Token) : await AiClient.Translate(Ai, card.Source, card.Cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (AiException ex) { card.Error = ex.Message; }
        catch (Exception ex) { card.Error = "出错了：" + ex.GetType().Name; App.Log("AI: " + ex.GetType().Name); }
        finally { card.Busy = false; }
        if (_ai == card) { _peekUntil = DateTime.Now.AddSeconds(45); RefreshAiCard(); }
    }

    private async Task PasteToDoubao(AiCard card)
    {
        card.Engine = "豆包";
        if (!Launcher.Open("doubao")) { card.Error = "没找到豆包客户端"; ShowAiCard(card, 5); return; }
        var h = await ForegroundWait.For("Doubao", 4000);
        if (h == IntPtr.Zero || !await SelectionGrabber.PasteInto(h, card.Source))
        {
            SelectionGrabber.SetPrivateText(card.Source);
            card.Note = "豆包没到前台，文字已复制，到豆包里按 Ctrl+V";
        }
        else card.Note = "已粘贴到豆包输入框，没有发送";
        ShowAiCard(card, 4);
    }

    private void ShowAiCard(AiCard card, double seconds)
    {
        _ai = card;
        _page = Page.Home;
        _peekUntil = DateTime.Now.AddSeconds(seconds);
        _expDirty = true;
        if (_mode == Mode.Expanded) RebuildExpanded();
        SetMode(Mode.Expanded);
        Kick();
    }

    private void RefreshAiCard()
    {
        if (_mode == Mode.Expanded && _page == Page.Home && !_animating) RebuildExpanded(); else _expDirty = true;
    }

    private void CloseAiCard()
    {
        _ai?.Cts.Cancel();
        _ai = null;
        if (_peekUntil > DateTime.Now.AddSeconds(1)) _peekUntil = DateTime.Now.AddSeconds(1);
        RefreshAiCard();
    }

    private FrameworkElement? AiCardView()
    {
        var c = _ai;
        if (c == null) return null;
        if (!c.Busy && DateTime.Now - c.At > TimeSpan.FromMinutes(10)) { _ai = null; return null; }   // 放久了就丢掉
        var sp = new StackPanel();

        var head = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Glyph(c.Ask ? "\uE99A" : "\uE8C1", 13, Theme.Blue));
        var tt = Txt(12.5, FontWeights.SemiBold, Theme.Primary);
        tt.Text = (c.Ask ? "问 AI" : "翻译") + (c.Engine.Length > 0 ? " · " + c.Engine : "");
        tt.Margin = new Thickness(7, 0, 0, 0);
        title.Children.Add(tt);
        if (c.Busy) { var w = Txt(11.5, FontWeights.Normal, Theme.Tertiary); w.Text = "  正在处理…"; title.Children.Add(w); }
        head.Children.Add(title);
        var close = RoundIcon("\uE711", 22, CloseAiCard, Theme.Secondary, "关闭（内容不保留）");
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        sp.Children.Add(head);

        if (c.Source.Length == 0 || c.Note != null)
        {
            var msg = Txt(12.5, FontWeights.Normal, c.Error != null ? Theme.Orange : Theme.Secondary);
            msg.Text = c.Error ?? c.Note; msg.TextWrapping = TextWrapping.Wrap; msg.Margin = new Thickness(0, 0, 0, 6);
            sp.Children.Add(msg);
            return Glassy(sp);
        }

        TextBlock Body(string text, Brush fg, double maxH)
        {
            var t = Txt(12.5, FontWeights.Normal, fg);
            t.Text = text; t.TextWrapping = TextWrapping.Wrap; t.MaxHeight = maxH; t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.ToolTip = text.Length > 1200 ? text[..1200] + "…" : text;
            return t;
        }
        var right = c.Error != null ? Body("⚠ " + c.Error, Theme.Orange, 150) : Body(c.Result ?? "…", Theme.Primary, 150);
        if (c.Ask)
        {
            var q = Body(c.Source, Theme.Tertiary, 34); q.Margin = new Thickness(0, 0, 0, 6);
            sp.Children.Add(q);
            sp.Children.Add(right);
        }
        else
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var left = Body(c.Source, Theme.Secondary, 150); left.Margin = new Thickness(0, 0, 10, 0);
            g.Children.Add(left);
            var line = new Border { Background = Theme.Fill }; Grid.SetColumn(line, 1); g.Children.Add(line);
            right.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(right, 2); g.Children.Add(right);
            sp.Children.Add(g);
        }

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 4) };
        if (c.Result != null)
        {
            btns.Children.Add(Pill("复制", Theme.Fill, Theme.Primary, () =>
            {
                if (SelectionGrabber.SetPrivateText(c.Result)) { c.Note = null; _copiedFlash = DateTime.Now; }
            }));
            if (!c.Ask && c.Target != IntPtr.Zero)
                btns.Children.Add(Pill("替换原文", Theme.Blue, Brushes.White, async () =>
                {
                    var ok = await SelectionGrabber.PasteInto(c.Target, c.Result);
                    if (ok) CloseAiCard();
                    else { SelectionGrabber.SetPrivateText(c.Result); c.Error = null; c.Note = "原窗口回不到前台，译文已复制，自己粘贴一下"; RefreshAiCard(); }
                }));
        }
        if (!c.Ask && !c.Busy)
            btns.Children.Add(Pill("问 AI", Theme.Fill, Theme.Primary, async () =>
            {
                var next = new AiCard { Ask = true, Source = c.Source, Target = c.Target };
                c.Cts.Cancel();
                if (Ai.AskTarget == "doubao") await PasteToDoubao(next); else await RunAi(next);
            }));
        if (btns.Children.Count > 0) sp.Children.Add(btns);
        return Glassy(sp);
    }

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey")] private static extern bool RegHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey")] private static extern bool UnregHotKey(IntPtr h, int id);
}
