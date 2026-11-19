using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 翻译 / 问 AI 的网络调用。只在你按快捷键或点按钮时发出，只发选中的这段文字；
/// 原文、译文、回答都只在内存里，不写日志（日志里只记引擎名、字数和耗时）。
/// </summary>
public static class AiClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(40),
    };
    public const int MaxChars = 4000;

    public static async Task<string> Translate(AiSettings s, string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length > MaxChars) text = text[..MaxChars];
        var dir = AiCatalog.Direction(text);
        var e = AiCatalog.ById(s.TranslateEngine) ?? AiCatalog.Info(AiEngine.MyMemory);
        if (!AiSettings.Ready(e)) e = AiCatalog.Info(AiEngine.MyMemory);   // 没填 Key 就退回免费引擎
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string res;
        if (e.Engine == AiEngine.MyMemory)
        {
            var parts = new List<string>();
            foreach (var chunk in AiCatalog.Chunk(text).Take(6))
            {
                using var resp = await Http.GetAsync(AiCatalog.MyMemoryUrl(chunk, dir), ct);
                if ((int)resp.StatusCode == 429) throw new AiException("免费翻译今天额度用完了，可以在设置里换一个引擎");
                if (!resp.IsSuccessStatusCode) throw new AiException($"免费翻译暂时不可用（HTTP {(int)resp.StatusCode}）");
                parts.Add(AiCatalog.ParseMyMemory(await resp.Content.ReadAsStringAsync(ct)));
            }
            res = string.Join(dir.to == "en" ? " " : "", parts);
        }
        else res = await Chat(s, e, AiCatalog.TranslatePrompt(AiCatalog.TargetName(dir.to)), text, 1500, ct);
        App.Log($"翻译: {e.Name} · {text.Length} 字 · {sw.ElapsedMilliseconds} ms");
        return res;
    }

    public static async Task<string> Ask(AiSettings s, string text, CancellationToken ct)
    {
        var e = AiCatalog.ById(s.AskEngine);
        if (e == null || e.Engine == AiEngine.MyMemory) throw new AiException("先在「翻译与 AI 设置」里选一个 AI 引擎");
        if (!AiSettings.Ready(e)) throw new AiException($"还没填 {e.Name} 的 API Key（托盘 → 翻译与 AI 设置）");
        text = text.Trim();
        if (text.Length > MaxChars) text = text[..MaxChars];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await Chat(s, e, AiCatalog.AskPrompt, text, 600, ct);
        App.Log($"问 AI: {e.Name} · {text.Length} 字 · {sw.ElapsedMilliseconds} ms");
        return res;
    }

    private static async Task<string> Chat(AiSettings s, AiEngineInfo e, string system, string user, int maxTokens, CancellationToken ct)
    {
        if (!AiCatalog.EndpointAllowed(e.Engine, e.Endpoint)) throw new AiException("接口地址不在允许名单里");
        using var req = new HttpRequestMessage(HttpMethod.Post, e.Endpoint)
        {
            Content = new StringContent(AiCatalog.ChatBody(e.Engine, s.ModelFor(e), system, user, maxTokens), Encoding.UTF8, "application/json"),
        };
        if (e.NeedsKey)
        {
            var key = SecretStore.Load(AiSettings.KeyName(e));
            if (string.IsNullOrWhiteSpace(key)) throw new AiException($"还没填 {e.Name} 的 API Key");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Trim());
        }
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, ct); }
        catch (HttpRequestException) when (e.Engine == AiEngine.Ollama) { throw new AiException("连不上本机 Ollama（127.0.0.1:11434），先启动它"); }
        catch (HttpRequestException) { throw new AiException("网络连不上 " + e.Name); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AiException(e.Name + " 响应超时"); }
        using (resp) return AiCatalog.ParseChat(e.Engine, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    public sealed record Balance(string Name, string Text);

    /// <summary>用你已经保存的 Key 读 DeepSeek / Kimi 的账户余额（官方接口，只读）。没 Key 的跳过。</summary>
    public static async Task<List<Balance>> Balances(CancellationToken ct)
    {
        var res = new List<Balance>();
        foreach (var (engine, url) in new[] { (AiEngine.DeepSeek, "https://api.deepseek.com/user/balance"), (AiEngine.Kimi, "https://api.moonshot.cn/v1/users/me/balance") })
        {
            var e = AiCatalog.Info(engine);
            var key = SecretStore.Load(AiSettings.KeyName(e));
            if (string.IsNullOrWhiteSpace(key)) continue;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Trim());
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) { res.Add(new(e.Name, (int)resp.StatusCode is 401 or 403 ? "Key 失效" : "读不到")); continue; }
                var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                string text;
                if (engine == AiEngine.DeepSeek)
                {
                    var b = (j?["balance_infos"] as JsonArray)?.FirstOrDefault();
                    text = b == null ? "读不到" : $"{(b["currency"]?.ToString() == "USD" ? "$" : "¥")}{b["total_balance"]}";
                }
                else text = j?["data"]?["available_balance"] is { } ab ? $"¥{double.Parse(ab.ToString(), System.Globalization.CultureInfo.InvariantCulture):0.##}" : "读不到";
                res.Add(new(e.Name, text));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { res.Add(new(e.Name, "读不到")); }
        }
        return res;
    }
}
