// ---------------------------------------------------------------
// MainWindow — WndProc Hook: Hotkeys, Clipboard Monitoring & System Settings
// HwndHook handler for WM_HOTKEY (Alt+C, Alt+1-0), WM_CLIPBOARDUPDATE,
// and WM_SETTINGCHANGE messages.
// Split from MainWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private int _clipboardUpdateToken;

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                if (hotkeyId == HOTKEY_ID)
                {
                    Classes.Logger.LogAction("TELEMETRY", "Hotkey Alt+C received inside WndProc");
                    ToggleMainClipboard();
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
                            SetWritingClipboard(true);
                            try
                            {
                                if (!string.IsNullOrEmpty(item.RawContent))
                                    System.Windows.Clipboard.SetText(item.RawContent);
                                else if (!string.IsNullOrEmpty(item.FilePath))
                                {
                                    var dropList = new System.Collections.Specialized.StringCollection();
                                    dropList.Add(item.FilePath);
                                    System.Windows.Clipboard.SetFileDropList(dropList);
                                }
                            }
                            catch { }

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
                            // Also clear the clipboard write guard after delay
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(50);
                                keybd_event((byte)VK_CONTROL, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, 0);
                                keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                                await Task.Delay(500); // Absorb WM_CLIPBOARDUPDATE
                                SetWritingClipboard(false);
                            });
                        }
                    });
                    handled = true;
                }
            }
            else if (msg == Classes.NativeMethods.WM_SETTINGCHANGE)
            {
                _cachedDesktopWallpaperPath = null;
                // Only re-apply if we're in FlyShelf desktop wallpaper mode
                if ((Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica") == "desktop")
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
            else if (msg == WM_CLIPBOARDUPDATE)
            {
                // GUARD: Skip clipboard events triggered by our own writes
                if (_isWritingClipboard)
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
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
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
                    var capturedBitmap = bitmap;
                    _ = Task.Run(() =>
                    {
                        Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as BITMAP ({capturedBitmap.PixelWidth}x{capturedBitmap.PixelHeight})");
                        vm.HandleDropInternal(null, capturedBitmap, null, false, false);
                    });
                }
                else if (files != null && files.Length > 0)
                {
                    Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as FILES ({files.Length} items)");
                    var capturedFiles = files;
                    _ = Task.Run(() => vm.HandleDropInternal(capturedFiles, null, null, false, false));
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
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
    }
}
