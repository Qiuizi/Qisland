using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Qisland.App;

/// <summary>
/// `Qisland.exe hook codex`：Codex 的 hook 把事件 JSON 从 stdin 交给它，原样转给岛的本地接口后立即退出。
/// 不初始化 WPF；岛没开就静默退出，绝不阻塞 Codex。
/// </summary>
public static class HookRelay
{
    public static int Run(string source)
    {
        string body;
        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            body = stdin.ReadToEnd();
        }
        catch { body = ""; }
        try
        {
            if (body.Length > 0)
            {
                using var tcp = new TcpClient();
                if (tcp.ConnectAsync("127.0.0.1", App.Port).Wait(400))
                {
                    var payload = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes($"POST /v1/agent/{source} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    var s = tcp.GetStream();
                    s.WriteTimeout = s.ReadTimeout = 800;
                    s.Write(head);
                    s.Write(payload);
                    try { s.ReadByte(); } catch { }
                }
            }
        }
        catch { }
        // Stop / Interrupt 要求 exit 0 时 stdout 是 JSON
        try { using var o = Console.OpenStandardOutput(); o.Write("{}"u8); } catch { }
        return 0;
    }
}
