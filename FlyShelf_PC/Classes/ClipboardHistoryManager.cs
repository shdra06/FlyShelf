using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Persists clipboard history (text + images) to disk so items survive app restarts.
    /// Uses SQLite (flyshelf.db) as the storage engine with FTS5 for sub-millisecond keyword searches
    /// and atomic transactions with Write-Ahead Logging (WAL) mode for database integrity.
    /// Images are stored permanently in %AppData%\FlyShelf\Images\.
    /// </summary>
    public static class ClipboardHistoryManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _historyPath = Path.Combine(_appDataDir, "clipboard_history.json");
        private static readonly string _journalPath = Path.Combine(_appDataDir, "clipboard_journal.jsonl");
        private static readonly string _imagesDir = Path.Combine(_appDataDir, "Images");

        private static Timer? _debounceTimer;
        private static readonly object _lock = new object();

        /// <summary>Maximum items to retain in history. Oldest items are evicted beyond this cap.</summary>
        private const int MAX_HISTORY_ITEMS = 2000;

        static ClipboardHistoryManager()
        {
            InitDb();
        }

        /// <summary>
        /// Returns the permanent SQLite connection string.
        /// </summary>
        private static string GetDbConnectionString()
        {
            string dbPath = Path.Combine(_appDataDir, "flyshelf.db");
            return $"Data Source={dbPath}";
        }

        /// <summary>
        /// Initializes the SQLite database, tables, virtual tables, triggers, and indices.
        /// Configures WAL mode for atomic safety and speed.
        /// </summary>
        public static void InitDb()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_appDataDir);
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            // Configure WAL mode & normal synchronous writes for power-loss safety
                            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                            cmd.ExecuteNonQuery();

                            // 1. Create base table
                            cmd.CommandText = @"
                                CREATE TABLE IF NOT EXISTS clipboard_items (
                                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                    DateCopied INTEGER NOT NULL,
                                    FilePath TEXT,
                                    ZippedArchivePath TEXT,
                                    FileName TEXT,
                                    Extension TEXT,
                                    AssociatedContextTitle TEXT,
                                    SourceDeviceName TEXT,
                                    SourceDeviceType TEXT,
                                    TransferMethod TEXT,
                                    FormattedSize TEXT,
                                    ItemType INTEGER NOT NULL,
                                    RawContent TEXT,
                                    IsPinned INTEGER DEFAULT 0,
                                    HasSmartAction INTEGER DEFAULT 0,
                                    SmartActionName TEXT,
                                    SmartActionIcon TEXT,
                                    SmartActionType TEXT,
                                    DetectedColor TEXT
                                );";
                            cmd.ExecuteNonQuery();

                            // 2. Create index on Pinned/DateCopied for fast paginated queries
                            cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_clipboard_items_pinned_date ON clipboard_items(IsPinned, DateCopied DESC);";
                            cmd.ExecuteNonQuery();

                            // 3. Create FTS5 virtual table
                            cmd.CommandText = @"
                                CREATE VIRTUAL TABLE IF NOT EXISTS clipboard_fts USING fts5(
                                    FileName,
                                    RawContent,
                                    content='clipboard_items',
                                    content_rowid='Id'
                                );";
                            cmd.ExecuteNonQuery();

                            // 4. Create database triggers to keep FTS5 virtual table synced automatically
                            cmd.CommandText = @"
                                CREATE TRIGGER IF NOT EXISTS clipboard_items_ai AFTER INSERT ON clipboard_items BEGIN
                                    INSERT INTO clipboard_fts(rowid, FileName, RawContent) VALUES (new.Id, new.FileName, new.RawContent);
                                END;";
                            cmd.ExecuteNonQuery();

                            cmd.CommandText = @"
                                CREATE TRIGGER IF NOT EXISTS clipboard_items_ad AFTER DELETE ON clipboard_items BEGIN
                                    INSERT INTO clipboard_fts(clipboard_fts, rowid, FileName, RawContent) VALUES('delete', old.Id, old.FileName, old.RawContent);
                                END;";
                            cmd.ExecuteNonQuery();

                            cmd.CommandText = @"
                                CREATE TRIGGER IF NOT EXISTS clipboard_items_au AFTER UPDATE ON clipboard_items BEGIN
                                    INSERT INTO clipboard_fts(clipboard_fts, rowid, FileName, RawContent) VALUES('delete', old.Id, old.FileName, old.RawContent);
                                    INSERT INTO clipboard_fts(rowid, FileName, RawContent) VALUES (new.Id, new.FileName, new.RawContent);
                                END;";
                            cmd.ExecuteNonQuery();
                        }
                    }

                    // Check and perform legacy migrations
                    MigrateLegacyFiles();
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_INIT_ERROR", $"Failed to initialize SQLite database: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Migrates legacy JSON snapshot files, journal files, and pinned files to SQLite and deletes them.
        /// </summary>
        private static void MigrateLegacyFiles()
        {
            try
            {
                var migratedItems = new List<ViewModels.ClipboardItem>();

                // 1. Read legacy clipboard_history.json
                if (File.Exists(_historyPath))
                {
                    try
                    {
                        var json = File.ReadAllText(_historyPath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            string trimmed = json.Trim();
                            List<ViewModels.ClipboardItem>? snapshot = null;
                            if (trimmed.StartsWith("["))
                            {
                                snapshot = JsonSerializer.Deserialize<List<ViewModels.ClipboardItem>>(json);
                            }
                            else if (trimmed.StartsWith("{"))
                            {
                                var container = JsonSerializer.Deserialize<ClipboardHistoryContainer>(json);
                                if (container != null) snapshot = container.Items;
                            }

                            if (snapshot != null)
                            {
                                foreach (var item in snapshot)
                                {
                                    item.IsPinned = false;
                                    migratedItems.Add(item);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("MIGRATION_WARN", $"Failed to read legacy history file: {ex.Message}");
                    }
                }

                // 2. Replay legacy journal
                if (File.Exists(_journalPath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(_journalPath);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                            if (entry == null) continue;

                            switch (entry.Action)
                            {
                                case "add":
                                    if (entry.Item != null)
                                    {
                                        entry.Item.IsPinned = false;
                                        migratedItems.Insert(0, entry.Item);
                                    }
                                    break;
                                case "delete":
                                    if (!string.IsNullOrEmpty(entry.ItemId))
                                    {
                                        migratedItems.RemoveAll(i => $"{i.ItemType}_{i.DateCopied.Ticks}_{i.GetHashCode():X4}" == entry.ItemId);
                                    }
                                    break;
                                case "clear":
                                    migratedItems.Clear();
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("MIGRATION_WARN", $"Failed to replay legacy journal: {ex.Message}");
                    }
                }

                // 3. Read legacy pinned_items.json
                var pinnedPath = Path.Combine(_appDataDir, "pinned_items.json");
                var migratedPinned = new List<ViewModels.ClipboardItem>();
                if (File.Exists(pinnedPath))
                {
                    try
                    {
                        var json = File.ReadAllText(pinnedPath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var snapshot = JsonSerializer.Deserialize<List<ViewModels.ClipboardItem>>(json);
                            if (snapshot != null)
                            {
                                foreach (var item in snapshot)
                                {
                                    item.IsPinned = true;
                                    migratedPinned.Add(item);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("MIGRATION_WARN", $"Failed to read legacy pinned file: {ex.Message}");
                    }
                }

                // Combine them
                var allItems = migratedPinned.Concat(migratedItems).ToList();
                var validItems = FilterValidItems(allItems);

                if (validItems.Count > 0)
                {
                    Logger.LogAction("MIGRATION_START", $"Migrating {validItems.Count} legacy items to SQLite...");
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var transaction = conn.BeginTransaction())
                        {
                            foreach (var item in validItems)
                            {
                                InsertItemInternal(conn, transaction, item);
                            }
                            transaction.Commit();
                        }
                    }
                    Logger.LogAction("MIGRATION_SUCCESS", $"Successfully imported {validItems.Count} items into SQLite database.");
                }

                // Cleanup legacy files to free disk space and avoid re-migration
                try
                {
                    if (File.Exists(_historyPath)) File.Delete(_historyPath);
                    if (File.Exists(_journalPath)) File.Delete(_journalPath);
                    if (File.Exists(pinnedPath)) File.Delete(pinnedPath);
                    Logger.LogAction("MIGRATION_CLEANUP", "Deleted legacy JSON files successfully.");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("MIGRATION_CLEANUP_WARN", $"Failed to delete some legacy files: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("MIGRATION_ERROR", $"Legacy migration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Inserts an item into the database helper.
        /// </summary>
        private static void InsertItemInternal(SqliteConnection conn, SqliteTransaction? transaction, ViewModels.ClipboardItem item)
        {
            using (var cmd = conn.CreateCommand())
            {
                if (transaction != null) cmd.Transaction = transaction;

                cmd.CommandText = @"
                    INSERT INTO clipboard_items (
                        DateCopied, FilePath, ZippedArchivePath, FileName, Extension,
                        AssociatedContextTitle, SourceDeviceName, SourceDeviceType, TransferMethod,
                        FormattedSize, ItemType, RawContent, IsPinned, HasSmartAction,
                        SmartActionName, SmartActionIcon, SmartActionType, DetectedColor
                    ) VALUES (
                        @DateCopied, @FilePath, @ZippedArchivePath, @FileName, @Extension,
                        @AssociatedContextTitle, @SourceDeviceName, @SourceDeviceType, @TransferMethod,
                        @FormattedSize, @ItemType, @RawContent, @IsPinned, @HasSmartAction,
                        @SmartActionName, @SmartActionIcon, @SmartActionType, @DetectedColor
                    );";

                cmd.Parameters.AddWithValue("@DateCopied", item.DateCopied.Ticks);
                cmd.Parameters.AddWithValue("@FilePath", item.FilePath ?? "");
                cmd.Parameters.AddWithValue("@ZippedArchivePath", item.ZippedArchivePath ?? "");
                cmd.Parameters.AddWithValue("@FileName", item.FileName ?? "");
                cmd.Parameters.AddWithValue("@Extension", item.Extension ?? "");
                cmd.Parameters.AddWithValue("@AssociatedContextTitle", item.AssociatedContextTitle ?? "");
                cmd.Parameters.AddWithValue("@SourceDeviceName", item.SourceDeviceName ?? "Local");
                cmd.Parameters.AddWithValue("@SourceDeviceType", item.SourceDeviceType ?? "PC");
                cmd.Parameters.AddWithValue("@TransferMethod", item.TransferMethod ?? "Local");
                cmd.Parameters.AddWithValue("@FormattedSize", item.FormattedSize ?? "");
                cmd.Parameters.AddWithValue("@ItemType", (int)item.ItemType);
                cmd.Parameters.AddWithValue("@RawContent", item.RawContent ?? "");
                cmd.Parameters.AddWithValue("@IsPinned", item.IsPinned ? 1 : 0);
                cmd.Parameters.AddWithValue("@HasSmartAction", item.HasSmartAction ? 1 : 0);
                cmd.Parameters.AddWithValue("@SmartActionName", item.SmartActionName ?? "");
                cmd.Parameters.AddWithValue("@SmartActionIcon", item.SmartActionIcon ?? "");
                cmd.Parameters.AddWithValue("@SmartActionType", item.SmartActionType ?? "");
                cmd.Parameters.AddWithValue("@DetectedColor", item.DetectedColor ?? "");

                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Returns the permanent image storage directory, creating it if needed.
        /// </summary>
        public static string GetPersistentImageDir()
        {
            Directory.CreateDirectory(_imagesDir);
            return _imagesDir;
        }

        /// <summary>
        /// Generates a unique permanent path for a clipboard image.
        /// </summary>
        public static string GetPersistentImagePath()
        {
            Directory.CreateDirectory(_imagesDir);
            return Path.Combine(_imagesDir, $"FlyShelf_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString().Substring(0, 4)}.png");
        }

        /// <summary>
        /// Loads persisted clipboard history from disk.
        /// Kept for backwards compatibility; loads the first page of history.
        /// </summary>
        public static List<ViewModels.ClipboardItem> LoadHistory()
        {
            return LoadHistoryPage(0, 100);
        }

        /// <summary>
        /// Loads a specific paginated page of unpinned history items from the database.
        /// </summary>
        public static List<ViewModels.ClipboardItem> LoadHistoryPage(int offset, int limit)
        {
            lock (_lock)
            {
                var items = new List<ViewModels.ClipboardItem>();
                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                SELECT DateCopied, FilePath, ZippedArchivePath, FileName, Extension,
                                       AssociatedContextTitle, SourceDeviceName, SourceDeviceType, TransferMethod,
                                       FormattedSize, ItemType, RawContent, IsPinned, HasSmartAction,
                                       SmartActionName, SmartActionIcon, SmartActionType, DetectedColor
                                FROM clipboard_items
                                WHERE IsPinned = 0
                                ORDER BY DateCopied DESC
                                LIMIT @limit OFFSET @offset;";

                            cmd.Parameters.AddWithValue("@limit", limit);
                            cmd.Parameters.AddWithValue("@offset", offset);

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    items.Add(ParseItem(reader));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_LOAD_ERROR", $"Failed to load history page: {ex.Message}");
                }
                return FilterValidItems(items);
            }
        }

        /// <summary>
        /// Loads all pinned clipboard items from SQLite.
        /// </summary>
        public static List<ViewModels.ClipboardItem> LoadPinnedHistory()
        {
            lock (_lock)
            {
                var items = new List<ViewModels.ClipboardItem>();
                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                SELECT DateCopied, FilePath, ZippedArchivePath, FileName, Extension,
                                       AssociatedContextTitle, SourceDeviceName, SourceDeviceType, TransferMethod,
                                       FormattedSize, ItemType, RawContent, IsPinned, HasSmartAction,
                                       SmartActionName, SmartActionIcon, SmartActionType, DetectedColor
                                FROM clipboard_items
                                WHERE IsPinned = 1
                                ORDER BY DateCopied DESC;";

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    items.Add(ParseItem(reader));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_LOAD_PINNED_ERROR", $"Failed to load pinned items: {ex.Message}");
                }
                return FilterValidItems(items);
            }
        }

        /// <summary>
        /// Parses a SqliteDataReader record into a ClipboardItem.
        /// </summary>
        private static ViewModels.ClipboardItem ParseItem(SqliteDataReader reader)
        {
            var item = new ViewModels.ClipboardItem
            {
                DateCopied = new DateTime(reader.GetInt64(0)),
                FilePath = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ZippedArchivePath = reader.IsDBNull(2) ? "" : reader.GetString(2),
                FileName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Extension = reader.IsDBNull(4) ? "" : reader.GetString(4),
                AssociatedContextTitle = reader.IsDBNull(5) ? "" : reader.GetString(5),
                SourceDeviceName = reader.IsDBNull(6) ? "Local" : reader.GetString(6),
                SourceDeviceType = reader.IsDBNull(7) ? "PC" : reader.GetString(7),
                TransferMethod = reader.IsDBNull(8) ? "Local" : reader.GetString(8),
                FormattedSize = reader.IsDBNull(9) ? "" : reader.GetString(9),
                ItemType = (ViewModels.ClipboardItemType)reader.GetInt32(10),
                RawContent = reader.IsDBNull(11) ? "" : reader.GetString(11),
                IsPinned = reader.GetInt32(12) == 1,
                HasSmartAction = reader.GetInt32(13) == 1,
                SmartActionName = reader.IsDBNull(14) ? "" : reader.GetString(14),
                SmartActionIcon = reader.IsDBNull(15) ? "" : reader.GetString(15),
                SmartActionType = reader.IsDBNull(16) ? "" : reader.GetString(16),
                DetectedColor = reader.IsDBNull(17) ? "" : reader.GetString(17)
            };
            return item;
        }

        /// <summary>
        /// Searches the history database using SQLite FTS5 MATCH indexing, with standard SQL LIKE as a fallback.
        /// </summary>
        public static List<ViewModels.ClipboardItem> SearchHistory(string query)
        {
            lock (_lock)
            {
                var items = new List<ViewModels.ClipboardItem>();
                if (string.IsNullOrWhiteSpace(query)) return items;

                string queryClean = query.Trim();
                // Tokenize and append '*' to each word for prefix autocomplete search
                var words = queryClean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string ftsQuery = string.Join(" ", words.Select(w => $"\"{w.Replace("\"", "\"\"")}*\""));

                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                SELECT i.DateCopied, i.FilePath, i.ZippedArchivePath, i.FileName, i.Extension,
                                       i.AssociatedContextTitle, i.SourceDeviceName, i.SourceDeviceType, i.TransferMethod,
                                       i.FormattedSize, i.ItemType, i.RawContent, i.IsPinned, i.HasSmartAction,
                                       i.SmartActionName, i.SmartActionIcon, i.SmartActionType, i.DetectedColor
                                FROM clipboard_items i
                                JOIN clipboard_fts f ON i.Id = f.rowid
                                WHERE clipboard_fts MATCH @ftsQuery
                                ORDER BY i.DateCopied DESC
                                LIMIT 200;";

                            cmd.Parameters.AddWithValue("@ftsQuery", ftsQuery);

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    items.Add(ParseItem(reader));
                                }
                            }
                        }
                    }
                }
                catch (Exception ftsEx)
                {
                    // Fallback to standard LIKE if FTS query fails (e.g. on invalid search syntax)
                    Logger.LogAction("DATABASE_SEARCH_WARN", $"FTS search failed, falling back to LIKE: {ftsEx.Message}");
                    items.Clear();

                    try
                    {
                        using (var conn = new SqliteConnection(GetDbConnectionString()))
                        {
                            conn.Open();
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = @"
                                    SELECT DateCopied, FilePath, ZippedArchivePath, FileName, Extension,
                                           AssociatedContextTitle, SourceDeviceName, SourceDeviceType, TransferMethod,
                                           FormattedSize, ItemType, RawContent, IsPinned, HasSmartAction,
                                           SmartActionName, SmartActionIcon, SmartActionType, DetectedColor
                                    FROM clipboard_items
                                    WHERE FileName LIKE @q OR RawContent LIKE @q
                                    ORDER BY DateCopied DESC
                                    LIMIT 200;";

                                cmd.Parameters.AddWithValue("@q", "%" + queryClean + "%");

                                using (var reader = cmd.ExecuteReader())
                                {
                                    while (reader.Read())
                                    {
                                        items.Add(ParseItem(reader));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("DATABASE_SEARCH_ERROR", $"Search fallback query failed: {ex.Message}");
                    }
                }
                return FilterValidItems(items);
            }
        }

        /// <summary>
        /// Saves unpinned history items to SQLite atomically.
        /// </summary>
        public static void SaveHistoryDebounced(ObservableCollection<ViewModels.ClipboardItem> items)
        {
            var newTimer = new Timer(_ => SaveHistoryNow(items), null, 500, Timeout.Infinite);
            var oldTimer = Interlocked.Exchange(ref _debounceTimer, newTimer);
            oldTimer?.Dispose();
        }

        /// <summary>
        /// Immediately saves clipboard history to disk (atomic write-out of unpinned items).
        /// </summary>
        public static void SaveHistoryNow(ObservableCollection<ViewModels.ClipboardItem> items)
        {
            lock (_lock)
            {
                try
                {
                    List<ViewModels.ClipboardItem> snapshot;
                    if (System.Windows.Application.Current != null)
                    {
                        var op = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            return items.Where(i => !i.IsPinned).ToList();
                        });
                        op.Wait();
                        snapshot = op.Result;
                    }
                    else
                    {
                        snapshot = items.Where(i => !i.IsPinned).ToList();
                    }

                    // Enforce cap before saving
                    if (snapshot.Count > MAX_HISTORY_ITEMS)
                        snapshot = snapshot.Take(MAX_HISTORY_ITEMS).ToList();

                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var transaction = conn.BeginTransaction())
                        {
                            // 1. Delete all unpinned items
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = "DELETE FROM clipboard_items WHERE IsPinned = 0;";
                                cmd.ExecuteNonQuery();
                            }

                            // 2. Insert new unpinned items
                            foreach (var item in snapshot)
                            {
                                InsertItemInternal(conn, transaction, item);
                            }

                            transaction.Commit();
                        }
                    }
                    Logger.LogAction("DATABASE_SAVE", $"Saved {snapshot.Count} unpinned history items to database.");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_SAVE_ERROR", $"Failed to save history to SQLite: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Saves all pinned items to the database atomically.
        /// </summary>
        public static void SavePinnedHistory(List<ViewModels.ClipboardItem> pinned)
        {
            lock (_lock)
            {
                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var transaction = conn.BeginTransaction())
                        {
                            // 1. Delete all pinned items
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = "DELETE FROM clipboard_items WHERE IsPinned = 1;";
                                cmd.ExecuteNonQuery();
                            }

                            // 2. Insert new pinned items
                            foreach (var item in pinned)
                            {
                                item.IsPinned = true;
                                InsertItemInternal(conn, transaction, item);
                            }

                            transaction.Commit();
                        }
                    }
                    Logger.LogAction("DATABASE_SAVE_PINNED", $"Saved {pinned.Count} pinned items to SQLite.");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_SAVE_PINNED_ERROR", $"Failed to save pinned items to SQLite: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Saves a single item directly to the SQLite database atomically, and prunes to MAX_HISTORY_ITEMS.
        /// </summary>
        public static void SaveItem(ViewModels.ClipboardItem item)
        {
            lock (_lock)
            {
                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var transaction = conn.BeginTransaction())
                        {
                            // 1. Insert item
                            InsertItemInternal(conn, transaction, item);

                            // 2. Enforce MAX_HISTORY_ITEMS cap on unpinned items
                            using (var pruneCmd = conn.CreateCommand())
                            {
                                pruneCmd.Transaction = transaction;
                                pruneCmd.CommandText = @"
                                    DELETE FROM clipboard_items 
                                    WHERE IsPinned = 0 
                                      AND Id NOT IN (
                                          SELECT Id FROM clipboard_items 
                                          WHERE IsPinned = 0 
                                          ORDER BY DateCopied DESC 
                                          LIMIT @maxItems
                                      );";
                                pruneCmd.Parameters.AddWithValue("@maxItems", MAX_HISTORY_ITEMS);
                                int prunedRows = pruneCmd.ExecuteNonQuery();
                                if (prunedRows > 0)
                                {
                                    Logger.LogAction("DATABASE_PRUNE", $"Pruned {prunedRows} oldest unpinned items from database.");
                                }
                            }

                            transaction.Commit();
                        }
                    }
                    Logger.LogAction("DATABASE_SAVE_ITEM", $"Successfully saved item atomically: {item.FileName}");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_SAVE_ITEM_ERROR", $"Failed to save item to SQLite: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Updates only the IsPinned status of an item matching DateCopied and FileName.
        /// </summary>
        public static void UpdateItemPinState(ViewModels.ClipboardItem item)
        {
            lock (_lock)
            {
                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                UPDATE clipboard_items 
                                SET IsPinned = @IsPinned 
                                WHERE DateCopied = @DateCopied AND FileName = @FileName;";
                            cmd.Parameters.AddWithValue("@IsPinned", item.IsPinned ? 1 : 0);
                            cmd.Parameters.AddWithValue("@DateCopied", item.DateCopied.Ticks);
                            cmd.Parameters.AddWithValue("@FileName", item.FileName ?? "");
                            
                            int rows = cmd.ExecuteNonQuery();
                            Logger.LogAction("DATABASE_UPDATE_PIN", $"Updated pin state to {item.IsPinned} for: {item.FileName} (Rows affected: {rows})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_UPDATE_PIN_ERROR", $"Failed to update pin state: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Updates the DateCopied timestamp of a specific item matching the old DateCopied and FileName.
        /// </summary>
        public static void UpdateItemDateCopied(ViewModels.ClipboardItem item, DateTime oldDate)
        {
            lock (_lock)
            {
                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                UPDATE clipboard_items 
                                SET DateCopied = @NewDate 
                                WHERE DateCopied = @OldDate AND FileName = @FileName;";
                            cmd.Parameters.AddWithValue("@NewDate", item.DateCopied.Ticks);
                            cmd.Parameters.AddWithValue("@OldDate", oldDate.Ticks);
                            cmd.Parameters.AddWithValue("@FileName", item.FileName ?? "");
                            
                            int rows = cmd.ExecuteNonQuery();
                            Logger.LogAction("DATABASE_UPDATE_DATE", $"Updated DateCopied timestamp for: {item.FileName} (Rows affected: {rows})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_UPDATE_DATE_ERROR", $"Failed to update DateCopied: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Updates only the networking fields (SourceDeviceName, SourceDeviceType, TransferMethod) of an item matching DateCopied and FileName.
        /// </summary>
        public static void UpdateItemNetworkFields(ViewModels.ClipboardItem item)
        {
            lock (_lock)
            {
                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
                                UPDATE clipboard_items 
                                SET SourceDeviceName = @SourceDeviceName,
                                    SourceDeviceType = @SourceDeviceType,
                                    TransferMethod = @TransferMethod
                                WHERE DateCopied = @DateCopied AND FileName = @FileName;";
                            cmd.Parameters.AddWithValue("@SourceDeviceName", item.SourceDeviceName ?? "Local");
                            cmd.Parameters.AddWithValue("@SourceDeviceType", item.SourceDeviceType ?? "PC");
                            cmd.Parameters.AddWithValue("@TransferMethod", item.TransferMethod ?? "Local");
                            cmd.Parameters.AddWithValue("@DateCopied", item.DateCopied.Ticks);
                            cmd.Parameters.AddWithValue("@FileName", item.FileName ?? "");
                            
                            int rows = cmd.ExecuteNonQuery();
                            Logger.LogAction("DATABASE_UPDATE_NET", $"Updated network fields for: {item.FileName} (Rows affected: {rows})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_UPDATE_NET_ERROR", $"Failed to update network fields: {ex.Message}");
                }
            }
        }


        /// <summary>
        /// Deletes the matching item directly from the SQLite database atomically.
        /// </summary>
        public static void AppendDeleteToJournal(ViewModels.ClipboardItem item)
        {
            lock (_lock)
            {
                try
                {
                    using (var conn = new SqliteConnection(GetDbConnectionString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "DELETE FROM clipboard_items WHERE DateCopied = @DateCopied AND FileName = @FileName;";
                            cmd.Parameters.AddWithValue("@DateCopied", item.DateCopied.Ticks);
                            cmd.Parameters.AddWithValue("@FileName", item.FileName ?? "");
                            int rows = cmd.ExecuteNonQuery();
                            Logger.LogAction("DATABASE_DELETE", $"Deleted item: {item.FileName} (Rows affected: {rows})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DATABASE_DELETE_ERROR", $"Failed to delete item: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Filters out items whose referenced files no longer exist.
        /// </summary>
        private static List<ViewModels.ClipboardItem> FilterValidItems(List<ViewModels.ClipboardItem> items)
        {
            var validItems = new List<ViewModels.ClipboardItem>();
            foreach (var item in items)
            {
                try
                {
                    // Text-only items are always valid (no file dependency)
                    if (item.ItemType == ViewModels.ClipboardItemType.Text ||
                        item.ItemType == ViewModels.ClipboardItemType.Code ||
                        item.ItemType == ViewModels.ClipboardItemType.Url)
                    {
                        if (!string.IsNullOrWhiteSpace(item.RawContent) || !string.IsNullOrWhiteSpace(item.FileName))
                            validItems.Add(item);
                        continue;
                    }

                    // Image/QRCode: MUST have a valid backing file
                    if (item.ItemType == ViewModels.ClipboardItemType.Image ||
                        item.ItemType == ViewModels.ClipboardItemType.QRCode)
                    {
                        if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                            validItems.Add(item);
                        else
                            Logger.LogAction("HISTORY_FILTER", $"Dropped orphaned image: {item.FilePath}");
                        continue;
                    }

                    // File-based items (PDF, Document, Archive, Video, Audio, Presentation, File, Group):
                    if (!string.IsNullOrEmpty(item.FilePath))
                    {
                        if (File.Exists(item.FilePath) || Directory.Exists(item.FilePath))
                        {
                            validItems.Add(item);
                        }
                        else
                        {
                            // For Group items, check if at least one file in the group still exists
                            if (item.ItemType == ViewModels.ClipboardItemType.Group && !string.IsNullOrEmpty(item.RawContent))
                            {
                                var paths = item.RawContent.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                if (paths.Any(p => File.Exists(p.Trim()) || Directory.Exists(p.Trim())))
                                {
                                    validItems.Add(item);
                                    continue;
                                }
                            }
                            Logger.LogAction("HISTORY_FILTER", $"Dropped missing file entry: {item.FilePath} ({item.ItemType})");
                        }
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(item.RawContent) || !string.IsNullOrWhiteSpace(item.FileName))
                    {
                        validItems.Add(item);
                        continue;
                    }

                    Logger.LogAction("HISTORY_FILTER", $"Dropped empty/corrupt entry: type={item.ItemType}");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("HISTORY_FILTER_ERROR", $"Dropped corrupt item: {ex.Message}");
                }
            }
            return validItems;
        }

        /// <summary>
        /// Deletes the persistent image file for a clipboard item.
        /// </summary>
        public static void DeletePersistentImage(ViewModels.ClipboardItem item)
        {
            if (item != null)
            {
                DeletePersistentImage(item.FilePath, item.ItemType);
            }
        }

        /// <summary>
        /// Deletes the persistent image file for a clipboard item.
        /// </summary>
        public static void DeletePersistentImage(string filePath, ViewModels.ClipboardItemType itemType)
        {
            try
            {
                if (itemType == ViewModels.ClipboardItemType.Image ||
                    itemType == ViewModels.ClipboardItemType.QRCode)
                {
                    if (!string.IsNullOrEmpty(filePath) && 
                        filePath.Contains(_imagesDir) && 
                        File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
            }
            catch { }
        }

        private class JournalEntry
        {
            public string Action { get; set; } = "";
            public ViewModels.ClipboardItem? Item { get; set; }
            public string? ItemId { get; set; }
        }
    }

    public class ClipboardHistoryContainer
    {
        public int Version { get; set; } = 1;
        public List<ViewModels.ClipboardItem> Items { get; set; } = new();
    }
}
