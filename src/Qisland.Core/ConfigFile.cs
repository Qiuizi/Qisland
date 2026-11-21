using System.Security.Cryptography;
using System.Text;

namespace Qisland.Core;

/// <summary>读到的某一版配置：内容（文件不存在为 null）和版本戳（内容的 SHA256）。</summary>
public sealed record ConfigVersion(string Path, string? Text, string Stamp);

/// <summary>
/// 改别的软件的配置文件：读的时候记下版本；提交前核对文件还是这一版才原子替换（临时文件 + 覆盖移动），
/// 期间被别的程序改过就重新读、在最新内容上重新合并，绝不拿旧快照覆盖新内容。
/// </summary>
public static class ConfigFile
{
    public const int Attempts = 3;

    /// <summary>测试用：在提交前一刻调用（模拟别的程序恰好在这时改了文件）。</summary>
    public static Action<string>? BeforeCommitForTests { get; set; }

    public static ConfigVersion Read(string path)
    {
        if (!File.Exists(path)) return new(path, null, "missing");
        var bytes = File.ReadAllBytes(path);
        return new(path, new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF'), Stamp(bytes));
    }

    public static string StampOf(string path) => File.Exists(path) ? Stamp(File.ReadAllBytes(path)) : "missing";

    private static string Stamp(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>文件还是 expected 那一版才写入（newText 为 null 表示删除）；版本变了返回 false，什么都不动。</summary>
    public static bool TryCommit(ConfigVersion expected, string? newText, out string? error)
    {
        BeforeCommitForTests?.Invoke(expected.Path);
        error = null;
        if (StampOf(expected.Path) != expected.Stamp) { error = "文件在读取之后被别的程序改过"; return false; }
        if (newText == null)
            return expected.Text == null || AtomicFile.TryDelete(expected.Path, out error);
        var dir = System.IO.Path.GetDirectoryName(expected.Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return AtomicFile.TryWrite(expected.Path, new UTF8Encoding(false).GetBytes(newText), out error);
    }

    /// <summary>
    /// 读最新内容 → transform 算出新内容 → 核对版本后原子替换；版本对不上就重读重算，最多 <see cref="Attempts"/> 次。
    /// transform 返回和原内容一样的文本表示不用改。beforeWrite 在每次真正提交前拿到这一版（用来备份）。
    /// </summary>
    public static bool Update(string path, Func<string?, string?> transform, Action<ConfigVersion>? beforeWrite, out string? error)
    {
        error = null;
        for (var i = 0; i < Attempts; i++)
        {
            var current = Read(path);
            var next = transform(current.Text);
            if (next == current.Text) return true;
            beforeWrite?.Invoke(current);
            if (TryCommit(current, next, out error)) return true;
        }
        error ??= "文件一直在被别的程序改动";
        return false;
    }
}
