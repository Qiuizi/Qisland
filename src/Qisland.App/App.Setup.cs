using Qisland.Core;

namespace Qisland.App;

public sealed partial class App
{
    /// <summary>
    /// 用 Setup.exe 装的：应用内自动更新覆盖完文件后，「设置 → 应用」里的版本号还停在安装时的版本。
    /// 启动时如果安装记录指向的就是本程序目录，把 DisplayVersion 改成当前版本（只写 HKCU 里属于 Qisland 的那一项）。
    /// zip 解压版没有这条记录，什么都不做。
    /// </summary>
    private static void SyncSetupVersion()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SetupInfo.UninstallKey, writable: true);
            if (k == null) return;
            if (!SetupInfo.SameDir(k.GetValue("InstallLocation") as string, AppContext.BaseDirectory)) return;
            if (k.GetValue("DisplayVersion") as string == Updater.Current) return;
            k.SetValue("DisplayVersion", Updater.Current);
            Log($"安装记录版本号同步为 {Updater.Current}");
        }
        catch (Exception ex) { Log("同步安装记录: " + ex.Message); }
    }
}
