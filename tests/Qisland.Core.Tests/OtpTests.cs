using Qisland.Core;
using Xunit;

public class OtpTests
{
    [Theory]
    [InlineData("【招商银行】验证码123456，您正在登录，5分钟内有效", "123456")]
    [InlineData("您的验证码是 4821。请勿泄露", "4821")]
    [InlineData("Your verification code is 739201", "739201")]
    [InlineData("G-552913 is your Google verification code", null)]
    [InlineData("尾号1234的卡消费 88.50 元", null)]
    [InlineData("明天 2026-11-18 开会", null)]
    [InlineData("王老师：明天的实验课改到 B403", null)]
    [InlineData("【12306】验证码：8899，订单号 E123456789", "8899")]
    public void Extracts(string text, string? code) => Assert.Equal(code, Otp.Extract(text));
}
