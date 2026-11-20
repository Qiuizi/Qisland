namespace Qisland.Core;

/// <summary>液态玻璃外观的背景做法（glass.txt）：system 系统合成模糊（默认）、capture 截屏自己模糊（旧做法，兼容）、off 不模糊。</summary>
public enum GlassMode { System, Capture, Off }

/// <summary>实际用哪种：系统模糊 / 系统层但关了透明效果时画实色 / 截屏模糊 / 不画。</summary>
public enum GlassRender { SystemBlur, SystemSolid, Capture, None }

public static class GlassPref
{
    /// <summary>宿主背景画刷（DWMWA_USE_HOSTBACKDROPBRUSH）从 Windows 11（22000）起才有。</summary>
    public const int MinSystemBuild = 22000;

    public static GlassMode Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "capture" => GlassMode.Capture,
        "off" => GlassMode.Off,
        _ => GlassMode.System,
    };

    public static string Serialize(GlassMode m) => m switch { GlassMode.Capture => "capture", GlassMode.Off => "off", _ => "system" };

    public static string Name(GlassMode m) => m switch
    {
        GlassMode.Capture => "截屏模糊（兼容）",
        GlassMode.Off => "不模糊",
        _ => "系统模糊（推荐）",
    };

    /// <summary>
    /// 选定做法 + 系统版本 + 「透明效果」开关 → 实际渲染：
    /// 系统模糊在 Win10 上退回截屏；关了透明效果时系统层画实色（和系统亚克力的做法一致），截屏模糊也不再截。
    /// </summary>
    public static GlassRender Resolve(GlassMode mode, int osBuild, bool transparencyOn) => mode switch
    {
        GlassMode.Off => GlassRender.None,
        GlassMode.Capture => transparencyOn ? GlassRender.Capture : GlassRender.None,
        _ when osBuild < MinSystemBuild => transparencyOn ? GlassRender.Capture : GlassRender.None,
        _ => transparencyOn ? GlassRender.SystemBlur : GlassRender.SystemSolid,
    };
}
