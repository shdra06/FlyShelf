using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlyShelf.Classes
{
    /// <summary>
    /// FlyShelf Motion System - duration and easing tokens shared by all code-driven
    /// animations. XAML counterpart: Resources/Styles/MotionSystem.xaml (keep in sync).
    /// </summary>
    internal static class Motion
    {
        // Duration tokens (milliseconds)
        public const double Instant  = 80;   // Press feedback, immediate reactions
        public const double Fast     = 120;  // Hover states, small fades
        public const double Normal   = 180;  // Standard UI transitions
        public const double Entrance = 220;  // Element entrances (fade + scale)
        public const double Slow     = 300;  // Large surfaces, emphasis moments

        // Stagger tokens for list entrances
        public const double StaggerStepMs = 30;
        public const int MaxStaggerItems  = 8;

        // Easing tokens (frozen, shared across threads)
        public static readonly IEasingFunction EaseOut   = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction EaseIn    = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
        public static readonly IEasingFunction EaseInOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
        public static readonly IEasingFunction Spring    = Frozen(new BackEase  { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });

        private static IEasingFunction Frozen(EasingFunctionBase easing)
        {
            easing.Freeze();
            return easing;
        }

        /// <summary>Per-item entrance delay for staggered list animations (capped at MaxStaggerItems).</summary>
        public static TimeSpan Stagger(int index)
            => TimeSpan.FromMilliseconds(Math.Min(Math.Max(index, 0), MaxStaggerItems) * StaggerStepMs);
    }

    /// <summary>
    /// Shared animation factory built on the Motion token system -
    /// eliminates duplicated animation boilerplate across call sites.
    /// </summary>
    internal static class AnimationHelper
    {
        // ──────────────────────────────────────────────────────────────────
        // [FIX ANIM-11]: Cached frozen animations — eliminates per-call allocations
        //
        // WPF allows frozen (immutable) animations to be reused across BeginAnimation
        // calls without cloning.  We cache the DEFAULT-parameter versions of PopIn
        // and PopOut scale animations.  Animations that need Completed event handlers
        // (PopOut fade, PressPulse down) CANNOT be frozen because you can't subscribe
        // to events on a frozen Freezable — those remain per-call allocations.
        // ──────────────────────────────────────────────────────────────────

        // PopIn defaults: fromScale=0.96 → 1, duration=Entrance, no delay
        private static readonly DoubleAnimation s_popInScale = FrozenAnim(0.96, 1, Motion.Entrance, Motion.Spring);
        private static readonly DoubleAnimation s_popInOpacity = FrozenAnim(0, 1, Motion.Entrance, Motion.EaseOut);

        // PopOut defaults: toScale=0.96, duration=Fast (scale only — fade needs Completed handler)
        private static readonly DoubleAnimation s_popOutScale = FrozenAnim(1, 0.96, Motion.Fast, Motion.EaseIn);

        // PressPulse spring-back: always 1.0 target, Normal duration
        private static readonly DoubleAnimation s_pressPulseBack = FrozenAnim(1, Motion.Normal, Motion.Spring);

        private static DoubleAnimation FrozenAnim(double from, double to, double durationMs, IEasingFunction easing)
        {
            var a = new DoubleAnimation(from, to, Dur(durationMs)) { EasingFunction = easing };
            a.Freeze();
            return a;
        }

        private static DoubleAnimation FrozenAnim(double to, double durationMs, IEasingFunction easing)
        {
            var a = new DoubleAnimation(to, Dur(durationMs)) { EasingFunction = easing };
            a.Freeze();
            return a;
        }

        // Primitive factories (back-compatible signatures)

        public static DoubleAnimation FadeIn(double durationMs = Motion.Fast)
            => new(0, 1, Dur(durationMs)) { EasingFunction = Motion.EaseOut };

        public static DoubleAnimation FadeOut(double durationMs = Motion.Fast)
            => new(1, 0, Dur(durationMs)) { EasingFunction = Motion.EaseOut };

        public static DoubleAnimation SlideIn(double fromY = -8, double durationMs = Motion.Normal)
            => new(fromY, 0, Dur(durationMs)) { EasingFunction = Motion.EaseOut };

        public static DoubleAnimation SlideOut(double toY = -8, double durationMs = Motion.Fast)
            => new(0, toY, Dur(durationMs)) { EasingFunction = Motion.EaseIn };

        public static DoubleAnimation Fade(double from, double to, double durationMs = Motion.Fast, IEasingFunction? easing = null)
            => new(from, to, Dur(durationMs)) { EasingFunction = easing ?? Motion.EaseOut };

        // Composite helpers

        /// <summary>
        /// Entrance: fades the element in while springing it from <paramref name="fromScale"/>
        /// to full size. Pass <see cref="Motion.Stagger"/> as <paramref name="delay"/> for list items.
        /// </summary>
        public static void PopIn(FrameworkElement element, double fromScale = 0.96,
                                 double durationMs = Motion.Entrance, TimeSpan? delay = null)
        {
            if (element is null) return;
            var scale = EnsureScaleTransform(element);
            var begin = delay ?? TimeSpan.Zero;

            // [FIX ANIM-11]: Use cached frozen animations when called with default parameters
            // (no delay, default scale & duration). Non-default calls still allocate.
            bool useCache = fromScale == 0.96 && durationMs == Motion.Entrance && begin == TimeSpan.Zero;

            var sx   = useCache ? s_popInScale   : new DoubleAnimation(fromScale, 1, Dur(durationMs)) { EasingFunction = Motion.Spring, BeginTime = begin };
            var sy   = useCache ? s_popInScale   : new DoubleAnimation(fromScale, 1, Dur(durationMs)) { EasingFunction = Motion.Spring, BeginTime = begin };
            var fade = useCache ? s_popInOpacity  : new DoubleAnimation(0, 1, Dur(durationMs)) { EasingFunction = Motion.EaseOut, BeginTime = begin };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>
        /// Exit: fades the element out while shrinking it, then invokes
        /// <paramref name="onCompleted"/> (e.g. to collapse or remove it from the tree).
        /// </summary>
        public static void PopOut(FrameworkElement element, Action? onCompleted = null,
                                  double toScale = 0.96, double durationMs = Motion.Fast)
        {
            if (element is null) { onCompleted?.Invoke(); return; }
            var scale = EnsureScaleTransform(element);

            // [FIX ANIM-11]: Use cached frozen scale animations when default params.
            // The fade animation CANNOT be cached when onCompleted != null because
            // Completed event handlers can't be attached to frozen Freezable objects.
            bool useCacheScale = toScale == 0.96 && durationMs == Motion.Fast;

            var sx = useCacheScale ? s_popOutScale : new DoubleAnimation(toScale, Dur(durationMs)) { EasingFunction = Motion.EaseIn };
            var sy = useCacheScale ? s_popOutScale : new DoubleAnimation(toScale, Dur(durationMs)) { EasingFunction = Motion.EaseIn };
            var fade = new DoubleAnimation(0, Dur(durationMs)) { EasingFunction = Motion.EaseIn };
            if (onCompleted != null) fade.Completed += (_, _) => onCompleted();

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>
        /// Tactile press feedback for elements without an animated template:
        /// quick scale down, then springs back to full size.
        /// </summary>
        public static void PressPulse(FrameworkElement element, double pressScale = 0.96)
        {
            if (element is null) return;
            var scale = EnsureScaleTransform(element);

            // [FIX ANIM-11]: The 'down' animation needs a Completed handler to chain the
            // spring-back, so it can't be frozen/cached. The spring-back IS cached though.
            var down = new DoubleAnimation(pressScale, Dur(Motion.Instant)) { EasingFunction = Motion.EaseOut };
            down.Completed += (_, _) =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, s_pressPulseBack);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, s_pressPulseBack);
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, down);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, down);
        }

        /// <summary>Animates the element's opacity to a target value.</summary>
        public static void FadeTo(UIElement element, double toOpacity, double durationMs = Motion.Fast)
        {
            element?.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(toOpacity, Dur(durationMs)) { EasingFunction = Motion.EaseOut });
        }

        // Internals

        private static Duration Dur(double ms) => new(TimeSpan.FromMilliseconds(ms));

        private static ScaleTransform EnsureScaleTransform(FrameworkElement element)
        {
            if (element.RenderTransform is ScaleTransform st && !st.IsFrozen) return st;
            var scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            return scale;
        }
    }
}
