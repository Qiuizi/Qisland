namespace Qisland.App;

/// <summary>岛上用的弹簧：数学在 Core 的 <see cref="Qisland.Core.SpringMotion"/>（有单元测试）。</summary>
public sealed class Spring(double value) : Qisland.Core.SpringMotion(value);
