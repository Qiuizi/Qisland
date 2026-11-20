namespace Qisland.Core;

/// <summary>
/// 一维阻尼弹簧，用解析解推进：任意帧间隔都精确落在同一条轨迹上（30 / 60 / 120 / 144 Hz 一样），不会发散。
/// 目标随时可改、速度保留——展开到一半移开、收起到一半回来都不跳。
/// 参数两种写法：response（无阻尼周期，秒）+ dampingRatio（SwiftUI 那套），
/// 或 stiffness k + 阻尼比 ζ（岛仔 Daozai 那套，response = 2π/√k）。
/// </summary>
public class SpringMotion
{
    public double Value;
    public double Velocity;
    public double Target;
    public double Response = 0.42;
    public double DampingRatio = 0.82;
    /// <summary>离目标这么近、速度小于 10 倍它，就算停稳。</summary>
    public double Epsilon = 0.05;
    /// <summary>单帧最长按多少秒算：卡了一下（比如 UI 线程被 GC 卡 300 ms）不会一帧跳到终点。</summary>
    public const double MaxStep = 0.1;

    public SpringMotion(double value) { Value = Target = value; }

    public bool IsSettled => Math.Abs(Value - Target) < Epsilon && Math.Abs(Velocity) < Epsilon * 10;

    public void Snap(double v) { Value = Target = v; Velocity = 0; }

    /// <summary>换参数（速度、位置都保留）。</summary>
    public SpringMotion Tune(double response, double dampingRatio)
    {
        Response = Math.Max(0.02, response);
        DampingRatio = Math.Max(0, dampingRatio);
        return this;
    }

    /// <summary>岛仔的写法：stiffness k、阻尼比 ζ。</summary>
    public SpringMotion TuneStiffness(double stiffness, double dampingRatio) => Tune(ResponseOf(stiffness), dampingRatio);

    public static double ResponseOf(double stiffness) => 2 * Math.PI / Math.Sqrt(Math.Max(1e-6, stiffness));

    /// <summary>改目标；速度保留（可选再加一个冲量）。</summary>
    public void Retarget(double target, double impulse = 0)
    {
        Target = target;
        Velocity += impulse;
    }

    public void Step(double dt)
    {
        if (IsSettled) { Value = Target; Velocity = 0; return; }
        if (!(dt > 0)) return;
        dt = Math.Min(dt, MaxStep);
        (Value, Velocity) = Solve(Value - Target, Velocity, Response, DampingRatio, dt);
        Value += Target;
    }

    /// <summary>从偏移 x0、速度 v0 出发，经过 t 秒后的（偏移，速度）。</summary>
    public static (double x, double v) Solve(double x0, double v0, double response, double zeta, double t)
    {
        var w0 = 2 * Math.PI / response;
        if (zeta < 1)
        {
            var wd = w0 * Math.Sqrt(1 - zeta * zeta);
            var e = Math.Exp(-zeta * w0 * t);
            var c1 = x0;
            var c2 = (v0 + zeta * w0 * x0) / wd;
            var cos = Math.Cos(wd * t);
            var sin = Math.Sin(wd * t);
            return (e * (c1 * cos + c2 * sin), e * ((c2 * wd - zeta * w0 * c1) * cos - (c1 * wd + zeta * w0 * c2) * sin));
        }
        if (zeta == 1)
        {
            var e = Math.Exp(-w0 * t);
            var c2 = v0 + w0 * x0;
            return (e * (x0 + c2 * t), e * (c2 - w0 * (x0 + c2 * t)));
        }
        // 过阻尼
        var s = w0 * Math.Sqrt(zeta * zeta - 1);
        var r1 = -zeta * w0 + s;
        var r2 = -zeta * w0 - s;
        var a = (v0 - r2 * x0) / (r1 - r2);
        var b = x0 - a;
        var e1 = Math.Exp(r1 * t);
        var e2 = Math.Exp(r2 * t);
        return (a * e1 + b * e2, a * r1 * e1 + b * r2 * e2);
    }

    /// <summary>从静止、离目标 distance 出发，要多久才能停稳（离目标小于 epsilon）。给测试和调参用。</summary>
    public static double SettleTime(double response, double zeta, double distance, double epsilon, double dt = 1.0 / 240)
    {
        var s = new SpringMotion(distance) { Target = 0, Epsilon = epsilon }.Tune(response, zeta);
        var t = 0.0;
        while (!s.IsSettled && t < 10) { s.Step(dt); t += dt; }
        return t;
    }

    /// <summary>从静止出发的最大过冲（占距离的比例）：ζ &lt; 1 时约为 e^(-πζ/√(1-ζ²))。</summary>
    public static double Overshoot(double zeta) => zeta >= 1 ? 0 : Math.Exp(-Math.PI * zeta / Math.Sqrt(1 - zeta * zeta));
}

/// <summary>插值 / 缓动小工具（和岛仔 anim.rs 的同名函数含义一致，C# 重写）。</summary>
public static class MotionMath
{
    public static double Clamp01(double t) => Math.Clamp(t, 0, 1);
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>x 从 [a, b] 映射到 [0, 1] 并钳制；允许 a &gt; b（反向渐隐）。</summary>
    public static double Remap(double x, double a, double b)
    {
        var d = b - a;
        if (Math.Abs(d) < 1e-9) return x >= b ? 1 : 0;
        return Clamp01((x - a) / d);
    }

    public static double Smoothstep(double a, double b, double x)
    {
        var t = Remap(x, a, b);
        return t * t * (3 - 2 * t);
    }

    public static double EaseOutCubic(double t) { var u = 1 - Clamp01(t); return 1 - u * u * u; }

    /// <summary>帧率无关的指数逼近：rate 越大越快（约 1/rate 秒走完 63%）。</summary>
    public static double Approach(double cur, double target, double rate, double dt) => cur + (target - cur) * (1 - Math.Exp(-rate * dt));
}

/// <summary>岛的形变参数（全部在一处，方便和岛仔对照调）。</summary>
public static class IslandMotion
{
    // 岛仔：OPEN_W k=330 ζ0.70，OPEN_H k=290 ζ0.74，CLOSE k=620 ζ1.0，SWELL k=420 ζ0.8，PAGE k=260 ζ0.92
    public static readonly (double response, double zeta) OpenW = (0.35, 0.72);
    public static readonly (double response, double zeta) OpenH = (0.37, 0.76);
    public static readonly (double response, double zeta) OpenR = (0.36, 0.80);
    public static readonly (double response, double zeta) Close = (0.27, 1.0);
    public static readonly (double response, double zeta) Swell = (0.30, 0.78);
    /// <summary>悬停胀大：宽 ×1.06、高 ×1.10（岛仔是 1.075 / 1.13，我们的胶囊更宽，所以稍收一点）。</summary>
    public const double SwellW = 1.06, SwellH = 1.10;
    /// <summary>系统「动画效果」关掉时：临界阻尼、快一倍，不过冲。</summary>
    public static (double response, double zeta) Reduced((double response, double zeta) p) => (p.response * 0.6, 1.0);

    /// <summary>
    /// 内容跟着形体「揭开」：展开时形体走到 45% 才开始显内容、95% 显完；收起时内容在形体缩回 30% 之前就淡掉。
    /// progress = 当前高度在（收起高度 → 展开高度）之间的位置。
    /// </summary>
    public static double Reveal(double progress, bool opening) =>
        opening ? MotionMath.Smoothstep(0.45, 0.95, progress) : MotionMath.Smoothstep(0.55, 0.92, progress);

    /// <summary>翻页：旧页 0.16 s 内淡出并往反方向挪 18 px；新页 0.03–0.28 s 淡入（岛仔 page_e 的时序）。</summary>
    public static (double oldAlpha, double oldDx, double newAlpha) PageCross(double elapsed, int dir) =>
        (1 - MotionMath.Smoothstep(0, 0.16, elapsed), -dir * 18 * MotionMath.EaseOutCubic(elapsed / 0.3), MotionMath.Smoothstep(0.03, 0.28, elapsed));
}
