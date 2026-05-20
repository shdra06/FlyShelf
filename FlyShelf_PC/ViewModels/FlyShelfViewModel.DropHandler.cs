// ---------------------------------------------------------------
// FlyShelfViewModel � Drop Handler & Utilities
// HandleDrop (file/text/image processing), SHFILEINFO interop,
// SaveGlobalSettings, RelayCommand
// Split from FlyShelfViewModel.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace AdvanceClip.ViewModels
{
    public partial class FlyShelfViewModel
    {
        public void HandleDrop(IDataObject data, bool forceClipboardSync = false, bool skipFirebaseSync = false)
        {
            string[] files = null;
            
            if (data.GetDataPresent(DataFormats.FileDrop))
                files = data.GetData(DataFormats.FileDrop) as string[];
                
            if ((files == null || files.Length == 0) && data.GetDataPresent("FileNameW"))
            {
                var fName = data.GetData("FileNameW") as string[];
                if (fName != null && fName.Length > 0 && fName[0] != null) files = fName;
            }
            
            if ((files == null || files.Length == 0) && data.GetDataPresent("text/uri-list"))
            {
                try 
                {
                    string text = data.GetData("text/uri-list") as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        var parsedPaths = new System.Collections.Generic.List<string>();
                        foreach (var l in lines)
                        {
                            string p = l.Trim();
                            if (p.StartsWith("file:///")) p = new Uri(p).LocalPath;
                            if (File.Exists(p) || Directory.Exists(p)) parsedPaths.Add(p);
                        }
                        if (parsedPaths.Count > 0) files = parsedPaths.ToArray();
                    }
                } 
                catch { }
            }

            if (files != null && files.Length > 0)
            {
                if (files.Length > 10)
                {
                    // Group files together!
                    var groupItem = new ClipboardItem(files);
                    DroppedItems.Insert(0, groupItem);
                    PruneOldItems();
                    OnPropertyChanged(nameof(ShelfVisibility));

                    AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Grouped {files.Length} files into a single Group item.");

                    // Instantly push SSE event to connected mobile clients
                    AdvanceClip.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(
                        groupItem.ItemType.ToString(), 
                        groupItem.FileName);

                    // Skip the regular individual batch file processing entirely!
                    return;
                }

                // ═══ BATCH FILE PROCESSING ═══
                // Cap at 100 files per clipboard event to prevent UI freeze.
                // Files beyond the cap are silently dropped — users rarely need 100+ items at once.
                const int MAX_FILES_PER_BATCH = 100;
                if (files.Length > MAX_FILES_PER_BATCH)
                {
                    AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Batch capped: {files.Length} files → processing first {MAX_FILES_PER_BATCH}");
                    files = files.Take(MAX_FILES_PER_BATCH).ToArray();
                }

                // Phase 1: Collect items — fast, no icon loading, no sync, no UI notifications
                var newItems = new List<(ClipboardItem item, string path)>();
                var bumped = new List<ClipboardItem>(); // Existing items to move to top

                foreach (string file in files)
                {
                    var existingFile = DroppedItems.FirstOrDefault(i => i.FilePath == file);
                    if (existingFile != null)
                    {
                        existingFile.RefreshPhysicalStats();
                        existingFile.DateCopied = DateTime.Now; // Fresh timestamp so it sorts to top on mobile
                        bumped.Add(existingFile);
                        continue;
                    }
                    newItems.Add((new ClipboardItem(file), file));
                }

                // Phase 2: Batch-insert into ObservableCollection — single UI notification burst
                // Move bumped items to top first
                foreach (var existing in bumped)
                {
                    DroppedItems.Remove(existing);
                    DroppedItems.Insert(0, existing);
                }

                // Insert new items in reverse order so first file ends up at index 0
                for (int i = newItems.Count - 1; i >= 0; i--)
                {
                    DroppedItems.Insert(0, newItems[i].item);
                }
                PruneOldItems();
                OnPropertyChanged(nameof(ShelfVisibility));

                AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Batch inserted {newItems.Count} new + {bumped.Count} bumped files");

                // Instantly push SSE event to connected mobile clients (zero-latency sync)
                if (newItems.Count > 0)
                {
                    var first = newItems[0].item;
                    AdvanceClip.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(first.ItemType.ToString(), first.FileName ?? first.RawContent?.Substring(0, Math.Min(40, first.RawContent?.Length ?? 0)) ?? "");
                }
                else if (bumped.Count > 0)
                {
                    // Bumped files (re-copied) also need to notify mobile — they expect the latest item
                    var first = bumped[0];
                    AdvanceClip.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(first.ItemType.ToString(), first.FileName ?? first.RawContent?.Substring(0, Math.Min(40, first.RawContent?.Length ?? 0)) ?? "");
                }

                // Phase 3: Background — load icons + run sync (completely off the UI thread)
                if (newItems.Count > 0)
                {
                    var capturedNewItems = newItems.ToList();
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        foreach (var (item, filePath) in capturedNewItems)
                        {
                            // Icon loading — throttled to 2 parallel decodes to prevent memory spikes
                            await _iconDecodeSemaphore.WaitAsync();
                            try
                            {
                                if (item.ItemType == ClipboardItemType.Image)
                                {
                                    try
                                    {
                                        var bmp = new BitmapImage();
                                        bmp.BeginInit();
                                        bmp.UriSource = new Uri(filePath);
                                        bmp.DecodePixelWidth = 800;
                                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                                        bmp.EndInit();
                                        bmp.Freeze();
                                        Application.Current.Dispatcher.InvokeAsync(() => item.Icon = bmp);
                                    }
                                    catch { }
                                }
                                else
                                {
                                    var icon = GetIcon(filePath);
                                    if (icon != null)
                                    {
                                        Application.Current.Dispatcher.InvokeAsync(() => item.Icon = icon);
                                    }
                                }
                            }
                            finally { _iconDecodeSemaphore.Release(); }


                            // Firebase sync — skip for large batches (>10 files) to prevent flooding
                            if (capturedNewItems.Count > 10 || !AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync || skipFirebaseSync)
                                continue;

                            var archPath = AdvanceClip.Classes.SettingsManager.Current.CustomArchiveExtractionPath;
                            if (string.IsNullOrWhiteSpace(archPath)) archPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Extracted");
                            bool isGlobalDownload = filePath.StartsWith(archPath, StringComparison.OrdinalIgnoreCase);

                            if (!isGlobalDownload)
                            {
                                if (item.ItemType == ClipboardItemType.Folder)
                                {
                                    var capturedItem = item;
                                    for (int wait = 0; wait < 120; wait++)
                                    {
                                        if (!string.IsNullOrEmpty(capturedItem.ZippedArchivePath) && File.Exists(capturedItem.ZippedArchivePath))
                                            break;
                                        await System.Threading.Tasks.Task.Delay(500);
                                    }
                                    if (!string.IsNullOrEmpty(capturedItem.ZippedArchivePath) && File.Exists(capturedItem.ZippedArchivePath))
                                        await SyncFileToDevicesAsync(capturedItem.ZippedArchivePath, capturedItem, label: "FOLDER");
                                    continue;
                                }

                                string fileExt = Path.GetExtension(filePath).ToLowerInvariant();
                                if (fileExt is ".crdownload" or ".part" or ".tmp" or ".download" or ".partial")
                                    continue;

                                try { using var probe = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                                catch (IOException) { continue; }
                                catch { }

                                await SyncFileToDevicesAsync(filePath, item, label: "FILE");
                            }
                        }
                    });
                }
                // Phase 3b: Firebase sync for BUMPED files (re-copied items that already exist in the list)
                // Only sync the first bumped file — same behavior as new files
                if (newItems.Count == 0 && bumped.Count > 0 && !skipFirebaseSync
                    && AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync)
                {
                    var capturedBumped = bumped[0];
                    var capturedPath = capturedBumped.FilePath;
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            string ext = Path.GetExtension(capturedPath).ToLowerInvariant();
                            if (ext is ".crdownload" or ".part" or ".tmp" or ".download" or ".partial") return;
                            
                            // Check the file isn't a download we extracted ourselves
                            var archPath = AdvanceClip.Classes.SettingsManager.Current.CustomArchiveExtractionPath;
                            if (string.IsNullOrWhiteSpace(archPath)) archPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Extracted");
                            if (capturedPath.StartsWith(archPath, StringComparison.OrdinalIgnoreCase)) return;

                            await SyncFileToDevicesAsync(capturedPath, capturedBumped, label: "FILE");
                            AdvanceClip.Classes.Logger.LogAction("FILE SYNC", $"Re-synced bumped file: {capturedBumped.FileName}");
                        }
                        catch (Exception ex) { AdvanceClip.Classes.Logger.LogAction("FILE SYNC", $"Bumped sync error: {ex.Message}"); }
                    });
                }

                // Clipboard writeback (only for single file or bumped items)
                if (forceClipboardSync && files.Length <= 10)
                {
                    Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            MainWindow.SetWritingClipboard(true);
                            var dropList = new System.Collections.Specialized.StringCollection();
                            dropList.Add(files[0]);
                            System.Windows.Clipboard.SetFileDropList(dropList);
                            await System.Threading.Tasks.Task.Delay(500);
                        }
                        catch { }
                        finally { MainWindow.SetWritingClipboard(false); }
                    });
                }
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib) || data.GetDataPresent(typeof(BitmapSource)))
            {
                BitmapSource? bmp = null;
                try { bmp = data.GetData(typeof(BitmapSource)) as BitmapSource; } catch { }
                if (bmp == null) try { bmp = data.GetData(DataFormats.Bitmap) as BitmapSource; } catch { }

                if (bmp != null)
                {
                    AdvanceClip.Classes.Logger.LogAction("DRAG IN", "Extracted physical Bitmap image payload");
                    if (DroppedItems.Count > 0)
                    {
                        // DEDUP: If any recent image item has the same pixel dimensions, skip it.
                        // Snipping Tool and other screenshot tools fire multiple clipboard events for the same image.
                        string incomingSize = $"{bmp.PixelWidth}x{bmp.PixelHeight}";
                        var recentDupe = DroppedItems.FirstOrDefault(i =>
                            (i.ItemType == ClipboardItemType.Image || i.ItemType == ClipboardItemType.QRCode) &&
                            i.FormattedSize == incomingSize &&
                            (DateTime.Now - i.DateCopied).TotalSeconds < 5.0);
                        if (recentDupe != null)
                        {
                            AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Skipped duplicate image ({incomingSize}, {(DateTime.Now - recentDupe.DateCopied).TotalMilliseconds:F0}ms old)");
                            return;
                        }
                    }

                    var item = new ClipboardItem();
                    item.ItemType = ClipboardItemType.Image;
                    item.FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}";
                    item.Extension = "IMAGE";
                    item.FormattedSize = $"{bmp.PixelWidth}x{bmp.PixelHeight}";
                    
                    item.EvaluateSmartActions();

                    // Set an immediate thumbnail from the raw bitmap so the card
                    // never renders blank while the background PNG save runs
                    var capturedBmp = bmp.Clone(); 
                    capturedBmp.Freeze();
                    try
                    {
                        var immediateThumbnail = new BitmapImage();
                        using (var ms = new MemoryStream())
                        {
                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(capturedBmp));
                            enc.Save(ms);
                            ms.Position = 0;
                            immediateThumbnail.BeginInit();
                            immediateThumbnail.CacheOption = BitmapCacheOption.OnLoad;
                            immediateThumbnail.DecodePixelWidth = 800;
                            immediateThumbnail.StreamSource = ms;
                            immediateThumbnail.EndInit();
                        }
                        immediateThumbnail.Freeze();
                        item.Icon = immediateThumbnail;
                    }
                    catch (Exception thumbEx)
                    {
                        Classes.Logger.LogAction("ICON IMMEDIATE", $"Inline thumbnail failed: {thumbEx.Message}");
                    }
                    
                    // Standard Stack Logic (Index 0)
                    DroppedItems.Insert(0, item);
                    PruneOldItems();
                    // Push instant notification to mobile clients
                    AdvanceClip.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(item.ItemType.ToString(), item.FileName ?? "");

                    // Clipboard preservation: external tools may overwrite clipboard with text ~500ms-4s later.
                    // We use the full-size bitmap (capturedBmp) to avoid size-mismatch dedup issues.
                    // Guard is set for the ENTIRE preservation window to prevent any re-processing.
                    if (!forceClipboardSync && capturedBmp != null)
                    {
                        var preserveBmp = capturedBmp; // Full-size frozen bitmap — not the 250px thumbnail
                        // Set guard immediately and keep it for the full preservation window
                        MainWindow.SetWritingClipboard(true);
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            // Stage 1: quick check at 800ms
                            await System.Threading.Tasks.Task.Delay(800);
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try
                                {
                                    if (!System.Windows.Clipboard.ContainsImage())
                                    {
                                        System.Windows.Clipboard.SetImage(preserveBmp);
                                        Classes.Logger.LogAction("CLIPBOARD", "Re-asserted bitmap after external overwrite (stage 1)");
                                    }
                                }
                                catch { }
                            });
                            // Stage 2: second check at 2500ms for slower overwrites
                            await System.Threading.Tasks.Task.Delay(1700);
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try
                                {
                                    if (!System.Windows.Clipboard.ContainsImage())
                                    {
                                        System.Windows.Clipboard.SetImage(preserveBmp);
                                        Classes.Logger.LogAction("CLIPBOARD", "Re-asserted bitmap after external overwrite (stage 2)");
                                    }
                                }
                                catch { }
                            });
                            // Clear guard after full preservation window + buffer
                            await System.Threading.Tasks.Task.Delay(500);
                            MainWindow.SetWritingClipboard(false);
                        });
                    }

                    System.Threading.Tasks.Task.Run(() => 
                    {
                        string tempFile = Classes.ClipboardHistoryManager.GetPersistentImagePath();
                        
                        try
                        {
                            var convertedBmp = new FormatConvertedBitmap(capturedBmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                            convertedBmp.Freeze();
                            
                            using (var fs = new FileStream(tempFile, FileMode.Create))
                            {
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(convertedBmp));
                                encoder.Save(fs);
                            }

                            Application.Current.Dispatcher.InvokeAsync(() => 
                            {
                                try
                                {
                                    var bitmapImage = LoadImageThumbnail(tempFile);
                                    if (bitmapImage != null)
                                        item.Icon = bitmapImage;
                                }
                                catch (Exception iconEx)
                                {
                                    Classes.Logger.LogAction("ICON FILE", $"Failed to load saved thumbnail: {iconEx.Message}");
                                }
                                item.FilePath = tempFile;
                                item.ScanForQRCodeAsync(tempFile);
                                OnPropertyChanged(nameof(ShelfVisibility));
                                
                                if (forceClipboardSync)
                                {
                                    try
                                    {
                                        MainWindow.SetWritingClipboard(true);
                                        System.Windows.Clipboard.SetImage(item.Icon);
                                    }
                                    catch { }
                                    // Delay clearing — absorb async WM_CLIPBOARDUPDATE
                                    _ = System.Threading.Tasks.Task.Run(async () =>
                                    {
                                        await System.Threading.Tasks.Task.Delay(500);
                                        MainWindow.SetWritingClipboard(false);
                                    });
                                }
                                // Sync image to devices via unified helper
                                if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync && !skipFirebaseSync)
                                {
                                    // Check if this image came from cloud — don't re-push
                                    string imgFp = $"IMG::{item.FormattedSize}";
                                    if (!IsCloudSourced(imgFp))
                                    {
                                        string capturedTempFile = tempFile;
                                        var capturedItem = item;
                                        _ = System.Threading.Tasks.Task.Run(async () => await SyncFileToDevicesAsync(capturedTempFile, capturedItem, maxFirebaseBytes: 5 * 1024 * 1024, label: "IMAGE"));
                                    }
                                    else
                                    {
                                        AdvanceClip.Classes.Logger.LogAction("IMAGE SYNC", "Skipped — image arrived from cloud (echo prevention)");
                                    }
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            AdvanceClip.Classes.Logger.LogAction("IMAGE CORE", $"Failed to encode web palette: {ex.Message}");
                            Application.Current.Dispatcher.Invoke(() => {
                                item.ItemType = ClipboardItemType.Text;
                                item.FileName = "Image Failed to Decode!";
                                item.RawContent = "The browser exported a highly compressed or corrupted image payload that the .NET Runtime could not safely rasterize to disk.";
                                item.Extension = "ERROR";
                                OnPropertyChanged(nameof(ShelfVisibility));
                            });
                        }
                    });
                }
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.StringFormat) || data.GetDataPresent(DataFormats.Text))
            {
                string text = "";
                try { text = data.GetData(DataFormats.UnicodeText) as string ?? ""; } catch { }
                if (string.IsNullOrEmpty(text)) try { text = data.GetData(DataFormats.StringFormat) as string ?? ""; } catch { }
                if (string.IsNullOrEmpty(text)) try { text = data.GetData(DataFormats.Text) as string ?? ""; } catch { }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    text = text.Trim().TrimEnd('\0');
                    // Re-check after trim — text might have been only whitespace/null chars
                    if (string.IsNullOrWhiteSpace(text)) return;

                    // Strip invisible Unicode characters that cause blank boxes
                    // (zero-width joiners, variation selectors, directional marks, etc.)
                    string visibleCheck = System.Text.RegularExpressions.Regex.Replace(text, 
                        @"[\u200B-\u200F\u2028-\u202F\u2060-\u206F\uFE00-\uFE0F\uFEFF\u00AD]", "");
                    if (string.IsNullOrWhiteSpace(visibleCheck)) return;
                    AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Extracted string text payload length: {text.Length}");

                    // DEDUP: If ANY existing item already has this exact content, bump it to the top — no duplicate.
                    // Pinned items stay pinned; they just move to position 0.
                    var existingMatch = DroppedItems.FirstOrDefault(i => i.RawContent == text);
                    if (existingMatch != null)
                    {
                        // Already at the top? True no-op.
                        if (DroppedItems.IndexOf(existingMatch) == 0)
                        {
                            AdvanceClip.Classes.Logger.LogAction("DRAG IN", "Skipped — already at top (dedup)");
                            return;
                        }
                        DroppedItems.Remove(existingMatch);
                        // Heal FileName if empty (legacy items from before fix)
                        if (string.IsNullOrWhiteSpace(existingMatch.FileName) && !string.IsNullOrWhiteSpace(existingMatch.RawContent))
                            existingMatch.FileName = existingMatch.RawContent.Length > 800 ? existingMatch.RawContent.Substring(0, 800) + "..." : existingMatch.RawContent;
                        DroppedItems.Insert(0, existingMatch);
                        AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Bumped existing item to top (dedup, pinned={existingMatch.IsPinned})");
                        // Push instant notification to mobile clients
                        AdvanceClip.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(existingMatch.ItemType.ToString(), existingMatch.FileName ?? existingMatch.RawContent?.Substring(0, Math.Min(40, existingMatch.RawContent?.Length ?? 0)) ?? "");
                        return;
                    }

                    // PERF: Capture text, then offload ALL processing to background thread
                    string capturedText = text;
                    bool capturedForceSync = forceClipboardSync;
                    
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        ClipboardItem? item = null;
                    
                        try
                        {
                            string possiblePath = capturedText;
                            if (possiblePath.StartsWith("file:///"))
                            {
                                possiblePath = new Uri(possiblePath).LocalPath;
                            }
                            
                            if (File.Exists(possiblePath))
                            {
                                AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Seamlessly resolved ambiguous text format to a localized physical file: {possiblePath}");
                                item = new ClipboardItem(possiblePath);
                            }
                        }
                        catch { }

                        if (item == null)
                        {
                            item = new ClipboardItem();
                            item.RawContent = capturedText;
                            item.FormattedSize = string.Empty;
                        }

                        if (Uri.TryCreate(capturedText, UriKind.Absolute, out Uri? uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
                        {
                            string cleanUrl = _rxUtmClean.Replace(capturedText, string.Empty).TrimEnd('?', '&');
                            
                            item.RawContent = cleanUrl;
                            item.ItemType = ClipboardItemType.Url;
                            item.FileName = cleanUrl;
                            item.Extension = "LINK";
                        }
                        else
                        {
                            bool isTerminal = _rxTerminal.IsMatch(capturedText);
                            
                            bool isCode = _rxCode.IsMatch(capturedText);
                            
                            if (isTerminal || isCode)
                            {
                                item.ItemType = ClipboardItemType.Code;
                                item.RawContent = isTerminal ? capturedText : AutoFormatCode(capturedText);
                                
                                if (isTerminal)
                                {
                                    item.Extension = "TERM";
                                }
                                else if (capturedText.Contains("std::") || capturedText.Contains("<iostream>") || capturedText.Contains("<cstdlib>") || capturedText.Contains("<vector>") || capturedText.Contains("using namespace") || Regex.IsMatch(capturedText, @"(cout|cin|endl|cerr)\s*<<"))
                                {
                                    item.Extension = "C++";
                                }
                                else if (capturedText.Contains("<stdio.h>") || capturedText.Contains("<stdlib.h>") || capturedText.Contains("<string.h>") || Regex.IsMatch(capturedText, @"\b(printf|scanf|malloc|free|sizeof|typedef|struct\s+\w+)\s*[\(;]"))
                                {
                                    item.Extension = "C";
                                }
                                else if (Regex.IsMatch(capturedText, @"(def\s+\w+\s*\(|import\s+(os|sys|json|re|math|numpy|pandas|flask|django|requests|typing|pathlib)|from\s+\w+\s+import|if\s+__name__\s*==|self\.|__init__|lambda\s|print\s*\(|class\s+\w+\s*[\(:]|@(staticmethod|classmethod|property)|except\s|elif\s|raise\s)"))
                                {
                                    item.Extension = "PYTHON";
                                }
                                else if (Regex.IsMatch(capturedText, @"(public\s+static\s+void\s+main|System\.(out|in|err)\.|import\s+java\.|throws\s|implements\s|extends\s|interface\s+\w+|abstract\s+class|@Override|@Deprecated|\.println\()"))
                                {
                                    item.Extension = "JAVA";
                                }
                                else if (Regex.IsMatch(capturedText, @"(function\s+\w+\s*\(|console\.(log|error|warn)\(|require\s*\(|module\.exports|export\s+(default|const|function|class)|async\s+function|await\s|const\s+\w+\s*=\s*(require|\(|async|\{)|=>\s*\{)"))
                                {
                                    item.Extension = "JS";
                                }
                                else if (capturedText.Contains("public class") || capturedText.Contains("private void") || capturedText.Contains("Console.") || capturedText.Contains("namespace ") || Regex.IsMatch(capturedText, @"(using\s+System|var\s+\w+\s*=\s*new|async\s+Task)"))
                                {
                                    item.Extension = "C#";
                                }
                                else if (Regex.IsMatch(capturedText, @"(SELECT\s+.*\s+FROM|INSERT\s+INTO|CREATE\s+(TABLE|DATABASE)|ALTER\s+TABLE|WHERE\s+\w+)", RegexOptions.IgnoreCase))
                                {
                                    item.Extension = "SQL";
                                }
                                else if (capturedText.TrimStart().StartsWith("{\"") || capturedText.TrimStart().StartsWith("[{\""))
                                {
                                    item.Extension = "JSON";
                                }
                                else if (Regex.IsMatch(capturedText, @"<\/?(html|div|span|body|script|style|form|table)[\s>]", RegexOptions.IgnoreCase))
                                {
                                    item.Extension = "HTML";
                                }
                                else
                                {
                                    item.Extension = "CODE";
                                }
                                string shortText = capturedText.Trim();
                                item.FileName = shortText.Length > 800 ? shortText.Substring(0, 800) + "..." : shortText;
                            }
                            else
                            {
                                item.ItemType = ClipboardItemType.Text;
                                item.Extension = "TEXT";
                                // CRITICAL: FileName is what the card UI displays — must be set or card is blank
                                string displayText = capturedText.Trim();
                                item.FileName = displayText.Length > 800 ? displayText.Substring(0, 800) + "..." : displayText;
                            }
                        }
                        
                        item.EvaluateSmartActions();
                        
                        // Sync to all devices via Firebase + Cloudflare
                        if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync && !skipFirebaseSync)
                        {
                            // Check if this text came from cloud — don't re-push
                            string txtFp = $"TXT::{(item.RawContent ?? "").Substring(0, Math.Min(200, (item.RawContent ?? "").Length))}";
                            if (!IsCloudSourced(txtFp))
                            {
                                AdvanceClip.Classes.SyncQueue.Enqueue(item);
                            }
                            else
                            {
                                AdvanceClip.Classes.Logger.LogAction("TEXT SYNC", "Skipped — text arrived from cloud (echo prevention)");
                            }
                        }

                        // Dispatch ONLY the UI mutations back to the UI thread
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var existingText = DroppedItems.FirstOrDefault(i => i.RawContent == capturedText || i.RawContent == item.RawContent);
                            if (existingText != null) DroppedItems.Remove(existingText);
                            
                            DroppedItems.Insert(0, item);
                            PruneOldItems();
                            // Push instant notification to mobile clients
                            AdvanceClip.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(item.ItemType.ToString(), item.FileName ?? item.RawContent?.Substring(0, Math.Min(40, item.RawContent?.Length ?? 0)) ?? "");
                            
                            if (item.SmartActionType == "SetTimer" && System.Text.RegularExpressions.Regex.IsMatch(item.RawContent.Trim(), @"^\/\d+$"))
                            {
                                var tw = new AdvanceClip.Windows.TimerWindow(item.RawContent.Trim());
                                tw.Show();
                            }
                            
                            if (capturedForceSync)
                            {
                                try
                                {
                                    MainWindow.SetWritingClipboard(true);
                                    System.Windows.Clipboard.SetText(item.RawContent);
                                }
                                catch { }
                                // Delay clearing — absorb async WM_CLIPBOARDUPDATE
                                _ = System.Threading.Tasks.Task.Run(async () =>
                                {
                                    await System.Threading.Tasks.Task.Delay(500);
                                    MainWindow.SetWritingClipboard(false);
                                });
                            }
                            
                            OnPropertyChanged(nameof(ShelfVisibility));
                        });
                    });
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        };

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Reliably loads an image file as a 250px-wide thumbnail BitmapImage.
        /// Uses StreamSource (not UriSource) to avoid URI-related loading failures.
        /// </summary>
        private static BitmapImage? LoadImageThumbnail(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            var bmp = new BitmapImage();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 800;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }

        private BitmapImage? GetIcon(string filePath)
        {
            try
            {
                const uint SHGFI_ICON = 0x100;
                const uint SHGFI_LARGEICON = 0x0;
                const uint SHGFI_USEFILEATTRIBUTES = 0x10;
                const uint FILE_ATTRIBUTE_NORMAL = 0x80;

                SHFILEINFO shinfo = new SHFILEINFO();
                // SHGFI_USEFILEATTRIBUTES: icon from extension even if file is missing
                IntPtr res = SHGetFileInfo(filePath, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        
                        var bitmapImage = new BitmapImage();
                        using (var memStream = new System.IO.MemoryStream())
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                            encoder.Save(memStream);
                            
                            bitmapImage.BeginInit();
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.StreamSource = memStream;
                            bitmapImage.EndInit();
                            bitmapImage.Freeze();
                        }
                        return bitmapImage;
                    }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { }
            return null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SaveGlobalSettings()
        {
            AdvanceClip.Classes.SettingsManager.Save();
            AdvanceClip.Windows.ToastWindow.ShowToast("System Configuration Saved ✅");
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        public RelayCommand(Action<T> execute) { _execute = execute; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            if (parameter is T typedParameter)
            {
                _execute(typedParameter);
            }
        }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
