using Qisland.Core;

namespace Qisland.Core.Tests;

public class MotionTests
{
    private static SpringMotion Run(SpringMotion s, double dt, double seconds)
    {
        var n = (int)Math.Round(seconds / dt);
        for (var i = 0; i < n; i++) s.Step(dt);
        return s;
    }

    [Theory]
    [InlineData(0.35, 0.72)]
    [InlineData(0.37, 0.76)]
    [InlineData(0.27, 1.0)]
    [InlineData(0.30, 1.4)]
    public void Converges_ToTarget(double response, double zeta)
    {
        var s = new SpringMotion(36) { Target = 300, Epsilon = 0.05 }.Tune(response, zeta);
        Run(s, 1.0 / 120, 3);
        Assert.True(s.IsSettled, $"value {s.Value} vel {s.Velocity}");
        s.Step(1.0 / 120);
        Assert.Equal(300, s.Value);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    public void FrameRateIndependent(int hz)
    {
        var reference = Run(new SpringMotion(0) { Target = 1, Epsilon = 1e-9 }.Tune(0.35, 0.72), 1.0 / 1000, 0.5);
        var s = Run(new SpringMotion(0) { Target = 1, Epsilon = 1e-9 }.Tune(0.35, 0.72), 1.0 / hz, 0.5);
        Assert.Equal(reference.Value, s.Value, 6);
        Assert.Equal(reference.Velocity, s.Velocity, 4);
    }

    [Fact]
    public void JitteryFrames_SameTrajectory()
    {
        var a = new SpringMotion(0) { Target = 100, Epsilon = 1e-9 }.Tune(0.37, 0.76);
        var b = new SpringMotion(0) { Target = 100, Epsilon = 1e-9 }.Tune(0.37, 0.76);
        var rng = new Random(7);
        var t = 0.0;
        while (t < 0.4) { var dt = 0.004 + rng.NextDouble() * 0.03; a.Step(dt); t += dt; }
        for (var i = 0; i < 8; i++) b.Step(t / 8);
        Assert.Equal(b.Value, a.Value, 6);
    }

    [Fact]
    public void Retarget_KeepsVelocity_NoJump()
    {
        var s = new SpringMotion(0) { Target = 300 }.Tune(0.35, 0.72);
        Run(s, 1.0 / 120, 0.08);
        var (x, v) = (s.Value, s.Velocity);
        Assert.True(v > 100);
        s.Retarget(0);                       // 展开到一半移开
        Assert.Equal(x, s.Value);
        Assert.Equal(v, s.Velocity);
        s.Tune(0.27, 1.0);                   // 换成收起参数，速度不丢
        Assert.Equal(v, s.Velocity);
        s.Step(1.0 / 120);
        Assert.True(s.Value > x, "惯性还会往前走一点，再掉头");
        Assert.True(Math.Abs(s.Value - x) < v / 120 + 0.5, "一帧内不会跳变");
    }

    [Fact]
    public void Retarget_ImpulseAddsVelocity()
    {
        var s = new SpringMotion(10);
        s.Retarget(10, impulse: 220);
        Assert.False(s.IsSettled);
        Run(s, 1.0 / 120, 2);
        Assert.True(s.IsSettled);
    }

    [Fact]
    public void Underdamped_OvershootsGently_CriticalNever()
    {
        var open = new SpringMotion(0) { Target = 1, Epsilon = 1e-4 }.Tune(0.35, 0.72);
        var peak = 0.0;
        for (var i = 0; i < 240; i++) { open.Step(1.0 / 120); peak = Math.Max(peak, open.Value); }
        Assert.InRange(peak, 1.02, 1.06);
        Assert.Equal(SpringMotion.Overshoot(0.72), peak - 1, 2);

        var close = new SpringMotion(1) { Target = 0, Epsilon = 1e-4 }.Tune(0.27, 1.0);
        for (var i = 0; i < 240; i++) { close.Step(1.0 / 120); Assert.True(close.Value > -1e-6); }
    }

    [Fact]
    public void SettleTimes_AreSnappy()
    {
        // 展开（0.05 px 精度、走 300 px）在 0.8 s 内停稳；收起 0.6 s 内
        var open = SpringMotion.SettleTime(IslandMotion.OpenH.response, IslandMotion.OpenH.zeta, 300, 0.05);
        var close = SpringMotion.SettleTime(IslandMotion.Close.response, IslandMotion.Close.zeta, 300, 0.05);
        Assert.InRange(open, 0.3, 0.8);
        Assert.InRange(close, 0.15, 0.6);
        // 系统关了动画效果：更快、不过冲
        var r = IslandMotion.Reduced(IslandMotion.OpenH);
        Assert.Equal(1.0, r.zeta);
        Assert.True(SpringMotion.SettleTime(r.response, r.zeta, 300, 0.05) < close);
    }

    [Fact]
    public void StiffnessForm_MatchesDaozai()
    {
        // 岛仔 OPEN_W k=330 → 周期约 0.346 s
        Assert.Equal(0.346, SpringMotion.ResponseOf(330), 3);
        var s = new SpringMotion(0).TuneStiffness(620, 1.0);
        Assert.Equal(0.252, s.Response, 3);
    }

    [Fact]
    public void HugeFrameGap_IsClamped()
    {
        var s = new SpringMotion(0) { Target = 100 }.Tune(0.37, 0.76);
        s.Step(1.5);                         // UI 线程卡了 1.5 秒
        var once = s.Value;
        var t = new SpringMotion(0) { Target = 100 }.Tune(0.37, 0.76);
        t.Step(SpringMotion.MaxStep);
        Assert.Equal(t.Value, once, 9);
        Assert.True(once < 100);
    }

    [Fact]
    public void ZeroOrNegativeDt_DoesNothing()
    {
        var s = new SpringMotion(0) { Target = 1 };
        s.Step(0); s.Step(-1); s.Step(double.NaN);
        Assert.Equal(0, s.Value);
    }

    [Fact]
    public void Overdamped_Converges()
    {
        var s = Run(new SpringMotion(0) { Target = 50, Epsilon = 0.01 }.Tune(0.3, 2.0), 1.0 / 60, 4);
        Assert.True(s.IsSettled);
    }

    [Fact]
    public void MotionMath_Basics()
    {
        Assert.Equal(1, MotionMath.Remap(200, 392, 358));
        Assert.Equal(0.5, MotionMath.Smoothstep(392, 358, 375), 4);
        Assert.Equal(0, MotionMath.Smoothstep(0.45, 0.95, 0.2));
        Assert.Equal(1, MotionMath.EaseOutCubic(2));
        // Approach 帧率无关
        var a = 0.0; for (var i = 0; i < 30; i++) a = MotionMath.Approach(a, 1, 8, 1.0 / 30);
        var b = 0.0; for (var i = 0; i < 144; i++) b = MotionMath.Approach(b, 1, 8, 1.0 / 144);
        Assert.Equal(a, b, 9);
    }

    [Fact]
    public void Reveal_GatesContentOnShape()
    {
        Assert.Equal(0, IslandMotion.Reveal(0.3, opening: true));
        Assert.Equal(1, IslandMotion.Reveal(1.0, opening: true));
        Assert.True(IslandMotion.Reveal(0.7, opening: true) is > 0.2 and < 0.8);
        Assert.Equal(0, IslandMotion.Reveal(0.5, opening: false));
    }

    [Fact]
    public void PageCross_OldOutFastNewInLater()
    {
        var (o0, dx0, n0) = IslandMotion.PageCross(0, 1);
        Assert.Equal((1.0, 0.0, 0.0), (o0, dx0, n0));
        var (o1, dx1, n1) = IslandMotion.PageCross(0.16, 1);
        Assert.Equal(0, o1);
        Assert.True(dx1 < -10);
        Assert.True(n1 is > 0.3 and < 1);
        Assert.Equal(1, IslandMotion.PageCross(0.3, -1).newAlpha);
    }
}
