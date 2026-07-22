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
                        _isLoaded = true;
                        return;
                    }

                    // NM-4 FIX: Use RunWithRetry so a brief file lock from concurrent .bak copy doesn't immediately fall to backup recovery
                    string json = RunWithRetry(() => File.ReadAllText(_notesPath));
                    var loaded = JsonSerializer.Deserialize<List<NoteDay>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded != null)
                    {
                        // Normalize all dates to local timezone to prevent UTC timezone shift bugs
                        foreach (var d in loaded)
                        {
                            d.Date = d.Date.Kind == DateTimeKind.Utc ? d.Date.ToLocalTime().Date : d.Date.Date;
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
                            var loadedBackup = JsonSerializer.Deserialize<List<NoteDay>>(backupJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (loadedBackup != null)
                            {
                                foreach (var d in loadedBackup)
                                {
                                    d.Date = d.Date.Kind == DateTimeKind.Utc ? d.Date.ToLocalTime().Date : d.Date.Date;
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
            int maxDays = LicenseManager.GetNoteHistoryDays();
            ObservableCollection<NoteDay> newDays;
            if (maxDays < int.MaxValue)
            {
                DateTime cutoff = DateTime.Today.AddDays(-maxDays);
                var visible = _allDays.Where(d => d.Date.Date >= cutoff.Date).ToList();
                if (_allDays.Count > visible.Count)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        UpgradePrompt.ShowNoteHistoryLimit());
                }
                newDays = new ObservableCollection<NoteDay>(visible);
            }
            else
            {
                newDays = new ObservableCollection<NoteDay>(_allDays);
            }
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
                    int maxDays = LicenseManager.GetNoteHistoryDays();
                    if (maxDays == int.MaxValue || dateOnly >= DateTime.Today.AddDays(-maxDays))
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

            // Offload PNG encode + file I/O to background thread to avoid UI lag
            await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Create);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                encoder.Save(stream);
            });

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

        /// <summary>Immediately persist to disk (atomic write).</summary>
        public static void SaveNow()
        {
            // DEADLOCK FIX: Snapshot the collection OUTSIDE the lock.
            // Previously, Dispatcher.Invoke() was called while holding _lock,
            // causing AB-BA deadlock when the UI thread also needed _lock.
            if (!_isLoaded) return;

            // NM-2a FIX: Serialize to JSON on the calling thread so the snapshot is
            // truly atomic with respect to the ObservableCollection. Only the file
            // I/O runs on a background thread.
            List<NoteDay> snapshot;
            try
            {
                // Must read ObservableCollection on UI thread if it was created there
                if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        List<NoteDay> snap;
                        try { snap = _days.ToList(); } catch { return; }
                        // NM-2a FIX: Serialize immediately on the UI thread
                        string jsonStr;
                        try
                        {
                            jsonStr = SerializeSnapshot(snap);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("NOTES", $"Failed to serialize notes snapshot: {ex.Message}");
                            return;
                        }
                        Task.Run(() => SaveSnapshotJson(snap, jsonStr));
                    });
                    return;
                }
                else
                {
                    snapshot = _days.ToList();
                }
            }
            catch
            {
                try { snapshot = _days.ToList(); } catch { return; }
            }

            // NM-2a FIX: Serialize on this thread before handing off to background
            string json;
            try
            {
                json = SerializeSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"Failed to serialize notes snapshot: {ex.Message}");
                return;
            }

            // Run merge + file I/O on a background thread so it doesn't block the UI thread
            Task.Run(() => SaveSnapshotJson(snapshot, json));
        }

        /// <summary>Serialize a snapshot list to JSON string (called on the thread that owns the data).</summary>
        private static string SerializeSnapshot(List<NoteDay> snapshot)
        {
            return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        /// <summary>Merge snapshot into _allDays, re-serialize under lock, and write to disk.</summary>
        private static void SaveSnapshotJson(List<NoteDay> snapshot, string preSerializedJson)
        {
            string json;
            // NM-2 FIX: Merge both lock acquisitions into one so _allDays cannot be mutated
            // between the merge step and the serialization step.
            lock (_lock)
            {
                // Merge snapshot back into _allDays
                var visibleDates = new HashSet<DateTime>(snapshot.Select(d => d.Date.Date));
                int maxDays = LicenseManager.GetNoteHistoryDays();
                DateTime? cutoff = maxDays < int.MaxValue ? (DateTime?)DateTime.Today.AddDays(-maxDays) : null;

                // 1. Remove visible days from _allDays if they are no longer present in the snapshot (user deleted them)
                _allDays.RemoveAll(d => {
                    if (cutoff.HasValue && d.Date.Date < cutoff.Value.Date) return false; // Keep hidden history
                    return !visibleDates.Contains(d.Date.Date); // Delete if it was visible but user removed it
                });

                // 2. Add or update days from snapshot
                foreach (var snapDay in snapshot)
                {
                    int idx = _allDays.FindIndex(d => d.Date.Date == snapDay.Date.Date);
                    if (idx >= 0)
                    {
                        _allDays[idx] = snapDay;
                    }
                    else
                    {
                        _allDays.Add(snapDay);
                    }
                }

                // Sort newest first
                _allDays = _allDays.OrderByDescending(d => d.Date).ToList();

                // Re-serialize _allDays (which now includes hidden history) inside the lock
                try
                {
                    json = JsonSerializer.Serialize(_allDays, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogAction("NOTES", $"Failed to serialize notes: {ex.Message}");
                    return;
                }
            }

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
                    return;
                }
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _notesPath, overwrite: true);
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

        public static List<(NoteDay Day, NoteBullet Bullet)> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new();
            string q = query.Trim();

            List<NoteDay> snapshot;
            lock (_lock) { snapshot = _days.ToList(); }

            var results = new List<(NoteDay, NoteBullet)>();
            foreach (var day in snapshot)
            {
                foreach (var bullet in day.Bullets)
                {
                    bool matchContent = FuzzyMatcher.IsMatch(q, bullet.Content ?? "");
                    bool matchHeader = FuzzyMatcher.IsMatch(q, bullet.Header ?? "");
                    bool matchTags = bullet.Tags.Any(t => FuzzyMatcher.IsMatch(q, t));
                    if (matchContent || matchHeader || matchTags)
                    {
                        results.Add((day, bullet));
                        if (results.Count >= MAX_SEARCH_RESULTS) return results;
                    }
                }
                // Also search freeform content — create a virtual bullet for display
                if (!string.IsNullOrEmpty(day.FreeformContent) && FuzzyMatcher.IsMatch(q, day.FreeformContent))
                {
                    var virtualBullet = new NoteBullet
                    {
                        Id = "freeform_" + day.Date.Ticks.ToString(CultureInfo.InvariantCulture),
                        Content = day.FreeformContent.Length > 200 ? string.Concat(day.FreeformContent.AsSpan(0, 200), "...") : day.FreeformContent,
                        CreatedAt = day.Date
                    };
                    results.Add((day, virtualBullet));
                    if (results.Count >= MAX_SEARCH_RESULTS) return results;
                }
            }
            return results;
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
    }
}
