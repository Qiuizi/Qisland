using Qisland.Core;

namespace Qisland.Core.Tests;

public class SessionUsableTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 20, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExpiredSessionCookie_CannotAuthenticate_AndHeaderIsEmpty()
    {
        var c = new CookieSet();
        c.Set("sessionid", "abc", Now.AddHours(-3));   // v3 存下来的、已经过了 12 小时滑动期
        Assert.Equal("", c.Header(Now));
        Assert.False(c.IsLive("sessionid", Now));
        Assert.False(c.CanAuthenticate("sessionid", Now));
    }

    [Fact]
    public void MissingSession_OtherCookiesAlive_CannotAuthenticate()
    {
        var c = new CookieSet();
        c.Set("csrftoken", "t", Now.AddDays(300));
        Assert.NotEqual("", c.Header(Now));
        Assert.False(c.CanAuthenticate("sessionid", Now));
    }

    [Fact]
    public void EmptyValue_IsNotLive_AndNotSent()
    {
        var c = new CookieSet();
        c.Set("sessionid", "", Now.AddHours(5));
        Assert.False(c.CanAuthenticate("sessionid", Now));
        Assert.Equal("", c.Header(Now));
    }

    [Fact]
    public void LiveSession_CanAuthenticate()
    {
        var c = new CookieSet();
        c.Set("sessionid", "abc", Now.AddHours(1));
        c.Set("old", "x", Now.AddHours(-1));
        Assert.True(c.CanAuthenticate("sessionid", Now));
        Assert.Equal("sessionid=abc", c.Header(Now));
        var s = new CookieSet();
        s.Set("sessionid", "abc");                       // 没写过期时间 = 会话 Cookie
        Assert.True(s.CanAuthenticate("sessionid", Now));
    }
}

public class HotkeyFallbackTests
{
    [Fact]
    public void Translate_DefaultThenShiftThenExtras()
    {
        Assert.Equal(["Ctrl+Alt+T", "Ctrl+Alt+Shift+T", "Ctrl+Alt+Y", "Ctrl+Alt+Shift+Y"],
            HotkeyFallback.Candidates("Ctrl+Alt+T", HotkeyFallback.TranslateExtras));
    }

    [Fact]
    public void Ask_SkipsWhatTranslateTook()
    {
        var c = HotkeyFallback.Candidates("Ctrl+Alt+Y", HotkeyFallback.AskExtras, ["Ctrl+Alt+Y"]);
        Assert.DoesNotContain("Ctrl+Alt+Y", c);
        Assert.Equal("Ctrl+Alt+Shift+Y", c[0]);
    }

    [Fact]
    public void ShiftAlreadyPresent_NoDuplicate_InvalidConfiguredFallsBackToExtras()
    {
        Assert.Equal(["Ctrl+Alt+Shift+K", "Ctrl+Alt+J", "Ctrl+Alt+Shift+J"],
            HotkeyFallback.Candidates("Shift+Alt+Ctrl+K", HotkeyFallback.AskExtras));
        Assert.Equal(["Ctrl+Alt+J", "Ctrl+Alt+Shift+J"], HotkeyFallback.Candidates("K", HotkeyFallback.AskExtras));
        Assert.Equal(["Ctrl+Alt+Y", "Ctrl+Alt+Shift+Y"], HotkeyFallback.Candidates(null, HotkeyFallback.TranslateExtras));
    }
}
