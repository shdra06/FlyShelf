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
            
            // Create an ultra-safe fallback window
            Window safeWindow = new Window
            {
                Title = "FlyShelf Safe Mode",
                Width = 520,
                Height = 330,
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
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 20, 20)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38)), // Crimson border
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(12)
            };

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
            
            stack.Children.Add(new System.Windows.Controls.TextBlock { 
                Text = "FlyShelf (Safe Mode)", 
                FontSize = 18, 
                FontWeight = FontWeights.Bold, 
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            });
            
            stack.Children.Add(new System.Windows.Controls.TextBlock { 
                Text = "A critical layout or resource exception prevented FlyShelf from starting normally. FlyShelf is running in diagnostic safe mode.", 
                FontSize = 12, 
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 180, 180)),
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap
            });

            // Show exception details
            var detailText = new System.Windows.Controls.TextBox {
                Text = originalException.ToString(),
                Height = 110,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 15)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 180)),
                BorderThickness = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                IsReadOnly = true,
                Margin = new Thickness(0, 0, 0, 16),
                Padding = new Thickness(8),
                FontSize = 11
            };
            stack.Children.Add(detailText);

            var buttonPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            // "Restart Normally" button
            var btnRestart = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)), // Emerald-500
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand
            };
            var txtRestart = new System.Windows.Controls.TextBlock { Text = "Restart Normally", Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12 };
            btnRestart.Child = txtRestart;
            btnRestart.MouseLeftButtonDown += (s, ev) => {
                try
                {
                    string crashCleanPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "crash_error.txt");
                    if (System.IO.File.Exists(crashCleanPath))
                    {
                        System.IO.File.Delete(crashCleanPath);
                    }
                }
                catch {}
                try
                {
                    _mutex?.Dispose();
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe"),
                        UseShellExecute = true
                    });
                    Application.Current.Shutdown();
                }
                catch (Exception ex) { MessageBox.Show($"Failed to restart: {ex.Message}"); }
            };
            buttonPanel.Children.Add(btnRestart);

            // "Reset settings" button
            var btnReset = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand
            };
            var txtReset = new System.Windows.Controls.TextBlock { Text = "Reset Settings", Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12 };
            btnReset.Child = txtReset;
            btnReset.MouseLeftButtonDown += (s, ev) => {
                try
                {
                    FlyShelf.Classes.SettingsManager.ResetToDefaults();
                    MessageBox.Show("Settings reset to default. Please restart FlyShelf.", "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    Application.Current.Shutdown();
                }
                catch (Exception ex) { MessageBox.Show($"Failed to reset settings: {ex.Message}"); }
            };
            buttonPanel.Children.Add(btnReset);

            // "Exit" button
            var btnExit = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                Cursor = Cursors.Hand
            };
            var txtExit = new System.Windows.Controls.TextBlock { Text = "Close App", Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12 };
            btnExit.Child = txtExit;
            btnExit.MouseLeftButtonDown += (s, ev) => {
                Application.Current.Shutdown();
            };
            buttonPanel.Children.Add(btnExit);

            stack.Children.Add(buttonPanel);
            outerBorder.Child = stack;
            safeWindow.Content = outerBorder;

            // Register native Alt+C hotkey on the safe window to display a safe mode notification!
            safeWindow.SourceInitialized += (s, ev) =>
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(safeWindow);
                var hwnd = helper.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    RegisterHotKey(hwnd, 9000, 0x0001 | 0x4000, 0x43); // Alt+C
                    System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                    {
                        if (msg == 0x0312 && wp.ToInt32() == 9000)
                        {
                            MessageBox.Show("FlyShelf is running in Safe Mode due to a startup crash:\n\n" + originalException.Message, "FlyShelf Safe Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
                            handled = true;
                        }
                        return IntPtr.Zero;
                    });
                }
            };

            safeWindow.Show();
        }
        catch (Exception fatalEx)
        {
            MessageBox.Show($"FlyShelf encountered a fatal initialization error:\n\n{originalException.Message}\n\nFallback UI failed:\n{fatalEx.Message}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.IO.File.WriteAllText(crashPath, errorDetails);
        }
        catch { }

        try
        {
            FlyShelf.Classes.Logger.LogAction("FATAL_CRASH", "App crashed, restarting in Safe Mode...");
            FlyShelf.Classes.Logger.Shutdown();
        }
        catch { }

        try
        {
            _mutex?.Dispose();
        }
        catch { }

        try
        {
            string exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--safemode",
                UseShellExecute = true
            });
        }
        catch { }

        try
        {
            Environment.Exit(1);
        }
        catch { }
    }
}
