namespace Qisland.Core;

/// <summary>原子写文件：先写同目录的临时文件，再整体替换目标。写一半断电 / 目标被锁时旧文件原样保留。</summary>
public static class AtomicFile
{
    public static bool TryWrite(string path, byte[] data, out string? error)
    {
        error = null;
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    /// <summary>原子删除：失败返回 false（文件被锁）。</summary>
    public static bool TryDelete(string path, out string? error)
    {
        error = null;
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; return false; }
    }
}
