namespace Qisland.Core;

/// <summary>划词时记下的现场：前台窗口、焦点控件（句柄数值）和选中的文字。</summary>
public sealed record SelectionSnapshot(long Window, long Focus, string? Text);

/// <summary>粘贴目标：前台窗口、焦点控件、进程和抓取时间。</summary>
public sealed record PasteTargetSnapshot(long Window, long Focus, int ProcessId, DateTime CapturedAt);

public enum PasteDecision { Paste, CopyInstead }

/// <summary>
/// 「替换原文」前的检查：窗口、焦点控件、选中的文字都没变才粘贴，否则只复制译文并提示，
/// 免得用户在同一个窗口里改了选区后把译文粘到别处。
/// </summary>
public static class PasteGuard
{
    public static PasteDecision Decide(SelectionSnapshot original, SelectionSnapshot now)
    {
        if (original.Window == 0 || now.Window != original.Window) return PasteDecision.CopyInstead;
        if (original.Focus != 0 && now.Focus != original.Focus) return PasteDecision.CopyInstead;
        if (string.IsNullOrWhiteSpace(now.Text) || Norm(now.Text) != Norm(original.Text)) return PasteDecision.CopyInstead;
        return PasteDecision.Paste;
    }

    /// <summary>写剪贴板后、发送按键前的最后一次检查：目标四要素和剪贴板序号都必须没变。</summary>
    public static PasteDecision DecideTarget(PasteTargetSnapshot original, PasteTargetSnapshot now, uint ourSequence, uint currentSequence)
    {
        if (original.Window == 0 || now.Window != original.Window) return PasteDecision.CopyInstead;
        if (original.Focus == 0 || now.Focus != original.Focus) return PasteDecision.CopyInstead;
        if (original.ProcessId <= 0 || now.ProcessId != original.ProcessId) return PasteDecision.CopyInstead;
        if (ourSequence == 0 || currentSequence != ourSequence) return PasteDecision.CopyInstead;
        return PasteDecision.Paste;
    }

    private static string Norm(string? s) => (s ?? "").Replace("\r\n", "\n").Trim();

    /// <summary>粘贴 / 临时复制后只在剪贴板还是我们放进去的那一份时才还原，期间别人复制的内容不覆盖。</summary>
    public static bool ShouldRestoreClipboard(uint ourSequence, uint currentSequence) => ourSequence != 0 && currentSequence == ourSequence;
}
