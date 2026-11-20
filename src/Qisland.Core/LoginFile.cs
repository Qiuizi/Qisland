namespace Qisland.Core;

/// <summary>退出登录的结果：凭据删掉了 / 删不掉但写好了退出标记（会继续重试删除）/ 两样都没做成（重启后可能恢复登录）。</summary>
public enum LogoutOutcome { Removed, Pending, Failed }

/// <summary>
/// 保存登录凭据的文件（学习通 xuexitong.bin、雨课堂 yuketang.bin）的退出登录处理。
/// 凭据文件被别的程序占用时删不掉、也不能被替换，所以退出时在旁边写一个退出标记（.loggedout，内容是退出时间）：
/// 读凭据前先看标记，凭据的保存时间不晚于退出时间就不恢复登录。之后按退避重试删除，删掉了连标记一起清掉。
/// 新登录保存的凭据带着更晚的保存时间，即使旧标记还没删掉也照常生效。
/// </summary>
public sealed class LoginFile(string path, Func<string, bool>? tryDelete = null, Func<string, string, bool>? tryWrite = null, Func<DateTimeOffset>? clock = null)
{
    public string Path => path;
    public string TombPath => path + ".loggedout";

    public long NowMs => (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

    /// <summary>退出登录：删凭据；删不掉就写退出标记。</summary>
    public LogoutOutcome Logout()
    {
        if (Delete(path)) { Delete(TombPath); return LogoutOutcome.Removed; }
        return Write(TombPath, NowMs.ToString()) ? LogoutOutcome.Pending : LogoutOutcome.Failed;
    }

    /// <summary>读凭据前：这份凭据（保存于 savedMs，旧版本没有保存时间传 0）是不是已经退出登录了。</summary>
    public bool Revoked(long savedMs)
    {
        try
        {
            if (!File.Exists(TombPath)) return false;
            return !long.TryParse(File.ReadAllText(TombPath).Trim(), out var at) || at >= savedMs;
        }
        catch { return true; }   // 标记在但读不了：按已退出处理
    }

    /// <summary>重试清理：删凭据，再删标记。返回是否都删干净了。</summary>
    public bool Cleanup() => Delete(path) && Delete(TombPath);

    /// <summary>新登录写好凭据之后：旧的退出标记没用了（删不掉也不要紧，保存时间更晚）。</summary>
    public void Saved() => Delete(TombPath);

    private bool Delete(string p)
    {
        try
        {
            if (tryDelete != null) return tryDelete(p);
            if (File.Exists(p)) File.Delete(p);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool Write(string p, string text)
    {
        try
        {
            if (tryWrite != null) return tryWrite(p, text);
            return AtomicFile.TryWrite(p, System.Text.Encoding.UTF8.GetBytes(text), out _);
        }
        catch { return false; }
    }
}
