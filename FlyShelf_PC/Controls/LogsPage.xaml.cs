using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlyShelf.Classes;
using FlyShelf.Windows;

namespace FlyShelf.Controls
{
    /// <summary>
    /// Interaction logic for LogsPage.xaml
    /// Real-time scrollable log viewer with monospaced font, auto-scroll, Copy All, and Clear.
    /// </summary>
    public partial class LogsPage : UserControl
    {
        private bool _isSubscribed;

        public LogsPage()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            RefreshLogs();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (_isSubscribed) return;
            AppLogger.LogAdded += OnLogAdded;
            AppLogger.LogsCleared += OnLogsCleared;
            _isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed) return;
            AppLogger.LogAdded -= OnLogAdded;
            AppLogger.LogsCleared -= OnLogsCleared;
            _isSubscribed = false;
        }

        public void RefreshLogs()
        {
            try
            {
                string logs = AppLogger.GetAllLogsText();
                LogTextBox.Text = logs;
                LogTextBox.ScrollToEnd();
                UpdateCount();
            }
            catch
            {
                // Best-effort
            }
        }

        private void OnLogAdded(string entry)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (LogTextBox.LineCount > 600)
                    {
                        LogTextBox.Text = AppLogger.GetAllLogsText();
                    }
                    else
                    {
                        if (LogTextBox.Text.Length > 0 && !LogTextBox.Text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                        {
                            LogTextBox.AppendText(Environment.NewLine);
                        }
                        LogTextBox.AppendText(entry + Environment.NewLine);
                    }
                    LogTextBox.ScrollToEnd();
                    UpdateCount();
                }
                catch
                {
                    // Best-effort UI update
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnLogsCleared()
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    LogTextBox.Clear();
                    UpdateCount();
                }
                catch
                {
                    // Best-effort UI update
                }
            });
        }

        private void UpdateCount()
        {
            try
            {
                int count = LogTextBox.LineCount;
                if (count == 1 && string.IsNullOrEmpty(LogTextBox.Text)) count = 0;
                LogCountText.Text = $"{count} entries";
            }
            catch
            {
                // Best-effort
            }
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string text = LogTextBox.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = AppLogger.GetAllLogsText();
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    ClipboardHelper.SafeSetText(text);
                    ToastWindow.ShowToast("Logs copied to clipboard");
                }
                else
                {
                    ToastWindow.ShowToast("No logs to copy");
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Failed to copy logs: {ex.Message}");
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppLogger.Clear();
                LogTextBox.Clear();
                UpdateCount();
                ToastWindow.ShowToast("Logs cleared");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Failed to clear logs: {ex.Message}");
            }
        }

        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");
                if (!Directory.Exists(logsDir))
                {
                    Directory.CreateDirectory(logsDir);
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logsDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Failed to open folder: {ex.Message}");
            }
        }
    }
}
