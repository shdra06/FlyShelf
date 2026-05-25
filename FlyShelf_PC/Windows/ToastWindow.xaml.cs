using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlyShelf.Windows
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
            string smartMessage = MakeMessageSmart(message);
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

                var toast = new ToastWindow(smartMessage);
                toast.Show();
                toast.StartDismissTimer();
            });
        }

        public static string FormatSize(long bytes) => FlyShelf.Classes.FormatHelper.FormatSize(bytes);

        public static string GetFileTypeFriendly(string fileName) => FlyShelf.Classes.FormatHelper.GetFileTypeFriendly(fileName);

        public static string MakeMessageSmart(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            // 1. Detect any full filenames and swap them with friendly type names
            string[] extensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".zip", ".rar", ".7z", ".mp3", ".wav", ".m4a", ".mp4", ".mkv", ".apk" };
            foreach (var ext in extensions)
            {
                int extIdx = message.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (extIdx >= 0)
                {
                    // Scan backward for start of the filename
                    int startIdx = extIdx;
                    while (startIdx > 0 && message[startIdx - 1] != ' ' && message[startIdx - 1] != ':' && message[startIdx - 1] != '\\' && message[startIdx - 1] != '/' && message[startIdx - 1] != '"' && message[startIdx - 1] != '\'')
                    {
                        startIdx--;
                    }
                    string fullFileName = message.Substring(startIdx, extIdx + ext.Length - startIdx);
                    
                    // Avoid replacing already friendly type words or short descriptors
                    if (fullFileName.Length > ext.Length)
                    {
                        string friendlyType = GetFileTypeFriendly(fullFileName);
                        message = message.Replace(fullFileName, friendlyType);
                    }
                }
            }

            // 2. Condense common verbose phrases to make notifications extremely clean & compact
            // - Redundant copy-to-clipboard mentions
            message = message.Replace("copied to clipboard", "Copied");
            message = message.Replace("copied to Clipboard", "Copied");
            message = message.Replace("copied to your clipboard", "Copied");
            
            // - Store version terminal / compilation notices
            message = message.Replace("Terminal execution is not available in the Store version.", "Terminal unavailable in Store version");
            message = message.Replace("Elevated terminal is not available in the Store version.", "Elevated terminal unavailable");
            message = message.Replace("Code compilation is not available in the Store version.", "Compilation unavailable");
            
            // - File transfers
            message = message.Replace("paired successfully!", "paired!");
            message = message.Replace("paired successfully", "paired");
            message = message.Replace("joined your sync group!", "joined group!");
            
            message = message.Replace("Assembling ", "Receiving ");
            message = message.Replace(" (via WS)... 📥", "... 📥");
            
            message = message.Replace("Text from ", "Text received: ");
            message = message.Replace(" via WebSocket!", "!");
            message = message.Replace(" via LAN!", "!");
            message = message.Replace(" via Cloudflare!", "!");

            // Clean up double-spaces
            while (message.Contains("  "))
            {
                message = message.Replace("  ", " ");
            }

            // Remove trailing spaces / colons/ periods where unnecessary
            message = message.Trim();
            if (message.EndsWith("! !")) message = message.Substring(0, message.Length - 2) + "!";

            return message;
        }
    }
}
