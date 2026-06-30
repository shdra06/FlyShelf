using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace FlyShelf;

/// <summary>
/// App partial — Safe Mode UI and Crash Recovery.
/// Contains: LaunchSafeMode (builds the diagnostic fallback window),
///           TriggerSafeModeAndRestart (saves crash log and restarts with --safemode).
/// </summary>
public partial class App
{
    private void LaunchSafeMode(Exception originalException)
    {
        try
        {
            FlyShelf.Classes.Logger.LogAction("SAFEMODE", $"Launching FlyShelf in Safe Mode due to startup failure: {originalException.Message}");

            // Create a clean, friendly fallback window — no error codes shown to users
            Window safeWindow = new Window
            {
                Title = "FlyShelf",
                Width = 480,
                Height = 280,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = true
            };

            safeWindow.Closed += (s, ev) => Application.Current.Shutdown();

            var outerBorder = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 20, 35)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 45, 63)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16)
            };
            // Drop shadow
            outerBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect {
                BlurRadius = 40, Opacity = 0.6, ShadowDepth = 0,
                Color = System.Windows.Media.Color.FromRgb(0, 0, 0)
            };

            var stack = new System.Windows.Controls.StackPanel {
                Margin = new Thickness(40, 36, 40, 36),
                VerticalAlignment = VerticalAlignment.Center
            };

            // Icon + title row
            var titlePanel = new System.Windows.Controls.StackPanel {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12)
            };
            titlePanel.Children.Add(new System.Windows.Controls.TextBlock {
                Text = "🚧",
                FontSize = 28,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            titlePanel.Children.Add(new System.Windows.Controls.TextBlock {
                Text = "Coming Soon",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(titlePanel);

            stack.Children.Add(new System.Windows.Controls.TextBlock {
                Text = "This section is not finished yet. We're working hard to get it ready — please check back in a future update.",
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                Margin = new Thickness(0, 0, 0, 28),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            });

            var buttonPanel = new System.Windows.Controls.StackPanel {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // "Restart" button
            var btnRestart = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 102, 241)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 10, 20, 10),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand
            };
            btnRestart.MouseEnter += (s, ev) => btnRestart.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(79, 82, 221));
            btnRestart.MouseLeave += (s, ev) => btnRestart.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 102, 241));
            var txtRestart = new System.Windows.Controls.TextBlock {
                Text = "Restart App",
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13
            };
            btnRestart.Child = txtRestart;
            btnRestart.MouseLeftButtonDown += (s, ev) => {
                try
                {
                    string crashCleanPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "crash_error.txt");
                    if (System.IO.File.Exists(crashCleanPath))
                        System.IO.File.Delete(crashCleanPath);
                }
                catch {} // Best-effort: failure is acceptable
                try
                {
                    _mutex?.Dispose();
#if MSIX_STORE
                    Application.Current.Shutdown();
#else
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe"),
                        UseShellExecute = true
                    });
                    Application.Current.Shutdown();
#endif
                }
                catch { Application.Current.Shutdown(); }
            };
            buttonPanel.Children.Add(btnRestart);

            // "Close" button
            var btnExit = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 45, 63)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 10, 20, 10),
                Cursor = Cursors.Hand
            };
            btnExit.MouseEnter += (s, ev) => btnExit.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 58, 82));
            btnExit.MouseLeave += (s, ev) => btnExit.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 45, 63));
            var txtExit = new System.Windows.Controls.TextBlock {
                Text = "Close",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13
            };
            btnExit.Child = txtExit;
            btnExit.MouseLeftButtonDown += (s, ev) => Application.Current.Shutdown();
            buttonPanel.Children.Add(btnExit);

            stack.Children.Add(buttonPanel);
            outerBorder.Child = stack;
            safeWindow.Content = outerBorder;

            // Alt+C shows the same friendly message — no error codes exposed
            safeWindow.SourceInitialized += (s, ev) =>
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(safeWindow);
                var hwnd = helper.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var settings = Classes.SettingsManager.Current;
                    RegisterHotKey(hwnd, 9000, settings.HotkeyModifier | 0x4000, settings.HotkeyKey);
                    System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                    {
                        if (msg == 0x0312 && wp.ToInt32() == 9000)
                        {
                            MessageBox.Show(
                                "This section is not finished yet.\n\nWe're working hard to get it ready — please check back in a future update.",
                                "FlyShelf",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                            handled = true;
                        }
                        return IntPtr.Zero;
                    });

                    safeWindow.Closed += (s2, ev2) =>
                    {
                        try { UnregisterHotKey(hwnd, 9000); } catch { } // Best-effort: failure is acceptable
                    };
                }
            };

            safeWindow.MouseLeftButtonDown += (s, ev) => { try { safeWindow.DragMove(); } catch { } /* Best-effort: failure is acceptable */ };
            safeWindow.Show();
        }
        catch (Exception fatalEx)
        {
            // Even the fallback failed — just close silently; error is already in the log
            FlyShelf.Classes.Logger.LogAction("SAFEMODE", $"Safe mode fallback UI failed: {fatalEx.Message}");
            Application.Current.Shutdown();
        }
    }

    private static bool _safeModeRestartTriggered = false;

    private static void TriggerSafeModeAndRestart(string errorDetails)
    {
        if (_safeModeRestartTriggered) return;
        _safeModeRestartTriggered = true;

        try
        {
            string crashPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "crash_error.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashPath));
            System.IO.File.WriteAllText(crashPath, errorDetails);
        }
        catch { } // Best-effort: failure is acceptable

        try
        {
            FlyShelf.Classes.Logger.LogAction("FATAL_CRASH", "App crashed, restarting in Safe Mode...");
            FlyShelf.Classes.Logger.Shutdown();
        }
        catch { } // Best-effort: failure is acceptable

        try
        {
            _mutex?.Dispose();
        }
        catch { } // Best-effort: failure is acceptable

        try
        {
#if MSIX_STORE
            // Store apps cannot self-restart; just exit
            Environment.Exit(1);
#else
            string exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--safemode",
                UseShellExecute = true
            });
#endif
        }
        catch { } // Best-effort: failure is acceptable

        try
        {
            Environment.Exit(1);
        }
        catch { } // Best-effort: failure is acceptable
    }
}
