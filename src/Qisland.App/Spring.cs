namespace Qisland.App;

/// <summary>
/// 一维阻尼弹簧。用解析解推进，帧率抖动也不会发散。
/// response = 回弹周期（秒），dampingRatio &lt; 1 有轻微回弹。参数含义与 SwiftUI 的 spring 一致。
/// </summary>
public sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;
    public double Response = 0.42;
    public double DampingRatio = 0.82;

    public Spring(double value) { Value = Target = value; }

    public double Epsilon = 0.05;
    public bool IsSettled => Math.Abs(Value - Target) < Epsilon && Math.Abs(Velocity) < Epsilon * 10;

    public void Snap(double v) { Value = Target = v; Velocity = 0; }

    public void Step(double dt)
    {
        if (IsSettled) { Value = Target; Velocity = 0; return; }
        dt = Math.Min(dt, 1.0 / 30);
        var w0 = 2 * Math.PI / Response;
        var z = DampingRatio;
        var x0 = Value - Target;
        var v0 = Velocity;
        double x, v;
        if (z < 1)
        {
            var wd = w0 * Math.Sqrt(1 - z * z);
            var e = Math.Exp(-z * w0 * dt);
            var c1 = x0;
            var c2 = (v0 + z * w0 * x0) / wd;
            var cos = Math.Cos(wd * dt);
            var sin = Math.Sin(wd * dt);
            x = e * (c1 * cos + c2 * sin);
            v = e * ((c2 * wd - z * w0 * c1) * cos - (c1 * wd + z * w0 * c2) * sin);
        }
        else
        {
            var e = Math.Exp(-w0 * dt);
            var c1 = x0;
            var c2 = v0 + w0 * x0;
            x = e * (c1 + c2 * dt);
            v = e * (c2 - w0 * (c1 + c2 * dt));
        }
        Value = Target + x;
        Velocity = v;
    }
}
