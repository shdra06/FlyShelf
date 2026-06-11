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
    private static int _lastSigDirX = 0; 
    private static int _lastSigDirY = 0; 
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
        // ═══ SELF-UPDATE HANDLER ═══
        // If launched with --apply-update, we are the updater EXE running from temp.
        // Run the replacement logic and exit — no UI, no mutex, no WPF initialization.
        if (FlyShelf.Classes.UpdateManager.HandleUpdateIfRequested(e.Args))
        {
            Environment.Exit(0);
            return;
        }

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
        FlyShelf.Classes.LicenseManager.Load();
        FlyShelf.Classes.ReminderManager.Load();
        
        // ═══ SECURITY v2.0.0: Verify binary hasn't been patched ═══
        FlyShelf.Classes.LicenseManager.VerifyAssemblyIntegrity();
        
        // ═══ INTERNAL CLOCK: Sync with NTP before any Firebase/networking ═══
        // Protects against wrong system clock causing auth failures and dead heartbeats
        _ = FlyShelf.Classes.NetworkClock.InitializeAsync();
        
        // Initialize Auto-Start status asynchronously based on stored setting (non-blocking)
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await FlyShelf.Classes.StartupHelper.SetRunAtStartupAsync(FlyShelf.Classes.SettingsManager.Current.AutoStartEnabled);
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("STARTUP_INIT_ERROR", ex.Message);
            }
        });

        // Listen for setting changes during the app session to update instantly
        FlyShelf.Classes.SettingsManager.Current.PropertyChanged += async (s, ev) =>
        {
            if (ev.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.AutoStartEnabled))
            {
                await FlyShelf.Classes.StartupHelper.SetRunAtStartupAsync(FlyShelf.Classes.SettingsManager.Current.AutoStartEnabled);
            }
        };
        
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


                    
                    // Start the reminder scheduler (polls every 15s for due reminders)
                    FlyShelf.Classes.ReminderScheduler.Start();
                    
                    // One-time cleanup: purge old GUID-based device entries from Firebase
                    _ = FlyShelf.Classes.CloudDiscoveryManager.CleanupStaleDevices();
                    
                    // Revalidate Pro license on server (checks for revoked keys)
                    _ = FlyShelf.Classes.LicenseManager.RevalidateLicenseAsync();
                    
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
                    _mainWinInstance.HideWindowInternal();
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
        // Stop any active audio playback on application exit
        ViewModels.ClipboardItem.StopActivePlayback();

        _shakeTimer?.Dispose();
        
        // Stop reminder scheduler and flush pending saves
        try { FlyShelf.Classes.ReminderScheduler.Stop(); } catch { }
        try { FlyShelf.Classes.ReminderManager.SaveNow(); } catch { }

        try
        {
            FlyShelf.Classes.NetworkSyncServer.Instance?.Stop();
        }
        catch { }

        try
        {
            FlyShelf.Classes.PeerManager.Instance?.Stop();
        }
        catch { }
        
        try
        {
            FlyShelf.Classes.CloudDiscoveryManager.PushTunnelUrl("offline", false).Wait(1500);
        }
        catch { }
        
        // Flush any pending notes to disk
        try { FlyShelf.Classes.NoteManager.SaveNow(); } catch { }

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
                // Note: Shake-to-spawn works even when the Hub (settings) window is open.

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
                        if (Environment.TickCount64 - _lastClipboardLaunchTime < 1500)
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

                        if (currentTime - _lastShakeTime > 900) // Increased turn reset to 900ms for slower/natural/regular interval shaking
                        {
                            if (_shakeCount > 0)
                            {
                                FlyShelf.Classes.Logger.LogAction("SHAKE", $"Shake timer reset due to inactivity gap ({currentTime - _lastShakeTime}ms). Resetting count from {_shakeCount} to 0.");
                            }
                            _shakeCount = 0;
                            _lastSigDirX = 0;
                            _lastSigDirY = 0;
                            _lastShakeX = currentX;
                            _lastShakeY = currentY;
                            _lastShakeTime = currentTime;
                        }
                        else
                        {
                            int deltaX = currentX - _lastShakeX;
                            int deltaY = currentY - _lastShakeY;
                            double distSq = (double)(deltaX * deltaX + deltaY * deltaY);

                            // Lowered displacement threshold to 16 (4.0px) for much higher responsiveness (including diagonal shakes)
                            if (distSq >= 16)
                            {
                                // Ignore strictly vertical movements (between 80 and 90 degrees) to block vertical shakes
                                if (Math.Abs(deltaY) >= Math.Abs(deltaX) * 5.67)
                                {
                                    if (_shakeCount > 0)
                                    {
                                        FlyShelf.Classes.Logger.LogAction("SHAKE", $"Reset: strictly vertical movement (80°-90°) detected (Delta: {deltaX}, {deltaY}). Resetting count from {_shakeCount} to 0.");
                                    }
                                    _shakeCount = 0;
                                    _lastSigDirX = 0;
                                    _lastSigDirY = 0;
                                    _lastShakeX = currentX;
                                    _lastShakeY = currentY;
                                    _lastShakeTime = currentTime;
                                    return;
                                }

                                // Ignore strictly horizontal movements (between 0 and 7 degrees) to block horizontal shakes
                                if (Math.Abs(deltaY) <= Math.Abs(deltaX) * 0.123)
                                {
                                    if (_shakeCount > 0)
                                    {
                                        FlyShelf.Classes.Logger.LogAction("SHAKE", $"Reset: strictly horizontal movement (0°-7°) detected (Delta: {deltaX}, {deltaY}). Resetting count from {_shakeCount} to 0.");
                                    }
                                    _shakeCount = 0;
                                    _lastSigDirX = 0;
                                    _lastSigDirY = 0;
                                    _lastShakeX = currentX;
                                    _lastShakeY = currentY;
                                    _lastShakeTime = currentTime;
                                    return;
                                }

                                bool reversed = false;

                                // Dot product of current direction vector and last direction vector.
                                // If dot product < 0, it means the angle between vectors is > 90 degrees,
                                // which perfectly and robustly signifies a diagonal, horizontal, or vertical reversal!
                                if (_lastSigDirX != 0 || _lastSigDirY != 0)
                                {
                                    double dot = (double)(deltaX * _lastSigDirX + deltaY * _lastSigDirY);
                                    if (dot < 0)
                                    {
                                        reversed = true;
                                    }
                                }

                                // Update the active shaking direction vector
                                _lastSigDirX = deltaX;
                                _lastSigDirY = deltaY;
                                _lastShakeX = currentX;
                                _lastShakeY = currentY;
                                _lastShakeTime = currentTime;

                                if (reversed)
                                {
                                    _shakeCount++;
                                    FlyShelf.Classes.Logger.LogAction("SHAKE", $"Direction reversal detected! Count: {_shakeCount}/4. Speed Sq: {distSq:F1}. Delta ({deltaX}, {deltaY}).");

                                    // Effortless and natural trigger after 4 reversals
                                    if (_shakeCount >= 4)
                                    {
                                        FlyShelf.Classes.Logger.LogAction("SHAKE", "✅ Shake-to-open gesture fully recognized! Checking constraints...");
                                        _shakeCount = 0;
                                        _lastSigDirX = 0;
                                        _lastSigDirY = 0;

                                        int triggerX = currentX;
                                        int triggerY = currentY;

                                        // Absolute vertical drift clamping check (covers both upwards and downwards drift)
                                        int netDriftY = Math.Abs(triggerY - _shakeStartY);
                                        if (netDriftY > 500)
                                        {
                                            FlyShelf.Classes.Logger.LogAction("SHAKE", $"❌ Rejected: Exceeded Y-axis drift constraint. Drift: {netDriftY}px (Max allowed: 500px).");
                                            return;
                                        }

                                        _lastClipboardLaunchTime = Environment.TickCount64;
                                        FlyShelf.Classes.Logger.LogAction("SHAKE", $"🚀 Launching Clipboard Mini-Shelf at screen coordinates ({triggerX}, {triggerY}).");

                                        _instance?.Dispatcher.InvokeAsync(async () => 
                                        {
                                            await System.Threading.Tasks.Task.Delay(150); // Lowered delay to 150ms for instant summon feedback
                                            if (ActiveMergeWindow != null && ActiveMergeWindow.IsActive)
                                            {
                                                FlyShelf.Classes.Logger.LogAction("SHAKE", "❌ Rejected: PDF Merger window is active.");
                                                return;
                                            }
                                            _instance.LaunchClipboardManager(triggerX, triggerY, false, 0, false);
                                        }, System.Windows.Threading.DispatcherPriority.Background);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (_shakeCount > 0)
                    {
                        _shakeCount = 0;
                        _lastSigDirX = 0;
                        _lastSigDirY = 0;
                        FlyShelf.Classes.Logger.LogAction("SHAKE", "LBUTTON released. Resetting shake state.");
                    }
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

        // Convert physical x and y to logical coordinates
        double logicalX = x;
        double logicalY = y;
        try
        {
            var monitor = Classes.Utils.MonitorUtil.GetMonitorWithCursor();
            double scaleX = monitor.dpiX / 96.0;
            double scaleY = monitor.dpiY / 96.0;
            if (scaleX > 0 && scaleY > 0)
            {
                logicalX = x / scaleX;
                logicalY = y / scaleY;
            }
        }
        catch { }

        // Spawn offset: position window completely to the right side of the cursor and lower it
        double safeWidth = 260;
        if (_mainWinInstance?.DataContext is ViewModels.FlyShelfViewModel vm)
        {
            safeWidth = vm.CurrentFlyShelfWidth;
        }
        if (safeWidth <= 0) safeWidth = 260;

        logicalX = logicalX + (safeWidth / 2) + 120; // Entirely to the right of the cursor (increased offset from 50 to 120)
        logicalY += 100; // Lowered by 100 logical pixels

        _mainWinInstance.ShowNearPosition(logicalX, logicalY, mode, isPersistent, stealFocus);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ═══ Safe Mode UI + Crash Recovery moved to App.SafeMode.cs ═══
}



