namespace Qisland.Core;

/// <summary>
/// 本机推送接口 Token 的查找规则，所有内置接入共用一份：
/// 环境变量 QISLAND_TOKEN 优先 → 其次 QISLAND_API_TOKEN_FILE 指向的文件 → 默认 %APPDATA%\Qisland\api-token.txt；
/// Token 只能是一段不含空白的可见 ASCII（拼进 HTTP 头不会多出别的头），不符合就当没有。
/// C# 的 Hook 中转（ApiTokenFile）和生成的 OpenCode 插件（<see cref="JsLookup"/>）都按这里走。
/// </summary>
public static class ApiTokenRule
{
    public const string EnvToken = "QISLAND_TOKEN";
    public const string EnvFile = "QISLAND_API_TOKEN_FILE";
    public const string FileName = "api-token.txt";
    public const string DataFolder = "Qisland";

    public static bool IsValid(string? token) =>
        !string.IsNullOrEmpty(token) && token.All(c => c > ' ' && c < '\x7f');

    /// <summary>给 JS 插件用的同一套规则（Node / Bun 都能跑）：定义 qislandToken()，每次上报时现读，Token 换了不用重装插件。</summary>
    public static string JsLookup => $$"""
        const qislandValid = (t) => /^[\x21-\x7e]+$/.test(t);
        const qislandToken = () => {
          const env = (process.env.{{EnvToken}} || "").trim();
          if (env) return qislandValid(env) ? env : "";
          try {
            const file = process.env.{{EnvFile}} || join(process.env.APPDATA || "", "{{DataFolder}}", "{{FileName}}");
            const t = readFileSync(file, "utf8").replace(/^\uFEFF/, "").trim();
            return qislandValid(t) ? t : "";
          } catch { return ""; }
        };
        """;
}
