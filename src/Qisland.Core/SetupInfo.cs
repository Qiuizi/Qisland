namespace Qisland.Core;

/// <summary>
/// 和 installer/Qisland.iss 的约定（自检里逐项比对 .iss，改一边必须改另一边）：
/// · AppId 固定不变：覆盖安装、升级、卸载都靠它找到同一份安装记录；
/// · 默认装到 %LOCALAPPDATA%\Programs\Qisland（免管理员，程序目录可写，应用内自动更新可以直接覆盖）；
/// · 装的是自带 .NET 运行时的全量包（目录里有 hostfxr.dll），更新器据此只挑 -full-x64.zip；
/// · 用户数据在 %APPDATA%\Qisland，卸载默认保留。
/// </summary>
public static class SetupInfo
{
    public const string AppId = "{835DC0CA-BDF5-4972-AAA4-1BC585564F72}";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";
    public const string DefaultDirName = @"{localappdata}\Programs\Qisland";
    public const string SetupFileFormat = "Qisland-{0}-Setup.exe";

    public static string SetupFileName(string version) => string.Format(System.Globalization.CultureInfo.InvariantCulture, SetupFileFormat, version.TrimStart('v', 'V'));

    /// <summary>两个目录是不是同一个（忽略大小写、结尾斜杠、/ 和 \）。</summary>
    public static bool SameDir(string? a, string? b)
    {
        static string N(string s) => s.Replace('/', '\\').TrimEnd('\\').Trim();
        return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }
}
