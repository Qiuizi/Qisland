using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qisland.Core;

/// <summary>翻译 / 问 AI 可用的引擎。MyMemory 免 Key（默认）；其余要你自己的 API Key（Ollama 是本机，不要 Key）。</summary>
public enum AiEngine { MyMemory, Doubao, DeepSeek, Kimi, Qwen, Ollama }

public sealed record AiEngineInfo(AiEngine Engine, string Id, string Name, string Endpoint, string DefaultModel, bool NeedsKey, string Vendor, string KeyHint);

/// <summary>
/// 引擎表 + 纯函数（拼请求、解析返回、判断语言方向、切段），App 层只负责发 HTTP。
/// 安全规范：只连 https 的官方地址（Ollama 只允许本机 127.0.0.1 / localhost），不跟随跨站跳转，
/// 不回显服务器原文（错误只给状态码和一句话），文本和 Key 都不写日志。
/// </summary>
public static class AiCatalog
{
    public static readonly IReadOnlyList<AiEngineInfo> All =
    [
        new(AiEngine.MyMemory, "mymemory", "MyMemory（免 Key）", "https://api.mymemory.translated.net/get", "", false, "MyMemory（translated.net）", ""),
        new(AiEngine.Doubao, "doubao", "豆包（火山方舟）", "https://ark.cn-beijing.volces.com/api/v3/chat/completions", "doubao-seed-1-6-flash-250828", true, "字节跳动 火山方舟", "火山方舟控制台 → API Key；模型填推理接入点 ID（ep-…）或模型名"),
        new(AiEngine.DeepSeek, "deepseek", "DeepSeek", "https://api.deepseek.com/chat/completions", "deepseek-chat", true, "DeepSeek", "platform.deepseek.com → API keys"),
        new(AiEngine.Kimi, "kimi", "Kimi（月之暗面）", "https://api.moonshot.cn/v1/chat/completions", "kimi-k2-turbo-preview", true, "Moonshot AI", "platform.moonshot.cn → API Key"),
        new(AiEngine.Qwen, "qwen", "通义千问（百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "qwen-plus", true, "阿里云 百炼", "百炼控制台 → API-KEY"),
        new(AiEngine.Ollama, "ollama", "本机 Ollama（离线）", "http://127.0.0.1:11434/api/chat", "qwen2.5:7b", false, "本机 Ollama（不出电脑）", "先装 Ollama 并 ollama pull 一个模型"),
    ];

    public static AiEngineInfo Info(AiEngine e) => All.First(x => x.Engine == e);
    public static AiEngineInfo? ById(string? id) => All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>只允许 https 官方地址；Ollama 只允许本机 http。带用户信息、查询串或片段的地址一律拒绝。</summary>
    public static bool EndpointAllowed(AiEngine e, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.UserInfo.Length > 0 || u.Fragment.Length > 0) return false;
        if (e == AiEngine.Ollama) return u.Scheme == "http" && (u.Host is "127.0.0.1" or "localhost" or "[::1]") && u.Query.Length == 0;
        if (u.Scheme != "https") return false;
        if (e != AiEngine.MyMemory && u.Query.Length > 0) return false;
        var official = new Uri(Info(e).Endpoint).Host;
        return string.Equals(u.Host, official, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── 语言 ─────────────

    public static bool HasCjk(string s) => s.Any(c => c is >= '\u4e00' and <= '\u9fff' or >= '\u3400' and <= '\u4dbf');

    /// <summary>中文为主 → 译成英文；否则 → 译成中文。返回 (源, 目标) 的 MyMemory 语言码。</summary>
    public static (string from, string to) Direction(string text)
    {
        int cjk = 0, latin = 0, kana = 0, hangul = 0;
        foreach (var c in text)
        {
            if (c is >= '\u4e00' and <= '\u9fff') cjk++;
            else if (c is >= '\u3040' and <= '\u30ff') kana++;
            else if (c is >= '\uac00' and <= '\ud7af') hangul++;
            else if (char.IsLetter(c) && c < '\u0250') latin++;
        }
        if (kana > 0 && kana * 3 >= cjk) return ("ja", "zh-CN");
        if (hangul > 0 && hangul >= cjk) return ("ko", "zh-CN");
        return cjk > 0 && cjk >= latin / 4 ? ("zh-CN", "en") : ("en", "zh-CN");
    }

    public static string TargetName(string code) => code switch { "zh-CN" => "简体中文", "en" => "English", _ => code };

    /// <summary>MyMemory 单次最多 500 字：按句末标点 / 换行切段，切不开的硬切。</summary>
    public static List<string> Chunk(string text, int max = 480)
    {
        var res = new List<string>();
        var rest = text.Trim();
        while (rest.Length > max)
        {
            var cut = -1;
            for (var i = max; i > max / 2; i--)
                if (rest[i - 1] is '\n' or '。' or '！' or '？' or '.' or '!' or '?' or ';' or '；') { cut = i; break; }
            if (cut < 0) cut = rest.LastIndexOf(' ', max - 1) is var sp && sp > max / 2 ? sp + 1 : max;
            res.Add(rest[..cut].Trim());
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) res.Add(rest);
        return res;
    }

    // ───────────── 请求 ─────────────

    public static string MyMemoryUrl(string chunk, (string from, string to) dir) =>
        $"{Info(AiEngine.MyMemory).Endpoint}?q={Uri.EscapeDataString(chunk)}&langpair={Uri.EscapeDataString(dir.from + "|" + dir.to)}";

    public static string ParseMyMemory(string json)
    {
        var r = JsonNode.Parse(json) as JsonObject ?? throw new AiException("翻译接口返回了看不懂的内容");
        var status = r["responseStatus"]?.ToString();
        var t = r["responseData"]?["translatedText"]?.ToString() ?? "";
        if (status is not null and not "200") throw new AiException("免费翻译今天额度用完了或暂时不可用，可以在设置里换一个引擎");
        if (t.Length == 0) throw new AiException("翻译接口没有返回结果");
        return System.Net.WebUtility.HtmlDecode(t);
    }

    public static string TranslatePrompt(string targetLanguage) =>
        $"你是翻译引擎。把用户给的文本翻译成{targetLanguage}，保留原有换行、列表和代码，不解释、不加引号、不加前后缀，只输出译文。";

    public const string AskPrompt = "你是灵动岛上的小助手。针对用户选中的这段文字，用简体中文给出简短、直接的回答：是问题就回答，是外文或术语就解释，是长文就三句话内总结。不超过 150 字，不要客套。";

    /// <summary>OpenAI 兼容（豆包方舟、DeepSeek、Kimi、通义兼容模式）或 Ollama 的请求体。</summary>
    public static string ChatBody(AiEngine e, string model, string system, string user, int maxTokens = 800)
    {
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = user } };
        var o = new JsonObject { ["model"] = model, ["messages"] = msgs, ["stream"] = false };
        if (e == AiEngine.Ollama) o["options"] = new JsonObject { ["temperature"] = 0.2 };
        else { o["temperature"] = 0.2; o["max_tokens"] = maxTokens; }
        return o.ToJsonString();
    }

    /// <summary>从返回里取出文字；出错时抛带一句人话的 AiException（不含服务器原文）。</summary>
    public static string ParseChat(AiEngine e, int status, string json)
    {
        if (status is 401 or 403) throw new AiException("API Key 不对或没有权限，去设置里重新填一下");
        if (status == 402) throw new AiException("账户余额不足");
        if (status == 429) throw new AiException("请求太频繁或额度用完了，稍后再试");
        if (status == 404) throw new AiException(e == AiEngine.Ollama ? "Ollama 里没有这个模型，先 ollama pull 一下" : "模型名不对，去设置里改一下");
        if (status >= 400) throw new AiException($"服务暂时不可用（HTTP {status}）");
        JsonNode? r;
        try { r = JsonNode.Parse(json); } catch { throw new AiException("返回了看不懂的内容"); }
        var text = e == AiEngine.Ollama
            ? r?["message"]?["content"]?.ToString()
            : r?["choices"]?[0]?["message"]?["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new AiException("没有返回结果");
        return StripThink(text).Trim();
    }

    /// <summary>推理模型会把思考过程放在 &lt;think&gt; 里，岛上只要结论。</summary>
    public static string StripThink(string s)
    {
        var i = s.IndexOf("<think>", StringComparison.Ordinal);
        var j = s.IndexOf("</think>", StringComparison.Ordinal);
        return i >= 0 && j > i ? (s[..i] + s[(j + 8)..]) : s;
    }
}

public sealed class AiException(string message) : Exception(message);

/// <summary>全局快捷键文本（「Ctrl+Alt+T」）和 RegisterHotKey 参数互转。</summary>
public static class HotkeyText
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw.ToUpperInvariant();
            switch (p)
            {
                case "CTRL" or "CONTROL": mods |= Ctrl; continue;
                case "ALT": mods |= Alt; continue;
                case "SHIFT": mods |= Shift; continue;
                case "WIN" or "WINDOWS": mods |= Win; continue;
            }
            if (vk != 0) return false;
            if (p.Length == 1 && (char.IsAsciiLetterUpper(p[0]) || char.IsAsciiDigit(p[0]))) vk = p[0];
            else if (p.StartsWith('F') && int.TryParse(p[1..], out var f) && f is >= 1 and <= 12) vk = (uint)(0x70 + f - 1);
            else if (p == "SPACE") vk = 0x20;
            else return false;
        }
        // 至少要有 Ctrl 或 Alt（或 Win），免得单键 / Shift+字母 把正常打字吃掉
        return vk != 0 && (mods & (Ctrl | Alt | Win)) != 0;
    }

    public static string Format(uint mods, uint vk)
    {
        var sb = new StringBuilder();
        if ((mods & Ctrl) != 0) sb.Append("Ctrl+");
        if ((mods & Alt) != 0) sb.Append("Alt+");
        if ((mods & Shift) != 0) sb.Append("Shift+");
        if ((mods & Win) != 0) sb.Append("Win+");
        sb.Append(vk is >= 0x70 and <= 0x7B ? "F" + (vk - 0x70 + 1) : vk == 0x20 ? "Space" : ((char)vk).ToString());
        return sb.ToString();
    }
}
