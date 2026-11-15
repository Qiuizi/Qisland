using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Qisland.App;

/// <summary>
/// `Qisland.exe hook codex`：Codex 的 hook 把事件 JSON 从 stdin 交给它，原样转给岛的本地接口后立即退出。
/// `hook codex --approve`（只挂在 PermissionRequest 上）：等你在岛上点允许 / 拒绝，再把决定交还给 Codex。
/// 不初始化 WPF；岛没开就静默退出，绝不阻塞 Codex，也绝不替你默认同意。
/// </summary>
public static class HookRelay
{
    public static int Run(string source, bool approve = false)
    {
        string body;
        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            body = stdin.ReadToEnd();
        }
        catch { body = ""; }

        var output = "{}";
        if (approve && body.Contains("PermissionRequest"))
        {
            // 没拿到决定就什么都不输出，Codex 回到终端里正常问你
            output = "";
            var resp = Post($"/v1/approval/{source}", body, (int)ApprovalBroker.Wait.TotalMilliseconds + 8000);
            var d = resp.Contains("\"allow\"") ? "allow" : resp.Contains("\"deny\"") ? "deny" : "";
            output = ApprovalBroker.CodexOutput(d);
        }
        else if (body.Length > 0) Post($"/v1/agent/{source}", body, 800);

        // Stop / Interrupt 要求 exit 0 时 stdout 是 JSON
        if (output.Length > 0)
            try { using var o = Console.OpenStandardOutput(); o.Write(Encoding.UTF8.GetBytes(output)); } catch { }
        return 0;
    }

    private static string Post(string path, string body, int readTimeout)
    {
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync("127.0.0.1", App.Port).Wait(400)) return "";
            var payload = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            var s = tcp.GetStream();
            s.WriteTimeout = 800;
            s.ReadTimeout = readTimeout;
            s.Write(head);
            s.Write(payload);
            using var ms = new MemoryStream();
            try { s.CopyTo(ms); } catch { }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return ""; }
    }
}
