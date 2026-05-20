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
        private const int TOAST_GAP = 6;

        public ToastWindow(string message)
        {
            InitializeComponent();
            MessageText.Text = message;
            
            string msgLower = message.ToLowerInvariant();
            Color accentColor;
            Color accentEnd;
            Wpf.Ui.Controls.SymbolRegular symbol;

            if (msgLower.Contains("failed") || msgLower.Contains("error") || msgLower.Contains("❌") || msgLower.Contains("busy") || msgLower.Contains("timeout") || msgLower.Contains("offline") || msgLower.Contains("unreachable"))
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
                accentColor = Color.FromRgb(244, 63, 94);  // Rose
                accentEnd = Color.FromRgb(190, 18, 60);
            }
            else if (msgLower.Contains("warning") || msgLower.Contains("⚠️") || msgLower.Contains("limit") || msgLower.Contains("retry"))
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                accentColor = Color.FromRgb(245, 158, 11); // Amber
                accentEnd = Color.FromRgb(217, 119, 6);
            }
            else if (msgLower.Contains("copy") || msgLower.Contains("copied") || msgLower.Contains("clipboard") || msgLower.Contains("📋"))
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24;
                accentColor = Color.FromRgb(167, 139, 250); // Violet
                accentEnd = Color.FromRgb(139, 92, 246);
            }
            else if (msgLower.Contains("sync") || msgLower.Contains("pairing") || msgLower.Contains("paired") || msgLower.Contains("device") || msgLower.Contains("lan") || msgLower.Contains("cloudflare"))
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.Router24;
                accentColor = Color.FromRgb(56, 189, 248);  // Sky
                accentEnd = Color.FromRgb(29, 78, 216);
            }
            else
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
                accentColor = Color.FromRgb(129, 140, 248); // Indigo
                accentEnd = Color.FromRgb(99, 102, 241);
            }

            // Apply
            ToastIcon.Symbol = symbol;
            ToastIcon.Foreground = new SolidColorBrush(accentColor);
            AccentGlowStart.Color = accentColor;
            AccentGlowEnd.Color = accentEnd;

            // Subtle accent-colored outer glow
            ToastShadow.Color = accentColor;
            ToastShadow.Opacity = 0.18;
            ToastShadow.BlurRadius = 14;
        }
        
        private void PositionAndShow()
        {
            var workArea = SystemParameters.WorkArea;
            double baseBottom = workArea.Bottom - 56;

            lock (_toastLock)
            {
                double stackOffset = 0;
                foreach (var existing in _activeToasts)
                {
                    stackOffset += existing.ActualHeight + TOAST_GAP;
                }

                this.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double targetWidth = this.DesiredSize.Width > 0 ? this.DesiredSize.Width : 280;
                double targetHeight = this.DesiredSize.Height > 0 ? this.DesiredSize.Height : 48;

                this.Left = workArea.Left + (workArea.Width - targetWidth) / 2;
                this.Top = baseBottom - targetHeight - stackOffset;
                _activeToasts.Add(this);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PositionAndShow();

            var sb = new Storyboard();
            
            var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeAnim, this);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(Window.OpacityProperty));
            
            var slideAnim = new DoubleAnimation(12.0, 0.0, TimeSpan.FromMilliseconds(240))
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
            await Task.Delay(2400);
            await DismissAsync();
        }

        private async Task DismissAsync()
        {
            var sb = new Storyboard();
            
            var fadeAnim = new DoubleAnimation(this.Opacity, 0.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fadeAnim, this);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(Window.OpacityProperty));
            
            var slideAnim = new DoubleAnimation(ToastTranslate.Y, 8.0, TimeSpan.FromMilliseconds(160))
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
            Application.Current.Dispatcher.InvokeAsync(() => 
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
