// ---------------------------------------------------------------
// TipBadge — Lightweight contextual tip system
// Shows beautiful, animated pill-shaped hints near UI elements.
// Self-contained Window (no XAML) — follows DragPreviewWindow pattern.
// Anti-spam: each tip key is remembered per session (HashSet).
// Max 1 visible at a time — new tip dismisses existing one.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FlyShelf.Helpers;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    /// <summary>
    /// Static API for showing contextual tip badges anywhere in the app.
    /// Tips are non-intrusive, animated pill-shaped overlays that auto-dismiss.
    /// </summary>
    public static class TipBadge
    {
        // ═══ Anti-Spam State ═══
        private static readonly HashSet<string> _shownKeys = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _showCounts = new(StringComparer.OrdinalIgnoreCase);
        private static TipBadgeWindow? _activeInstance;

        /// <summary>
        /// Show a tip near an anchor element. If anchor is null, shows near bottom-center of app.
        /// </summary>
        public static void Show(string key, string message, UIElement? anchor = null, double offsetY = 0)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(message)) return;
            if (_shownKeys.Contains(key)) return;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    DismissInternal();
                    _shownKeys.Add(key);
                    IncrementCount(key);

                    double x = 0, y = 0;
                    bool positioned = false;

                    if (anchor != null)
                    {
                        try
                        {
                            // Get anchor's screen position
                            var point = anchor.PointToScreen(new Point(0, 0));
                            var size = anchor is FrameworkElement fe
                                ? new Size(fe.ActualWidth, fe.ActualHeight)
                                : new Size(0, 0);

                            // Position below the anchor, centered horizontally
                            x = point.X + size.Width / 2;
                            y = point.Y + size.Height + 8 + offsetY;
                            positioned = true;
                        }
                        catch { /* Anchor may not be connected to visual tree */ }
                    }

                    if (!positioned)
                    {
                        // Fallback: bottom-center of primary screen work area
                        var workArea = SystemParameters.WorkArea;
                        x = workArea.Left + workArea.Width / 2;
                        y = workArea.Bottom - 80;
                    }

                    var window = new TipBadgeWindow(message);
                    _activeInstance = window;
                    window.ShowAtPosition(x, y);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TIPBADGE", $"Show failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Show a tip at absolute screen coordinates.
        /// </summary>
        public static void ShowAt(string key, string message, double x, double y)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(message)) return;
            if (_shownKeys.Contains(key)) return;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    DismissInternal();
                    _shownKeys.Add(key);
                    IncrementCount(key);

                    var window = new TipBadgeWindow(message);
                    _activeInstance = window;
                    window.ShowAtPosition(x, y);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TIPBADGE", $"ShowAt failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Show a tip near an anchor element, allowing up to maxTimes shows.
        /// Useful for tips that should appear the first N times.
        /// </summary>
        public static void ShowLimited(string key, string message, int maxTimes, UIElement? anchor = null, double offsetY = 0)
        {
            if (string.IsNullOrEmpty(key)) return;
            int count = GetShowCount(key);
            if (count >= maxTimes) return;

            // Temporarily remove from shown set so Show() doesn't reject it
            _shownKeys.Remove(key);
            Show(key, message, anchor, offsetY);
        }

        /// <summary>Dismiss the currently visible tip immediately.</summary>
        public static void Dismiss()
        {
            Application.Current?.Dispatcher?.InvokeAsync(DismissInternal);
        }

        /// <summary>Check if a tip was already shown this session.</summary>
        public static bool WasShown(string key) => _shownKeys.Contains(key);

        /// <summary>Get how many times a tip key has been shown.</summary>
        public static int GetShowCount(string key)
            => _showCounts.TryGetValue(key, out int count) ? count : 0;

        private static void IncrementCount(string key)
        {
            if (_showCounts.ContainsKey(key))
                _showCounts[key]++;
            else
                _showCounts[key] = 1;
        }

        private static void DismissInternal()
        {
            if (_activeInstance != null)
            {
                try { _activeInstance.DismissNow(); } catch { }
                _activeInstance = null;
            }
        }

        /// <summary>Called by TipBadgeWindow when it self-dismisses.</summary>
        internal static void OnWindowDismissed(TipBadgeWindow window)
        {
            if (_activeInstance == window)
                _activeInstance = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TipBadgeWindow — The actual floating pill Window
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Lightweight borderless topmost window displaying a pill-shaped tip badge.
    /// Fully click-through (WS_EX_TRANSPARENT), auto-dismisses after 4 seconds.
    /// Built entirely in code (no XAML) for simplicity.
    /// </summary>
    internal sealed class TipBadgeWindow : Window
    {
        // ═══ Win32 Constants ═══
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        // ═══ Design Constants ═══
        private const double PillHeight = 28;
        private const double PillCornerRadius = 14;
        private const double FontSize = 11.5;
        private const double ShadowBlur = 8;
        private const double ShadowOpacity = 0.3;
        private const int AutoDismissMs = 4000;
        private const int EntranceDurationMs = 120;
        private const int ExitDurationMs = 100;
        private const double EntranceSlideY = 6;
        private const double ExitSlideY = 4;

        private readonly Border _pill;
        private readonly TranslateTransform _translateTransform;
        private DispatcherTimer? _dismissTimer;
        private bool _isDismissed;

        public TipBadgeWindow(string message)
        {
            // ═══ Window Configuration ═══
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;

            // ═══ Build pill UI ═══
            _translateTransform = new TranslateTransform(0, EntranceSlideY);

            var textBlock = new TextBlock
            {
                Text = message,
                FontSize = FontSize,
                Foreground = BrushHelper.Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 320
            };

            _pill = new Border
            {
                Height = PillHeight,
                CornerRadius = new CornerRadius(PillCornerRadius),
                Background = BrushHelper.Frozen(Color.FromArgb(0xE0, 0x1A, 0x1A, 0x2E)),
                BorderBrush = BrushHelper.Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(14, 0, 14, 0),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                RenderTransform = _translateTransform,
                Child = textBlock,
                Effect = new DropShadowEffect
                {
                    BlurRadius = ShadowBlur,
                    ShadowDepth = 2,
                    Opacity = ShadowOpacity,
                    Color = Colors.Black,
                    Direction = 270
                }
            };

            // Start invisible
            _pill.Opacity = 0;

            // Add margin for shadow padding
            var container = new Border
            {
                Padding = new Thickness(12),
                Child = _pill
            };

            Content = container;
        }

        /// <summary>
        /// Show the tip centered horizontally on the given screen coordinate.
        /// </summary>
        public void ShowAtPosition(double screenX, double screenY)
        {
            // Show first so we can measure
            Show();

            // Measure to get actual width
            UpdateLayout();
            double actualW = ActualWidth > 0 ? ActualWidth : 200;
            double actualH = ActualHeight > 0 ? ActualHeight : PillHeight + 24;

            // DPI-aware positioning
            var source = PresentationSource.FromVisual(this);
            double dpiX = 1, dpiY = 1;
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformFromDevice.M11;
                dpiY = source.CompositionTarget.TransformFromDevice.M22;
            }

            // Center the pill on the X coordinate, place at Y
            Left = screenX * dpiX - actualW / 2;
            Top = screenY * dpiY;

            // Clamp to work area
            var workArea = SystemParameters.WorkArea;
            if (Left < workArea.Left) Left = workArea.Left + 4;
            if (Left + actualW > workArea.Right) Left = workArea.Right - actualW - 4;
            if (Top + actualH > workArea.Bottom) Top = workArea.Bottom - actualH - 4;
            if (Top < workArea.Top) Top = workArea.Top + 4;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Make click-through, no taskbar, no focus steal
            var hwnd = new WindowInteropHelper(this).Handle;
            int extStyle = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE,
                extStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            PlayEntranceAnimation();
            StartDismissTimer();
        }

        private void PlayEntranceAnimation()
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(EntranceDurationMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            // Fade in
            _pill.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

            // Slide up from offset
            _translateTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(EntranceSlideY, 0, duration) { EasingFunction = ease });
        }

        private void PlayExitAnimation(Action onCompleted)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(ExitDurationMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

            // Fade out
            var fadeAnim = new DoubleAnimation(_pill.Opacity, 0, duration) { EasingFunction = ease };
            fadeAnim.Completed += (_, _) => onCompleted();

            _pill.BeginAnimation(OpacityProperty, fadeAnim);

            // Slide down
            _translateTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, ExitSlideY, duration) { EasingFunction = ease });
        }

        private void StartDismissTimer()
        {
            _dismissTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AutoDismissMs)
            };
            _dismissTimer.Tick += (_, _) =>
            {
                _dismissTimer?.Stop();
                DismissAnimated();
            };
            _dismissTimer.Start();
        }

        private void DismissAnimated()
        {
            if (_isDismissed) return;
            _isDismissed = true;
            _dismissTimer?.Stop();

            PlayExitAnimation(() =>
            {
                TipBadge.OnWindowDismissed(this);
                try { Close(); } catch { }
            });
        }

        /// <summary>Immediately close without exit animation.</summary>
        public void DismissNow()
        {
            if (_isDismissed) return;
            _isDismissed = true;
            _dismissTimer?.Stop();

            // Stop all animations
            _pill.BeginAnimation(OpacityProperty, null);
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);

            _pill.Opacity = 0;
            TipBadge.OnWindowDismissed(this);
            try { Close(); } catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _dismissTimer?.Stop();
            _dismissTimer = null;
            Content = null;
            base.OnClosed(e);
        }
    }
}
