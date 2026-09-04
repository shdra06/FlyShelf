// ---------------------------------------------------------------
// NoteManager — Quick Notes Persistence Manager
// [FIX M-59]: Models extracted to NoteModels.cs
// Persisted to %AppData%\FlyShelf\notes.json
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;


namespace FlyShelf.Classes
{
    // [FIX M-59]: Data model classes (NoteBullet, SubBulletItem, FreeformImage,
    // FreeformSection, NoteDay) extracted to NoteModels.cs for modularity.

    // ═══════════════════════════════════════════════════════════
    // PERSISTENCE MANAGER
    // ═══════════════════════════════════════════════════════════

    public static class NoteManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _notesPath = Path.Combine(_appDataDir, "notes.json");
        private static readonly string _notesImagesDir = Path.Combine(_appDataDir, "Notes", "Images");

        private static ObservableCollection<NoteDay> _days = new();
        private static List<NoteDay> _allDays = new();
        private static ObservableCollection<NoteFolder> _folders = new();
        private static Timer? _saveTimer;
        private static readonly object _lock = new();
        private static readonly SemaphoreSlim _fileLock = new(1, 1);
        private static int _isDirty = 0;
        private static bool _isLoaded;

        // [FIX STABLE-1]: Consolidated into FileRetryHelper — tightened from bare catch to IOException/UnauthorizedAccessException only
        private static T RunWithRetry<T>(Func<T> action, int maxAttempts = 3, int delayMs = 100)
            => FileRetryHelper.RunWithRetry(action, maxAttempts, delayMs);

        /// <summary>All note days, sorted newest-first.</summary>
        public static ObservableCollection<NoteDay> Days => _days;

        /// <summary>All note folders, sorted by SortOrder.</summary>
        public static ObservableCollection<NoteFolder> Folders => _folders;

        /// <summary>Returns the permanent image storage directory for notes, creating it if needed.</summary>
        public static string GetImagesDirectory()
        {
            if (!Directory.Exists(_notesImagesDir))
                Directory.CreateDirectory(_notesImagesDir);
            return _notesImagesDir;
        }

        /// <summary>
        /// Load notes from disk. Call once at startup or on first panel open.
        /// </summary>
        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_notesPath))
                    {
                        _days = new ObservableCollection<NoteDay>();
                        _allDays = new List<NoteDay>();
                        _folders = new ObservableCollection<NoteFolder>();
                        _isLoaded = true;
                        return;
                    }

                    // NM-4 FIX: Use RunWithRetry so a brief file lock from concurrent .bak copy doesn't immediately fall to backup recovery
                    string json = RunWithRetry(() => File.ReadAllText(_notesPath));
                    
                    List<NoteDay>? loaded = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if ((doc.RootElement.TryGetProperty("Version", out var versionElement) && versionElement.GetInt32() >= 2) || doc.RootElement.TryGetProperty("Folders", out _))
                        {
                            var v2Data = JsonSerializer.Deserialize<NotesDataV2>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (v2Data != null)
                            {
                                loaded = v2Data.Days;
                                if (v2Data.Folders != null)
                                {
                                    _folders = new ObservableCollection<NoteFolder>(v2Data.Folders);
                                }
                            }
                        }
                        else
                        {
                            loaded = JsonSerializer.Deserialize<List<NoteDay>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        }
                    }
                    catch
                    {
                        loaded = JsonSerializer.Deserialize<List<NoteDay>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }

                    if (loaded != null)
                    {
                        // Normalize all dates to local timezone to prevent UTC timezone shift bugs
                        foreach (var d in loaded)
                        {
                            d.Date = DateTime.SpecifyKind(d.Date.Kind == DateTimeKind.Utc ? d.Date.ToLocalTime().Date : d.Date.Date, DateTimeKind.Local);
                            d.MigrateFreeformIfNeeded(); // Migrate legacy FreeformContent → FreeformSections
                        }

                        // Sort newest first
                        _allDays = loaded.OrderByDescending(d => d.Date).ToList();
                        
                        // Populate visible days list
                        FilterVisibleDays();
                    }
                    else
                    {
                        _days = new ObservableCollection<NoteDay>();
                        _allDays = new List<NoteDay>();
                    }
                    _isLoaded = true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("NOTES", $"Failed to load notes: {ex.Message}");
                    
                    // Attempt backup recovery
                    string backupPath = _notesPath + ".bak";
                    if (File.Exists(backupPath))
                    {
                        try
                        {
                            string backupJson = RunWithRetry(() => File.ReadAllText(backupPath));
                            List<NoteDay>? loadedBackup = null;
                            try
                            {
                                using var doc = JsonDocument.Parse(backupJson);
                                if ((doc.RootElement.TryGetProperty("Version", out var versionElement) && versionElement.GetInt32() >= 2) || doc.RootElement.TryGetProperty("Folders", out _))
                                {
                                    var v2Data = JsonSerializer.Deserialize<NotesDataV2>(backupJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                    if (v2Data != null)
                                    {
                                        loadedBackup = v2Data.Days;
                                        if (v2Data.Folders != null)
                                        {
                                            _folders = new ObservableCollection<NoteFolder>(v2Data.Folders);
                                        }
                                    }
                                }
                                else
                                {
                                    loadedBackup = JsonSerializer.Deserialize<List<NoteDay>>(backupJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                }
                            }
                            catch
                            {
                                loadedBackup = JsonSerializer.Deserialize<List<NoteDay>>(backupJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            }

                            if (loadedBackup != null)
                            {
                                foreach (var d in loadedBackup)
                                {
                                    d.Date = DateTime.SpecifyKind(d.Date.Kind == DateTimeKind.Utc ? d.Date.ToLocalTime().Date : d.Date.Date, DateTimeKind.Local);
                                    d.MigrateFreeformIfNeeded();
                                }
                                _allDays = loadedBackup.OrderByDescending(d => d.Date).ToList();
                                FilterVisibleDays();
                                _isLoaded = true;
                                Logger.LogAction("NOTES", "Successfully recovered notes from backup file (.bak)!");
                                return;
                            }
                        }
                        catch (Exception backupEx)
                        {
                            Logger.LogAction("NOTES", $"Backup recovery also failed: {backupEx.Message}");
                        }
                    }

                    _days = new ObservableCollection<NoteDay>();
                    _allDays = new List<NoteDay>();
                    _isLoaded = true;
                }
            }
        }

        private static void FilterVisibleDays()
        {
            // v7.2 FREE: All notes visible forever — no day-based filtering
            List<NoteDay> source = new List<NoteDay>(_allDays);
            // ORIGINAL PRO GATE:
            // int maxDays = LicenseManager.GetNoteHistoryDays();
            // List<NoteDay> source;
            // if (maxDays < int.MaxValue)
            // {
            //     DateTime cutoff = DateTime.Today.AddDays(-maxDays);
            //     source = _allDays.Where(d => d.Date.Date >= cutoff.Date).ToList();
            //     if (_allDays.Count > source.Count)
            //     {
            //         System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            //             UpgradePrompt.ShowNoteHistoryLimit());
            //     }
            // }
            // else
            // {
            //     source = new List<NoteDay>(_allDays);
            // }

            // Filter out empty days (no content at all) — today is always kept
            var today = DateTime.Today;
            var filtered = source.Where(d =>
            {
                if (d.Date.Date == today) return true; // Always show today
                bool hasBullets = d.Bullets.Any(b => !string.IsNullOrWhiteSpace(b.Header) || !string.IsNullOrWhiteSpace(b.Content) || b.HasImage);
                bool hasFreeform = !string.IsNullOrWhiteSpace(d.FreeformContent) || d.FreeformImages.Count > 0
                    || (d.FreeformSections != null && d.FreeformSections.Any(s => !string.IsNullOrWhiteSpace(s.Content) || s.Images.Count > 0));
                return hasBullets || hasFreeform;
            }).ToList();

            var newDays = new ObservableCollection<NoteDay>(filtered);
            if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == true)
            {
                _days = newDays;
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => _days = newDays);
            }
        }

        public static NoteDay GetOrCreateDay(DateTime date)
        {
            lock (_lock)
            {
                var dateOnly = date.Date;
                var existing = _allDays.FirstOrDefault(d => d.Date.Date == dateOnly);
                if (existing == null)
                {
                    existing = new NoteDay { Date = dateOnly };
                    existing.MigrateFreeformIfNeeded(); // Ensure at least one freeform section
                    _allDays.Add(existing);
                    _allDays = _allDays.OrderByDescending(d => d.Date).ToList();
                    
                    // If it falls within the visible days, insert it into _days too
                    // v7.2 FREE: Always insert into visible days (no day limit)
                    if (true)
                    // ORIGINAL PRO GATE:
                    // int maxDays = LicenseManager.GetNoteHistoryDays();
                    // if (maxDays == int.MaxValue || dateOnly >= DateTime.Today.AddDays(-maxDays))
                    {
                        int insertIdx = 0;
                        while (insertIdx < _days.Count && _days[insertIdx].Date > dateOnly)
                        {
                            insertIdx++;
                        }
                        _days.Insert(insertIdx, existing);
                    }
                    ScheduleSave();
                }
                return existing;
            }
        }

        public static NoteDay EnsureToday()
        {
            return GetOrCreateDay(DateTime.Today);
        }

        /// <summary>Check if a day exists.</summary>
        public static bool HasDay(DateTime date) => _days.Any(d => d.Date.Date == date.Date);

        /// <summary>Get a specific day's data.</summary>
        public static NoteDay? GetDay(DateTime date) => _days.FirstOrDefault(d => d.Date.Date == date.Date);

        /// <summary>Add a bullet to a specific day.</summary>
        public static NoteBullet AddBullet(NoteDay day, string content = "")
        {
            var bullet = new NoteBullet { Content = content };
            day.Bullets.Add(bullet);
            ScheduleSave();
            return bullet;
        }

        public static NoteBullet InsertBullet(NoteDay day, int index, string content = "")
        {
            var bullet = new NoteBullet { Content = content };
            if (index < 0 || index >= day.Bullets.Count)
            {
                day.Bullets.Add(bullet);
            }
            else
            {
                day.Bullets.Insert(index, bullet);
            }
            
            for (int i = 0; i < day.Bullets.Count; i++)
            {
                day.Bullets[i].SortOrder = i;
            }
            
            ScheduleSave();
            return bullet;
        }


        /// <summary>Remove a bullet from a day.</summary>
        public static void RemoveBullet(NoteDay day, NoteBullet bullet)
        {
            day.Bullets.Remove(bullet);
            // Clean up image file if exists
            if (bullet.HasImage)
            {
                try { File.Delete(bullet.ImagePath); } catch { } // Best-effort: failure is acceptable
            }
            ScheduleSave();
        }

        /// <summary>
        /// Save an image from clipboard/file to the notes images directory.
        /// Returns the saved file path.
        /// </summary>
        public static async Task<string> SaveImage(System.Windows.Media.Imaging.BitmapSource image)
        {
            // NM-1 FIX: Cap image dimensions to 4096×4096 to prevent huge PNG writes
            const int MAX_DIM = 4096;
            if (image.PixelWidth > MAX_DIM || image.PixelHeight > MAX_DIM)
            {
                double scale = Math.Min((double)MAX_DIM / image.PixelWidth, (double)MAX_DIM / image.PixelHeight);
                var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
                    image,
                    new System.Windows.Media.ScaleTransform(scale, scale));
                scaled.Freeze();
                image = scaled;
            }

            // Freeze so the BitmapSource can be used on a background thread
            if (!image.IsFrozen) image.Freeze();

            var dir = GetImagesDirectory();
            string filename = $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..6]}.png";
            string path = Path.Combine(dir, filename);

            // [FIX C-2]: Add error handling for image save
            try
            {
                if (!DiskSpaceHelper.HasSufficientDiskSpace(path, 5_000_000)) // 5MB buffer
                {
                    Logger.LogAction("NOTE_IMAGE", "Insufficient disk space for image save");
                    try { System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Cannot save — disk is full. Free up space to prevent data loss.")); } catch { }
                    return null;
                }

                // Offload PNG encode + file I/O to background thread to avoid UI lag
                await Task.Run(() =>
                {
                    using var stream = new FileStream(path, FileMode.Create);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    encoder.Save(stream);
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTE_IMAGE_ERR", $"Failed to save image: {ex.Message}");
                // Clean up partial file
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return null;
            }

            return path;
        }

        /// <summary>Mark data as dirty — will persist on next debounce cycle.</summary>
        public static void MarkDirty()
        {
            Interlocked.Exchange(ref _isDirty, 1);
            ScheduleSave();
        }

        /// <summary>
        /// Schedule a debounced save (2-second cooldown).
        /// </summary>
        public static void ScheduleSave()
        {
            Interlocked.Exchange(ref _isDirty, 1);
            lock (_lock)
            {
                _saveTimer?.Dispose();
                _saveTimer = new Timer(_ =>
                {
                    if (Interlocked.CompareExchange(ref _isDirty, 0, 1) == 0) return; // was already clean
                    SaveNow();
                }, null, 2000, Timeout.Infinite);
            }
        }

        public static Task? LastSaveTask;

        /// <summary>Immediately persist to disk (atomic write). AUDIT FIX: serialization moved off UI thread.</summary>
        public static void SaveNow()
        {
            if (!_isLoaded) return;

            // Take lightweight snapshot on current thread (fast — just list copy)
            List<NoteDay> snapshot;
            List<NoteDay> allDaysCopy;
            List<NoteFolder> foldersCopy;
            try
            {
                snapshot = _days.ToList();
                foldersCopy = _folders.ToList();
                lock (_lock)
                {
                    var visibleDates = new HashSet<DateTime>(snapshot.Select(d => d.Date.Date));
                    int maxDays = LicenseManager.GetNoteHistoryDays();
                    DateTime? cutoff = maxDays < int.MaxValue ? (DateTime?)DateTime.Today.AddDays(-maxDays) : null;

                    _allDays.RemoveAll(d => {
                        if (cutoff.HasValue && d.Date.Date < cutoff.Value.Date) return false;
                        return !visibleDates.Contains(d.Date.Date);
                    });

                    foreach (var snapDay in snapshot)
                    {
                        int idx = _allDays.FindIndex(d => d.Date.Date == snapDay.Date.Date);
                        if (idx >= 0) _allDays[idx] = snapDay;
                        else _allDays.Add(snapDay);
                    }
                    _allDays = _allDays.OrderByDescending(d => d.Date).ToList();
                    allDaysCopy = _allDays.ToList();
                }
            }
            catch
            {
                return;
            }

            // AUDIT FIX: Move heavy serialization + disk write entirely to background thread
            LastSaveTask = Task.Run(() =>
            {
                string jsonStr = SerializeSnapshot(allDaysCopy, foldersCopy);
                SaveSnapshotJson(snapshot, jsonStr);
            });
        }

        public static void SaveNowSync()
        {
            if (!_isLoaded) return;
            List<NoteDay> snapshot;
            List<NoteFolder> foldersCopy;
            string jsonStr;
            try
            {
                snapshot = _days.ToList();
                foldersCopy = _folders.ToList();
                lock (_lock)
                {
                    var visibleDates = new HashSet<DateTime>(snapshot.Select(d => d.Date.Date));
                    int maxDays = LicenseManager.GetNoteHistoryDays();
                    DateTime? cutoff = maxDays < int.MaxValue ? (DateTime?)DateTime.Today.AddDays(-maxDays) : null;

                    _allDays.RemoveAll(d => {
                        if (cutoff.HasValue && d.Date.Date < cutoff.Value.Date) return false;
                        return !visibleDates.Contains(d.Date.Date);
                    });

                    foreach (var snapDay in snapshot)
                    {
                        int idx = _allDays.FindIndex(d => d.Date.Date == snapDay.Date.Date);
                        if (idx >= 0) _allDays[idx] = snapDay;
                        else _allDays.Add(snapDay);
                    }
                    _allDays = _allDays.OrderByDescending(d => d.Date).ToList();

                    var serializableList = _allDays.ToList();
                    jsonStr = SerializeSnapshot(serializableList, foldersCopy);
                }
            }
            catch { return; }
            SaveSnapshotJson(snapshot, jsonStr);
        }

        /// <summary>Serialize a snapshot list to JSON string (called on the thread that owns the data).</summary>
        private static string SerializeSnapshot(List<NoteDay> snapshot, List<NoteFolder> foldersSnapshot)
        {
            var data = new NotesDataV2 { Folders = foldersSnapshot, Days = snapshot };
            return JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        /// <summary>Merge snapshot into _allDays, re-serialize under lock, and write to disk.</summary>
        private static void SaveSnapshotJson(List<NoteDay> snapshot, string preSerializedJson)
        {
            if (snapshot.Count == 0) { Logger.LogAction("NOTES_SAVE", "Skipping save — empty snapshot"); return; }
            string json = preSerializedJson;


            if (!_fileLock.Wait(TimeSpan.FromSeconds(30)))
            {
                Logger.LogAction("NOTES", "Failed to acquire file lock within 30s — skipping save");
                return;
            }
            try
            {
                if (!Directory.Exists(_appDataDir))
                    Directory.CreateDirectory(_appDataDir);

                // Create backup copy first
                if (File.Exists(_notesPath))
                {
                    try
                    {
                        File.Copy(_notesPath, _notesPath + ".bak", overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("NOTES", $"Failed to create notes backup: {ex.Message}");
                    }
                }

                // Atomic write: tmp → rename
                string tmpPath = _notesPath + ".tmp";
                if (!DiskSpaceHelper.HasSufficientDiskSpace(_notesPath, json.Length * 2 + 1_000_000))
                {
                    Logger.LogAction("NOTES", "Insufficient disk space to save notes — skipping write");
                    try { System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Cannot save — disk is full. Free up space to prevent data loss.")); } catch { }
                    return;
                }
                File.WriteAllText(tmpPath, json);
                // AUDIT FIX: Retry loop for File.Move to handle transient file locks (AV, indexers)
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        File.Move(tmpPath, _notesPath, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"Failed to save notes: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Search all notes for a query string. Returns matching bullets with their parent day.
        /// </summary>
        // NM-15b FIX: Cap search results to prevent UI freezes on large note histories
        private const int MAX_SEARCH_RESULTS = 200;

        /// <summary>
        /// Extracts a contextual snippet of text surrounding the search query.
        /// Normalizes line breaks and whitespace for card preview.
        /// </summary>
        public static string GetSmartSnippet(string? text, string query, int maxLen = 180)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            // Normalize newlines and excessive spaces for compact preview
            string cleaned = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"[\r\n]+", " ");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ");

            if (cleaned.Length <= maxLen) return cleaned;

            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Concat(cleaned.AsSpan(0, maxLen).TrimEnd(), "…");
            }

            // Find match location (exact query, or individual tokens)
            int matchIdx = cleaned.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (matchIdx < 0)
            {
                var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var w in words)
                {
                    if (w.Length >= 2)
                    {
                        matchIdx = cleaned.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                        if (matchIdx >= 0) break;
                    }
                }
            }

            if (matchIdx < 0)
            {
                return string.Concat(cleaned.AsSpan(0, maxLen).TrimEnd(), "…");
            }

            // Center the snippet around the match
            int leadChars = Math.Min(35, maxLen / 4);
            int start = Math.Max(0, matchIdx - leadChars);

            // Snap forward to word boundary if not at start
            if (start > 0)
            {
                int spaceIdx = cleaned.IndexOf(' ', start);
                if (spaceIdx >= 0 && spaceIdx < matchIdx)
                {
                    start = spaceIdx + 1;
                }
            }

            int length = Math.Min(maxLen, cleaned.Length - start);
            string snippet = cleaned.Substring(start, length).Trim();

            if (start > 0)
            {
                snippet = "…" + snippet;
            }
            if (start + length < cleaned.Length)
            {
                snippet = snippet.TrimEnd('.', ' ', ',') + "…";
            }

            return snippet;
        }

        public static List<(NoteDay Day, NoteBullet Bullet)> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new();
            string q = query.Trim();

            List<NoteDay> snapshot;
            lock (_lock) { snapshot = _days.ToList(); }

            var results = new List<(NoteDay Day, NoteBullet Bullet, double Score)>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var day in snapshot)
            {
                // 1. Primary: Search modern freeform sections ("pages")
                bool hasFreeformSections = day.FreeformSections != null && day.FreeformSections.Count > 0;
                if (hasFreeformSections)
                {
                    int totalSections = day.FreeformSections!.Count;
                    for (int secIdx = 0; secIdx < totalSections; secIdx++)
                    {
                        var sec = day.FreeformSections[secIdx];
                        string content = sec.Content ?? "";
                        string title = sec.Title ?? "";

                        bool matchContent = FuzzyMatcher.IsMatch(q, content);
                        bool matchTitle = !string.IsNullOrEmpty(title) && FuzzyMatcher.IsMatch(q, title);

                        // Check subnotes in this section
                        bool matchSubnotes = false;
                        string matchingSubnoteSnippet = "";
                        if (sec.SubNotes != null)
                        {
                            foreach (var sn in sec.SubNotes)
                            {
                                if (FuzzyMatcher.IsMatch(q, sn.Content ?? "") || (!string.IsNullOrEmpty(sn.Title) && FuzzyMatcher.IsMatch(q, sn.Title)))
                                {
                                    matchSubnotes = true;
                                    if (string.IsNullOrEmpty(matchingSubnoteSnippet))
                                    {
                                        string subTitle = string.IsNullOrEmpty(sn.Title) ? "" : $"[{sn.Title}] ";
                                        matchingSubnoteSnippet = subTitle + (sn.Content ?? "");
                                    }
                                }
                            }
                        }

                        if (matchContent || matchTitle || matchSubnotes)
                        {
                            string dedupKey = $"{day.Date:yyyyMMdd}_sec_{sec.Id}";
                            if (!seenKeys.Add(dedupKey)) continue;

                            int pageNum = secIdx + 1;
                            string headerText;
                            if (totalSections > 1)
                            {
                                // Multi-page note: clearly label the page number
                                headerText = string.IsNullOrEmpty(title) ? $"Page {pageNum}" : $"Page {pageNum}: {title}";
                            }
                            else
                            {
                                // Single-page note: keep title clean
                                headerText = title;
                            }

                            string snippetSource = matchContent ? content : (matchSubnotes ? matchingSubnoteSnippet : content);
                            string snippet = GetSmartSnippet(snippetSource, q, 180);

                            var virtualBullet = new NoteBullet
                            {
                                Id = "section_" + sec.Id,
                                Header = headerText,
                                Content = snippet,
                                CreatedAt = day.Date
                            };

                            double score = Math.Max(FuzzyMatcher.Score(q, content), FuzzyMatcher.Score(q, title));
                            if (matchSubnotes)
                            {
                                score = Math.Max(score, FuzzyMatcher.Score(q, matchingSubnoteSnippet));
                            }

                            results.Add((day, virtualBullet, score));
                            if (results.Count >= MAX_SEARCH_RESULTS) break;
                        }
                    }
                }
                else if (day.Bullets != null && day.Bullets.Count > 0)
                {
                    // 2. Fallback: Search legacy bullets (only if no freeform sections exist)
                    foreach (var bullet in day.Bullets)
                    {
                        bool matchContent = FuzzyMatcher.IsMatch(q, bullet.Content ?? "");
                        bool matchHeader = FuzzyMatcher.IsMatch(q, bullet.Header ?? "");
                        bool matchTags = bullet.Tags.Any(t => FuzzyMatcher.IsMatch(q, t));
                        bool matchSub = bullet.SubBullets.Any(sb => FuzzyMatcher.IsMatch(q, sb.Text ?? ""));
                        if (matchContent || matchHeader || matchTags || matchSub)
                        {
                            string dedupKey = $"{day.Date:yyyyMMdd}_bullet_{bullet.Id}";
                            if (!seenKeys.Add(dedupKey)) continue;

                            double score = Math.Max(
                                FuzzyMatcher.Score(q, bullet.Content ?? ""),
                                FuzzyMatcher.Score(q, bullet.Header ?? "")
                            );
                            results.Add((day, bullet, score));
                            if (results.Count >= MAX_SEARCH_RESULTS) break;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(day.FreeformContent) && FuzzyMatcher.IsMatch(q, day.FreeformContent))
                {
                    // 3. Fallback: Unmigrated legacy freeform content (only if no sections and no bullets exist)
                    string dedupKey = $"{day.Date:yyyyMMdd}_legacy";
                    if (seenKeys.Add(dedupKey))
                    {
                        var virtualBullet = new NoteBullet
                        {
                            Id = "freeform_" + day.Date.Ticks.ToString(CultureInfo.InvariantCulture),
                            Content = GetSmartSnippet(day.FreeformContent, q, 180),
                            CreatedAt = day.Date
                        };
                        double score = FuzzyMatcher.Score(q, day.FreeformContent);
                        results.Add((day, virtualBullet, score));
                        if (results.Count >= MAX_SEARCH_RESULTS) break;
                    }
                }

                if (results.Count >= MAX_SEARCH_RESULTS) break;
            }

            // Rank results by match score (highest score first)
            results.Sort((a, b) => b.Score.CompareTo(a.Score));

            return results.Select(r => (r.Day, r.Bullet)).ToList();
        }

        // ═══════════════════════════════════════════════════════════
        // EXPORT
        // ═══════════════════════════════════════════════════════════

        /// <summary>Export a day's notes as Markdown text.</summary>
        public static string ExportToMarkdown(NoteDay day)
        {
            if (day == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"# Notes — {day.Date:MMMM d, yyyy}");
            sb.AppendLine();

            if (day.IsFreeformMode || day.FreeformSections.Any(s => !string.IsNullOrEmpty(s.Content)))
            {
                foreach (var section in day.FreeformSections)
                {
                    if (!string.IsNullOrEmpty(section.Content))
                    {
                        sb.AppendLine(section.Content);
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.AppendLine();
                    }
                }
            }

            foreach (var bullet in day.Bullets)
            {
                if (!string.IsNullOrEmpty(bullet.Header))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"## {bullet.Header}");
                if (!string.IsNullOrEmpty(bullet.Content))
                    sb.AppendLine(bullet.Content);
                if (bullet.HasTags)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"*Tags: {bullet.TagsDisplay}*");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>Export a day's notes as plain text.</summary>
        public static string ExportToText(NoteDay day)
        {
            if (day == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Notes — {day.Date:MMMM d, yyyy}");
            sb.AppendLine(new string('─', 40));
            sb.AppendLine();

            if (day.IsFreeformMode || day.FreeformSections.Any(s => !string.IsNullOrEmpty(s.Content)))
            {
                foreach (var section in day.FreeformSections)
                {
                    if (!string.IsNullOrEmpty(section.Content))
                    {
                        sb.AppendLine(section.Content);
                        sb.AppendLine();
                    }
                }
            }

            foreach (var bullet in day.Bullets)
            {
                if (!string.IsNullOrEmpty(bullet.Header))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"• {bullet.Header}");
                if (!string.IsNullOrEmpty(bullet.Content))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {bullet.Content}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>Permanently deletes a bullet from the given day.</summary>
        public static void DeleteBullet(NoteDay day, NoteBullet bullet)
        {
            // PM-5 FIX: Wrap mutation in lock to prevent concurrent access
            lock (_lock)
            {
                day.Bullets.Remove(bullet);
            }
            ScheduleSave();
        }

        // ═══════════════════════════════════════════════════════════
        // MOBILE SYNC
        // ═══════════════════════════════════════════════════════════

        public static string GetSyncPayload()
        {
            List<NoteDay> snapshot;
            lock (_lock)
            {
                if (!_isLoaded) return "[]";
                try { snapshot = _days.ToList(); } catch (Exception ex) { Logger.LogAction("SYNC", $"GetSyncPayload error: {ex.Message}"); return "[]"; }
            }

            // Build sync-safe payload
            var payload = snapshot.Select(day => {
                long lastMod = 0;
                foreach (var b in day.Bullets)
                {
                    long bTs = new DateTimeOffset(b.LastEdited).ToUnixTimeMilliseconds();
                    if (bTs > lastMod) lastMod = bTs;
                }
                foreach (var s in day.FreeformSections)
                {
                    long sTs = new DateTimeOffset(s.CreatedAt).ToUnixTimeMilliseconds();
                    if (sTs > lastMod) lastMod = sTs;
                }
                if (lastMod == 0) lastMod = new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();

                return new {
                    Date = day.Date.ToString("o", CultureInfo.InvariantCulture),
                    IsFreeformMode = day.IsFreeformMode,
                    Bullets = day.Bullets.Select(b => new {
                        b.Id, b.Header, b.Content, b.IsCollapsed,
                        b.ImageDisplayWidth, b.ImageDisplayWidth2,
                        CreatedAt = b.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                        LastEdited = b.LastEdited.ToString("o", CultureInfo.InvariantCulture),
                        b.Tags, b.Color, b.IsPinned, b.SortOrder,
                        b.CreatedByDevice, b.LastEditedByDevice,
                        SubBullets = b.SubBullets.Select(sb => new { sb.Id, sb.Text, sb.IsDone }).ToList()
                    }).ToList(),
                    FreeformSections = day.FreeformSections.Select(s => new {
                        s.Id, s.Title, s.Content, CreatedAt = s.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                        ImageCount = s.Images?.Count ?? 0
                    }).ToList(),
                    LastModified = lastMod
                };
            }).ToList();

            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        }

        /// <summary>
        /// Returns a sync payload filtered by the given predicate.
        /// Uses the same anonymous-object projection as GetSyncPayload for consistent JSON format.
        /// </summary>
        public static string GetSyncPayloadFiltered(Func<NoteDay, bool> predicate)
        {
            List<NoteDay> snapshot;
            lock (_lock)
            {
                if (!_isLoaded) return "[]";
                try { snapshot = _days.Where(predicate).ToList(); } catch { return "[]"; }
            }

            var payload = snapshot.Select(day => {
                long lastMod = 0;
                foreach (var b in day.Bullets)
                {
                    long bTs = new DateTimeOffset(b.LastEdited).ToUnixTimeMilliseconds();
                    if (bTs > lastMod) lastMod = bTs;
                }
                foreach (var s in day.FreeformSections)
                {
                    long sTs = new DateTimeOffset(s.CreatedAt).ToUnixTimeMilliseconds();
                    if (sTs > lastMod) lastMod = sTs;
                }
                if (lastMod == 0) lastMod = new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();

                return new {
                    Date = day.Date.ToString("o", CultureInfo.InvariantCulture),
                    IsFreeformMode = day.IsFreeformMode,
                    Bullets = day.Bullets.Select(b => new {
                        b.Id, b.Header, b.Content, b.IsCollapsed,
                        b.ImageDisplayWidth, b.ImageDisplayWidth2,
                        CreatedAt = b.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                        LastEdited = b.LastEdited.ToString("o", CultureInfo.InvariantCulture),
                        b.Tags, b.Color, b.IsPinned, b.SortOrder,
                        b.CreatedByDevice, b.LastEditedByDevice,
                        SubBullets = b.SubBullets.Select(sb => new { sb.Id, sb.Text, sb.IsDone }).ToList()
                    }).ToList(),
                    FreeformSections = day.FreeformSections.Select(s => new {
                        s.Id, s.Title, s.Content, CreatedAt = s.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                        ImageCount = s.Images?.Count ?? 0
                    }).ToList(),
                    LastModified = lastMod
                };
            }).ToList();

            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        }

        public static void MergeFromMobile(string json, string? deviceName = null)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                if (!_isLoaded) Load();

                var remoteDays = JsonSerializer.Deserialize<List<NoteDay>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (remoteDays == null || remoteDays.Count == 0) return;

                bool changed = false;
                lock (_lock)
                {
                    foreach (var remoteDay in remoteDays)
                    {
                        remoteDay.Date = remoteDay.Date.Kind == DateTimeKind.Utc ? remoteDay.Date.ToLocalTime().Date : remoteDay.Date.Date;
                        remoteDay.MigrateFreeformIfNeeded();

                        long remoteMod = remoteDay.LastModified ?? 0;

                        var localDay = _allDays.FirstOrDefault(d => d.Date.Date == remoteDay.Date.Date);
                        if (localDay == null)
                        {
                            // New day from mobile — add it
                            _allDays.Add(remoteDay);
                            // Tag new day's bullets with device origin
                            if (!string.IsNullOrEmpty(deviceName))
                            {
                                foreach (var b in remoteDay.Bullets)
                                {
                                    b.LastEditedByDevice = deviceName;
                                    if (string.IsNullOrEmpty(b.CreatedByDevice))
                                        b.CreatedByDevice = deviceName;
                                }
                            }
                            changed = true;
                        }
                        else
                        {
                            // XP-1 FIX: Per-bullet merge (mirrors TodoManager per-item merge pattern).
                            // Previously the entire day's Bullets collection was replaced when the
                            // remote day was newer — concurrent edits to OTHER bullets were silently lost.
                            // Bullets now merge by Id (newer LastEdited wins).
                            bool dayChanged = false;

                            // Build lookup of local bullets by ID
                            var bulletsById = new Dictionary<string, NoteBullet>(StringComparer.Ordinal);
                            foreach (var lb in localDay.Bullets)
                                if (!string.IsNullOrEmpty(lb.Id)) bulletsById[lb.Id] = lb;

                            // Merge remote bullets: newer wins, new bullets are added
                            foreach (var rb in remoteDay.Bullets)
                            {
                                if (string.IsNullOrEmpty(rb.Id)) continue;
                                bool exists = bulletsById.TryGetValue(rb.Id, out var existingBullet);
                                if (!exists || rb.LastEdited > existingBullet!.LastEdited)
                                {
                                    // Tag with device origin
                                    if (!string.IsNullOrEmpty(deviceName))
                                    {
                                        rb.LastEditedByDevice = deviceName;
                                        if (string.IsNullOrEmpty(rb.CreatedByDevice))
                                            rb.CreatedByDevice = exists ? existingBullet!.CreatedByDevice : deviceName;
                                    }
                                    bulletsById[rb.Id] = rb;
                                    dayChanged = true;
                                }
                            }

                            // Merge freeform sections by ID (same pattern)
                            var sectionsById = new Dictionary<string, FreeformSection>(StringComparer.Ordinal);
                            foreach (var ls in localDay.FreeformSections)
                                if (!string.IsNullOrEmpty(ls.Id)) sectionsById[ls.Id] = ls;
                            foreach (var rs in remoteDay.FreeformSections)
                            {
                                if (string.IsNullOrEmpty(rs.Id)) continue;
                                bool exists = sectionsById.TryGetValue(rs.Id, out var existingSection);
                                if (!exists || rs.CreatedAt > existingSection!.CreatedAt)
                                {
                                    sectionsById[rs.Id] = rs;
                                    dayChanged = true;
                                }
                            }

                            if (dayChanged)
                            {
                                var mergedBullets = new System.Collections.ObjectModel.ObservableCollection<NoteBullet>(
                                    bulletsById.Values.OrderBy(b => b.SortOrder).ToList());
                                var mergedSections = new System.Collections.ObjectModel.ObservableCollection<FreeformSection>(
                                    sectionsById.Values.OrderBy(s => s.CreatedAt).ToList());
                                bool mergedFreeform = remoteDay.IsFreeformMode;
                                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                {
                                    localDay.Bullets = mergedBullets;
                                    localDay.FreeformSections = mergedSections;
                                    localDay.IsFreeformMode = mergedFreeform;
                                });
                                changed = true;
                            }
                        }
                    }
                    if (changed)
                    {
                        _allDays = _allDays.OrderByDescending(d => d.Date).ToList();
                        FilterVisibleDays();
                    }
                }
                if (changed) ScheduleSave();
                Logger.LogAction("NOTES_SYNC", $"Merged {remoteDays.Count} days from mobile (changed={changed})");
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES_SYNC", $"MergeFromMobile failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // FOLDER CRUD
        // ═══════════════════════════════════════════════════════════

        /// <summary>Create a new folder, optionally nested under a parent.</summary>
        public static NoteFolder CreateFolder(string name, string? parentId = null)
        {
            var folder = new NoteFolder
            {
                Name = name,
                ParentId = parentId,
                SortOrder = _folders.Count
            };
            _folders.Add(folder);
            MarkDirty();
            return folder;
        }

        /// <summary>Rename a folder.</summary>
        public static void RenameFolder(string folderId, string newName)
        {
            var folder = _folders.FirstOrDefault(f => f.Id == folderId);
            if (folder != null)
            {
                folder.Name = newName;
                folder.LastModified = DateTime.Now;
                MarkDirty();
            }
        }

        /// <summary>Delete a folder and optionally move its children to the parent.</summary>
        public static void DeleteFolder(string folderId)
        {
            var folder = _folders.FirstOrDefault(f => f.Id == folderId);
            if (folder == null) return;
            
            // Re-parent child folders to the deleted folder's parent
            foreach (var child in _folders.Where(f => f.ParentId == folderId).ToList())
            {
                child.ParentId = folder.ParentId;
            }
            
            // Un-assign notes from deleted folder (move to root/daily)
            foreach (var day in _allDays.Where(d => d.FolderId == folderId))
            {
                day.FolderId = folder.ParentId;
            }
            
            _folders.Remove(folder);
            MarkDirty();
        }

        /// <summary>Get child folders of a parent (null = root level).</summary>
        public static IEnumerable<NoteFolder> GetChildFolders(string? parentId)
        {
            return _folders.Where(f => f.ParentId == parentId).OrderBy(f => f.SortOrder);
        }

        /// <summary>Get notes assigned to a specific folder.</summary>
        public static IEnumerable<NoteDay> GetNotesInFolder(string? folderId)
        {
            return _allDays.Where(d => d.FolderId == folderId).OrderByDescending(d => d.Date);
        }

        /// <summary>Move a note day to a folder.</summary>
        public static void MoveNoteToFolder(NoteDay day, string? folderId)
        {
            day.FolderId = folderId;
            MarkDirty();
        }

        /// <summary>Wrapper for the new JSON schema that includes folders.</summary>
        private class NotesDataV2
        {
            public int Version { get; set; } = 2;
            public List<NoteFolder> Folders { get; set; } = new();
            public List<NoteDay> Days { get; set; } = new();
        }
    }
}
