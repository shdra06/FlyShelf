using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Shared animation factory — eliminates duplicated animation boilerplate across 14+ call sites.
    /// </summary>
    internal static class AnimationHelper
    {
        private static readonly QuadraticEase _easeOut = new() { EasingMode = EasingMode.EaseOut };
        private static readonly QuadraticEase _easeIn = new() { EasingMode = EasingMode.EaseIn };

        public static DoubleAnimation FadeIn(double durationMs = 150)
            => new(0, 1, new Duration(TimeSpan.FromMilliseconds(durationMs))) { EasingFunction = _easeOut };

        public static DoubleAnimation FadeOut(double durationMs = 150)
            => new(1, 0, new Duration(TimeSpan.FromMilliseconds(durationMs))) { EasingFunction = _easeOut };

        public static DoubleAnimation SlideIn(double fromY = -8, double durationMs = 150)
            => new(fromY, 0, new Duration(TimeSpan.FromMilliseconds(durationMs))) { EasingFunction = _easeOut };

        public static DoubleAnimation SlideOut(double toY = -8, double durationMs = 120)
            => new(0, toY, new Duration(TimeSpan.FromMilliseconds(durationMs))) { EasingFunction = _easeIn };
    }
}
