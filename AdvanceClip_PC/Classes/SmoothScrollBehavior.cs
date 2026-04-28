using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Provides buttery-smooth animated scrolling for any ScrollViewer.
    /// Attach via SmoothScrollBehavior.IsEnabled="True" on any ScrollViewer,
    /// or call ApplyToAllScrollViewers() to blanket the entire visual tree.
    /// </summary>
    public static class SmoothScrollBehavior
    {
        // ─── Tuning ───
        // How many pixels per notch of the mouse wheel (default WPF is 48*3 = 144, which is way too fast)
        private const double PIXELS_PER_NOTCH = 60;
        // Duration of each scroll animation segment
        private static readonly Duration SCROLL_DURATION = new Duration(TimeSpan.FromMilliseconds(300));
        // Easing curve — cubic ease out feels natural
        private static readonly IEasingFunction EASE = new CubicEase { EasingMode = EasingMode.EaseOut };

        // ─── Attached Property ───
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        // We store the "target offset" so rapid wheel spins accumulate correctly
        private static readonly DependencyProperty TargetVerticalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "TargetVerticalOffset",
                typeof(double),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(0.0));

        private static readonly DependencyProperty TargetHorizontalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "TargetHorizontalOffset",
                typeof(double),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(0.0));

        // Animatable proxy — WPF can't directly animate ScrollViewer.VerticalOffset
        // so we use an attached "AnimatableVerticalOffset" property that we push to ScrollToVerticalOffset()
        private static readonly DependencyProperty AnimatableVerticalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "AnimatableVerticalOffset",
                typeof(double),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(0.0, OnAnimatableVerticalOffsetChanged));

        private static readonly DependencyProperty AnimatableHorizontalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "AnimatableHorizontalOffset",
                typeof(double),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(0.0, OnAnimatableHorizontalOffsetChanged));

        private static void OnAnimatableVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
                sv.ScrollToVerticalOffset((double)e.NewValue);
        }

        private static void OnAnimatableHorizontalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
                sv.ScrollToHorizontalOffset((double)e.NewValue);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
            {
                if ((bool)e.NewValue)
                {
                    sv.PreviewMouseWheel += OnPreviewMouseWheel;
                    sv.ScrollChanged += OnScrollChanged;
                }
                else
                {
                    sv.PreviewMouseWheel -= OnPreviewMouseWheel;
                    sv.ScrollChanged -= OnScrollChanged;
                }
            }
        }

        // Sync target offset when user scrolls by other means (drag scrollbar, touch)
        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is ScrollViewer sv && e.VerticalChange != 0)
            {
                // Only sync if NOT in the middle of an animation
                if (!sv.Tag?.ToString()?.Contains("_smoothAnimating") ?? true)
                {
                    sv.SetValue(TargetVerticalOffsetProperty, sv.VerticalOffset);
                    sv.SetValue(TargetHorizontalOffsetProperty, sv.HorizontalOffset);
                }
            }
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            e.Handled = true;

            // Delta is ±120 per notch (standard Windows mouse)
            double notches = e.Delta / 120.0;
            double pixelDelta = notches * PIXELS_PER_NOTCH;

            // Vertical scrolling (default)
            if (sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && sv.ScrollableHeight > 0)
            {
                double currentTarget = (double)sv.GetValue(TargetVerticalOffsetProperty);
                // On first interaction, sync to actual position
                if (Math.Abs(currentTarget - sv.VerticalOffset) > sv.ViewportHeight)
                    currentTarget = sv.VerticalOffset;

                double newTarget = currentTarget - pixelDelta;
                newTarget = Math.Max(0, Math.Min(newTarget, sv.ScrollableHeight));
                sv.SetValue(TargetVerticalOffsetProperty, newTarget);

                var animation = new DoubleAnimation
                {
                    From = sv.VerticalOffset,
                    To = newTarget,
                    Duration = SCROLL_DURATION,
                    EasingFunction = EASE
                };
                animation.Freeze();

                sv.BeginAnimation(AnimatableVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
            // Horizontal scrolling (for horizontal-only scrollviewers like emoji picker)
            else if (sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && sv.ScrollableWidth > 0)
            {
                double currentTarget = (double)sv.GetValue(TargetHorizontalOffsetProperty);
                if (Math.Abs(currentTarget - sv.HorizontalOffset) > sv.ViewportWidth)
                    currentTarget = sv.HorizontalOffset;

                double newTarget = currentTarget - pixelDelta;
                newTarget = Math.Max(0, Math.Min(newTarget, sv.ScrollableWidth));
                sv.SetValue(TargetHorizontalOffsetProperty, newTarget);

                var animation = new DoubleAnimation
                {
                    From = sv.HorizontalOffset,
                    To = newTarget,
                    Duration = SCROLL_DURATION,
                    EasingFunction = EASE
                };
                animation.Freeze();

                sv.BeginAnimation(AnimatableHorizontalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
        }

        /// <summary>
        /// Walk the visual tree and attach smooth scrolling to every ScrollViewer found.
        /// Call this once from Window.Loaded or ContentRendered.
        /// </summary>
        public static void ApplyToAllScrollViewers(DependencyObject root)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv)
                {
                    SetIsEnabled(sv, true);
                }
                ApplyToAllScrollViewers(child);
            }
        }

        /// <summary>
        /// Attach smooth scrolling to a specific ListView/ListBox by finding its internal ScrollViewer.
        /// </summary>
        public static void ApplyToListControl(ItemsControl control)
        {
            // ItemsControl creates its ScrollViewer lazily — ensure template is applied
            control.ApplyTemplate();
            var sv = FindDescendant<ScrollViewer>(control);
            if (sv != null)
            {
                SetIsEnabled(sv, true);
            }
        }

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                var result = FindDescendant<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}
