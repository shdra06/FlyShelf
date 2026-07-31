using System;
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
        private bool _isExpanded;
        private bool _isAnimating;
        private const double EXPANDED_CONTENT_WIDTH = 76;
        private const double BOUNCE_BUFFER = 16;
        private System.Windows.Threading.DispatcherTimer? _autoDismissTimer;
        private Storyboard? _spinnerStoryboard;

        public static FlyShelfWidgetControl? Instance { get; private set; }
        public Action? OnSizeChangeRequested { get; set; }

        public FlyShelfWidgetControl()
        {
            InitializeComponent();
            Instance = this;
        }

        public void SetMainWindow(MainWindow window) => _mainWindow = window;

        public (double Width, double Height) CalculateSize(double dpiScale)
        {
            return _isExpanded
                ? (72 + EXPANDED_CONTENT_WIDTH + BOUNCE_BUFFER, 36)
                : (72, 36);
        }

        // ═══ PUBLIC API ═══

        /// <summary>
        /// Shows [SourceIcon] → [TargetIcon] + spinner on the widget.
        /// Uses vector DrawingImage icons from FileTypeIcons.xaml.
        /// </summary>
        public void ShowConversionNotification(string sourceExt, string targetExt)
        {
            if (!Dispatcher.CheckAccess())
            { Dispatcher.InvokeAsync(() => ShowConversionNotification(sourceExt, targetExt)); return; }

            try
            {
                SourceIcon.Source = GetIconForFormat(sourceExt);
                TargetIcon.Source = GetIconForFormat(targetExt);

                SpinnerTrack.Visibility = Visibility.Visible;
                SpinnerArc.Visibility = Visibility.Visible;
                CheckmarkPath.Visibility = Visibility.Collapsed;
                ErrorPath.Visibility = Visibility.Collapsed;
                StartSpinner();

                if (_isExpanded && !_isAnimating) return;
                if (!_isExpanded) AnimateExpand();
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("WIDGET", $"ShowConversion failed: {ex.Message}");
            }
        }

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

        public void DismissMiniNotification()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(DismissMiniNotification); return; }
            CancelAutoDismiss();
            if (_isExpanded) AnimateContract();
        }

        // Legacy compat for ToastWindow
        public void ShowMiniNotification(string text, WidgetNotifyType type = WidgetNotifyType.Progress)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => ShowMiniNotification(text, type)); return; }
            if (!_isExpanded && !_isAnimating)
            {
                SourceIcon.Source = GetIconForFormat("DOC");
                TargetIcon.Source = GetIconForFormat("PDF");
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

        public void UpdateMiniNotification(string text) { /* No-op in icon mode */ }

        // ═══ ICON RESOLUTION ═══

        /// <summary>
        /// Maps a file extension to the corresponding DrawingImage vector icon.
        /// Falls back to DocIcon for unknown formats.
        /// </summary>
        private ImageSource? GetIconForFormat(string ext)
        {
            string clean = ext.TrimStart('.').ToUpperInvariant();
            string resourceKey = clean switch
            {
                "PDF" => "PdfIcon",
                "DOC" or "DOCX" or "RTF" or "ODT" or "TXT" or "MD" or "LOG" => "DocIcon",
                "PPT" or "PPTX" or "ODP" => "PptIcon",
                "PNG" or "JPG" or "JPEG" or "BMP" or "GIF" or "WEBP" or "TIFF" or "SVG" or "ICO" => "ImageIcon",
                "CSV" or "XLS" or "XLSX" or "ODS" => "PptIcon", // Closest match — amber/chart
                _ => "DocIcon"
            };

            try
            {
                return Application.Current?.TryFindResource(resourceKey) as ImageSource;
            }
            catch
            {
                return null;
            }
        }

        // ═══ ANIMATIONS ═══

        private void AnimateExpand()
        {
            if (_isAnimating) return;
            _isAnimating = true;
            _isExpanded = true;
            OnSizeChangeRequested?.Invoke();

            var sb = new Storyboard();

            var widthAnim = new DoubleAnimation(0, EXPANDED_CONTENT_WIDTH, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 } };
            Storyboard.SetTarget(widthAnim, StatusPanel);
            Storyboard.SetTargetProperty(widthAnim, new PropertyPath(WidthProperty));
            sb.Children.Add(widthAnim);

            var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            { BeginTime = TimeSpan.FromMilliseconds(70), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fadeAnim, StatusContent);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(OpacityProperty));
            sb.Children.Add(fadeAnim);

            var bounceX = new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(320))
            { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } };
            Storyboard.SetTarget(bounceX, StatusContent);
            Storyboard.SetTargetProperty(bounceX, new PropertyPath("RenderTransform.ScaleX"));
            sb.Children.Add(bounceX);

            var bounceY = new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(320))
            { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } };
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

            var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(fadeAnim, StatusContent);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(OpacityProperty));
            sb.Children.Add(fadeAnim);

            double curWidth = StatusPanel.Width;
            if (double.IsNaN(curWidth) || curWidth <= 0) curWidth = EXPANDED_CONTENT_WIDTH;
            var widthAnim = new DoubleAnimation(curWidth, 0, TimeSpan.FromMilliseconds(200))
            { BeginTime = TimeSpan.FromMilliseconds(40), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
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

        private void StartSpinner()
        {
            StopSpinner();
            _spinnerStoryboard = new Storyboard();
            var r = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(r, SpinnerArc);
            Storyboard.SetTargetProperty(r, new PropertyPath("RenderTransform.Angle"));
            _spinnerStoryboard.Children.Add(r);
            _spinnerStoryboard.Begin();
        }

        private void StopSpinner()
        { try { _spinnerStoryboard?.Stop(); _spinnerStoryboard = null; } catch { } }

        private void ScheduleAutoDismiss(int ms)
        {
            CancelAutoDismiss();
            _autoDismissTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _autoDismissTimer.Tick += (s, e) => { _autoDismissTimer?.Stop(); AnimateContract(); };
            _autoDismissTimer.Start();
        }

        private void CancelAutoDismiss()
        { try { _autoDismissTimer?.Stop(); _autoDismissTimer = null; } catch { } }

        // ═══ CLICK HANDLER ═══

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
                    { logicalX = pt.X / scaleX; logicalY = pt.Y / scaleY; }
                }
                catch { }
            }
            else
            {
                try
                { var point = PointToScreen(e.GetPosition(this)); logicalX = point.X; logicalY = point.Y; }
                catch
                { logicalX = System.Windows.SystemParameters.PrimaryScreenWidth / 2; logicalY = System.Windows.SystemParameters.PrimaryScreenHeight / 2; }
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
