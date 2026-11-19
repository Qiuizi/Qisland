using System.Runtime.InteropServices;
using System.Text;

namespace Qisland.App;

/// <summary>
/// API Key 存在 Windows 凭据管理器（控制面板 → 凭据管理器 → Windows 凭据，名字以 Qisland/ 开头），只有当前 Windows 账户能读。
/// 不写日志、不写配置文件；岛上和设置窗口里都只显示「已保存」。
/// </summary>
public static class SecretStore
{
    private const int Generic = 1, LocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int BlobSize;
        public IntPtr Blob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref Credential c, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, int type, int flags, out IntPtr c);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr p);

    private static string Target(string name) => "Qisland/" + name;

    public static bool Save(string name, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var c = new Credential
            {
                Type = Generic, TargetName = Target(name), Persist = LocalMachine, UserName = "qisland",
                BlobSize = bytes.Length, Blob = h.AddrOfPinnedObject(), Comment = "Qisland 灵动岛保存的密钥",
            };
            var ok = CredWrite(ref c, 0);
            if (!ok) App.Log($"凭据管理器: 保存 {name} 失败（{Marshal.GetLastWin32Error()}）");
            return ok;
        }
        finally { Array.Clear(bytes); h.Free(); }
    }

    public static string? Load(string name)
    {
        if (!CredRead(Target(name), Generic, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<Credential>(p);
            return c.BlobSize > 0 ? Marshal.PtrToStringUni(c.Blob, c.BlobSize / 2) : null;
        }
        finally { CredFree(p); }
    }

    public static bool Has(string name) => Load(name) is { Length: > 0 };

    public static void Delete(string name) => CredDelete(Target(name), Generic, 0);
}
