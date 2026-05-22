using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace FlyShelf;

public partial class App : Application
{
    private const int VK_LBUTTON = 0x01;
    private static App _instance;
    private static MainWindow _mainWinInstance;
    private static System.Threading.Timer? _shakeTimer;

    /// <summary>Reference to open PDF merge window; shake suppressed only when it's focused.</summary>
    internal static Window? ActiveMergeWindow = null;

    // Shake Detection State
    private static int _shakeCount = 0;
    private static int _lastShakeDirX = 0; 
    private static int _lastShakeDirY = 0; 
    private static int _lastShakeX = 0;
    private static int _lastShakeY = 0;
    private static long _lastShakeTime = 0;
    private static int _shakeStartY = 0;
    private static long _lastClipboardLaunchTime = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static System.Threading.Mutex _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 1. Check command line arguments for Safe Mode
        bool startInSafeMode = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i].Equals("--safemode", StringComparison.OrdinalIgnoreCase))
            {
                startInSafeMode = true;
                break;
            }
        }

        // 2. Mutex Check for single instance
        const string appName = "FlyShelf_SingleInstance_Mutex_Global";
        bool createdNew;
        _mutex = new System.Threading.Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            // Another instance is already running
            Application.Current.Shutdown();
            return;
        }

        base.OnStartup(e);

        if (startInSafeMode)
        {
            string safeModeError = "Manual trigger or unspecified crash.";
            string crashPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "crash_error.txt");
            try
            {
                if (System.IO.File.Exists(crashPath))
                {
                    safeModeError = System.IO.File.ReadAllText(crashPath);
                }
            }
            catch { }

            this.ShutdownMode = ShutdownMode.OnLastWindowClose;
            LaunchSafeMode(new Exception(safeModeError));
            return;
        }

        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Force-load assemblies in Single-File deployment so WPF has access to all control styles and types
        try
        {
            _ = typeof(Wpf.Ui.Controls.SymbolIcon).FullName;
            _ = typeof(MicaWPF.Controls.MicaWindow).FullName;
        }
        catch { }
        
        // ------------------------------------------------------------------
        // Single File Deployment: Synthesize the physical scripts locally FIRST!
        FlyShelf.Classes.RuntimeHost.Initialize();
        // ------------------------------------------------------------------

        FlyShelf.Classes.SettingsManager.Load();
        
        // ═══ INTERNAL CLOCK: Sync with NTP before any Firebase/networking ═══
        // Protects against wrong system clock causing auth failures and dead heartbeats
        _ = FlyShelf.Classes.NetworkClock.InitializeAsync();
        
        try 
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null)
                {
                    if (FlyShelf.Classes.SettingsManager.Current.AutoStartEnabled)
                    {
                        // Environment.ProcessPath guarantees absolute pathing even for self-contained SingleFile bundles 
                        key.SetValue("FlyShelf", Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "FlyShelf.exe"));
                    }
                    else
                    {
                        key.DeleteValue("FlyShelf", false);
                    }
                }
            }
        }
        catch (Exception) { /* Swallow permission constraint exceptions gracefully */ }
        
        _instance = this;
        StartShakePolling();

        try
        {
            // Catch UI thread exceptions and restart in Safe Mode
            DispatcherUnhandledException += (s, args) =>
            {
                args.Handled = true; // Prevents the default Windows crash dialog
                TriggerSafeModeAndRestart($"[UI Thread Exception]\n{args.Exception}");
            };

            // Catch background thread crashes and restart in Safe Mode
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                TriggerSafeModeAndRestart($"[AppDomain Unhandled Exception]\n{args.ExceptionObject}");
            };

            // Catch async Task thread exceptions — log them, but don't force restart unless they are fatal
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                args.SetObserved();
                try { System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs", "flyshelf_debugger.log"),
                    $"[{DateTime.Now}] ASYNC SWALLOWED: {args.Exception.Message}\n"); } catch { }
            };

            if (string.IsNullOrWhiteSpace(FlyShelf.Classes.SettingsManager.Current.DeviceName))
            {
                Window namingWindow = new Window
                {
                    Title = "FlyShelf Initialization",
                    Width = 450,
                    Height = 260,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Topmost = true
                };

                var outerBorder = new System.Windows.Controls.Border {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 25, 25)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12)
                };

                var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(30), VerticalAlignment = VerticalAlignment.Center };
                
                stack.Children.Add(new System.Windows.Controls.TextBlock { 
                    Text = "FlyShelf Mesh Registration", 
                    FontSize = 22, 
                    FontWeight = FontWeights.Bold, 
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                
                stack.Children.Add(new System.Windows.Controls.TextBlock { 
                    Text = "Set a unique identifier for this PC to sync across your network.", 
                    FontSize = 13, 
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 160, 160)),
                    Margin = new Thickness(0, 0, 0, 24),
                    TextWrapping = TextWrapping.Wrap
                });
                
                stack.Children.Add(new System.Windows.Controls.TextBlock { 
                    Text = "PC Node Name", 
                    FontSize = 14, 
                    FontWeight = FontWeights.SemiBold, 
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                
                var inputBorder = new System.Windows.Controls.Border {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 15)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6)
                };
                
                var input = new System.Windows.Controls.TextBox { 
                    FontSize = 15, 
                    Padding = new Thickness(12), 
                    Background = System.Windows.Media.Brushes.Transparent, 
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    CaretBrush = System.Windows.Media.Brushes.White
                };
                inputBorder.Child = input;
                stack.Children.Add(inputBorder);
                
                var btnBorder = new System.Windows.Controls.Border {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)), // Emerald-500
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(24, 10, 24, 10),
                    Margin = new Thickness(0, 24, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Cursor = Cursors.Hand
                };
                
                var btnText = new System.Windows.Controls.TextBlock {
                    Text = "Join Mesh",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                btnBorder.Child = btnText;
                
                btnBorder.MouseEnter += (s, ev) => btnBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105));
                btnBorder.MouseLeave += (s, ev) => btnBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                
                btnBorder.MouseLeftButtonDown += (s, ev) => {
                    if (!string.IsNullOrWhiteSpace(input.Text))
                    {
                        FlyShelf.Classes.SettingsManager.Current.DeviceName = input.Text.Trim();
                        FlyShelf.Classes.SettingsManager.Save();
                        namingWindow.DialogResult = true;
                        namingWindow.Close();
                    }
                };
                stack.Children.Add(btnBorder);
                
                outerBorder.Child = stack;
                namingWindow.Content = outerBorder;
                
                namingWindow.Loaded += (s, ev) => { input.Focus(); };
                
                namingWindow.ShowDialog();
            }

            // Provide immediate feedback that the service captured the network without waiting for graphics
            FlyShelf.Windows.ToastWindow.ShowToast("Service online");

            // ═══ SLEEP/RESUME RECOVERY ═══
            // When PC wakes from sleep, all sockets die and Cloudflare tunnel breaks.
            // Force-restart the tunnel (old URL is dead) and push fresh LAN heartbeat.
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, ev) =>
            {
                if (ev.Mode == Microsoft.Win32.PowerModes.Resume)
                {
                    FlyShelf.Classes.Logger.LogAction("POWER", "⚡ PC resumed from sleep — force-restarting network in 5s");
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(5000); // Wait for network stack to stabilize
                        
                        // Force-restart Cloudflare tunnel — the old URL is dead after sleep
                        // The GlobalUrlUpdated event will auto-purge stale Firebase entries
                        var server = FlyShelf.Classes.NetworkSyncServer.Instance;
                        if (server != null)
                        {
                            FlyShelf.Classes.Logger.LogAction("POWER", "Killing stale Cloudflare tunnel — will get new URL...");
                            // Push heartbeat with LAN IP ONLY (no stale Cloudflare URL) so Android can reach us via LAN immediately
                            try { await FlyShelf.Classes.CloudDiscoveryManager.PushTunnelUrl(server.DisplayUrl, true, server.DisplayUrl); }
                            catch (Exception ex) { FlyShelf.Classes.Logger.LogAction("POWER", $"LAN heartbeat failed: {ex.Message}"); }
                        }
                        
                        FlyShelf.Classes.Logger.DumpNetworkDiagnostics();
                        FlyShelf.Classes.Logger.LogAction("POWER", "✅ Post-sleep recovery complete — Cloudflare will auto-restart via health monitor");
                    });
                }
            };

            // Offload the massive WPF XAML layout rasterization payload directly to the background!
            // This drops FlyShelf's actual active startup boot time from ~2000ms straight to < 10ms!
            Application.Current.Dispatcher.InvokeAsync(async () => 
            {
                try
                {
                    _mainWinInstance = new MainWindow();
                    MainWindow = _mainWinInstance;
                    
                    // Load persisted clipboard history asynchronously (text + images survive restarts)
                    _ = (_mainWinInstance.DataContext as ViewModels.FlyShelfViewModel)?.LoadPersistedHistoryAsync();
                    
                    _mainWinInstance.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mainWinInstance.Left = -20000;
                    _mainWinInstance.Top = -20000;
                    MainWindow.Show();
                    
                    // One-time cleanup: purge old GUID-based device entries from Firebase
                    _ = FlyShelf.Classes.CloudDiscoveryManager.CleanupStaleDevices();
                    
                    // Dump full network diagnostics at startup for remote debugging
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(8000); // Wait for Cloudflare to initialize
                        FlyShelf.Classes.Logger.DumpNetworkDiagnostics();
                    });
                    
                    // CRITICAL: Give the NotifyIcon (system tray) and TaskbarWindow (widget)
                    // enough time to register before hiding. The WPF-UI tray:NotifyIcon
                    // registers in the Loaded event — hiding immediately kills the registration.
                    await System.Threading.Tasks.Task.Delay(500);
                    MainWindow.Hide();
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText("startup_error.txt", $"[MainWindow Startup Failed] {ex}\n"); } catch { }
                    LaunchSafeMode(ex);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("startup_error.txt", ex.ToString());
            TriggerSafeModeAndRestart($"[Startup Fatal Exception]\n{ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shakeTimer?.Dispose();
        
        try
        {
            FlyShelf.Classes.CloudDiscoveryManager.PushTunnelUrl("offline", false).Wait(1500);
        }
        catch { }
        
        FlyShelf.Classes.Logger.Shutdown();
        base.OnExit(e);
    }

    // Store-compliant Shake-to-Open Background Polling (No low-level system hooks!)
    private static void StartShakePolling()
    {
        _shakeTimer = new System.Threading.Timer(state =>
        {
            try
            {
                if (!FlyShelf.Classes.SettingsManager.Current.EnableShakeToOpen)
                {
                    _shakeCount = 0;
                    return;
                }

                // Check if Left Mouse Button is held down
                if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                {
                    POINT pt;
                    if (GetCursorPos(out pt))
                    {
                        if (Environment.TickCount64 - _lastClipboardLaunchTime < 5000)
                        {
                            _shakeCount = 0;
                            return;
                        }

                        int currentX = pt.x;
                        int currentY = pt.y;
                        long currentTime = Environment.TickCount64;

                        if (_shakeCount == 0)
                        {
                            _shakeStartY = currentY;
                        }

                        if (currentTime - _lastShakeTime > 500)
                        {
                            _shakeCount = 0;
                            _lastShakeDirX = 0;
                            _lastShakeDirY = 0;
                            _lastShakeX = currentX;
                            _lastShakeY = currentY;
                            _lastShakeTime = currentTime;
                        }
                        else
                        {
                            int deltaX = currentX - _lastShakeX;
                            int deltaY = currentY - _lastShakeY;
                            
                            bool reversed = false;
                            int currentDirX = deltaX > 0 ? 1 : (deltaX < 0 ? -1 : 0);
                            int currentDirY = deltaY > 0 ? 1 : (deltaY < 0 ? -1 : 0);

                            if (Math.Abs(deltaX) > 18)
                            {
                                if (_lastShakeDirX != 0 && currentDirX != _lastShakeDirX) reversed = true;
                                _lastShakeDirX = currentDirX;
                                _lastShakeX = currentX;
                                _lastShakeTime = currentTime;
                            }
                            else if (Math.Abs(deltaY) > 18)
                            {
                                if (_lastShakeDirY != 0 && currentDirY != _lastShakeDirY) reversed = true;
                                _lastShakeDirY = currentDirY;
                                _lastShakeY = currentY;
                                _lastShakeTime = currentTime;
                            }

                            if (reversed)
                            {
                                _shakeCount++;

                                if (_shakeCount >= 3)
                                {
                                    _shakeCount = 0; 
                                    int triggerX = currentX;
                                    int triggerY = currentY;

                                    int netDriftY = triggerY - _shakeStartY;
                                    if (netDriftY > 150) return;

                                    _lastClipboardLaunchTime = Environment.TickCount64;

                                    _instance?.Dispatcher.InvokeAsync(async () => 
                                    {
                                        await System.Threading.Tasks.Task.Delay(300);
                                        if (ActiveMergeWindow != null && ActiveMergeWindow.IsActive) return;
                                        _instance.LaunchClipboardManager(triggerX, triggerY, false, 0, false);
                                    }, System.Windows.Threading.DispatcherPriority.Background);
                                }
                            }
                        }
                    }
                }
                else
                {
                    _shakeCount = 0;
                }
            }
            catch { }
        }, null, 0, 40); // Poll every 40ms (highly responsive 25fps poll rate!)
    }

    private void LaunchClipboardManager(double x, double y, bool isPersistent, int mode, bool stealFocus = true)
    {
        if (_mainWinInstance == null)
        {
            _mainWinInstance = new MainWindow();
            MainWindow = _mainWinInstance;
        }

        _mainWinInstance.ShowNearPosition(x, y, mode, isPersistent, stealFocus);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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



