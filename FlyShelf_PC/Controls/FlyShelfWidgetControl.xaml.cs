using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlyShelf.Controls
{
    public enum WidgetNotifyType { Progress, Success, Error, Info }

    public partial class FlyShelfWidgetControl : UserControl
    {
        private MainWindow? _mainWindow;

        // ═══ Mini-Notification State ═══
        private bool _isExpanded;
        private bool _isAnimating;
        private const double EXPANDED_CONTENT_WIDTH = 88;
        private const double BOUNCE_BUFFER = 18;
        private System.Windows.Threading.DispatcherTimer? _autoDismissTimer;
        private Storyboard? _spinnerStoryboard;

        /// <summary>Global singleton for cross-component access.</summary>
        public static FlyShelfWidgetControl? Instance { get; private set; }

        /// <summary>Callback to resize native TaskbarWindow. Set by TaskbarWindow.</summary>
        public Action? OnSizeChangeRequested { get; set; }

        // ═══ Format Pill Styles ═══
        private static readonly Dictionary<string, (string label, Color bg)> _formatStyles = new(StringComparer.OrdinalIgnoreCase)
        {
            { "PDF",  ("PDF",  Color.FromArgb(0xCC, 0xDC, 0x26, 0x26)) }, // Red
            { "DOC",  ("DOC",  Color.FromArgb(0xCC, 0x25, 0x63, 0xEB)) }, // Blue
            { "DOCX", ("DOC",  Color.FromArgb(0xCC, 0x25, 0x63, 0xEB)) },
            { "RTF",  ("RTF",  Color.FromArgb(0xCC, 0x25, 0x63, 0xEB)) },
            { "MD",   ("MD",   Color.FromArgb(0xCC, 0x6B, 0x72, 0x80)) }, // Gray
            { "TXT",  ("TXT",  Color.FromArgb(0xCC, 0x6B, 0x72, 0x80)) },
            { "LOG",  ("LOG",  Color.FromArgb(0xCC, 0x6B, 0x72, 0x80)) },
            { "PNG",  ("PNG",  Color.FromArgb(0xCC, 0x05, 0x96, 0x69)) }, // Green
            { "JPG",  ("JPG",  Color.FromArgb(0xCC, 0x05, 0x96, 0x69)) },
            { "JPEG", ("JPG",  Color.FromArgb(0xCC, 0x05, 0x96, 0x69)) },
            { "WEBP", ("IMG",  Color.FromArgb(0xCC, 0x05, 0x96, 0x69)) },
            { "BMP",  ("BMP",  Color.FromArgb(0xCC, 0x05, 0x96, 0x69)) },
            { "GIF",  ("GIF",  Color.FromArgb(0xCC, 0x05, 0x96, 0x69)) },
            { "CSV",  ("CSV",  Color.FromArgb(0xCC, 0xD9, 0x77, 0x06)) }, // Amber
            { "XLSX", ("XLS",  Color.FromArgb(0xCC, 0x05, 0x96, 0x69)) },
            { "XLS",  ("XLS",  Color.FromArgb(0xCC, 0x05, 0x96, 0x69)) },
        };

        public FlyShelfWidgetControl()
        {
            InitializeComponent();
            Instance = this;
        }

        public void SetMainWindow(MainWindow window) => _mainWindow = window;

        public (double Width, double Height) CalculateSize(double dpiScale)
        {
            return _isExpanded
                ? (68 + EXPANDED_CONTENT_WIDTH + BOUNCE_BUFFER, 36)
                : (68, 36);
        }

        // ═══════════════════════════════════════════════════════════════
        //  PUBLIC API
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows an icon-based conversion notification: [SRC] → [TGT] + spinner.
        /// Thread-safe. sourceExt/targetExt are file extensions without dot (e.g. "DOC", "PDF").
        /// </summary>
        public void ShowConversionNotification(string sourceExt, string targetExt)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => ShowConversionNotification(sourceExt, targetExt));
                return;
            }

            try
            {
                // Configure format pills
                ConfigurePill(SourcePill, SourceLabel, sourceExt);
                ConfigurePill(TargetPill, TargetLabel, targetExt);

                // Show spinner, hide completion indicators
                SpinnerTrack.Visibility = Visibility.Visible;
                SpinnerArc.Visibility = Visibility.Visible;
                CheckmarkPath.Visibility = Visibility.Collapsed;
                ErrorPath.Visibility = Visibility.Collapsed;
                StartSpinner();

                if (_isExpanded && !_isAnimating)
                    return; // Already showing — just update pills

                if (!_isExpanded)
                    AnimateExpand();
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("WIDGET", $"ShowConversion failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Completes the notification — crossfades spinner to checkmark, auto-contracts after 1.8s.
        /// </summary>
        public void CompleteMiniNotification(string? _ = null)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => CompleteMiniNotification(_)); return; }
            if (!_isExpanded) return;

            try
            {
                StopSpinner();
                SpinnerTrack.Visibility = Visibility.Collapsed;
                SpinnerArc.Visibility = Visibility.Collapsed;
                CheckmarkPath.Visibility = Visibility.Visible;
                ScheduleAutoDismiss(1800);
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("WIDGET", $"Complete failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows error state — crossfades to red X, auto-contracts after 3s.
        /// </summary>
        public void ErrorMiniNotification(string? _ = null)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => ErrorMiniNotification(_)); return; }
            if (!_isExpanded) return;

            try
            {
                StopSpinner();
                SpinnerTrack.Visibility = Visibility.Collapsed;
                SpinnerArc.Visibility = Visibility.Collapsed;
                CheckmarkPath.Visibility = Visibility.Collapsed;
                ErrorPath.Visibility = Visibility.Visible;
                ScheduleAutoDismiss(3000);
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("WIDGET", $"Error failed: {ex.Message}");
            }
        }

        /// <summary>Immediately contracts and hides the notification.</summary>
        public void DismissMiniNotification()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(DismissMiniNotification); return; }
            CancelAutoDismiss();
            if (_isExpanded) AnimateContract();
        }

        // Keep legacy API for ToastWindow compatibility
        public void ShowMiniNotification(string text, WidgetNotifyType type = WidgetNotifyType.Progress)
        {
            // For backward compat — try to parse format from text, else show generic
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => ShowMiniNotification(text, type)); return; }
            // Generic: just show spinner with current pills
            if (!_isExpanded && !_isAnimating)
            {
                // Default to generic conversion icon
                ConfigurePill(SourcePill, SourceLabel, "DOC");
                ConfigurePill(TargetPill, TargetLabel, "PDF");
                SpinnerTrack.Visibility = Visibility.Visible;
                SpinnerArc.Visibility = Visibility.Visible;
                CheckmarkPath.Visibility = Visibility.Collapsed;
                ErrorPath.Visibility = Visibility.Collapsed;
                StartSpinner();
                AnimateExpand();
            }
            if (type != WidgetNotifyType.Progress)
                ScheduleAutoDismiss(type == WidgetNotifyType.Error ? 3000 : 2000);
        }

        public void UpdateMiniNotification(string text) { /* No-op: icon mode has no text */ }

        // ═══════════════════════════════════════════════════════════════
        //  FORMAT PILL CONFIGURATION
        // ═══════════════════════════════════════════════════════════════

        private static void ConfigurePill(Border pill, TextBlock label, string ext)
        {
            string cleanExt = ext.TrimStart('.').ToUpperInvariant();
            if (_formatStyles.TryGetValue(cleanExt, out var style))
            {
                label.Text = style.label;
                pill.Background = new SolidColorBrush(style.bg);
            }
            else
            {
                label.Text = cleanExt.Length > 3 ? cleanExt[..3] : cleanExt;
                pill.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x6B, 0x72, 0x80));
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  ANIMATIONS
        // ═══════════════════════════════════════════════════════════════

        private void AnimateExpand()
        {
            if (_isAnimating) return;
            _isAnimating = true;
            _isExpanded = true;

            // Resize native window first (transparent extra space is invisible)
            OnSizeChangeRequested?.Invoke();

            var sb = new Storyboard();

            // Width reveal
            var widthAnim = new DoubleAnimation(0, EXPANDED_CONTENT_WIDTH, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
            };
            Storyboard.SetTarget(widthAnim, StatusPanel);
            Storyboard.SetTargetProperty(widthAnim, new PropertyPath(WidthProperty));
            sb.Children.Add(widthAnim);

            // Content fade-in (staggered)
            var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                BeginTime = TimeSpan.FromMilliseconds(70),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeAnim, StatusContent);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(OpacityProperty));
            sb.Children.Add(fadeAnim);

            // Content bounce (spring settle)
            var bounceX = new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
            };
            Storyboard.SetTarget(bounceX, StatusContent);
            Storyboard.SetTargetProperty(bounceX, new PropertyPath("RenderTransform.ScaleX"));
            sb.Children.Add(bounceX);

            var bounceY = new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
            };
            Storyboard.SetTarget(bounceY, StatusContent);
            Storyboard.SetTargetProperty(bounceY, new PropertyPath("RenderTransform.ScaleY"));
            sb.Children.Add(bounceY);

            sb.Completed += (s, e) => _isAnimating = false;
            sb.Begin();
        }

        private void AnimateContract()
        {
            if (_isAnimating) return;
            _isAnimating = true;

            var sb = new Storyboard();

            // Fade out
            var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fadeAnim, StatusContent);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(OpacityProperty));
            sb.Children.Add(fadeAnim);

            // Width collapse
            double curWidth = StatusPanel.Width;
            if (double.IsNaN(curWidth) || curWidth <= 0) curWidth = EXPANDED_CONTENT_WIDTH;
            var widthAnim = new DoubleAnimation(curWidth, 0, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(40),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(widthAnim, StatusPanel);
            Storyboard.SetTargetProperty(widthAnim, new PropertyPath(WidthProperty));
            sb.Children.Add(widthAnim);

            sb.Completed += (s, e) =>
            {
                _isExpanded = false;
                _isAnimating = false;
                StopSpinner();
                SpinnerTrack.Visibility = Visibility.Collapsed;
                SpinnerArc.Visibility = Visibility.Collapsed;
                CheckmarkPath.Visibility = Visibility.Collapsed;
                ErrorPath.Visibility = Visibility.Collapsed;
                OnSizeChangeRequested?.Invoke();
            };
            sb.Begin();
        }

        // ═══ SPINNER ═══

        private void StartSpinner()
        {
            StopSpinner();
            _spinnerStoryboard = new Storyboard();
            var rotateAnim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(rotateAnim, SpinnerArc);
            Storyboard.SetTargetProperty(rotateAnim, new PropertyPath("RenderTransform.Angle"));
            _spinnerStoryboard.Children.Add(rotateAnim);
            _spinnerStoryboard.Begin();
        }

        private void StopSpinner()
        {
            try { _spinnerStoryboard?.Stop(); _spinnerStoryboard = null; } catch { }
        }

        // ═══ AUTO-DISMISS ═══

        private void ScheduleAutoDismiss(int ms)
        {
            CancelAutoDismiss();
            _autoDismissTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _autoDismissTimer.Tick += (s, e) => { _autoDismissTimer?.Stop(); AnimateContract(); };
            _autoDismissTimer.Start();
        }

        private void CancelAutoDismiss()
        {
            try { _autoDismissTimer?.Stop(); _autoDismissTimer = null; } catch { }
        }

        // ═══ CLICK HANDLER (preserved) ═══

        private void WidgetGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            double logicalX = 0, logicalY = 0;

            if (FlyShelf.Classes.NativeMethods.GetCursorPos(out var pt))
            {
                logicalX = pt.X;
                logicalY = pt.Y;
                try
                {
                    var monitor = FlyShelf.Classes.Utils.MonitorUtil.GetMonitorWithCursor();
                    double scaleX = monitor.dpiX / 96.0;
                    double scaleY = monitor.dpiY / 96.0;
                    if (scaleX > 0 && scaleY > 0)
                    {
                        logicalX = pt.X / scaleX;
                        logicalY = pt.Y / scaleY;
                    }
                }
                catch { }
            }
            else
            {
                try
                {
                    var point = PointToScreen(e.GetPosition(this));
                    logicalX = point.X;
                    logicalY = point.Y;
                }
                catch
                {
                    logicalX = System.Windows.SystemParameters.PrimaryScreenWidth / 2;
                    logicalY = System.Windows.SystemParameters.PrimaryScreenHeight / 2;
                }
            }

            FlyShelf.Classes.Logger.LogAction("TELEMETRY", $"Widget left click received, screen point=({logicalX}, {logicalY})");

            if (_mainWindow != null)
            {
                bool isMode1 = _mainWindow.DataContext is FlyShelf.ViewModels.FlyShelfViewModel vm && vm.CurrentMode == 1;
                if (_mainWindow.IsSummoned && isMode1)
                    _mainWindow.AnimateAndHide();
                else
                    _mainWindow.ShowNearPosition(logicalX, logicalY, 1, false, false);
            }
        }
    }
}
