// ---------------------------------------------------------------
// TransferHistory — Persistent log of all completed file transfers
// Stores entries as JSON, provides search/filter/export/stats
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.Json.Serialization;

namespace FlyShelf.Classes
{
    /// <summary>
    /// A single completed transfer record for history persistence.
    /// </summary>
    public class TransferHistoryEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
        [JsonPropertyName("fileSize")] public long FileSize { get; set; }
        [JsonPropertyName("direction")] public string Direction { get; set; } = "Sent"; // "Sent" or "Received"
        [JsonPropertyName("peerName")] public string PeerName { get; set; } = "";
        [JsonPropertyName("peerDeviceId")] public string PeerDeviceId { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "Completed"; // "Completed", "Failed", "Cancelled"
        [JsonPropertyName("startedAt")] public DateTime StartedAt { get; set; }
        [JsonPropertyName("completedAt")] public DateTime CompletedAt { get; set; }
        [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; set; }
        [JsonPropertyName("averageSpeedBps")] public double AverageSpeedBps { get; set; }
        [JsonPropertyName("peakSpeedBps")] public double PeakSpeedBps { get; set; }
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }

        // ═══ Computed Display Properties ═══

        [JsonIgnore]
        public string FileSizeText => FormatBytes(FileSize);

        [JsonIgnore]
        public string SpeedText => FormatSpeed(AverageSpeedBps);

        [JsonIgnore]
        public string DurationText
        {
            get
            {
                if (DurationSeconds <= 0) return "—";
                if (DurationSeconds < 1) return string.Create(CultureInfo.InvariantCulture, $"{DurationSeconds * 1000:F0}ms");
                if (DurationSeconds < 60) return string.Create(CultureInfo.InvariantCulture, $"{DurationSeconds:F1}s");
                int minutes = (int)(DurationSeconds / 60);
                int seconds = (int)(DurationSeconds % 60);
                if (minutes < 60) return string.Create(CultureInfo.InvariantCulture, $"{minutes}m {seconds}s");
                int hours = minutes / 60;
                minutes %= 60;
                return string.Create(CultureInfo.InvariantCulture, $"{hours}h {minutes}m");
            }
        }

        [JsonIgnore]
        public string StatusIcon => Status switch
        {
            "Completed" => "✅",
            "Failed" => "❌",
            "Cancelled" => "⚠️",
            _ => "❓"
        };

        // ═══ Formatting Helpers ═══

        // [FIX M-58]: Delegated to shared FormatHelper
        private static string FormatBytes(long bytes) => Classes.FormatHelper.FormatBytes(bytes);
        private static string FormatSpeed(double bytesPerSecond) => Classes.FormatHelper.FormatSpeed(bytesPerSecond);
    }

    /// <summary>
    /// Singleton that persists transfer history to disk as JSON.
    /// Thread-safe via lock on all save/load/mutation operations.
    /// </summary>
    public class TransferHistory
    {
        public static TransferHistory? Instance { get; private set; }

        private const int MAX_ENTRIES = 500;

        private static readonly string _historyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlyShelf", "transfer_history.json");

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false
        };

        private readonly object _lock = new();

        /// <summary>
        /// Observable collection of history entries, newest first. Bind to UI directly.
        /// </summary>
        public ObservableCollection<TransferHistoryEntry> Entries { get; } = new();

        public TransferHistory()
        {
            Instance = this;
            Load();
            Logger.LogAction("HISTORY", $"Transfer history loaded — {Entries.Count} entries");
        }

        // ═══ Log from LanTransferSession ═══

        /// <summary>
        /// Converts a completed LanTransferSession into a history entry and persists it.
        /// </summary>
        public void LogTransfer(LanTransferSession session)
        {
            if (session == null) return;

            double durationSec = session.ElapsedTime.TotalSeconds;
            double avgSpeed = durationSec > 0 ? session.BytesTransferred / durationSec : 0;

            string status = session.State switch
            {
                TransferState.Completed => "Completed",
                TransferState.Failed => "Failed",
                TransferState.Cancelled => "Cancelled",
                _ => "Failed"
            };

            string direction = session.Direction == TransferDirection.Send ? "Sent" : "Received";

            var entry = new TransferHistoryEntry
            {
                Id = session.TransferId.ToString(),
                FileName = session.FileName,
                FileSize = session.FileSize,
                Direction = direction,
                PeerName = session.PeerDeviceName,
                PeerDeviceId = session.PeerDeviceId,
                Status = status,
                StartedAt = session.StartTime,
                CompletedAt = session.EndTime ?? DateTime.UtcNow,
                DurationSeconds = durationSec,
                AverageSpeedBps = avgSpeed,
                PeakSpeedBps = session.PeakSpeedBps,
                ErrorMessage = session.ErrorMessage
            };

            AddEntry(entry);
            Logger.LogAction("HISTORY", $"{entry.StatusIcon} Logged: {entry.Direction} {entry.FileName} ({entry.FileSizeText}) — {entry.Status}");
        }

        // ═══ Manual Entry ═══

        /// <summary>
        /// Creates a history entry manually for legacy or non-session transfers.
        /// </summary>
        public void LogManualEntry(string fileName, long fileSize, string direction, string peerName,
            string peerDeviceId, string status, double durationSec, double avgSpeed, double peakSpeed,
            string? error = null)
        {
            var entry = new TransferHistoryEntry
            {
                Id = Guid.NewGuid().ToString(),
                FileName = fileName ?? "",
                FileSize = fileSize,
                Direction = direction ?? "Sent",
                PeerName = peerName ?? "",
                PeerDeviceId = peerDeviceId ?? "",
                Status = status ?? "Completed",
                StartedAt = DateTime.UtcNow.AddSeconds(-durationSec),
                CompletedAt = DateTime.UtcNow,
                DurationSeconds = durationSec,
                AverageSpeedBps = avgSpeed,
                PeakSpeedBps = peakSpeed,
                ErrorMessage = error
            };

            AddEntry(entry);
            Logger.LogAction("HISTORY", $"Manual entry logged: {entry.Direction} {entry.FileName}");
        }

        // ═══ Collection Management ═══

        private void AddEntry(TransferHistoryEntry entry)
        {
            // Snapshot for Save() outside the Dispatcher call to avoid deadlock.
            // Dispatcher.InvokeAsync is safe because Save() snapshots via ToList() under its own lock.
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                lock (_lock)
                {
                    Entries.Insert(0, entry);

                    // Enforce FIFO cap — remove oldest entries beyond MAX_ENTRIES
                    while (Entries.Count > MAX_ENTRIES)
                    {
                        Entries.RemoveAt(Entries.Count - 1);
                    }
                }
                Save();
            });
        }

        /// <summary>
        /// Clears all history entries and persists the empty state.
        /// </summary>
        public void ClearAll()
        {
            // Use synchronous Invoke so Clear+Save happen atomically before returning
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                lock (_lock)
                {
                    Entries.Clear();
                }
                Save();
            });
            Logger.LogAction("HISTORY", "All transfer history cleared");
        }

        // ═══ Search & Filter ═══

        /// <summary>
        /// Searches entries by file name or peer name (case-insensitive).
        /// </summary>
        public IEnumerable<TransferHistoryEntry> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Entries;

            string q = query.Trim();
            lock (_lock)
            {
                return Entries.Where(e =>
                    (!string.IsNullOrEmpty(e.FileName) && e.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(e.PeerName) && e.PeerName.Contains(q, StringComparison.OrdinalIgnoreCase))
                ).ToList();
            }
        }

        /// <summary>
        /// Filters entries by peer device ID.
        /// </summary>
        public IEnumerable<TransferHistoryEntry> FilterByDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return Entries;

            lock (_lock)
            {
                return Entries.Where(e =>
                    string.Equals(e.PeerDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }
        }

        /// <summary>
        /// Filters entries by status ("Completed", "Failed", "Cancelled").
        /// </summary>
        public IEnumerable<TransferHistoryEntry> FilterByStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return Entries;

            lock (_lock)
            {
                return Entries.Where(e =>
                    string.Equals(e.Status, status, StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }
        }

        // ═══ Export ═══

        /// <summary>
        /// Exports all entries as a CSV string with headers.
        /// </summary>
        public string ExportCsv()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Id,FileName,FileSize,Direction,PeerName,PeerDeviceId,Status,StartedAt,CompletedAt,DurationSeconds,AverageSpeedBps,PeakSpeedBps,ErrorMessage");

                foreach (var e in Entries)
                {
                    sb.AppendLine(string.Join(",",
                        CsvEscape(e.Id),
                        CsvEscape(e.FileName),
                        e.FileSize.ToString(CultureInfo.InvariantCulture),
                        CsvEscape(e.Direction),
                        CsvEscape(e.PeerName),
                        CsvEscape(e.PeerDeviceId),
                        CsvEscape(e.Status),
                        CsvEscape(e.StartedAt.ToString("o", CultureInfo.InvariantCulture)),
                        CsvEscape(e.CompletedAt.ToString("o", CultureInfo.InvariantCulture)),
                        e.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture),
                        e.AverageSpeedBps.ToString("F0", CultureInfo.InvariantCulture),
                        e.PeakSpeedBps.ToString("F0", CultureInfo.InvariantCulture),
                        CsvEscape(e.ErrorMessage ?? "")
                    ));
                }

                return sb.ToString();
            }
        }

        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return $"\"{value}\"";
        }

        // ═══ Computed Stats ═══

        public int TotalSentCount
        {
            get { lock (_lock) { return Entries.Count(e => e.Direction == "Sent"); } }
        }

        public int TotalReceivedCount
        {
            get { lock (_lock) { return Entries.Count(e => e.Direction == "Received"); } }
        }

        public long TotalBytesSent
        {
            get
            {
                lock (_lock)
                {
                    return Entries.Where(e => e.Direction == "Sent" && e.Status == "Completed")
                        .Sum(e => e.FileSize);
                }
            }
        }

        public long TotalBytesReceived
        {
            get
            {
                lock (_lock)
                {
                    return Entries.Where(e => e.Direction == "Received" && e.Status == "Completed")
                        .Sum(e => e.FileSize);
                }
            }
        }

        // ═══ Persistence ═══

        /// <summary>
        /// Serializes all entries to JSON and writes atomically to disk.
        /// </summary>
        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    // Snapshot the collection on the current thread
                    List<TransferHistoryEntry> snapshot;
                    try
                    {
                        snapshot = Entries.ToList();
                    }
                    catch
                    {
                        // Collection may be modified on dispatcher — safe fallback
                        snapshot = new List<TransferHistoryEntry>();
                    }

                    string json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                    string dir = Path.GetDirectoryName(_historyFile)!;
                    Directory.CreateDirectory(dir);

                    // Create .bak before overwriting so we can recover from corruption
                    string bakFile = _historyFile + ".bak";
                    if (File.Exists(_historyFile))
                    {
                        try { File.Copy(_historyFile, bakFile, true); }
                        catch { /* Best-effort backup */ }
                    }

                    // Atomic write via temp file
                    string tmp = _historyFile + ".tmp";
                    File.WriteAllText(tmp, json, Encoding.UTF8);
                    File.Move(tmp, _historyFile, true);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("HISTORY", $"Save error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Loads history entries from JSON on disk. Called once in constructor.
        /// </summary>
        private void Load()
        {
            lock (_lock)
            {
                if (TryLoadFromFile(_historyFile)) return;

                // Main file missing or corrupt — try .bak fallback
                string bakFile = _historyFile + ".bak";
                if (TryLoadFromFile(bakFile))
                {
                    Logger.LogAction("HISTORY", "Recovered from .bak file");
                }
            }
        }

        /// <summary>
        /// Attempts to load history entries from the given file path.
        /// Returns true if entries were successfully loaded.
        /// </summary>
        private bool TryLoadFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                string json = File.ReadAllText(filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return false;

                var entries = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(json, _jsonOptions);
                if (entries == null || entries.Count == 0) return false;

                // Take only the newest MAX_ENTRIES, ordered newest first
                var sorted = entries.OrderByDescending(e => e.CompletedAt).Take(MAX_ENTRIES).ToList();
                foreach (var entry in sorted)
                {
                    Entries.Add(entry);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("HISTORY", $"Load error ({Path.GetFileName(filePath)}): {ex.Message}");
                return false;
            }
        }
    }
}
