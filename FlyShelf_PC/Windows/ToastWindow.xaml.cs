using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace AdvanceClip.Windows
{
    public partial class ToastWindow : Window
    {
        // ═══ Toast Stacking System ═══
        // Tracks active toasts so new ones stack above existing ones instead of overlapping.
        private static readonly List<ToastWindow> _activeToasts = new();
        private static readonly object _toastLock = new();
        private const int TOAST_GAP = 6; // Pixels between stacked toasts

        public ToastWindow(string message)
        {
            InitializeComponent();
            MessageText.Text = message;
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

                this.Left = workArea.Left + (workArea.Width - this.Width) / 2;
                this.Top = baseBottom - this.Height - stackOffset;
                _activeToasts.Add(this);
            }
        }

        private async void StartDismissTimer()
        {
            await Task.Delay(2500);
            
            // Fade out
            for (double i = 1; i > 0; i -= 0.1)
            {
                this.Opacity = i;
                await Task.Delay(20);
            }

            lock (_toastLock)
            {
                _activeToasts.Remove(this);
            }

            this.Close();
        }
        
        public static void ShowToast(string message)
        {
            // Ensures global dispatcher captures cross-threaded Process.Start events.
            Application.Current.Dispatcher.Invoke(() => 
            {
                // Cap active toasts at 4 to prevent screen flooding
                lock (_toastLock)
                {
                    if (_activeToasts.Count >= 4)
                    {
                        // Close the oldest toast to make room
                        try { _activeToasts[0].Close(); _activeToasts.RemoveAt(0); } catch { }
                    }
                }

                var toast = new ToastWindow(message);
                toast.Show();
                toast.PositionAndShow();
                toast.StartDismissTimer();
            });
        }
    }
}
