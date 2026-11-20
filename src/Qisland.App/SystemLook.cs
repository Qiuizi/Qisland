namespace Qisland.App;

/// <summary>系统个性化里的「透明效果」和应用深浅色（注册表读一次，改了再读）。</summary>
internal static class SystemLook
{
    public static bool TransparencyOn { get; private set; } = true;
    public static bool AppsLight { get; private set; }

    static SystemLook() => Refresh();

    public static void Refresh()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            TransparencyOn = k?.GetValue("EnableTransparency") is not int t || t != 0;
            AppsLight = k?.GetValue("AppsUseLightTheme") is int l && l != 0;
        }
        catch { TransparencyOn = true; }
    }
}
