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
        // ═══ Single-Instance Toast Pool ═══
        // Reuses a single window to avoid expensive Window construction on every toast.
        private static ToastWindow? _pooledInstance;
        private static readonly object _poolLock = new();
        private static readonly Queue<string> _pendingMessages = new();
        private static bool _isShowing;

        // ═══ Toast Stacking System ═══
        private static readonly List<ToastWindow> _activeToasts = new();
        private static readonly object _toastLock = new();
        private const int TOAST_GAP = 6;

        // Anti-spam: track the last message to prevent identical back-to-back toasts
        private static string? _lastMessage;
        private static long _lastMessageTime;

        // Dismiss timer
        private System.Windows.Threading.DispatcherTimer? _dismissTimer;

        public ToastWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Configures the toast content and icon based on message semantics.
        /// Called each time a message is shown (no Window re-creation needed).
        /// </summary>
        private void ConfigureForMessage(string message)
        {
            MessageText.Text = message;

            string msgLower = message.ToLowerInvariant();
            Wpf.Ui.Controls.SymbolRegular symbol;
            string accentKey; // Theme token key for accent color lookup

            if (msgLower.Contains("failed") || msgLower.Contains("error") || msgLower.Contains("❌") || msgLower.Contains("busy") || msgLower.Contains("timeout") || msgLower.Contains("offline") || msgLower.Contains("unreachable"))
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
                accentKey = "DangerColor"; // Rose/Red from palette
            }
            else if (msgLower.Contains("warning") || msgLower.Contains("⚠️") || msgLower.Contains("⚠") || msgLower.Contains("limit") || msgLower.Contains("retry"))
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                accentKey = "WarningColor"; // Amber from palette
            }
            else if (msgLower.Contains("copy") || msgLower.Contains("copied") || msgLower.Contains("clipboard") || msgLower.Contains("📋"))
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24;
                accentKey = "ThemeAccentLight"; // Theme accent light
            }
            else if (msgLower.Contains("sync") || msgLower.Contains("pairing") || msgLower.Contains("paired") || msgLower.Contains("device") || msgLower.Contains("lan") || msgLower.Contains("cloudflare"))
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.Router24;
                accentKey = "InfoColor"; // Sky/info from palette
            }
            else
            {
                symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
                accentKey = "ThemeAccentLight"; // Default theme accent
            }

            // Apply icon
            ToastIcon.Symbol = symbol;

            // Resolve accent color from theme-aware resources
            Color accentColor;
            try
            {
                var brush = Application.Current?.Resources[accentKey] as SolidColorBrush;
                accentColor = brush?.Color ?? Color.FromRgb(129, 140, 248); // Fallback indigo
            }
            catch
            {
                accentColor = Color.FromRgb(129, 140, 248); // Fallback indigo
            }

            ToastIcon.Foreground = new SolidColorBrush(accentColor);

            // Subtle accent-colored outer glow
            ToastShadow.Color = accentColor;
            ToastShadow.Opacity = 0.18;
            ToastShadow.BlurRadius = 14;

            // Reset transform for fresh animation
            ToastTranslate.Y = 12;
            this.Opacity = 0;
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
                    if (existing != this)
                        stackOffset += existing.ActualHeight + TOAST_GAP;
                }

                this.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double targetWidth = this.DesiredSize.Width > 0 ? this.DesiredSize.Width : 280;
                double targetHeight = this.DesiredSize.Height > 0 ? this.DesiredSize.Height : 48;

                this.Left = workArea.Left + (workArea.Width - targetWidth) / 2;
                this.Top = baseBottom - targetHeight - stackOffset;

                if (!_activeToasts.Contains(this))
                    _activeToasts.Add(this);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Only runs on first show; subsequent shows use ShowAndAnimate
            ShowAndAnimate();
        }

        private void ShowAndAnimate()
        {
            PositionAndShow();
            RunEntranceAnimation();
            RestartDismissTimer();
        }

        private void RunEntranceAnimation()
        {
            var sb = new Storyboard();

            var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeAnim, this);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(Window.OpacityProperty));

            var slideAnim = new DoubleAnimation(12.0, 0.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slideAnim, ToastBorder);
            Storyboard.SetTargetProperty(slideAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

            sb.Children.Add(fadeAnim);
            sb.Children.Add(slideAnim);
            sb.Begin();
        }

        private void RestartDismissTimer()
        {
            _dismissTimer?.Stop();
            _dismissTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2400)
            };
            _dismissTimer.Tick += async (s, e) =>
            {
                _dismissTimer?.Stop();
                await DismissAsync();
            };
            _dismissTimer.Start();
        }

        private async Task DismissAsync()
        {
            try
            {
                var sb = new Storyboard();

                var fadeAnim = new DoubleAnimation(this.Opacity, 0.0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                Storyboard.SetTarget(fadeAnim, this);
                Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(Window.OpacityProperty));

                var slideAnim = new DoubleAnimation(ToastTranslate.Y, 8.0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                Storyboard.SetTarget(slideAnim, ToastBorder);
                Storyboard.SetTargetProperty(slideAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

                sb.Children.Add(fadeAnim);
                sb.Children.Add(slideAnim);

                var tcs = new TaskCompletionSource<bool>();
                sb.Completed += (s, e) => tcs.TrySetResult(true);
                sb.Begin();

                await tcs.Task;

                lock (_toastLock)
                {
                    _activeToasts.Remove(this);
                }

                // Hide instead of Close — keeps the window alive for reuse
                this.Hide();

                lock (_poolLock)
                {
                    _isShowing = false;

                    // If there are queued messages, show the next one immediately
                    if (_pendingMessages.Count > 0)
                    {
                        string nextMsg = _pendingMessages.Dequeue();
                        ShowNextFromPool(nextMsg);
                    }
                }
            }
            catch
            {
                // Failsafe: ensure we don't block the queue
                lock (_poolLock) { _isShowing = false; }
                lock (_toastLock) { _activeToasts.Remove(this); }
                try { this.Hide(); } catch { }
            }
        }

        /// <summary>
        /// Shows a toast by reusing the pooled instance. If the pool is busy,
        /// the message is queued and will display after the current toast dismisses.
        /// </summary>
        public static void ShowToast(string message)
        {
            string smartMessage = MakeMessageSmart(message);

            // Anti-spam: skip exact duplicate within 500ms
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastMessage == smartMessage && (now - _lastMessageTime) < 500)
                return;
            _lastMessage = smartMessage;
            _lastMessageTime = now;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                lock (_poolLock)
                {
                    if (_isShowing)
                    {
                        // Queue it — max 3 pending to prevent unbounded growth
                        if (_pendingMessages.Count < 3)
                            _pendingMessages.Enqueue(smartMessage);
                        return;
                    }

                    ShowNextFromPool(smartMessage);
                }
            });
        }

        /// <summary>
        /// Internal: configures and shows the pooled window with a new message.
        /// Must be called on the UI thread while holding _poolLock.
        /// </summary>
        private static void ShowNextFromPool(string message)
        {
            _isShowing = true;

            try
            {
                if (_pooledInstance == null)
                {
                    _pooledInstance = new ToastWindow();
                }

                _pooledInstance.ConfigureForMessage(message);

                if (!_pooledInstance.IsLoaded)
                {
                    _pooledInstance.Show();
                    // Window_Loaded will call ShowAndAnimate
                }
                else
                {
                    _pooledInstance.Show();
                    _pooledInstance.ShowAndAnimate();
                }
            }
            catch (Exception ex)
            {
                _isShowing = false;
                Classes.Logger.LogAction("TOAST", $"Show failed: {ex.Message}");

                // If the pooled instance is corrupt, discard it
                try { _pooledInstance?.Close(); } catch { }
                _pooledInstance = null;
            }
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
