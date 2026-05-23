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

                // DEBOUNCE: Reuse a single timer to avoid GC pressure.
                // 100ms collapses burst events while staying responsive.
                if (_clipboardDebounceTimer == null)
                {
                    _clipboardDebounceTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(150) // 150ms debounce — fast response while still collapsing burst events
                    };
                    _clipboardDebounceTimer.Tick += (s, ev) =>
                    {
                        _clipboardDebounceTimer.Stop();
                        try
                        {
                            // PERF: Clipboard.GetDataObject() is a COM call that MUST run on the STA UI thread.
                            // Extract the minimum data here, then offload ALL processing to a background thread.
                            IDataObject data = Clipboard.GetDataObject();
                            if (data == null) return;

                            // PERF: Verbose format logging removed — was causing string alloc + I/O on every clipboard event

                            // Snapshot all data now while we're on the STA thread — IDataObject can't cross threads
                            string[] files = null;
                            string text = null;
                            System.Windows.Media.Imaging.BitmapSource bitmap = null;

                            // STEP 1: Always try to extract bitmap FIRST — screenshots from Snipping Tool
                            // set BOTH FileDrop AND Bitmap, but the file may not exist yet (async save).
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
                                
                                // PERF: File list logging removed
                            }
                            catch { }

                            // STEP 3: If we have BOTH bitmap AND files, prefer bitmap for screenshots
                            // (Snipping Tool sets FileDrop but the file may not exist yet)
                            if (bitmap != null && files != null && files.Length > 0)
                            {
                                // Check if file actually exists — if not, the bitmap is the real data
                                bool allFilesExist = files.All(f => System.IO.File.Exists(f));
                                if (!allFilesExist)
                                {
                                    Classes.Logger.LogAction("CLIPBOARD", "Files don't exist yet — using bitmap instead");
                                    files = null; // Force bitmap path
                                }
                                else
                                {
                                    // Files exist — check if they're image files (prefer bitmap for images)
                                    string ext = System.IO.Path.GetExtension(files[0]).ToLower();
                                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
                                    {
                                        Classes.Logger.LogAction("CLIPBOARD", "Image file detected — using bitmap for richer preview");
                                        files = null; // Force bitmap path for image files
                                    }
                                }
                            }

                            // STEP 4: Extract text only if no bitmap and no files
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

                            // Now dispatch to background — no more COM calls needed
                            var vm = (FlyShelfViewModel)DataContext;
                            if (bitmap != null && (files == null || files.Length == 0))
                            {
                                // ═══ FIX: Filter out fully transparent/ghost images ═══
                                // Some apps and screenshot tools place transparent bitmaps on clipboard.
                                // Check if >95% of pixels are fully transparent — if so, discard.
                                bool isGhostImage = false;
                                try
                                {
                                    var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                                    int w = converted.PixelWidth;
                                    int h = converted.PixelHeight;
                                    // Ultra-light ghost check: read 16 single pixels from a 4×4 grid (64 bytes total)
                                    byte[] pixel = new byte[4];
                                    int transparentCount = 0;
                                    const int gridSize = 4;
                                    for (int gy = 0; gy < gridSize; gy++)
                                    {
                                        int y = (gy * 2 + 1) * h / (gridSize * 2); // Centered samples
                                        for (int gx = 0; gx < gridSize; gx++)
                                        {
                                            int x = (gx * 2 + 1) * w / (gridSize * 2);
                                            converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
                                            if (pixel[3] < 10) transparentCount++;
                                        }
                                    }
                                    if (transparentCount >= 15) // 15/16 = 93.75% transparent
                                    {
                                        isGhostImage = true;
                                        Classes.Logger.LogAction("CLIPBOARD", $"⛔ Rejected ghost image ({w}x{h}) — {transparentCount}/16 samples transparent");
                                    }
                                }
                                catch { }

                                if (!isGhostImage)
                                {
                                    Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as BITMAP ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
                                    var dataObj = new System.Windows.DataObject(typeof(System.Windows.Media.Imaging.BitmapSource), bitmap);
                                    Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false));
                                }
                            }
                            else if (files != null && files.Length > 0)
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as FILES ({files.Length} items)");
                                var dataObj = new System.Windows.DataObject(DataFormats.FileDrop, files);
                                Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false));
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as TEXT ({text.Length} chars)");
                                var dataObj = new System.Windows.DataObject(DataFormats.UnicodeText, text);
                                Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false));
                            }
                            else
                            {
                                Classes.Logger.LogAction("CLIPBOARD", "→ No actionable data found on clipboard");
                            }
                        }
                        catch (Exception cbEx) { Classes.Logger.LogAction("CLIPBOARD", $"Handler error: {cbEx.Message}"); }
                    };
                }
                _clipboardDebounceTimer.Stop();
                _clipboardDebounceTimer.Start();
                
                handled = true;
            }
            return IntPtr.Zero;
        }
    }
}
