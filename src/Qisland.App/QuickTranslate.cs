using System.Net.Http;
using System.Text.Json;

namespace Qisland.App;

/// <summary>
/// 剪贴板一键翻译：只在你点「译」时把这一条文字发给 MyMemory 免费接口（国内直连可用），不自动上传任何东西。
/// 有中文就译成英文，否则译成中文；单次最多 500 字。
/// </summary>
public static class QuickTranslate
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static bool HasCjk(string s) => s.Any(c => c is >= '\u4e00' and <= '\u9fff');

    public static async Task<string> Run(string text)
    {
        text = text.Trim();
        if (text.Length > 500) text = text[..500];
        var pair = HasCjk(text) ? "zh-CN|en" : "en|zh-CN";
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(pair)}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var r = doc.RootElement;
        var status = r.TryGetProperty("responseStatus", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 200;
        var t = r.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
        if (status != 200) throw new InvalidOperationException(t.Length > 0 ? t : "翻译接口没有返回结果");
        return System.Net.WebUtility.HtmlDecode(t);
    }
}
