// ---------------------------------------------------------------
// MainWindow — WndProc Hook: Hotkeys, Clipboard Monitoring & System Settings
// HwndHook handler for WM_HOTKEY (Alt+C, Alt+1-0), WM_CLIPBOARDUPDATE,
// and WM_SETTINGCHANGE messages.
// Split from MainWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private int _clipboardUpdateToken;
        private DateTime _lastClipboardCaptureTime = DateTime.MinValue;
        private DateTime _lastHotkeyTime = DateTime.MinValue;
        private bool _waitingForHotkeyRelease = false;



        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCACTIVATE = 0x0086;
            const int WM_ACTIVATE = 0x0006;
            const int WM_NCPAINT = 0x0085;
            if (msg == WM_NCACTIVATE || msg == WM_ACTIVATE || msg == WM_NCPAINT)
            {
                try
                {
                    int cn = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                }
                catch { }

                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        int cn = DWMWA_COLOR_NONE;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Send);
            }

            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;

            if (msg == WM_MOUSEACTIVATE)
            {
                // If Notes or Todo mode is active, allow normal activation so text boxes receive focus and keyboard input
                if (_isNotesActive || _isTodoActive)
                {
                    return IntPtr.Zero;
                }

                // In standard clipboard mode, we want to prevent activation when clicking on the list items, buttons, or scrollbars
                // to avoid stealing focus from the target text editor.
                // However, if the user explicitly clicks on the SearchTextBox or SearchBarContainer, we must allow activation
                // so they can type search queries.
                try
                {
                    // Convert screen cursor position to client/WPF coordinates
                    Classes.NativeMethods.POINT mousePos;
                    if (Classes.NativeMethods.GetCursorPos(out mousePos))
                    {
                        // Convert screen point to logical WPF point relative to this window
                        Point wpfPoint = this.PointFromScreen(new Point(mousePos.X, mousePos.Y));

                        // Hit test the WPF visual tree
                        var hitResult = VisualTreeHelper.HitTest(this, wpfPoint);
                        if (hitResult != null && hitResult.VisualHit != null)
                        {
                            DependencyObject? current = hitResult.VisualHit;
                            bool isInputControl = false;
                            while (current != null)
                            {
                                if (current is TextBox || current is System.Windows.Controls.Primitives.TextBoxBase)
                                {
                                    isInputControl = true;
                                    break;
                                }
                                if (current is FrameworkElement fe && (fe.Name == "SearchTextBox" || fe.Name == "AltSearchTextBox" || fe.Name == "SearchBarContainer"))
                                {
                                    isInputControl = true;
                                    break;
                                }
                                current = VisualTreeHelper.GetParent(current);
                            }

                            if (isInputControl)
                            {
                                // User clicked a search input, let it activate normally
                                SuppressDwmBorder();
                                this.Activate();
                                return IntPtr.Zero;
                            }
                        }
                    }
                }
                catch { }

                // Otherwise, do not activate this window, but still process the mouse click
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                if (hotkeyId == HOTKEY_ID)
                {
                    Classes.Logger.LogAction("TELEMETRY", "Hotkey Alt+C received inside WndProc");
                    // Defer spawn out of WndProc — Background priority ensures WndProc fully
                    // returns before toggle runs (Input priority fires inside the message loop,
                    // causing "Dispatcher processing has been suspended" crash).
                    Dispatcher.InvokeAsync(() => ToggleMainClipboard(), System.Windows.Threading.DispatcherPriority.Background);
                    handled = true;
                }
                else if (hotkeyId >= HOTKEY_QUICKPASTE_BASE + 1 && hotkeyId <= HOTKEY_QUICKPASTE_BASE + 10)
                {
                    // Alt+1=item0, Alt+2=item1, ..., Alt+9=item8, Alt+0=item9
                    int index = hotkeyId == HOTKEY_QUICKPASTE_BASE + 10 ? 9 : (hotkeyId - HOTKEY_QUICKPASTE_BASE - 1);
                    // CRITICAL: Defer clipboard + focus work out of WndProc to avoid dispatcher suspension crash
                    Dispatcher.InvokeAsync(() =>
                    {
                        Classes.Logger.LogAction("HOTKEY", $"Alt+{(index + 1) % 10} fired, items={_viewModel.DroppedItems.Count}");
                        if (index < _viewModel.DroppedItems.Count)
                        {
                            // Capture the target window — filter out our own window
                            IntPtr targetWindow = GetTargetForegroundWindow();
                            Classes.Logger.LogAction("HOTKEY", $"Target window: 0x{targetWindow:X}");
                            var item = _viewModel.DroppedItems[index];
                            
                            // Set clipboard directly — guard against echo
                            if (!string.IsNullOrEmpty(item.RawContent))
                            {
                                Classes.ClipboardHelper.SafeSetText(item.RawContent, suppressEcho: true, echoDelayMs: 600);
                            }
                            else if (!string.IsNullOrEmpty(item.FilePath))
                            {
                                var dropList = new System.Collections.Specialized.StringCollection();
                                dropList.Add(item.FilePath);
                                Classes.ClipboardHelper.SafeSetFileDropList(dropList, suppressEcho: true, echoDelayMs: 600);
                            }

                            // Force-restore focus using AttachThreadInput trick
                            uint targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
                            uint ourThreadId = GetCurrentThreadId();
                            if (targetThreadId != ourThreadId)
                                AttachThreadInput(ourThreadId, targetThreadId, true);
                            
                            SetForegroundWindow(targetWindow);
                            
                            if (targetThreadId != ourThreadId)
                                AttachThreadInput(ourThreadId, targetThreadId, false);

                            // Release Alt key FIRST — user is still holding it from Alt+N,
                            // otherwise the target app receives Alt+Ctrl+V instead of Ctrl+V
                            keybd_event((byte)VK_MENU, 0, KEYEVENTF_KEYUP, 0);

                            // Fire Ctrl+V after a short async pause for key state to propagate
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(50);
                                keybd_event((byte)VK_CONTROL, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, 0);
                                keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                            });
                        }
                    });
                    handled = true;
                }
            }
            else if (msg == Classes.NativeMethods.WM_SETTINGCHANGE)
            {
                _cachedDesktopWallpaperPath = null;

                // Reposition the taskbar widget immediately when settings (like taskbar auto-hide) change
                try
                {
                    _taskbarWidget?.ForceReposition();
                }
                catch { }

                // Only re-apply desktop wallpaper if we're in FlyShelf mode AND no manual wallpaper is set
                if ((Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica") == "desktop")
                {
                    string manualWp = Classes.SettingsManager.Current.ManualWallpaperPath ?? "";
                    if (string.IsNullOrEmpty(manualWp) || !System.IO.File.Exists(manualWp))
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                string desktopWp = GetDesktopWallpaperPath();
                                if (!string.IsNullOrEmpty(desktopWp) && System.IO.File.Exists(desktopWp))
                                {
                                    Classes.SettingsManager.Current.ClipboardWallpaperPath = desktopWp;
                                    _currentLoadedWallpaperPath = ""; // Force reload
                                    ApplyWallpaper();
                                }
                            }
                            catch { }
                        });
                    }
                }
            }
            else if (msg == WM_CLIPBOARDUPDATE)
            {
                // GUARD: Skip clipboard events triggered by our own writes
                if (_isWritingClipboard || _clipboardPanelSuppressed)
                {
                    handled = true;
                    return IntPtr.Zero;
                }

                int currentToken = ++_clipboardUpdateToken;
                
                // Defer clipboard update handling entirely to the thread pool,
                // freeing the WndProc message pump immediately and avoiding dispatcher suspension crash
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100); // Debounce — 100ms to coalesce Windows double-fire
                    
                    if (currentToken != _clipboardUpdateToken)
                        return; // A newer update has arrived, cancel this one

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (currentToken == _clipboardUpdateToken)
                        {
                            HandleClipboardUpdateDeferred();
                        }
                    });
                });
                
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void HandleClipboardUpdateDeferred()
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastClipboardCaptureTime).TotalMilliseconds < 150)
                {
                    Classes.Logger.LogAction("CLIPBOARD", "Skipped clipboard update: Cooldown (150ms) active.");
                    return;
                }

                // PERF: Clipboard.GetDataObject() is a COM call that MUST run on the STA UI thread.
                // Extract the MINIMUM data here, then offload ALL processing to a background thread.
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return;

                // Snapshot all data now while we're on the STA thread — IDataObject can't cross threads
                string[] files = null;
                string text = null;
                System.Windows.Media.Imaging.BitmapSource bitmap = null;

                // STEP 1: Try bitmap extraction (lightweight — just a COM query)
                try
                {
                    if (data.GetDataPresent(DataFormats.Bitmap))
                    {
                        bitmap = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                    }
                    if (bitmap == null && data.GetDataPresent(typeof(System.Windows.Media.Imaging.BitmapSource)))
                    {
                        bitmap = data.GetData(typeof(System.Windows.Media.Imaging.BitmapSource)) as System.Windows.Media.Imaging.BitmapSource;
                    }
                    if (bitmap == null && data.GetDataPresent(DataFormats.Dib))
                    {
                        bitmap = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                    }
                    if (bitmap != null && bitmap.CanFreeze) bitmap.Freeze(); // Make thread-safe
                }
                catch (Exception bmpEx) 
                { 
                    Classes.Logger.LogAction("CLIPBOARD", $"Bitmap extraction failed: {bmpEx.Message}");
                }

                // STEP 2: Extract file paths
                try
                {
                    if (data.GetDataPresent(DataFormats.FileDrop))
                        files = data.GetData(DataFormats.FileDrop) as string[];
                    if ((files == null || files.Length == 0) && data.GetDataPresent("FileNameW"))
                        files = data.GetData("FileNameW") as string[];
                }
                catch { }

                // STEP 3: Extract text only if no bitmap and no files
                if (bitmap == null && (files == null || files.Length == 0))
                {
                    try
                    {
                        if (data.GetDataPresent(DataFormats.UnicodeText))
                            text = data.GetData(DataFormats.UnicodeText) as string;
                        if (string.IsNullOrEmpty(text) && data.GetDataPresent(DataFormats.Text))
                            text = data.GetData(DataFormats.Text) as string;
                    }
                    catch { }
                }

                // ═══ SHORTCUT EXPANSION: Intercept /trigger text before normal processing ═══
                if (!string.IsNullOrWhiteSpace(text) && text.Trim().StartsWith("/"))
                {
                    var matchedShortcut = Classes.ShortcutManager.TryExpand(text);
                    if (matchedShortcut != null)
                    {
                        Classes.Logger.LogAction("SHORTCUTS", $"Expanding '{matchedShortcut.Trigger}' → '{matchedShortcut.Label}'");
                        
                        // Premium Safeguard: Detect target window integrity/elevation
                        IntPtr targetWindow = GetForegroundWindow();
                        bool isElevated = IsTargetProcessElevatedOrAccessDenied(targetWindow);
                        Classes.Logger.LogAction("SHORTCUTS", $"Target window elevated: {isElevated}");

                        Classes.ClipboardHelper.SafeSetText(matchedShortcut.Expansion, suppressEcho: true, echoDelayMs: 500);

                        _lastClipboardCaptureTime = DateTime.UtcNow;

                        if (isElevated)
                        {
                            // Premium Fallback: Display manual paste instructions if auto-paste is blocked by UIPI
                            Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                Windows.ToastWindow.ShowToast($"✦ {matchedShortcut.Label} Copied! (Manual Ctrl+V needed in Admin app)");
                            });
                        }
                        else
                        {
                            // Standard: Auto-paste: simulate Ctrl+V after a brief delay so the clipboard is ready.
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(80);
                                keybd_event((byte)VK_CONTROL, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, 0);
                                keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                            });

                            Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                Windows.ToastWindow.ShowToast($"✦ {matchedShortcut.Label} Auto-Pasted! ✦");
                            });
                        }
                        
                        return; // Don't create a clipboard card for the trigger text
                    }
                }

                // ═══ PERF: ALL heavy processing moves to background thread ═══
                // No more COM calls needed — dispatch pre-extracted data directly
                var vm = (FlyShelfViewModel)DataContext;

                if (bitmap != null && (files == null || files.Length == 0))
                {
                    // STEP 3.5: bitmap+files disambiguation (kept on UI thread — very fast)
                    // No-op: files is already null/empty
                }
                else if (bitmap != null && files != null && files.Length > 0)
                {
                    // If we have BOTH bitmap AND files, decide which to use
                    bool allFilesExist = files.All(f => System.IO.File.Exists(f));
                    if (!allFilesExist)
                    {
                        files = null; // Snipping Tool — file doesn't exist yet
                    }
                    else
                    {
                        string ext = System.IO.Path.GetExtension(files[0]).ToLower();
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp")
                        {
                            files = null; // Image file — prefer bitmap for richer preview
                        }
                        else
                        {
                            bitmap = null; // Non-image files — use FileDrop path
                        }
                    }
                }

                // ═══ PERF: Route directly to HandleDropInternal on background thread ═══
                // Bypasses HandleDrop() which would re-extract data from IDataObject (redundant COM calls)
                if (bitmap != null && (files == null || files.Length == 0))
                {
                    _lastClipboardCaptureTime = DateTime.UtcNow;
                    var capturedBitmap = bitmap;
                    _ = Task.Run(() =>
                    {
                        Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as BITMAP ({capturedBitmap.PixelWidth}x{capturedBitmap.PixelHeight})");
                        vm.HandleDropInternal(null, capturedBitmap, null, false, false);
                    });
                }
                else if (files != null && files.Length > 0)
                {
                    _lastClipboardCaptureTime = DateTime.UtcNow;
                    Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as FILES ({files.Length} items)");
                    var capturedFiles = files;
                    _ = Task.Run(() => vm.HandleDropInternal(capturedFiles, null, null, false, false));
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    // ═══ ONE-CLICK ACTIVATE: Detect activation trigger from website ═══
                    // The FlyShelf website copies "FLYSHELF_ACTIVATE::FS-PRO-XXXX-XXXX-XXXX-XXXX"
                    // to clipboard when the user clicks "One-Click Activate PC App".
                    const string ACTIVATION_PREFIX = "FLYSHELF_ACTIVATE::";
                    if (text.Trim().StartsWith(ACTIVATION_PREFIX, StringComparison.OrdinalIgnoreCase))
                    {
                        string keyCandidate = text.Trim().Substring(ACTIVATION_PREFIX.Length).Trim();
                        Classes.Logger.LogAction("LICENSE", $"Clipboard activation trigger detected: {keyCandidate.Substring(0, Math.Min(12, keyCandidate.Length))}...");
                        
                        // Clear the trigger from clipboard so it doesn't re-fire
                        _lastClipboardCaptureTime = DateTime.UtcNow;

                        // If already Pro, just copy the plain key — no need to re-activate
                        if (Classes.LicenseManager.IsPro)
                        {
                            try { Clipboard.SetText(keyCandidate); } catch { }
                            Classes.Logger.LogAction("LICENSE", "Already Pro — copied plain key to clipboard");
                            return;
                        }

                        try { Clipboard.Clear(); } catch { }

                        // Validate format before attempting activation
                        if (keyCandidate.StartsWith("FS-PRO-", StringComparison.OrdinalIgnoreCase) && keyCandidate.Length >= 23)
                        {
                            // Auto-activate on background thread
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                                        Windows.ToastWindow.ShowToast("⚡ Activating your Pro license..."));

                                    bool success = await Classes.LicenseManager.ActivateLicenseAsync(keyCandidate);
                                    
                                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                                    {
                                        if (success)
                                        {
                                            Windows.ToastWindow.ShowToast("✅ FlyShelf Pro Activated! Restart to apply.");
                                            Classes.Logger.LogAction("LICENSE", "One-click activation SUCCESS");
                                        }
                                        else
                                        {
                                            Windows.ToastWindow.ShowToast("❌ Activation failed — check your key or internet.");
                                            Classes.Logger.LogAction("LICENSE", "One-click activation FAILED");
                                        }
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Classes.Logger.LogAction("LICENSE", $"One-click activation error: {ex.Message}");
                                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                                        Windows.ToastWindow.ShowToast("❌ Activation error — please try again."));
                                }
                            });
                        }
                        return; // Don't create a clipboard card for the activation trigger
                    }

                    _lastClipboardCaptureTime = DateTime.UtcNow;
                    Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as TEXT ({text.Length} chars)");
                    var capturedText = text;
                    _ = Task.Run(() => vm.HandleDropInternal(null, null, capturedText, false, false));
                }
                else
                {
                    Classes.Logger.LogAction("CLIPBOARD", "→ No actionable data found on clipboard");
                }
            }
            catch (Exception cbEx) { Classes.Logger.LogAction("CLIPBOARD", $"Deferred handler error: {cbEx.Message}"); }
        }

        // ═══════════════════════════════════════════════════════
        // TARGET ELEVATION DETECTION (UIPI SAFEGUARDS)
        // ═══════════════════════════════════════════════════════

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr TokenHandle,
            int TokenInformationClass,
            IntPtr TokenInformation,
            int TokenInformationLength,
            out int ReturnLength);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION
        {
            public uint TokenIsElevated;
        }

        private bool IsTargetProcessElevatedOrAccessDenied(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            
            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId == 0) return false;

            // Step 1: Try to open process. If access is denied (error 5), it is elevated/higher integrity
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 5) // ERROR_ACCESS_DENIED
                {
                    return true;
                }
                return false;
            }

            // Step 2: Open process token to check elevation explicitly
            IntPtr hToken = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(hProcess, TOKEN_QUERY, out hToken))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 5) return true; // Access Denied
                }
                else
                {
                    TOKEN_ELEVATION elevationType;
                    int size = Marshal.SizeOf<TOKEN_ELEVATION>();
                    IntPtr pElevationType = Marshal.AllocHGlobal(size);
                    try
                    {
                        // 20 is TokenElevation in the enum
                        if (GetTokenInformation(hToken, 20, pElevationType, size, out _))
                        {
                            elevationType = Marshal.PtrToStructure<TOKEN_ELEVATION>(pElevationType);
                            return elevationType.TokenIsElevated != 0;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pElevationType);
                    }
                }
            }
            catch { }
            finally
            {
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
                CloseHandle(hProcess);
            }

            return false;
        }
    }
}
