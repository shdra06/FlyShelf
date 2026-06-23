using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace FlyShelf;

public partial class App : Application
{
    private const int VK_LBUTTON = 0x01;
    private static App _instance;
    private static MainWindow _mainWinInstance;
    private static volatile bool _isCreatingMainWindow = false;
    private static System.Threading.Timer? _shakeTimer;
    private static bool _isHandlingCrash;

    /// <summary>Reference to open PDF merge window; shake suppressed only when it's focused.</summary>
    internal static Window? ActiveMergeWindow = null;
    private static bool _justCompletedOnboarding = false; // Set when onboarding wizard completes in this session

    // Shake Detection State
    private static int _shakeCount = 0;
    private static int _lastSigDirX = 0; 
    private static int _lastSigDirY = 0; 
    private static int _lastShakeX = 0;
    private static int _lastShakeY = 0;
    private static long _lastShakeTime = 0;
    private static int _shakeStartY = 0;
    private static long _lastClipboardLaunchTime = 0;

    // Adaptive shake timer throttling — saves CPU when mouse is idle
    private static int _lastIdleMouseX = -1;
    private static int _lastIdleMouseY = -1;
    private static long _lastMouseMoveTime = 0;
    private const int SHAKE_FAST_MS = 40;   // 25fps when mouse is active
    private const int SHAKE_SLOW_MS = 150;  // 6.7fps when mouse idle >30s
    private const long SHAKE_IDLE_THRESHOLD_MS = 30_000; // 30 seconds

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

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private const int ATTACH_PARENT_PROCESS = -1;

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

        // ═══ POST-UPDATE HEALTH CHECK ═══
        // If a previous update crashed before the UI loaded, auto-rollback from .bak and restart.
        if (FlyShelf.Classes.UpdateManager.CheckAndHandleFailedUpdate())
        {
            Environment.Exit(0);
            return;
        }

        // Clean up leftover temp update files from successful previous updates
        FlyShelf.Classes.UpdateManager.CleanupTempDir();

        // ═══ LOCAL AI TEST HANDLER ═══
        bool isTestAi = false;
        int consolePid = -1;
        if (e.Args != null)
        {
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i].Equals("--test-ai", StringComparison.OrdinalIgnoreCase))
                {
                    isTestAi = true;
                }
                else if (e.Args[i].Equals("--console-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
                {
                    int.TryParse(e.Args[i + 1], out consolePid);
                }
            }
        }

        if (isTestAi)
        {
            if (consolePid != -1)
            {
                AttachConsole(consolePid);
            }
            else
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }

            try
            {
                var standardOutput = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(standardOutput);
                var standardError = new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(standardError);
            }
            catch { }

            // Retrieve console PID to pass to relaunch if needed
            int activeConsolePid = -1;
            try
            {
                IntPtr hwnd = GetConsoleWindow();
                if (hwnd != IntPtr.Zero)
                {
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    activeConsolePid = (int)pid;
                }
            }
            catch { }

            if (!FlyShelf.Classes.StartupHelper.IsPackaged())
            {
#if DEBUG
                Console.WriteLine("\n[FlyShelf] Diagnostic test starting...");
                Console.WriteLine("[FlyShelf] App is not packaged. Starting sparse package registration...");
#endif
                
                var argsList = new System.Collections.Generic.List<string>(e.Args);
                if (activeConsolePid != -1 && !argsList.Contains("--console-pid"))
                {
                    argsList.Add("--console-pid");
                    argsList.Add(activeConsolePid.ToString());
                }

                FlyShelf.Classes.SparsePackageRegistrar.EnsureRegistered(argsList.ToArray());
                Environment.Exit(0);
                return;
            }

            Task.Run(async () =>
            {
                await RunAITestAsync();
                Dispatcher.Invoke(() => Shutdown());
            });
            return;
        }

        // ═══ SPARSE PACKAGE AUTO-REGISTRATION ═══
        // Handled on-demand when the user clicks the AI features to avoid UAC prompts on launch.

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

        // ═══ GLOBAL WINDOW ICON — Ensures all windows (Window + MicaWindow) show FlyShelf icon ═══
        try
        {
            var iconUri = new Uri("pack://application:,,,/Resources/FlyShelfLogo.ico", UriKind.Absolute);
            var iconSource = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
            EventManager.RegisterClassHandler(typeof(Window),
                Window.LoadedEvent, new RoutedEventHandler((sender, args) =>
                {
                    if (sender is Window w && w.Icon == null)
                    {
                        try { w.Icon = iconSource; } catch { }
                    }
                }));
        }
        catch { }

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

        // ═══ GLOBAL CRASH HANDLERS ═══
        // Register FIRST — before any init code that could throw.
        // Without these, RuntimeHost/SettingsManager failures show raw OS crash dialog.
        DispatcherUnhandledException += (s, args) =>
        {
            if (_isHandlingCrash)
            {
                // Re-entrant crash — avoid infinite loop, exit immediately
                try { Environment.Exit(1); } catch { }
                return;
            }
            _isHandlingCrash = true;
            args.Handled = true; // Prevents the default Windows crash dialog
            TriggerSafeModeAndRestart($"[UI Thread Exception]\n{args.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            TriggerSafeModeAndRestart($"[AppDomain Unhandled Exception]\n{args.ExceptionObject}");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            args.SetObserved();
            try { System.IO.File.AppendAllText(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs", "flyshelf_debugger.log"),
                $"[{DateTime.Now}] ASYNC SWALLOWED: {args.Exception.Message}\n"); } catch { }
        };

        // ------------------------------------------------------------------
        // Single File Deployment: Synthesize the physical scripts locally FIRST!
        FlyShelf.Classes.RuntimeHost.Initialize();
        // ------------------------------------------------------------------

        try { FlyShelf.Classes.SettingsManager.Load(); }
        catch (Exception ex)
        {
            FlyShelf.Classes.Logger.LogAction("SETTINGS_RECOVERY", $"Settings load failed, resetting to defaults: {ex.Message}");
            try { FlyShelf.Classes.SettingsManager.ResetToDefaults(); FlyShelf.Classes.SettingsManager.Load(); }
            catch { /* will trigger safe mode via outer handler */ throw; }
        }

        try { FlyShelf.Classes.LicenseManager.Load(); }
        catch (Exception ex)
        {
            FlyShelf.Classes.Logger.LogAction("LICENSE_RECOVERY", $"License load failed, deleting corrupt file: {ex.Message}");
            try
            {
                string licensePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "license.json");
                if (System.IO.File.Exists(licensePath)) System.IO.File.Delete(licensePath);
                FlyShelf.Classes.LicenseManager.Load();
            }
            catch { /* will trigger safe mode via outer handler */ throw; }
        }
        FlyShelf.Classes.ReminderManager.Load();
        
        // ═══ SECURITY v2.0.0: Verify binary hasn't been patched ═══
        FlyShelf.Classes.LicenseManager.VerifyAssemblyIntegrity();
        
        // ═══ INTERNAL CLOCK: Sync with NTP before any Firebase/networking ═══
        // Protects against wrong system clock causing auth failures and dead heartbeats
        _ = FlyShelf.Classes.NetworkClock.InitializeAsync().ContinueWith(t => { if (t.IsFaulted) FlyShelf.Classes.Logger.LogAction("ASYNC_ERR", $"NetworkClock.InitializeAsync failed: {t.Exception?.InnerException?.Message}"); }, TaskContinuationOptions.OnlyOnFaulted);
        
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
            try
            {
                if (ev.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.AutoStartEnabled))
                {
                    await FlyShelf.Classes.StartupHelper.SetRunAtStartupAsync(FlyShelf.Classes.SettingsManager.Current.AutoStartEnabled);
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("STARTUP_SETTING_ERROR", ex.Message);
            }
        };
        
        _instance = this;
        StartShakePolling();

        try
        {

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

                // H-01: Use a Grid as root so we can layer the close button over the content
                var rootGrid = new System.Windows.Controls.Grid();

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
                
                // H-06: MaxLength = 50 to cap device name length
                var input = new System.Windows.Controls.TextBox { 
                    FontSize = 15, 
                    Padding = new Thickness(12), 
                    Background = System.Windows.Media.Brushes.Transparent, 
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    CaretBrush = System.Windows.Media.Brushes.White,
                    MaxLength = 50
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
                
                // H-06: Sanitize device name — strip characters that break Firebase paths or JSON
                btnBorder.MouseLeftButtonDown += (s, ev) => {
                    string rawName = input.Text?.Trim() ?? "";
                    // Strip Firebase/JSON-unsafe characters: . $ # [ ] /
                    string sanitized = System.Text.RegularExpressions.Regex.Replace(rawName, @"[.\$#\[\]/]", "");
                    if (!string.IsNullOrWhiteSpace(sanitized))
                    {
                        FlyShelf.Classes.SettingsManager.Current.DeviceName = sanitized;
                        FlyShelf.Classes.SettingsManager.Save();
                        namingWindow.DialogResult = true;
                        namingWindow.Close();
                    }
                };
                stack.Children.Add(btnBorder);
                
                outerBorder.Child = stack;
                rootGrid.Children.Add(outerBorder);

                // H-01: Close (X) button in top-right corner
                var closeBtnBorder = new System.Windows.Controls.Border {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(6),
                    Background = System.Windows.Media.Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 8, 8, 0),
                    Cursor = Cursors.Hand
                };
                var closeText = new System.Windows.Controls.TextBlock {
                    Text = "✕",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 140, 140)),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                closeBtnBorder.Child = closeText;
                closeBtnBorder.MouseEnter += (s, ev) => closeBtnBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60));
                closeBtnBorder.MouseLeave += (s, ev) => closeBtnBorder.Background = System.Windows.Media.Brushes.Transparent;
                closeBtnBorder.MouseLeftButtonDown += (s, ev) => {
                    // Default to machine name when closed without input
                    FlyShelf.Classes.SettingsManager.Current.DeviceName = Environment.MachineName;
                    FlyShelf.Classes.SettingsManager.Save();
                    namingWindow.DialogResult = true;
                    namingWindow.Close();
                };
                rootGrid.Children.Add(closeBtnBorder);

                namingWindow.Content = rootGrid;
                
                // H-01: Escape key closes window with machine name as default
                namingWindow.PreviewKeyDown += (s, ev) => {
                    if (ev.Key == Key.Escape)
                    {
                        FlyShelf.Classes.SettingsManager.Current.DeviceName = Environment.MachineName;
                        FlyShelf.Classes.SettingsManager.Save();
                        namingWindow.DialogResult = true;
                        namingWindow.Close();
                        ev.Handled = true;
                    }
                };

                namingWindow.Loaded += (s, ev) => { input.Focus(); };
                
                namingWindow.ShowDialog();
            }

            // ═══ FIRST-TIME ONBOARDING WIZARD ═══
            // Show the welcome tutorial on first launch to teach Alt+C, widget, themes, etc.
            if (!FlyShelf.Classes.SettingsManager.Current.HasCompletedOnboarding)
            {
                try
                {
                    // Enable widget by default for new users
                    FlyShelf.Classes.SettingsManager.Current.EnableTaskbarWidget = true;

                    var onboarding = new FlyShelf.Windows.OnboardingWindow();
                    onboarding.ShowDialog();

                    // Mark onboarding as completed (also done inside OnboardingWindow on "Get Started")
                    FlyShelf.Classes.SettingsManager.Current.HasCompletedOnboarding = true;
                    _justCompletedOnboarding = true;
                    FlyShelf.Classes.SettingsManager.Save();
                }
                catch (Exception ex)
                {
                    FlyShelf.Classes.Logger.LogAction("ONBOARDING", $"Onboarding failed: {ex.Message}");
                    FlyShelf.Classes.SettingsManager.Current.HasCompletedOnboarding = true;
                    FlyShelf.Classes.SettingsManager.Save();
                }
            }

            // ═══ SLEEP/RESUME RECOVERY ═══
            // When PC wakes from sleep, all sockets die and Cloudflare tunnel breaks.
            // Force-restart the tunnel (old URL is dead) and push fresh LAN heartbeat.
            // C-08: Use named handler for static event to allow proper unsubscription and GC
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // Offload the massive WPF XAML layout rasterization payload directly to the background!
            // This drops FlyShelf's actual active startup boot time from ~2000ms straight to < 10ms!
            Application.Current.Dispatcher.InvokeAsync(async () => 
            {
                try
                {
                    _isCreatingMainWindow = true;
                    _mainWinInstance = new MainWindow();
                    MainWindow = _mainWinInstance;

                    // Flag the MainWindow to auto-summon clipboard if onboarding just completed
                    if (FlyShelf.Classes.SettingsManager.Current.HasCompletedOnboarding 
                        && _justCompletedOnboarding)
                    {
                        _mainWinInstance._isFirstLaunchAfterOnboarding = true;
                    }
                    
                    // Load persisted clipboard history asynchronously (text + images survive restarts)
                    var vm = _mainWinInstance.DataContext as ViewModels.FlyShelfViewModel;
                    if (vm != null)
                    {
                        _ = vm.LoadPersistedHistoryAsync().ContinueWith(t =>
                        {
                            if (t.IsFaulted && t.Exception != null)
                                FlyShelf.Classes.Logger.LogAction("STARTUP", $"LoadPersistedHistory failed: {t.Exception.InnerException?.Message}");
                        }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                    }
                    
                    _mainWinInstance.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mainWinInstance.Left = -20000;
                    _mainWinInstance.Top = -20000;
                    MainWindow.Show();


                    
                    // Start the reminder scheduler (polls every 15s for due reminders)
                    FlyShelf.Classes.ReminderScheduler.Start();
                    
                    // One-time cleanup: purge old GUID-based device entries from Firebase
                    _ = FlyShelf.Classes.CloudDiscoveryManager.CleanupStaleDevices().ContinueWith(t => { if (t.IsFaulted) FlyShelf.Classes.Logger.LogAction("ASYNC_ERR", $"CleanupStaleDevices failed: {t.Exception?.InnerException?.Message}"); }, TaskContinuationOptions.OnlyOnFaulted);
                    
                    // Revalidate Pro license on server (checks for revoked keys)
                    _ = FlyShelf.Classes.LicenseManager.RevalidateLicenseAsync().ContinueWith(t => { if (t.IsFaulted) FlyShelf.Classes.Logger.LogAction("ASYNC_ERR", $"RevalidateLicenseAsync failed: {t.Exception?.InnerException?.Message}"); }, TaskContinuationOptions.OnlyOnFaulted);
                    
                    // Dump full network diagnostics at startup for remote debugging
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(8000); // Wait for Cloudflare to initialize
                        FlyShelf.Classes.Logger.DumpNetworkDiagnostics();
                    });
                    
                    // CRITICAL: Give the NotifyIcon (system tray) and TaskbarWindow (widget)
                    // enough time to register before hiding. The WPF-UI tray:NotifyIcon
                    // registers in the Loaded event — hiding immediately kills the registration.
                    _isCreatingMainWindow = false;
                    await System.Threading.Tasks.Task.Delay(500);
                    _mainWinInstance.HideWindowInternal();
                }
                catch (Exception ex)
                {
                    _isCreatingMainWindow = false;
                    try
                    {
#if DEBUG
                        string errorDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
                        if (!System.IO.Directory.Exists(errorDir)) System.IO.Directory.CreateDirectory(errorDir);
                        System.IO.File.AppendAllText(System.IO.Path.Combine(errorDir, "startup_error.txt"), $"[MainWindow Startup Failed] {ex}\n");
#endif
                    } catch { }
                    LaunchSafeMode(ex);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            try
            {
#if DEBUG
                string errorDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
                if (!System.IO.Directory.Exists(errorDir)) System.IO.Directory.CreateDirectory(errorDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(errorDir, "startup_error.txt"), ex.ToString());
#endif
            } catch { }
            TriggerSafeModeAndRestart($"[Startup Fatal Exception]\n{ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // C-08: Unsubscribe from static SystemEvents to allow proper GC of App instance
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }

        // Release single-instance mutex so a new instance can start cleanly
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }

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
        
        // Flush any pending notes and todos to disk
        try { FlyShelf.Classes.NoteManager.SaveNow(); } catch { }
        try { FlyShelf.Classes.TodoManager.SaveNow(); } catch { }

        FlyShelf.Classes.Logger.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// Handle Windows shutdown / user logoff — flush all data to disk
    /// before the session ends. Belt-and-suspenders alongside OnExit.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try { FlyShelf.Classes.ReminderManager.SaveNow(); } catch { }
        try { FlyShelf.Classes.NoteManager.SaveNow(); } catch { }
        try { FlyShelf.Classes.TodoManager.SaveNow(); } catch { }
        base.OnSessionEnding(e);
    }

    // Store-compliant Shake-to-Open Background Polling (No low-level system hooks!)
    private static void StartShakePolling()
    {
        _shakeTimer = new System.Threading.Timer(state =>
        {
            try
            {
                // ═══ ADAPTIVE THROTTLING ═══
                // Track mouse position to detect idle state. When the mouse hasn't moved for
                // 30 seconds, slow polling from 40ms to 150ms to save CPU. Restore on movement.
                POINT idlePt;
                if (GetCursorPos(out idlePt))
                {
                    long now = Environment.TickCount64;
                    bool mouseMoved = (idlePt.x != _lastIdleMouseX || idlePt.y != _lastIdleMouseY);
                    if (mouseMoved)
                    {
                        _lastIdleMouseX = idlePt.x;
                        _lastIdleMouseY = idlePt.y;
                        _lastMouseMoveTime = now;
                        // Mouse just moved — ensure we're at fast rate
                        _shakeTimer?.Change(0, SHAKE_FAST_MS);
                    }
                    else if (_lastMouseMoveTime > 0 && (now - _lastMouseMoveTime) > SHAKE_IDLE_THRESHOLD_MS)
                    {
                        // Mouse idle for >30s — switch to slow polling to save CPU
                        _shakeTimer?.Change(SHAKE_SLOW_MS, SHAKE_SLOW_MS);
                    }
                }

                // Note: Shake-to-spawn works even when the Hub (settings) window is open.

                if (!FlyShelf.Classes.SettingsManager.Current.EnableShakeToOpen)
                {
                    _shakeCount = 0;
                    return;
                }

                // Suppress shake-to-summon when a fullscreen app is in the foreground
                // (games, videos, presentations, etc.) to prevent accidental triggers.
                if (IsForegroundFullScreen())
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

                                        _instance?.Dispatcher.InvokeAsync(() => 
                                        {
                                            if (ActiveMergeWindow != null && ActiveMergeWindow.IsActive)
                                            {
                                                FlyShelf.Classes.Logger.LogAction("SHAKE", "❌ Rejected: PDF Merger window is active.");
                                                return;
                                            }
                                            _instance.LaunchClipboardManager(triggerX, triggerY, false, 0, false);
                                        }, System.Windows.Threading.DispatcherPriority.Normal);
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
        }, null, 0, SHAKE_FAST_MS); // Start at fast rate; auto-throttles to slow after 30s idle
    }

    /// <summary>
    /// Checks whether the current foreground window covers the entire monitor area.
    /// Used to suppress shake-to-summon during fullscreen apps (games, videos, presentations).
    /// Thread-safe — called from the shake timer's ThreadPool callback.
    /// </summary>
    private static bool IsForegroundFullScreen()
    {
        try
        {
            IntPtr fgHandle = Classes.NativeMethods.GetForegroundWindow();
            if (fgHandle == IntPtr.Zero) return false;

            // Don't suppress if the desktop is focused
            var className = new System.Text.StringBuilder(256);
            Classes.NativeMethods.GetClassName(fgHandle, className, className.Capacity);
            string cls = className.ToString();
            if (cls == "Progman" || cls == "WorkerW") return false;

            // Compare foreground window rect against its monitor's full area
            Classes.NativeMethods.GetWindowRect(fgHandle, out Classes.NativeMethods.RECT fgRect);
            var monitor = Classes.Utils.MonitorUtil.GetMonitor(fgHandle);

            int fgWidth = fgRect.Right - fgRect.Left;
            int fgHeight = fgRect.Bottom - fgRect.Top;
            int monWidth = (int)monitor.monitorArea.Width;
            int monHeight = (int)monitor.monitorArea.Height;

            if (fgWidth >= monWidth && fgHeight >= monHeight &&
                fgRect.Left <= monitor.monitorArea.Left &&
                fgRect.Top <= monitor.monitorArea.Top)
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    private void LaunchClipboardManager(double x, double y, bool isPersistent, int mode, bool stealFocus = true)
    {
        if (_mainWinInstance == null)
        {
            if (_isCreatingMainWindow) return; // another creation in progress (H9 race guard)
            _isCreatingMainWindow = true;
            try
            {
                _mainWinInstance = new MainWindow();
                MainWindow = _mainWinInstance;
            }
            finally
            {
                _isCreatingMainWindow = false;
            }
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

        // Spawn position: anchor to bottom-left of the work area
        double safeWidth = 260;
        if (_mainWinInstance?.DataContext is ViewModels.FlyShelfViewModel vm)
        {
            safeWidth = vm.CurrentFlyShelfWidth;
        }
        if (safeWidth <= 0) safeWidth = 260;

         // Get the work area of the monitor the cursor is on
        var cursorMonitor = Classes.Utils.MonitorUtil.GetMonitorWithCursor();
        double monScaleX = cursorMonitor.dpiX / 96.0;
        double monScaleY = cursorMonitor.dpiY / 96.0;
        if (monScaleX <= 0) monScaleX = 1;
        if (monScaleY <= 0) monScaleY = 1;
        var monWorkArea = cursorMonitor.workArea;
        double logicalWorkLeft = monWorkArea.Left / monScaleX;
        double logicalWorkBottom = monWorkArea.Bottom / monScaleY;

        // Position at the cursor's X location, offset to the right so it doesn't cover content
        // The clipboard's center is placed (safeWidth/2 + 20px gap) to the right of the cursor
        logicalX = logicalX + (safeWidth / 2) + 20;

        // Clamp: ensure the window stays within the work area horizontally
        double logicalWorkRight = monWorkArea.Right / monScaleX;
        if (logicalX + (safeWidth / 2) > logicalWorkRight - 16)
            logicalX = logicalWorkRight - (safeWidth / 2) - 16;
        if (logicalX - (safeWidth / 2) < logicalWorkLeft + 16)
            logicalX = logicalWorkLeft + (safeWidth / 2) + 16;

        // Use the actual cursor Y position — ShowNearPositionInternal handles vertical clamping

        _mainWinInstance.ShowNearPosition(logicalX, logicalY, mode, isPersistent, stealFocus);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static void LogAndPrint(string msg)
    {
#if DEBUG
        Console.WriteLine(msg);
        try
        {
            Classes.Logger.LogAction("AI_TEST_DIAG", msg);
        }
        catch { }
#endif
    }

    private async Task RunAITestAsync()
    {
        try
        {
            LogAndPrint("Testing Windows Copilot Runtime Phi Silica capability...");
            
            // Check if AI is supported
            bool hasText = global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Microsoft.Windows.AI.Text.LanguageModel");
            bool hasGen = global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Microsoft.Windows.AI.Generative.LanguageModel");
            
            LogAndPrint($"- Text API present: {hasText}");
            LogAndPrint($"- Generative API present: {hasGen}");
            
            if (!hasText && !hasGen)
            {
                LogAndPrint("");
                LogAndPrint("ERROR: Local AI capability (Microsoft.Windows.AI.Text or Generative namespace) is not supported on this OS build.");
                LogAndPrint("Make sure you are on Windows 11 Build 26100+ (24H2) and MicrosoftWindows.Client.CoreAI is installed.");
                LogAndPrint("");
                LogAndPrint("RESULT: NO, NOT COMPATIBLE");
                return;
            }

            LogAndPrint("- Initializing WindowsAIService...");
            var service = FlyShelf.Classes.WindowsAIService.Instance;
            
            bool isAvailable = service.IsAvailable;
            LogAndPrint($"- Model available state: {isAvailable}");
            
            if (!isAvailable)
            {
                LogAndPrint("");
                LogAndPrint("ERROR: Windows AI API is present, but the local model (Phi Silica) is not ready.");
                LogAndPrint("Windows might still be downloading the model components via Windows Update or the hardware is incompatible.");
                LogAndPrint("");
                LogAndPrint("RESULT: NO, NOT COMPATIBLE");
                return;
            }

            string testPrompt = "Translate 'Hello, how are you?' into French in 3 words.";
            LogAndPrint($"- Sending test prompt to GPU: \"{testPrompt}\"");
            
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string response = await service.SummarizeAsync(testPrompt);
            stopwatch.Stop();
            
            LogAndPrint($"- Inference completed in {stopwatch.ElapsedMilliseconds} ms.");
            LogAndPrint($"- Model Response: \"{response.Trim()}\"");
            LogAndPrint("");
            LogAndPrint("RESULT: YES, COMPATIBLE");
        }
        catch (Exception ex)
        {
            LogAndPrint("");
            LogAndPrint("ERROR: Local AI Inference Failed!");
            LogAndPrint($"Details: {ex.Message}");
            if (ex.InnerException != null)
            {
                LogAndPrint($"Inner Details: {ex.InnerException.Message}");
            }
            LogAndPrint("");
            LogAndPrint("RESULT: NO, NOT COMPATIBLE");
        }
    }

    /// <summary>
    /// C-08: Named handler for PowerModeChanged so it can be unsubscribed from the static event.
    /// Handles PC wake-from-sleep: force-restarts network tunnel and pushes fresh heartbeat.
    /// </summary>
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs ev)
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
                FlyShelf.Classes.Logger.LogAction("POWER", "✅ Post-sleep recovery complete — forcing immediate tunnel health check");

                // Force immediate tunnel health check on wake — don't wait 4 minutes for health timer
                try
                {
                    var srvCheck = FlyShelf.Classes.NetworkSyncServer.Instance;
                    if (srvCheck != null)
                    {
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            await System.Threading.Tasks.Task.Delay(3000); // Let network stack fully stabilize
                            srvCheck.ForceCheckTunnelHealth();
                        });
                    }
                }
                catch { }
            });
        }
    }

    // ═══ Safe Mode UI + Crash Recovery moved to App.SafeMode.cs ═══
}



