using System;
using System.Collections.Generic;
using System.Linq;
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

        // ═══ Recurring Notification Cooldown (Escalating) ═══
        // Prevents annoying auto-notifications from showing repeatedly.
        // Maps message text → (lastShownMs, repeatCount).
        // Cooldown escalates: 15 min base, +30 min per subsequent repeat.
        private static readonly Dictionary<string, (long lastShown, int repeatCount)> _cooldownTracker = new();
        private const long COOLDOWN_BASE_MS  = 15 * 60 * 1000; // 15 minutes
        private const long COOLDOWN_STEP_MS  = 30 * 60 * 1000; // +30 minutes per repeat

        // Patterns that identify recurring system notifications (not user-triggered).
        // These get cooldown-throttled so users see them at most once per 5 minutes.
        private static readonly string[] _recurringPatterns = new[]
        {
            "cloud sync unavailable",
            "check your internet",
            "sync failed",
            "connection lost",
            "reconnecting",
            "network error",
            "offline",
            "unreachable",
            "firebase auth",
            "token refresh",
            "sign-in failed",
        };

        // Dismiss timer
        private System.Windows.Threading.DispatcherTimer? _dismissTimer;
        private int _customDurationMs;
        
        // Progress mode tracking
        private bool _isProgressMode;
        private double _progressTrackWidth = 200; // Default track width for progress bar

        public ToastWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Configures the toast content based on message semantics.
        /// Uses a subtle accent-dot color instead of large icons.
        /// </summary>
        private void ConfigureForMessage(string message)
        {
            MessageText.Text = message;
            _isProgressMode = false;
            
            // Show standard layout, hide progress layout
            StandardLayout.Visibility = Visibility.Visible;
            ProgressLayout.Visibility = Visibility.Collapsed;

            string msgLower = message.ToLowerInvariant();
            
            // Determine accent color based on message type
            string accentKey;
            if (msgLower.Contains("failed", StringComparison.Ordinal) || msgLower.Contains("error", StringComparison.Ordinal) || msgLower.Contains("❌", StringComparison.Ordinal) || msgLower.Contains("busy", StringComparison.Ordinal) || msgLower.Contains("timeout", StringComparison.Ordinal) || msgLower.Contains("offline", StringComparison.Ordinal) || msgLower.Contains("unreachable", StringComparison.Ordinal))
            {
                accentKey = "DangerColor";
            }
            else if (msgLower.Contains("warning", StringComparison.Ordinal) || msgLower.Contains("⚠️", StringComparison.Ordinal) || msgLower.Contains("⚠", StringComparison.Ordinal) || msgLower.Contains("limit", StringComparison.Ordinal) || msgLower.Contains("retry", StringComparison.Ordinal))
            {
                accentKey = "WarningColor";
            }
            else if (msgLower.Contains("convert", StringComparison.Ordinal) || msgLower.Contains("merge", StringComparison.Ordinal) || msgLower.Contains("pdf", StringComparison.Ordinal))
            {
                accentKey = "InfoColor";
            }
            else if (msgLower.Contains("copy", StringComparison.Ordinal) || msgLower.Contains("copied", StringComparison.Ordinal) || msgLower.Contains("clipboard", StringComparison.Ordinal) || msgLower.Contains("📋", StringComparison.Ordinal))
            {
                accentKey = "ThemeAccentLight";
            }
            else if (msgLower.Contains("sync", StringComparison.Ordinal) || msgLower.Contains("pairing", StringComparison.Ordinal) || msgLower.Contains("paired", StringComparison.Ordinal) || msgLower.Contains("device", StringComparison.Ordinal) || msgLower.Contains("lan", StringComparison.Ordinal) || msgLower.Contains("cloudflare", StringComparison.Ordinal))
            {
                accentKey = "InfoColor";
            }
            else
            {
                accentKey = "ThemeAccentLight";
            }

            // Resolve accent color
            Color accentColor;
            try
            {
                var brush = Application.Current?.Resources[accentKey] as SolidColorBrush;
                accentColor = brush?.Color ?? Color.FromRgb(129, 140, 248);
            }
            catch
            {
                accentColor = Color.FromRgb(129, 140, 248);
            }

            // Apply accent to the dot indicator
            AccentDot.Background = new SolidColorBrush(accentColor);

            // Subtle accent-colored shadow
            ToastShadow.Color = accentColor;
            ToastShadow.Opacity = 0.12;
            ToastShadow.BlurRadius = 20;

            // Reset transform for fresh animation
            ToastTranslate.Y = 12;
            ToastScale.ScaleX = 0.97;
            ToastScale.ScaleY = 0.97;
            this.Opacity = 0;
        }

        /// <summary>
        /// Configures the toast for progress display (e.g., PDF conversion).
        /// </summary>
        private void ConfigureForProgress(string title, int percent, string accentKey = "InfoColor")
        {
            _isProgressMode = true;
            
            // Show progress layout, hide standard layout
            StandardLayout.Visibility = Visibility.Collapsed;
            ProgressLayout.Visibility = Visibility.Visible;
            
            ProgressTitle.Text = title;
            ProgressPercent.Text = $"{Math.Clamp(percent, 0, 100)}%";
            
            // Resolve accent color
            Color accentColor;
            try
            {
                var brush = Application.Current?.Resources[accentKey] as SolidColorBrush;
                accentColor = brush?.Color ?? Color.FromRgb(56, 189, 248);
            }
            catch
            {
                accentColor = Color.FromRgb(56, 189, 248);
            }

            ProgressAccentDot.Background = new SolidColorBrush(accentColor);
            ProgressFill.Background = new SolidColorBrush(accentColor);
            
            // Calculate fill width based on percentage
            UpdateProgressBarWidth(percent);

            // Subtle accent shadow
            ToastShadow.Color = accentColor;
            ToastShadow.Opacity = 0.12;
            ToastShadow.BlurRadius = 20;

            // Reset transform for fresh animation (only on first show)
            if (!_isShowing || this.Opacity < 0.5)
            {
                ToastTranslate.Y = 12;
                ToastScale.ScaleX = 0.97;
                ToastScale.ScaleY = 0.97;
                this.Opacity = 0;
            }
        }
        
        private void UpdateProgressBarWidth(int percent)
        {
            // The progress track is inside a Grid that auto-sizes.
            // We set ProgressFill width as a proportion of the track.
            // Use a reasonable default; will be recalculated on layout.
            double trackWidth = _progressTrackWidth;
            ProgressFill.Width = Math.Max(0, trackWidth * Math.Clamp(percent, 0, 100) / 100.0);
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
            Storyboard.SetTargetProperty(slideAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));

            // Subtle scale pop-in for tactile feel
            var scaleXAnim = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleXAnim, ToastBorder);
            Storyboard.SetTargetProperty(scaleXAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)"));

            var scaleYAnim = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleYAnim, ToastBorder);
            Storyboard.SetTargetProperty(scaleYAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)"));

            sb.Children.Add(fadeAnim);
            sb.Children.Add(slideAnim);
            sb.Children.Add(scaleXAnim);
            sb.Children.Add(scaleYAnim);
            sb.Begin();
        }

        private void RestartDismissTimer()
        {
            if (_dismissTimer == null)
            {
                _dismissTimer = new System.Windows.Threading.DispatcherTimer();
                _dismissTimer.Tick += async (s, e) =>
                {
                    _dismissTimer?.Stop();
                    await DismissAsync();
                };
            }
            _dismissTimer.Stop();
            
            // Progress toasts stay longer; normal toasts dismiss quickly
            int duration = _customDurationMs > 0 ? _customDurationMs 
                         : _isProgressMode ? 8000 
                         : 2400;
            _dismissTimer.Interval = TimeSpan.FromMilliseconds(duration);
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
                Storyboard.SetTargetProperty(slideAnim, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));

                // Scale-out for tactile dismiss
                var scaleXOut = new DoubleAnimation(ToastScale.ScaleX, 0.97, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                Storyboard.SetTarget(scaleXOut, ToastBorder);
                Storyboard.SetTargetProperty(scaleXOut, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)"));

                var scaleYOut = new DoubleAnimation(ToastScale.ScaleY, 0.97, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                Storyboard.SetTarget(scaleYOut, ToastBorder);
                Storyboard.SetTargetProperty(scaleYOut, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)"));

                sb.Children.Add(fadeAnim);
                sb.Children.Add(slideAnim);
                sb.Children.Add(scaleXOut);
                sb.Children.Add(scaleYOut);

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
                try { this.Hide(); } catch { } // Best-effort: failure is acceptable
            }
        }

        /// <summary>
        /// Shows a toast by reusing the pooled instance. If the pool is busy,
        /// the message is queued and will display after the current toast dismisses.
        /// </summary>
        public static void ShowToast(string message) => ShowToast(message, 0);

        public static void ShowToast(string message, int durationMs)
        {
            // Respect user preference to disable notifications
            try { if (!FlyShelf.Classes.SettingsManager.Current.EnableNotifications) return; } catch { } // Best-effort: failure is acceptable

            string smartMessage = MakeMessageSmart(message);

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                // M-11 FIX: Anti-spam check runs on UI thread (naturally serialized)
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_lastMessage == smartMessage && (now - _lastMessageTime) < 500)
                    return;

                // ═══ Recurring notification cooldown (escalating) ═══
                // Auto-notifications (cloud sync, network errors, etc.) are throttled.
                // 1st repeat: 15 min, 2nd: 45 min, 3rd: 75 min, etc.
                string msgLower = smartMessage.ToLowerInvariant();
                bool isRecurring = false;
                foreach (var pattern in _recurringPatterns)
                {
                    if (msgLower.Contains(pattern, StringComparison.Ordinal))
                    {
                        isRecurring = true;
                        break;
                    }
                }
                if (isRecurring)
                {
                    if (_cooldownTracker.TryGetValue(msgLower, out var entry))
                    {
                        long cooldownMs = COOLDOWN_BASE_MS + (entry.repeatCount * COOLDOWN_STEP_MS);
                        if ((now - entry.lastShown) < cooldownMs)
                        {
                            return; // Still in cooldown — suppress
                        }
                        // Cooldown expired — show it but escalate for next time
                        _cooldownTracker[msgLower] = (now, entry.repeatCount + 1);
                    }
                    else
                    {
                        // First occurrence — show it, start tracking
                        _cooldownTracker[msgLower] = (now, 0);
                    }
                }

                // Evict stale cooldown entries (>24h old) when tracker gets large
                if (_cooldownTracker.Count > 100)
                {
                    var staleKeys = _cooldownTracker.Where(kv => (now - kv.Value.lastShown) > 24 * 60 * 60 * 1000L).Select(kv => kv.Key).ToList();
                    foreach (var key in staleKeys) _cooldownTracker.Remove(key);
                }

                _lastMessage = smartMessage;
                _lastMessageTime = now;

                lock (_poolLock)
                {
                    if (_isShowing)
                    {
                        // Queue it — max 3 pending to prevent unbounded growth
                        if (_pendingMessages.Count < 3)
                            _pendingMessages.Enqueue(smartMessage);
                        return;
                    }

                    ShowNextFromPool(smartMessage, durationMs);
                }
            });
        }

        /// <summary>
        /// Shows a progress toast with a percentage bar.
        /// Call repeatedly to update progress. Auto-dismisses after completion.
        /// Thread-safe: can be called from background threads.
        /// </summary>
        public static void ShowProgress(string title, int percent, string accentKey = "InfoColor")
        {
            try { if (!FlyShelf.Classes.SettingsManager.Current.EnableNotifications) return; } catch { }

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                lock (_poolLock)
                {
                    try
                    {
                        if (_pooledInstance == null)
                            _pooledInstance = new ToastWindow();

                        bool wasAlreadyShowing = _isShowing && _pooledInstance._isProgressMode;

                        // If a progress toast is already visible, just update in-place
                        if (wasAlreadyShowing && _pooledInstance.IsVisible)
                        {
                            _pooledInstance.ProgressTitle.Text = title;
                            _pooledInstance.ProgressPercent.Text = $"{Math.Clamp(percent, 0, 100)}%";
                            _pooledInstance.UpdateProgressBarWidth(percent);
                            
                            // Reset the dismiss timer on each update
                            _pooledInstance.RestartDismissTimer();
                            return;
                        }

                        _isShowing = true;
                        _pooledInstance._customDurationMs = percent >= 100 ? 2000 : 0; // Quick dismiss when done
                        _pooledInstance.ConfigureForProgress(title, percent, accentKey);

                        if (!_pooledInstance.IsLoaded)
                        {
                            _pooledInstance.Show();
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
                        Classes.Logger.LogAction("TOAST", $"Progress show failed: {ex.Message}");
                        try { _pooledInstance?.Close(); } catch { }
                        _pooledInstance = null;
                    }
                }
            });
        }

        /// <summary>
        /// Internal: configures and shows the pooled window with a new message.
        /// Must be called on the UI thread while holding _poolLock.
        /// </summary>
        private static void ShowNextFromPool(string message, int durationMs = 0)
        {
            _isShowing = true;

            try
            {
                if (_pooledInstance == null)
                {
                    _pooledInstance = new ToastWindow();
                }

                _pooledInstance._customDurationMs = durationMs;
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
                try { _pooledInstance?.Close(); } catch { } // Best-effort: failure is acceptable
                _pooledInstance = null;
            }
        }

        public static string FormatSize(long bytes) => FlyShelf.Classes.FormatHelper.FormatSize(bytes);

        public static string GetFileTypeFriendly(string fileName) => FlyShelf.Classes.FormatHelper.GetFileTypeFriendly(fileName);

        public static string MakeMessageSmart(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            // 1. Strip emoji clutter — professional notifications don't need emoji
            message = StripEmoji(message);

            // 2. Detect any full filenames and swap them with friendly type names
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

            // 3. Condense common verbose phrases
            message = message.Replace("copied to clipboard", "Copied");
            message = message.Replace("copied to Clipboard", "Copied");
            message = message.Replace("copied to your clipboard", "Copied");
            
            message = message.Replace("Terminal execution is not available in the Store version.", "Terminal unavailable in Store version");
            message = message.Replace("Elevated terminal is not available in the Store version.", "Elevated terminal unavailable");
            message = message.Replace("Code compilation is not available in the Store version.", "Compilation unavailable");
            
            message = message.Replace("paired successfully!", "paired");
            message = message.Replace("paired successfully", "paired");
            message = message.Replace("joined your sync group!", "joined group");
            
            message = message.Replace("Assembling ", "Receiving ");
            message = message.Replace(" (via WS)... 📥", "");
            
            message = message.Replace("Text from ", "Text received: ");
            message = message.Replace(" via WebSocket!", "");
            message = message.Replace(" via LAN!", "");
            message = message.Replace(" via Cloudflare!", "");
            
            // Capitalize first letter for consistency
            message = message.Trim();
            if (message.Length > 0 && char.IsLower(message[0]))
                message = char.ToUpper(message[0]) + message[1..];

            // Clean up double-spaces
            while (message.Contains("  ", StringComparison.Ordinal))
            {
                message = message.Replace("  ", " ");
            }

            // Remove trailing exclamation pairs and clean endings
            message = message.Trim();
            if (message.EndsWith("! !", StringComparison.Ordinal)) message = message[..^2] + "!";
            // Remove trailing "!" spam (max 1 exclamation)
            while (message.EndsWith("!!", StringComparison.Ordinal)) message = message[..^1];

            return message;
        }

        /// <summary>
        /// Strips common emoji from notification messages for a cleaner, professional look.
        /// Keeps the text content intact.
        /// </summary>
        private static string StripEmoji(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            // Common emoji used in toast messages
            string[] emojiToStrip = { "✅", "❌", "⚠️", "⚠", "📋", "📄", "🖼️", "📊", "📥", "♻️", "🔗", "✨", "🎉", "💾", "🗑️", "📁", "🔄", "⏳", "🚀", "💡", "🔔", "🔒", "🔓", "🌐", "📡", "🛡️", "⬆️", "⬇️" };
            foreach (var emoji in emojiToStrip)
            {
                text = text.Replace(emoji, "");
            }
            
            // Clean up any resulting whitespace issues
            text = text.Trim();
            while (text.Contains("  ", StringComparison.Ordinal))
                text = text.Replace("  ", " ");
            
            // Remove trailing/leading punctuation artifacts
            text = text.Trim(' ', ':', '-', '—');
            
            return text;
        }
    }
}
