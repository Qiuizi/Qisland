namespace Qisland.Core;

/// <summary>原子写文件：先写同目录的临时文件，再整体替换目标。写一半断电 / 目标被锁时旧文件原样保留。</summary>
public static class AtomicFile
{
    public static bool TryWrite(string path, byte[] data, out string? error)
    {
        error = null;
        string? tmp = null;
        try
        {
            tmp = Prepare(path, data);
            File.Move(tmp, path, overwrite: true);
            tmp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        finally { Discard(tmp); }
    }

    /// <summary>把新内容完整写进同目录的临时文件并落盘，返回临时文件路径；调用方随后整体替换或 <see cref="Discard"/>。</summary>
    public static string Prepare(string path, byte[] data)
    {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(data);
            fs.Flush(true);
        }
        catch { Discard(tmp); throw; }
        return tmp;
    }

    public static void Discard(string? tmp)
    {
        try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
    }

    /// <summary>原子删除：失败返回 false（文件被锁）。</summary>
    public static bool TryDelete(string path, out string? error)
    {
        error = null;
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; return false; }
    }
}
