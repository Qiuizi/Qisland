namespace Qisland.Core;

/// <summary>参与挑选的一个来源：是否在播放、是否网易云。</summary>
public readonly record struct MediaCandidate(bool Playing, bool IsNcm);

public enum MediaPickResult { None, Smtc, Ncm }

/// <summary>
/// 在系统媒体会话（SMTC）和网易云日志里的状态之间选显示哪个。
/// 用户手动切到的来源优先：切到网易云日志（manualNcm）就显示网易云，切到别的会话（不是网易云、而且还在）就显示它；没有手动选择时保持原规则：
/// SMTC 在播而网易云没播用 SMTC，否则网易云优先（浏览器常年挂着暂停的会话）。
/// </summary>
public static class MediaPick
{
    public static MediaPickResult Choose(MediaCandidate? smtc, MediaCandidate? ncm, bool ncmReportsSmtc, bool manualPick, bool manualNcm = false)
    {
        if (ncm == null) return smtc != null ? MediaPickResult.Smtc : MediaPickResult.None;
        if (ncmReportsSmtc) return smtc != null ? MediaPickResult.Smtc : MediaPickResult.Ncm;
        if (manualNcm) return MediaPickResult.Ncm;
        if (manualPick && smtc is { IsNcm: false }) return MediaPickResult.Smtc;
        if (smtc is { Playing: true } && !ncm.Value.Playing) return MediaPickResult.Smtc;
        return MediaPickResult.Ncm;
    }
}
