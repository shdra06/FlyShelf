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
            
            // Premium contextual dynamic coloring & branding based on status type
            string msgLower = message.ToLowerInvariant();
            if (msgLower.Contains("failed") || msgLower.Contains("error") || msgLower.Contains("❌") || msgLower.Contains("busy") || msgLower.Contains("timeout") || msgLower.Contains("offline") || msgLower.Contains("unreachable"))
            {
                // Error (Red theme)
                ToastIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
                ToastIcon.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // #EF4444
                ToastBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 239, 68, 68));
                ToastBorder.Background = new SolidColorBrush(Color.FromArgb(240, 26, 12, 12));
                ToastShadow.Color = Color.FromRgb(239, 68, 68);
                ToastShadow.Opacity = 0.15;
            }
            else if (msgLower.Contains("warning") || msgLower.Contains("⚠️") || msgLower.Contains("limit") || msgLower.Contains("retry"))
            {
                // Warning (Amber theme)
                ToastIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                ToastIcon.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // #F59E0B
                ToastBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 245, 158, 11));
                ToastBorder.Background = new SolidColorBrush(Color.FromArgb(240, 26, 20, 10));
                ToastShadow.Color = Color.FromRgb(245, 158, 11);
                ToastShadow.Opacity = 0.15;
            }
            else if (msgLower.Contains("copy") || msgLower.Contains("copied") || msgLower.Contains("clipboard") || msgLower.Contains("📋"))
            {
                // Copy/Clipboard (Violet theme)
                ToastIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24;
                ToastIcon.Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)); // #A78BFA
                ToastBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 167, 139, 250));
                ToastBorder.Background = new SolidColorBrush(Color.FromArgb(240, 18, 14, 28));
                ToastShadow.Color = Color.FromRgb(167, 139, 250);
                ToastShadow.Opacity = 0.15;
            }
            else if (msgLower.Contains("sync") || msgLower.Contains("pairing") || msgLower.Contains("paired") || msgLower.Contains("device") || msgLower.Contains("lan") || msgLower.Contains("cloudflare"))
            {
                // Network/Sync (Blue theme)
                ToastIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Router24;
                ToastIcon.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // #3B82F6
                ToastBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246));
                ToastBorder.Background = new SolidColorBrush(Color.FromArgb(240, 12, 18, 28));
                ToastShadow.Color = Color.FromRgb(59, 130, 246);
                ToastShadow.Opacity = 0.15;
            }
            else
            {
                // Success / Default (Emerald theme)
                ToastIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark24;
                ToastIcon.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // #10B981
                ToastBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 16, 185, 129));
                ToastBorder.Background = new SolidColorBrush(Color.FromArgb(240, 12, 24, 18));
                ToastShadow.Color = Color.FromRgb(16, 185, 129);
                ToastShadow.Opacity = 0.15;
            }
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
