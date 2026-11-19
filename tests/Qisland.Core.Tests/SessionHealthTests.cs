using Qisland.Core;

public class SessionHealthTests
{
    static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0);

    [Theory]
    [InlineData(200, true, "0", AuthVerdict.Ok)]
    [InlineData(200, true, null, AuthVerdict.Ok)]
    [InlineData(200, true, "50000", AuthVerdict.Suspect)]
    [InlineData(200, true, "401002", AuthVerdict.Rejected)]
    [InlineData(403, true, null, AuthVerdict.Rejected)]
    [InlineData(403, false, null, AuthVerdict.Transient)]   // 门户 / 网关的 HTML 403
    [InlineData(502, true, null, AuthVerdict.Transient)]
    [InlineData(200, false, null, AuthVerdict.Transient)]   // 被重定向到登录门户
    [InlineData(200, true, "30001", AuthVerdict.Transient)] // 某门课自己的业务错误
    public void Classify(int status, bool json, string? code, AuthVerdict want) => Assert.Equal(want, SessionHealth.ClassifyYuketang(status, json, code));

    [Fact]
    public void DeletedSessionCookieIsRejection() => Assert.Equal(AuthVerdict.Rejected, SessionHealth.ClassifyYuketang(200, true, "0", sessionDeleted: true));

    [Fact]
    public void SingleFailureNeverExpires()
    {
        var h = new SessionHealth();
        Assert.False(h.Report(AuthVerdict.Rejected, T0));
        Assert.False(h.Report(AuthVerdict.Transient, T0.AddMinutes(5)));
        Assert.False(h.Report(AuthVerdict.Ok, T0.AddMinutes(10)));
        Assert.False(h.Report(AuthVerdict.Rejected, T0.AddMinutes(11)));
        Assert.Equal(1, h.Strikes);
    }

    [Fact]
    public void TwoSpacedConfirmationsExpire()
    {
        var h = new SessionHealth();
        h.Report(AuthVerdict.Suspect, T0);
        Assert.False(h.Report(AuthVerdict.Rejected, T0.AddSeconds(10))); // 太快，不算
        Assert.True(h.Report(AuthVerdict.Rejected, T0.AddMinutes(2)));
        Assert.False(h.Report(AuthVerdict.Ok, T0.AddMinutes(30)));       // 自愈
    }
}

public class CookieSetTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 20, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RollingMaxAgeExtendsExpiry()
    {
        var c = new CookieSet();
        c.Set("sessionid", "abc", Now.AddHours(1));
        var (changed, deleted) = c.Apply("sessionid=abc; expires=Fri, 20 Nov 2026 14:00:00 GMT; HttpOnly; Max-Age=43200; Path=/; SameSite=Lax", Now);
        Assert.True(changed); Assert.False(deleted);
        Assert.Equal(Now.AddHours(12), c.ExpiresOf("sessionid"));
    }

    [Fact]
    public void RotatedValueIsKeptAndSent()
    {
        var c = new CookieSet();
        c.Set("sessionid", "old"); c.Set("csrftoken", "t");
        c.Apply("sessionid=new; Path=/", Now);
        Assert.Equal("new", c["sessionid"]);
        Assert.Contains("sessionid=new", c.Header(Now));
        Assert.Contains("csrftoken=t", c.Header(Now));
    }

    [Fact]
    public void SameCookieNoChange()
    {
        var c = new CookieSet();
        c.Apply("a=1; Max-Age=100", Now);
        Assert.False(c.Apply("a=1; Max-Age=100", Now.AddSeconds(10)).changed);
    }

    [Fact]
    public void DeletionDetected()
    {
        var c = new CookieSet();
        c.Set("sessionid", "x");
        var r = c.Apply("sessionid=\"\"; expires=Thu, 01 Jan 1970 00:00:00 GMT; Max-Age=0; Path=/", Now);
        Assert.True(r.deleted);
        Assert.Null(c["sessionid"]);
    }

    [Fact]
    public void ExpiresHeaderWithoutMaxAge()
    {
        var c = new CookieSet();
        c.Apply("a=1; expires=Fri, 20 Nov 2026 14:00:00 GMT", Now);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero), c.ExpiresOf("a"));
        Assert.Equal("", c.Header(Now.AddDays(1)));
    }
}
