using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Qisland.App;

/// <summary>用 Windows DPAPI（当前用户）加密本地小文件：登录凭据只有这台电脑上的这个账户能解开。</summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);

    private const int UiForbidden = 0x1;

    private static byte[] Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var inp = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inp, "Qisland", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outp)
                : CryptUnprotectData(ref inp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outp);
            if (!ok) throw new IOException("DPAPI 失败: " + Marshal.GetLastWin32Error());
            var res = new byte[outp.Size];
            Marshal.Copy(outp.Data, res, 0, outp.Size);
            LocalFree(outp.Data);
            return res;
        }
        finally { h.Free(); }
    }

    public static void Save(string path, string text) => File.WriteAllBytes(path, Run(Encoding.UTF8.GetBytes(text), true));

    public static string? Load(string path)
    {
        try { return File.Exists(path) ? Encoding.UTF8.GetString(Run(File.ReadAllBytes(path), false)) : null; }
        catch (Exception ex) { App.Log("DPAPI: " + ex.Message); return null; }
    }
}
