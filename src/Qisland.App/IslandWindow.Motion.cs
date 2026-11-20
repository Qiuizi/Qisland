using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 动效：形体弹簧调参、悬停胀大、内容跟形体揭开、翻页残影、页码点、按压回弹、通知摇铃、系统「动画效果」开关。
/// 节奏参考岛仔 Daozai（MIT，见 THIRD_PARTY.md），在 WPF 里用 C# 重写。
/// 原则：一切每帧的东西都挂在 CompositionTarget.Rendering 那一个循环里，停稳就退订；
/// 只有一次性的小动画（按压、页码点、摇铃）交给 WPF 自己的时间线，播完自动停。
/// </summary>
public sealed partial class IslandWindow
{
    // ───────────── 系统「动画效果」 ─────────────

    private static bool _reducedMotion = !SystemParameters.ClientAreaAnimation;
    /// <summary>设置 → 辅助功能 → 视觉效果 → 动画效果 关掉时为 true：弹簧改临界阻尼且更快，不胀大、不模糊、不逐行弹出，翻页只交叉淡入淡出。</summary>
    internal static bool ReducedMotion => _reducedMotion;

    private void SetupMotion()
    {
        // WPF 收到 WM_SETTINGCHANGE 会刷新 SystemParameters 并发这个事件，不用自己挂 WndProc
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
            var r = !SystemParameters.ClientAreaAnimation;
            if (r == _reducedMotion) return;
            _reducedMotion = r;
            App.Log($"系统动画效果 {(r ? "关" : "开")}");
            Dispatcher.BeginInvoke(() => SetMode(_mode));
        };
        if (_reducedMotion) App.Log("系统动画效果已关：动效改为快速淡入淡出");
    }

    private static void TuneP(Spring s, (double response, double zeta) p)
    {
        if (_reducedMotion) p = IslandMotion.Reduced(p);
        s.Tune(p.response, p.zeta);
    }

    // ───────────── 形体 ─────────────

    /// <summary>正在展开或收起（不是展开后页面之间换高度）：内容按形体进度揭开。</summary>
    private bool _morphing;
    /// <summary>正在收起：越过停靠尺寸那一下直接贴住，不往里缩过头。</summary>
    private bool _closing;
    private bool _swell;
    private double _restH = IdleH;

    /// <summary>SetMode 定好目标之后调：按状态换弹簧参数、叠上悬停胀大。</summary>
    private void ShapeMotion(Mode m, bool wasExpanded)
    {
        if (m == Mode.Expanded)
        {
            if (!wasExpanded) { _morphing = true; _closing = false; }
            TuneP(_w, IslandMotion.OpenW); TuneP(_h, IslandMotion.OpenH); TuneP(_r, IslandMotion.OpenR);
            return;
        }
        _restH = _h.Target;
        if (wasExpanded) { _morphing = true; _closing = true; }
        var swell = _swell && !_reducedMotion && !_closing && !_hidden;
        if (swell)
        {
            _w.Target = Math.Round(_w.Target * IslandMotion.SwellW);
            _h.Target = Math.Round(_h.Target * IslandMotion.SwellH);
            _r.Target = _h.Target / 2;
        }
        var p = _closing ? IslandMotion.Close : IslandMotion.Swell;
        TuneP(_w, p); TuneP(_h, p); TuneP(_r, p);
    }

    /// <summary>收起的岛被指针罩住：先轻轻胀大一圈（停稳后才真正展开），移开就缩回。</summary>
    private void SetSwell(bool on)
    {
        if (_reducedMotion) on = false;
        if (on == _swell) return;
        _swell = on;
        if (_mode != Mode.Expanded && !_closing) SetMode(_mode);
    }

    /// <summary>每帧弹簧推完之后：收起越过停靠尺寸就贴住（岛仔 morph 里同样的处理），停稳后清状态。</summary>
    private void AfterStep(bool settled)
    {
        if (_closing)
            foreach (var s in new[] { _w, _h })
                if (s.Velocity < 0 && s.Value < s.Target) { s.Value = s.Target; s.Velocity = 0; }
        if (settled) { _morphing = false; _closing = false; if (_expanded.Effect != null) _expanded.Effect = null; }
    }

    private BlurEffect? _revealBlur;
    private bool BlurAllowed => !_reducedMotion && !PerfGuard.Degraded && (RenderCapability.Tier >> 16) >= 2;

    /// <summary>形体进度 0（停靠）→ 1（展开高度）。</summary>
    private double ShapeProgress(double h)
    {
        var span = _expandedH - _restH;
        return span < 1 ? (_mode == Mode.Expanded ? 1 : 0) : MotionMath.Clamp01((h - _restH) / span);
    }

    /// <summary>三层内容的透明度和缩放：展开时小岛内容放大淡出（像穿过去），展开内容在形体走到一半后才浮现，带一点模糊和缩放。</summary>
    private void ApplyLayers(double h)
    {
        if (!_morphing)
        {
            Fade(_idle, _idleOp.Value);
            Fade(_compact, _compactOp.Value);
            Fade(_expanded, _expOp.Value);
            return;
        }
        var p = ShapeProgress(h);
        var opening = _mode == Mode.Expanded;
        // 停靠内容：展开时形体走到 38% 前就放大淡出（像穿过去）；收起时形体缩回 40% 以内才出来
        var restVis = opening ? 1 - MotionMath.Smoothstep(0.06, 0.38, p) : 1 - MotionMath.Smoothstep(0.08, 0.4, p);
        var zoom = 1 + 0.10 * MotionMath.Smoothstep(0, 0.5, p);
        FadeScaled(_idle, Math.Min(_idleOp.Value, restVis), zoom);
        FadeScaled(_compact, Math.Min(_compactOp.Value, restVis), zoom);
        var reveal = IslandMotion.Reveal(p, opening);
        var e = Math.Clamp(Math.Min(_expOp.Value, reveal), 0, 1);
        FadeScaled(_expanded, e, 0.955 + 0.045 * e);
        if (BlurAllowed && e > 0.01 && e < 0.97)
        {
            _revealBlur ??= new BlurEffect { RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
            var r = Math.Round((1 - e) * 6, 1);
            if (_revealBlur.Radius != r) _revealBlur.Radius = r;
            if (_expanded.Effect != _revealBlur) _expanded.Effect = _revealBlur;
        }
        else if (_expanded.Effect != null) _expanded.Effect = null;
    }

    private static void FadeScaled(FrameworkElement layer, double v, double scale)
    {
        var o = Math.Clamp(v, 0, 1);
        if (o > 0.995) o = 1;
        layer.Opacity = o;
        var vis = o < 0.01 ? Visibility.Hidden : Visibility.Visible;
        if (layer.Visibility != vis) layer.Visibility = vis;
        var st = (ScaleTransform)layer.RenderTransform;
        var k = o >= 1 && Math.Abs(scale - 1) < 0.002 ? 1 : scale;
        if (st.ScaleX != k) st.ScaleX = st.ScaleY = k;
    }

    // ───────────── 翻页：旧页残影 + 新页滑入 ─────────────

    /// <summary>新页从这么远滑进来（岛仔 46 px；我们的页更宽，取 64）。</summary>
    private const double PageSlide = 64;
    private static readonly (double, double) PageSpring = (0.40, 0.86);
    private StackPanel? _ghost;
    private readonly TranslateTransform _ghostTT = new();
    private int _ghostDir;
    private readonly Stopwatch _flipClock = new();
    private bool _flipping;
    private double _flipAlpha = 1;

    /// <summary>GoTo 换完页之后调：旧页留在原地淡出并往反方向挪一点，新页从运动方向滑进来；连续快翻时带上一次的余速。</summary>
    private void BeginFlip(StackPanel? old, int dir, double carryX, double carryV)
    {
        EndGhost();
        _flipping = true;
        _flipClock.Restart();
        _flipAlpha = 0;
        if (old != null && old != _expStack)
        {
            _ghost = old;
            _ghostDir = dir;
            (old.Parent as Panel)?.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, null);
            old.RenderTransform = _ghostTT;
            _ghostTT.X = 0;
            old.IsHitTestVisible = false;
            if (old.CacheMode == null) old.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
            _expanded.Children.Insert(0, old);
        }
        TuneP(_pageX, PageSpring);
        if (_reducedMotion) { _pageX.Snap(0); return; }
        _pageX.Value = dir * PageSlide + carryX * 0.3;
        _pageX.Velocity = carryV * 0.3;
        _pageX.Target = 0;
    }

    /// <summary>每帧（ApplyFrame 开头）推进翻页交叉淡化；返回是否还在进行。</summary>
    private bool StepFlip()
    {
        if (!_flipping) { _flipAlpha = 1; return false; }
        var e = _flipClock.Elapsed.TotalSeconds * (_reducedMotion ? 1.5 : 1);
        var (oa, dx, na) = IslandMotion.PageCross(e, _ghostDir);
        _flipAlpha = na;
        if (_ghost != null) { _ghost.Opacity = oa; _ghostTT.X = _reducedMotion ? 0 : dx; }
        if (e >= 0.3) { EndGhost(); _flipping = false; _flipAlpha = 1; _flipClock.Reset(); }
        return _flipping;
    }

    private void EndGhost()
    {
        if (_ghost is not { } g) return;
        _ghost = null;
        if (g == _expStack) { g.IsHitTestVisible = true; return; } // 快速翻回到了这一页：它已经是当前页
        (g.Parent as Panel)?.Children.Remove(g);
        g.Opacity = 1;
        g.CacheMode = null;
        g.RenderTransform = null;
        g.IsHitTestVisible = true;
    }

    // ───────────── 页码点 ─────────────

    /// <summary>页码点：当前页那颗从 6 拉长到 16，上一页那颗从 16 缩回 6（带一点回弹）。</summary>
    private void AnimateDots(int from, int to)
    {
        if (_reducedMotion || _expStack == null || _expStack.Children.Count == 0) return;
        if (_expStack.Children[^1] is not FrameworkElement { Tag: Border[] dots }) return;
        void Run(int i, double a, double b)
        {
            if (i < 0 || i >= dots.Length) return;
            var anim = new DoubleAnimation(a, b, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
                FillBehavior = FillBehavior.Stop, // 播完回到本地值（本来就是终值），不留动画时钟
            };
            Timeline.SetDesiredFrameRate(anim, PerfGuard.Hz);
            dots[i].BeginAnimation(WidthProperty, anim);
        }
        Run(to, 6, 16);
        Run(from, 16, 6);
    }

    // ───────────── 按压 / 悬停微反馈 ─────────────

    private static readonly IEasingFunction PressEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction ReleaseEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 };
    private static readonly IEasingFunction SoftEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>按下缩一点（70 ms），松开带一点回弹弹回（260 ms）；移出也弹回。系统关了动画效果时不缩。</summary>
    internal static T Pressable<T>(T el, double pressed = 0.92) where T : FrameworkElement
    {
        var st = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = st;
        void To(double v, double ms, IEasingFunction ease)
        {
            if (_reducedMotion && v < 1) return;
            // 外层的逐行弹出（PopIn）可能临时接管了 RenderTransform：那期间不抢
            if (el.RenderTransform != st)
            {
                if (el.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true })) return;
                el.RenderTransform = st;
            }
            var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        el.PreviewMouseLeftButtonDown += (_, _) => To(pressed, 70, PressEase);
        el.PreviewMouseLeftButtonUp += (_, _) => To(1, 260, ReleaseEase);
        el.MouseLeave += (_, _) => { if (st.ScaleX < 0.999) To(1, 200, SoftEase); };
        return el;
    }

    /// <summary>悬停底色淡入淡出（140 ms），而不是一下子换色。</summary>
    private static void HoverFade(Border bg, bool on)
    {
        if (_reducedMotion) { bg.BeginAnimation(OpacityProperty, null); bg.Opacity = on ? 1 : 0; return; }
        var a = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 140 : 180)) { EasingFunction = SoftEase };
        bg.BeginAnimation(OpacityProperty, a);
    }

    // ───────────── 通知进出 ─────────────

    /// <summary>小岛换了一条新通知：内容依次从下面浮上来（错开 35 ms），而不是瞬间换字。</summary>
    private void CompactSwapIn()
    {
        if (_reducedMotion || _compact.Visibility != Visibility.Visible) return;
        var i = 0;
        foreach (UIElement c in _compact.Children)
        {
            if (c is not FrameworkElement el || el.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true })) continue;
            var tt = new TranslateTransform(0, 7);
            el.RenderTransform = tt;
            var begin = TimeSpan.FromMilliseconds(i * 35);
            // 关键帧从 0 开始：等待错开的那几十毫秒里也保持透明，播完回到元素自己的透明度
            var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(el.Opacity, KeyTime.FromTimeSpan(begin + TimeSpan.FromMilliseconds(200))));
            var slide = new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(280)) { BeginTime = begin, EasingFunction = SoftEase };
            slide.Completed += (_, _) => { if (el.RenderTransform == tt) el.RenderTransform = null; };
            el.BeginAnimation(OpacityProperty, fade);
            tt.BeginAnimation(TranslateTransform.YProperty, slide);
            i++;
        }
    }

    /// <summary>新通知到达：左侧图标像铃铛一样摆两下（岛仔 bell_swing：sin(4πt)·√(1−t)，0.95 s）。</summary>
    private void Ring()
    {
        if (_reducedMotion) return;
        if (_compact.Children.OfType<FrameworkElement>().FirstOrDefault(c => Grid.GetColumn(c) == 0) is not { } icon) return;
        if (icon.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true } or RotateTransform)) return;
        var rt = icon.RenderTransform as RotateTransform ?? new RotateTransform();
        icon.RenderTransformOrigin = new Point(0.5, 0.25);
        icon.RenderTransform = rt;
        var a = new DoubleAnimation(0, 16, TimeSpan.FromMilliseconds(950)) { EasingFunction = new BellEase(), FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(a, Math.Min(60, PerfGuard.Hz));
        a.Completed += (_, _) => { if (icon.RenderTransform == rt) icon.RenderTransform = null; };
        rt.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    /// <summary>摇铃曲线：两次来回、振幅按 √(1−t) 衰减，结束时回到 0。</summary>
    private sealed class BellEase : EasingFunctionBase
    {
        protected override double EaseInCore(double t) => Math.Sin(t * Math.PI * 4) * Math.Sqrt(Math.Max(0, 1 - t));
        protected override Freezable CreateInstanceCore() => new BellEase();
        public BellEase() { EasingMode = EasingMode.EaseIn; }
    }
}
