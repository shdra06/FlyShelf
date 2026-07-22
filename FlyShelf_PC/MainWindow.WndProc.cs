// ---------------------------------------------------------------
// MainWindow — WndProc Hook: Hotkeys, Clipboard Monitoring & System Settings
// HwndHook handler for WM_HOTKEY (Alt+C, Alt+1-0), WM_CLIPBOARDUPDATE,
// and WM_SETTINGCHANGE messages.
// Split from MainWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using FlyShelf.Classes;
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
        private static readonly System.Threading.SemaphoreSlim _clipboardStaSemaphore = new System.Threading.SemaphoreSlim(1, 1);
        private DateTime _lastClipboardCaptureTime = DateTime.MinValue;
        private DateTime _lastHotkeyTime = DateTime.MinValue;
        private bool _waitingForHotkeyRelease = false;

        // ═══ Source App Icon Cache: keyed by process name → frozen BitmapSource ═══
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Windows.Media.Imaging.BitmapSource?> _sourceAppIconCache = new();



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
                catch { } // Best-effort: failure is acceptable

                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        int cn = DWMWA_COLOR_NONE;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                    catch { } // Best-effort: failure is acceptable
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
                    if (!_isStartupReady)
                    {
                        Classes.Logger.LogAction("HOTKEY", "Summon hotkey ignored — app still initializing");
                        handled = true;
                        return IntPtr.Zero;
                    }
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
                    Dispatcher.InvokeAsync(async () =>
                    {
                        Classes.Logger.LogAction("HOTKEY", $"Alt+{(index + 1) % 10} fired, items={_viewModel.DroppedItems.Count}");
                        if (index < _viewModel.DroppedItems.Count)
                        {
                            // Capture the target window — filter out our own window
                            IntPtr targetWindow = GetTargetForegroundWindow();
                            Classes.Logger.LogAction("HOTKEY", $"Target window: 0x{targetWindow:X}");
                            var item = _viewModel.DroppedItems[index];
                            
                            // Wait for async PNG save if Image item has no FilePath yet
                            if (item.ItemType == ClipboardItemType.Image && string.IsNullOrEmpty(item.FilePath))
                            {
                                for (int w = 0; w < 15 && string.IsNullOrEmpty(item.FilePath); w++)
                                    await System.Threading.Tasks.Task.Delay(100); // Up to 1.5s
                            }
                            
                            // ═══ FIX: Image items should paste as IMAGE, not OCR text ═══
                            // Previously, RawContent (which OCR fills with extracted text) was
                            // checked first, so Alt+N on an image always pasted OCR text.
                            // Now: Image items get a rich DataObject (FileDrop + Bitmap + text)
                            // so the target app can choose the best format.
                            if (item.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(item.FilePath))
                            {
                                try
                                {
                                    var dataObj = new DataObject();
                                    var dropList = new System.Collections.Specialized.StringCollection();
                                    dropList.Add(item.FilePath);
                                    dataObj.SetFileDropList(dropList);
                                    dataObj.SetData("FileNameW", new string[] { item.FilePath });
                                    dataObj.SetData("FileName", new string[] { item.FilePath });
                                    
                                    // Load bitmap from file (capped at 1024px to keep it fast)
                                    try
                                    {
                                        var bmp = await System.Threading.Tasks.Task.Run(() =>
                                        {
                                            var bytes = System.IO.File.ReadAllBytes(item.FilePath);
                                            var bi = new System.Windows.Media.Imaging.BitmapImage();
                                            bi.BeginInit();
                                            using var ms = new System.IO.MemoryStream(bytes);
                                            bi.StreamSource = ms;
                                            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                            bi.DecodePixelWidth = 1024;
                                            bi.EndInit();
                                            bi.Freeze();
                                            return bi;
                                        });
                                        dataObj.SetImage(bmp);
                                    }
                                    catch (Exception imgEx) { Classes.Logger.LogAction("HOTKEY_IMG", imgEx.Message); }
                                    
                                    // Also provide text for text-only targets (OCR text or file path)
                                    string textContent = !string.IsNullOrEmpty(item.RawContent) ? item.RawContent : item.FilePath;
                                    dataObj.SetData(DataFormats.UnicodeText, textContent);
                                    
                                    Classes.ClipboardHelper.SafeSetDataObject(dataObj, true, suppressEcho: true, echoDelayMs: 600);
                                }
                                catch (Exception ex)
                                {
                                    // Fallback: file drop list if DataObject creation fails
                                    var dropList = new System.Collections.Specialized.StringCollection();
                                    dropList.Add(item.FilePath);
                                    Classes.ClipboardHelper.SafeSetFileDropList(dropList, suppressEcho: true, echoDelayMs: 600);
                                }
                            }
                            else if (!string.IsNullOrEmpty(item.RawContent))
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
                            }).ContinueWith(t => { if (t.IsFaulted) Classes.Logger.LogAction("ASYNC_ERR", $"WndProc task failed: {t.Exception?.InnerException?.Message}"); }, TaskContinuationOptions.OnlyOnFaulted);
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

                // Auto-refresh desktop wallpaper if it changed (uses unified refresh method)
                Dispatcher.InvokeAsync(() => RefreshDesktopWallpaperIfChanged());
            }
            else if (msg == WM_CLIPBOARDUPDATE)
            {
                // GUARD: Skip clipboard events triggered by our own writes
                if (_isWritingClipboard || _isDragging || _clipboardPanelSuppressed || Classes.IncognitoManager.IsIncognito)
                {
                    handled = true;
                    return IntPtr.Zero;
                }

                int currentToken = System.Threading.Interlocked.Increment(ref _clipboardUpdateToken);
                
                // Defer clipboard update handling entirely to the thread pool,
                // freeing the WndProc message pump immediately and avoiding dispatcher suspension crash
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50); // Debounce — 50ms to coalesce Windows double-fire (was 100ms, reduced for rapid screenshots)
                    
                    if (currentToken != System.Threading.Volatile.Read(ref _clipboardUpdateToken))
                        return; // A newer update has arrived, cancel this one

                    // ═══ PERF FIX: Run clipboard COM reads on a DEDICATED STA THREAD ═══
                    // Clipboard.GetDataObject() and data.GetData() are OLE COM calls that hold
                    // the global Windows clipboard lock. Running them on the main UI thread
                    // freezes the entire UI and blocks ALL other apps from pasting.
                    // By using a dedicated STA thread, we release the clipboard lock faster
                    // and keep the main UI thread completely free.
                    // Gate: only one STA thread at a time — prevents unbounded thread creation
                    if (!await _clipboardStaSemaphore.WaitAsync(0)) return;
                    try
                    {
                    var staThread = new System.Threading.Thread(() =>
                    {
                        // STABILITY: Re-check drag state — a drag may have started during the 50ms debounce
                        if (_isDragging)
                        {
                            Classes.Logger.LogAction("CLIPBOARD", "Skipped clipboard update: Drag in progress (post-debounce).");
                            return;
                        }
                        if (currentToken == System.Threading.Volatile.Read(ref _clipboardUpdateToken))
                        {
                            HandleClipboardUpdateOnStaThread(currentToken);
                        }
                    });
                    staThread.SetApartmentState(System.Threading.ApartmentState.STA);
                    staThread.IsBackground = true;
                    staThread.Priority = System.Threading.ThreadPriority.AboveNormal;
                    staThread.Start();
                    staThread.Join(); // Wait for STA thread to complete before releasing semaphore
                    }
                    finally
                    {
                        _clipboardStaSemaphore.Release();
                    }
                });
                
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// PERF FIX: Phase 1 — runs on a dedicated STA background thread (NOT the main UI thread).
        /// Extracts clipboard data via OLE COM calls. This holds the global clipboard lock but
        /// does NOT block the main UI thread, so the app and other programs remain responsive.
        /// After extraction, dispatches the pre-extracted data to HandleClipboardUpdateDeferred()
        /// on the UI thread for lightweight routing (dedup, shortcut expansion, background dispatch).
        /// </summary>
        private void HandleClipboardUpdateOnStaThread(int currentToken)
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastClipboardCaptureTime).TotalMilliseconds < 80)
                {
                    Classes.Logger.LogAction("CLIPBOARD", "Skipped clipboard update: Cooldown (80ms) active.");
                    return;
                }

                // ═══ SOURCE APP TRACKING: Capture on this thread (P/Invoke is thread-safe) ═══
                IntPtr capturedFgWindow = IntPtr.Zero;
                uint capturedProcessId = 0;
                try
                {
                    capturedFgWindow = GetForegroundWindow();
                    if (capturedFgWindow != IntPtr.Zero)
                        GetWindowThreadProcessId(capturedFgWindow, out capturedProcessId);
                }
                catch { }

                // ═══ CLIPBOARD COM READS (the expensive part — runs here, NOT on UI thread) ═══
                IDataObject data;
                try
                {
                    data = Clipboard.GetDataObject();
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("CLIPBOARD", $"GetDataObject failed on STA thread: {ex.Message}");
                    return;
                }
                if (data == null) return;

                string[] files = null;
                string text = null;
                System.Windows.Media.Imaging.BitmapSource bitmap = null;

                // STEP 1: Bitmap extraction
                try
                {
                    if (data.GetDataPresent(DataFormats.Bitmap))
                        bitmap = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                    if (bitmap == null && data.GetDataPresent(typeof(System.Windows.Media.Imaging.BitmapSource)))
                        bitmap = data.GetData(typeof(System.Windows.Media.Imaging.BitmapSource)) as System.Windows.Media.Imaging.BitmapSource;
                    if (bitmap == null && data.GetDataPresent(DataFormats.Dib))
                        bitmap = data.GetData(DataFormats.Dib) as System.Windows.Media.Imaging.BitmapSource;
                    if (bitmap != null && bitmap.CanFreeze) bitmap.Freeze(); // Make thread-safe
                }
                catch (Exception bmpEx)
                {
                    Classes.Logger.LogAction("CLIPBOARD", $"Bitmap extraction failed: {bmpEx.Message}");
                }

                // STEP 2: File paths
                try
                {
                    if (data.GetDataPresent(DataFormats.FileDrop))
                        files = data.GetData(DataFormats.FileDrop) as string[];
                    if ((files == null || files.Length == 0) && data.GetDataPresent("FileNameW"))
                        files = data.GetData("FileNameW") as string[];
                }
                catch { }

                // STEP 3: Text (only if no bitmap and no files)
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

                // STEP 4: Check FlyShelf internal format
                bool isFlyShelfInternal = false;
                try { isFlyShelfInternal = data.GetDataPresent(Classes.ClipboardHelper.FLYSHELF_INTERNAL_FORMAT); } catch { }

                // ═══ COM READS DONE — clipboard lock is released ═══
                // Stale check before dispatching
                if (currentToken != System.Threading.Volatile.Read(ref _clipboardUpdateToken))
                    return;

                // Dispatch pre-extracted data to UI thread for lightweight routing
                // (dedup check, shortcut expansion, background dispatch — NO more COM calls)
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    // STABILITY: Final drag guard — a drag may have started while the STA thread
                    // was reading clipboard data. Inserting items during DoDragDrop's nested
                    // message loop shifts indices and can crash the ListView.
                    if (_isDragging)
                    {
                        Classes.Logger.LogAction("CLIPBOARD", "Skipped clipboard routing: Drag in progress (UI dispatch).");
                        return;
                    }
                    if (currentToken == System.Threading.Volatile.Read(ref _clipboardUpdateToken))
                    {
                        HandleClipboardUpdateRouting(bitmap, files, text, isFlyShelfInternal, capturedFgWindow, capturedProcessId);
                    }
                });
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("CLIPBOARD", $"STA thread handler error: {ex.Message}");
            }
        }

        /// <summary>
        /// Phase 2 — runs on UI thread but does NO clipboard COM calls (data already extracted).
        /// Handles dedup checking, shortcut expansion, and dispatching to background processing.
        /// </summary>
        private void HandleClipboardUpdateRouting(
            System.Windows.Media.Imaging.BitmapSource bitmap,
            string[] files,
            string text,
            bool isFlyShelfInternal,
            IntPtr capturedFgWindow,
            uint capturedProcessId)
        {
            try
            {
                // ═══ FLYSHELF INTERNAL DEDUP CHECK ═══
                if (isFlyShelfInternal && !string.IsNullOrEmpty(text))
                {
                    var vm2 = DataContext as FlyShelfViewModel;
                    if (vm2 != null)
                    {
                        var recentItems = vm2.DroppedItems.Take(20);
                        foreach (var existing in recentItems)
                        {
                            string existingContent = existing.RawContent ?? existing.FileName ?? "";
                            if (string.Equals(existingContent.Trim(), text.Trim(), StringComparison.Ordinal))
                            {
                                Classes.Logger.LogAction("CLIPBOARD", "→ Skipped: FlyShelf internal copy is duplicate of recent item");
                                return;
                            }
                        }
                        Classes.Logger.LogAction("CLIPBOARD", "→ FlyShelf internal copy is NEW content — allowing capture");
                    }
                }

                // ═══ SHORTCUT EXPANSION ═══
                if (!string.IsNullOrWhiteSpace(text) && text.Trim().StartsWith('/'))
                {
                    var matchedShortcut = Classes.ShortcutManager.TryExpand(text);
                    if (matchedShortcut != null)
                    {
                        Classes.Logger.LogAction("SHORTCUTS", $"Expanding '{matchedShortcut.Trigger}' → '{matchedShortcut.Label}'");
                        IntPtr targetWindow = GetForegroundWindow();
                        bool isElevated = IsTargetProcessElevatedOrAccessDenied(targetWindow);

                        Classes.ClipboardHelper.SafeSetText(matchedShortcut.Expansion, suppressEcho: true, echoDelayMs: 500);
                        _lastClipboardCaptureTime = DateTime.UtcNow;

                        if (isElevated)
                        {
                            Windows.ToastWindow.ShowToast($"✦ {matchedShortcut.Label} Copied! (Manual Ctrl+V needed in Admin app)");
                        }
                        else
                        {
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(80);
                                keybd_event((byte)VK_CONTROL, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, 0);
                                keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                            });
                            Windows.ToastWindow.ShowToast($"✦ {matchedShortcut.Label} Auto-Pasted! ✦");
                        }
                        return;
                    }
                }

                // ═══ DISPATCH TO BACKGROUND PROCESSING ═══
                var vm = (FlyShelfViewModel)DataContext;

                if (bitmap != null && files != null && files.Length > 0)
                {
                    _lastClipboardCaptureTime = DateTime.UtcNow;
                    var capturedBitmap = bitmap;
                    var capturedFiles = files;
                    _ = Task.Run(() =>
                    {
                        var (sourceAppName, sourceAppIcon) = ResolveSourceApp(capturedFgWindow, capturedProcessId);
                        bool allFilesExist = capturedFiles.All(f => System.IO.File.Exists(f));
                        if (!allFilesExist)
                        {
                            Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as BITMAP ({capturedBitmap.PixelWidth}x{capturedBitmap.PixelHeight}) [files not yet on disk]");
                            vm.HandleDropInternal(null, capturedBitmap, null, false, false, sourceAppName: sourceAppName, sourceAppIcon: sourceAppIcon);
                        }
                        else
                        {
                            string ext = System.IO.Path.GetExtension(capturedFiles[0]).ToLower(System.Globalization.CultureInfo.InvariantCulture);
                            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp")
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as BITMAP ({capturedBitmap.PixelWidth}x{capturedBitmap.PixelHeight}) [image file, prefer bitmap]");
                                vm.HandleDropInternal(null, capturedBitmap, null, false, false, sourceAppName: sourceAppName, sourceAppIcon: sourceAppIcon);
                            }
                            else
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as FILES ({capturedFiles.Length} items) [non-image, prefer files]");
                                vm.HandleDropInternal(capturedFiles, null, null, false, false, sourceAppName: sourceAppName, sourceAppIcon: sourceAppIcon);
                            }
                        }
                    });
                }
                else if (bitmap != null && (files == null || files.Length == 0))
                {
                    _lastClipboardCaptureTime = DateTime.UtcNow;
                    var capturedBitmap = bitmap;
                    _ = Task.Run(() =>
                    {
                        var (sourceAppName, sourceAppIcon) = ResolveSourceApp(capturedFgWindow, capturedProcessId);
                        Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as BITMAP ({capturedBitmap.PixelWidth}x{capturedBitmap.PixelHeight})");
                        vm.HandleDropInternal(null, capturedBitmap, null, false, false, sourceAppName: sourceAppName, sourceAppIcon: sourceAppIcon);
                    });
                }
                else if (files != null && files.Length > 0)
                {
                    _lastClipboardCaptureTime = DateTime.UtcNow;
                    Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as FILES ({files.Length} items)");
                    var capturedFiles = files;
                    _ = Task.Run(() =>
                    {
                        var (sourceAppName, sourceAppIcon) = ResolveSourceApp(capturedFgWindow, capturedProcessId);
                        vm.HandleDropInternal(capturedFiles, null, null, false, false, sourceAppName: sourceAppName, sourceAppIcon: sourceAppIcon);
                    });
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    // ═══ ONE-CLICK ACTIVATE ═══
                    const string ACTIVATION_PREFIX = "FLYSHELF_ACTIVATE::";
                    if (text.Trim().StartsWith(ACTIVATION_PREFIX, StringComparison.OrdinalIgnoreCase))
                    {
                        string keyCandidate = text.Trim().Substring(ACTIVATION_PREFIX.Length).Trim();
                        Classes.Logger.LogAction("LICENSE", $"Clipboard activation trigger detected: ****-{keyCandidate[Math.Max(0, keyCandidate.Length - 4)..]}");
                        _lastClipboardCaptureTime = DateTime.UtcNow;

                        if (Classes.LicenseManager.IsPro)
                        {
                            try { ClipboardHelper.SafeSetText(keyCandidate); } catch { }
                            Classes.Logger.LogAction("LICENSE", "Already Pro — copied plain key to clipboard");
                            return;
                        }
                        try { Clipboard.Clear(); } catch { }

                        if (keyCandidate.StartsWith("FS-PRO-", StringComparison.OrdinalIgnoreCase) && keyCandidate.Length >= 23)
                        {
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
                        return;
                    }

                    _lastClipboardCaptureTime = DateTime.UtcNow;
                    Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as TEXT ({text.Length} chars)");
                    var capturedText = text;
                    _ = Task.Run(() =>
                    {
                        var (sourceAppName, sourceAppIcon) = ResolveSourceApp(capturedFgWindow, capturedProcessId);
                        vm.HandleDropInternal(null, null, capturedText, false, false, sourceAppName: sourceAppName, sourceAppIcon: sourceAppIcon);
                    });
                }
                else
                {
                    Classes.Logger.LogAction("CLIPBOARD", "→ No actionable data found on clipboard");
                }
            }
            catch (Exception cbEx) { Classes.Logger.LogAction("CLIPBOARD", $"Routing handler error: {cbEx.Message}"); }
        }

        // ═══ SOURCE APP RESOLUTION: Runs entirely on background thread ═══
        // Called from Task.Run dispatch blocks above. Receives the foreground window
        // handle + process ID that were cheaply captured on the UI thread.
        // Returns (sourceAppName, sourceAppIcon) after resolving process name, window
        // title, friendly name formatting, and icon extraction — all off the UI thread.
        private (string sourceAppName, System.Windows.Media.Imaging.BitmapSource sourceAppIcon) ResolveSourceApp(IntPtr fgWindow, uint processId)
        {
            string sourceAppName = "";
            System.Windows.Media.Imaging.BitmapSource sourceAppIcon = null;

            if (fgWindow == IntPtr.Zero || processId == 0)
                return (sourceAppName, sourceAppIcon);

            try
            {
                // ── Process name (5-50ms: Process.GetProcessById walks the process list) ──
                string processName = "";
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)processId);
                    processName = proc.ProcessName;
                }
                catch { } // Access denied for elevated/exited processes

                // ── Window title (GetWindowText is a P/Invoke — safe from any thread) ──
                var sb = new System.Text.StringBuilder(512);
                GetWindowText(fgWindow, sb, sb.Capacity);
                string windowTitle = sb.ToString();

                // ── Format: prefer "README.md - VS Code" style, fallback to process name ──
                if (!string.IsNullOrEmpty(windowTitle) && windowTitle != "FlyShelf")
                {
                    string friendlyName = processName?.ToLower(System.Globalization.CultureInfo.InvariantCulture) switch
                    {
                        "code" => "VS Code",
                        "devenv" => "Visual Studio",
                        "chrome" => "Chrome",
                        "msedge" => "Edge",
                        "firefox" => "Firefox",
                        "explorer" => "Explorer",
                        "notepad" => "Notepad",
                        "powershell" => "PowerShell",
                        "windowsterminal" => "Terminal",
                        "cmd" => "CMD",
                        "slack" => "Slack",
                        "teams" => "Teams",
                        "discord" => "Discord",
                        "outlook" => "Outlook",
                        "winword" => "Word",
                        "excel" => "Excel",
                        "powerpnt" => "PowerPoint",
                        _ => processName ?? ""
                    };

                    if (windowTitle.Length > 60)
                        windowTitle = string.Concat(windowTitle.AsSpan(0, 57), "...");

                    if (windowTitle.Equals(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        windowTitle.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    {
                        sourceAppName = friendlyName;
                    }
                    else
                    {
                        sourceAppName = $"{friendlyName} — {windowTitle}";
                    }
                }

                // ── Icon extraction (cached; MainModule + ExtractAssociatedIcon can take 5-50ms) ──
                if (!string.IsNullOrEmpty(processName))
                {
                    string cacheKey = processName.ToLower(System.Globalization.CultureInfo.InvariantCulture);
                    if (_sourceAppIconCache.TryGetValue(cacheKey, out var cachedIcon))
                    {
                        sourceAppIcon = cachedIcon;
                    }
                    else
                    {
                        // Already on background thread — extract synchronously (no extra Task.Run needed)
                        try
                        {
                            string exePath = null;
                            try
                            {
                                using var procForIcon = System.Diagnostics.Process.GetProcessById((int)processId);
                                exePath = procForIcon.MainModule?.FileName;
                            }
                            catch { } // Access denied for elevated processes
                            if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                            {
                                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                                if (icon != null)
                                {
                                    var bmpSrc = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                        icon.Handle, Int32Rect.Empty,
                                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                                    bmpSrc.Freeze(); // Thread-safe
                                    _sourceAppIconCache[cacheKey] = bmpSrc;
                                    sourceAppIcon = bmpSrc;
                                }
                            }
                        }
                        catch { } // Best-effort: icon extraction should never break clipboard
                        if (sourceAppIcon == null)
                            _sourceAppIconCache[cacheKey] = null; // Cache null to avoid re-trying
                    }
                }
            }
            catch { } // Best-effort: source tracking should never break clipboard

            return (sourceAppName, sourceAppIcon);
        }

        // TARGET ELEVATION DETECTION (UIPI SAFEGUARDS)
        // ═══════════════════════════════════════════════════════

        private bool IsTargetProcessElevatedOrAccessDenied(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            
            uint processId;
            NativeMethods.GetWindowThreadProcessId(hWnd, out processId);
            if (processId == 0) return false;

            // Step 1: Try to open process. If access is denied (error 5), it is elevated/higher integrity
            IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
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
                if (!NativeMethods.OpenProcessToken(hProcess, NativeMethods.TOKEN_QUERY, out hToken))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 5) return true; // Access Denied
                }
                else
                {
                    NativeMethods.TOKEN_ELEVATION elevationType;
                    int size = Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
                    IntPtr pElevationType = Marshal.AllocHGlobal(size);
                    try
                    {
                        // 20 is TokenElevation in the enum
                        if (NativeMethods.GetTokenInformation(hToken, 20, pElevationType, size, out _))
                        {
                            elevationType = Marshal.PtrToStructure<NativeMethods.TOKEN_ELEVATION>(pElevationType);
                            return elevationType.TokenIsElevated != 0;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pElevationType);
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable
            finally
            {
                if (hToken != IntPtr.Zero) NativeMethods.CloseHandle(hToken);
                NativeMethods.CloseHandle(hProcess);
            }

            return false;
        }
    }
}
