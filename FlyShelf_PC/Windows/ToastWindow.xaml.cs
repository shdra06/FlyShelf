using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AdvanceClip.Windows
{
    public partial class ToastWindow : Window
    {
        // ═══ Toast Stacking System ═══
        private static readonly List<ToastWindow> _activeToasts = new();
        private static readonly object _toastLock = new();
        private const int TOAST_GAP = 8; // Pixels between stacked toasts

        public ToastWindow(string message)
        {
            InitializeComponent();
            MessageText.Text = message;
            
            // Premium glassmorphic background & subtle border
            ToastBorder.Background = new SolidColorBrush(Color.FromArgb(238, 18, 18, 24)); // Very dark sleek charcoal
            ToastBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)); // Delicate frosted border
            
            string msgLower = message.ToLowerInvariant();
            Color accentColor;
            Color gradientEnd;
            Wpf.Ui.Controls.SymbolRegular symbol;

            if (msgLower.Contains("failed") || msgLower.Contains("error") || msgLower.Contains("❌") || msgLower.Contains("busy") || msgLower.Contains("timeout") || msgLower.Contains("offline") || msgLower.Contains("unreachable"))
            {
                // Error (Rose theme)
                symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
                accentColor = Color.FromRgb(244, 63, 94); // Rose #F43F5E
                gradientEnd = Color.FromRgb(190, 18, 60);  // Dark Red #BE123C
            }
            else if (msgLower.Contains("warning") || msgLower.Contains("⚠️") || msgLower.Contains("limit") || msgLower.Contains("retry"))
            {
                // Warning (Amber theme)
                symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                accentColor = Color.FromRgb(245, 158, 11); // Amber #F59E0B
                gradientEnd = Color.FromRgb(217, 119, 6);  // Orange #D97706
            }
            else if (msgLower.Contains("copy") || msgLower.Contains("copied") || msgLower.Contains("clipboard") || msgLower.Contains("📋"))
            {
                // Copy/Clipboard (Violet theme)
                symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24;
                accentColor = Color.FromRgb(167, 139, 250); // Violet #A78BFA
                gradientEnd = Color.FromRgb(192, 132, 252); // Purple #C084FC
            }
            else if (msgLower.Contains("sync") || msgLower.Contains("pairing") || msgLower.Contains("paired") || msgLower.Contains("device") || msgLower.Contains("lan") || msgLower.Contains("cloudflare"))
            {
                // Network/Sync (Sky Blue theme)
                symbol = Wpf.Ui.Controls.SymbolRegular.Router24;
                accentColor = Color.FromRgb(56, 189, 248); // Sky Blue #38BDF8
                gradientEnd = Color.FromRgb(29, 78, 216);  // Royal Blue #1D4ED8
            }
            else
            {
                // Success / Default (Premium Indigo to Violet - no green tint!)
                symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
                accentColor = Color.FromRgb(129, 140, 248); // Indigo #818CF8
                gradientEnd = Color.FromRgb(192, 132, 252); // Purple #C084FC
            }

            // Apply configuration
            ToastIcon.Symbol = symbol;
            ToastIcon.Foreground = new SolidColorBrush(accentColor);
            
            // Build vertical gradient for left accent strip
            var accentGradient = new LinearGradientBrush(accentColor, gradientEnd, new Point(0, 0), new Point(0, 1));
            accentGradient.Freeze();
            AccentStrip.Background = accentGradient;

            // Soft glow matching the notification type
            ToastShadow.Color = accentColor;
            ToastShadow.Opacity = 0.20;
            ToastShadow.BlurRadius = 20;
        }
        
        private void PositionAndShow()
        {
            var workArea = SystemParameters.WorkArea;
            double baseBottom = workArea.Bottom - 80; // Above taskbar

            lock (_toastLock)
            {
                // Calculate stacked offset: each existing toast pushes new ones up
                double stackOffset = 0;
                foreach (var existing in _activeToasts)
                {
                    stackOffset += existing.ActualHeight + TOAST_GAP;
                }

                // Force WPF to measure desired dimensions for correct placement before showing
                this.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double targetWidth = this.DesiredSize.Width > 0 ? this.DesiredSize.Width : 380;
                double targetHeight = this.DesiredSize.Height > 0 ? this.DesiredSize.Height : 78;

                this.Left = workArea.Left + (workArea.Width - targetWidth) / 2;
                this.Top = baseBottom - targetHeight - stackOffset;
                _activeToasts.Add(this);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PositionAndShow();

            // Run ultra-smooth GPU-accelerated entry animation
            var sb = new Storyboard();
            
            var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeAnim, this);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(Window.OpacityProperty));
            
            var slideAnim = new DoubleAnimation(20.0, 0.0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slideAnim, ToastBorder);
            Storyboard.SetTargetProperty(slideAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            
            sb.Children.Add(fadeAnim);
            sb.Children.Add(slideAnim);
            sb.Begin();
        }

        private async void StartDismissTimer()
        {
            await Task.Delay(2600);
            await DismissAsync();
        }

        private async Task DismissAsync()
        {
            // Exit animation: fade out and slide down slightly
            var sb = new Storyboard();
            
            var fadeAnim = new DoubleAnimation(this.Opacity, 0.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fadeAnim, this);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(Window.OpacityProperty));
            
            var slideAnim = new DoubleAnimation(ToastTranslate.Y, 15.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(slideAnim, ToastBorder);
            Storyboard.SetTargetProperty(slideAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            
            sb.Children.Add(fadeAnim);
            sb.Children.Add(slideAnim);
            
            var tcs = new TaskCompletionSource<bool>();
            sb.Completed += (s, e) => tcs.SetResult(true);
            sb.Begin();
            
            await tcs.Task;

            lock (_toastLock)
            {
                _activeToasts.Remove(this);
            }
            try { this.Close(); } catch { }
        }
        
        public static void ShowToast(string message)
        {
            Application.Current.Dispatcher.Invoke(() => 
            {
                lock (_toastLock)
                {
                    if (_activeToasts.Count >= 4)
                    {
                        try 
                        {
                            var oldest = _activeToasts[0];
                            oldest.Close();
                            _activeToasts.RemoveAt(0);
                        } 
                        catch { }
                    }
                }

                var toast = new ToastWindow(message);
                toast.Show();
                toast.StartDismissTimer();
            });
        }
    }
}
