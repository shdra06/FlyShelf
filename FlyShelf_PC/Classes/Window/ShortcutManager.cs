// ---------------------------------------------------------------
// ShortcutManager — Text Shortcuts Data Model & Persistence
// Stores user-defined /trigger → expansion text mappings.
// Persisted to %AppData%\FlyShelf\shortcuts.json
// ---------------------------------------------------------------
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlyShelf.Classes
{
    // ═══════════════════════════════════════════════════════════
    // DATA MODEL
    // ═══════════════════════════════════════════════════════════

    public partial class TextShortcut : ObservableObject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [ObservableProperty]
        private string _trigger = "";

        [ObservableProperty]
        private string _label = "";

        [ObservableProperty]
        private string _expansion = "";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // UI-only property for inline editing — not serialized
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _isEditing;
    }

    // ═══════════════════════════════════════════════════════════
    // MANAGER (static singleton, same pattern as NoteManager)
    // ═══════════════════════════════════════════════════════════

    public static class ShortcutManager
    {
        public static int MaxShortcuts => 100; // v7.2 FREE: Raised from 20/50 to 100 for all users (Original: LicenseManager.IsPro ? 50 : 20)

        public static ObservableCollection<TextShortcut> Shortcuts { get; private set; } = new();

        private static readonly object _saveLock = new();

        private static string GetFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "FlyShelf");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "shortcuts.json");
        }

        // ═══ LOAD ═══
        public static void Load()
        {
            try
            {
                string path = GetFilePath();
                if (File.Exists(path))
                {
                    var json = FileRetryHelper.RunWithRetry(() => File.ReadAllText(path));
                    var decrypted = SecureStorage.Decrypt(json);
                    var list = JsonSerializer.Deserialize<ObservableCollection<TextShortcut>>(decrypted);
                    if (list != null)
                    {
                        // PM-9: Clear + Add instead of replacing the collection reference,
                        // which would break any existing UI data bindings.
                        Shortcuts.Clear();
                        foreach (var item in list)
                            Shortcuts.Add(item);
                    }
                }
                Logger.LogAction("SHORTCUTS", $"Loaded {Shortcuts.Count} shortcuts.");
            }
            catch (Exception ex)
            {
                Logger.LogAction("SHORTCUTS", $"Failed to load shortcuts: {ex.Message}");
            }
        }

        // ═══ SAVE (debounced, async to disk) ═══
        public static void Save()
        {
            try
            {
                // PM-10: Snapshot the data on the calling thread so we capture
                // a consistent view, then serialize + write on the background thread.
                var snapshot = Shortcuts.ToList();
                string path = GetFilePath();
                System.Threading.Tasks.Task.Run(() =>
                {
                    lock (_saveLock)
                    {
                        try
                        {
                            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                            var encrypted = SecureStorage.Encrypt(json);
                            // PL-11: Create .bak backup before writing (consistent with NoteManager/TodoManager)
                            if (File.Exists(path))
                            {
                                try { File.Copy(path, path + ".bak", true); }
                                catch { /* best-effort backup */ }
                            }
                            string tempPath = path + ".tmp";
                            File.WriteAllText(tempPath, encrypted);
                            FileRetryHelper.RunWithRetry(() => File.Move(tempPath, path, true), 3, 100);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("SHORTCUTS", $"Failed to write shortcuts: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("SHORTCUTS", $"Failed to snapshot shortcuts for save: {ex.Message}");
            }
        }

        // ═══ ADD ═══
        public static bool Add(TextShortcut shortcut)
        {
            if (Shortcuts.Count >= MaxShortcuts)
            {
                Logger.LogAction("SHORTCUTS", $"Cannot add — max {MaxShortcuts} shortcuts reached.");
                return false;
            }

            // Ensure trigger starts with /
            if (!shortcut.Trigger.StartsWith('/'))
                shortcut.Trigger = "/" + shortcut.Trigger;

            // Check for duplicate trigger
            if (Shortcuts.Any(s => s.Trigger.Equals(shortcut.Trigger, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogAction("SHORTCUTS", $"Duplicate trigger: {shortcut.Trigger}");
                return false;
            }

            Shortcuts.Add(shortcut);
            Save();
            Logger.LogAction("SHORTCUTS", $"Added shortcut: {shortcut.Trigger} → {shortcut.Label}");
            return true;
        }

        // ═══ REMOVE ═══
        public static void Remove(string id)
        {
            var item = Shortcuts.FirstOrDefault(s => s.Id == id);
            if (item != null)
            {
                Shortcuts.Remove(item);
                Save();
                Logger.LogAction("SHORTCUTS", $"Removed shortcut: {item.Trigger}");
            }
        }

        // ═══ TRY EXPAND — clipboard text → expansion or null ═══
        public static TextShortcut? TryExpand(string clipboardText)
        {
            if (string.IsNullOrWhiteSpace(clipboardText))
                return null;

            string trimmed = clipboardText.Trim();
            if (!trimmed.StartsWith('/'))
                return null;

            return Shortcuts.FirstOrDefault(s =>
                s.Trigger.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        }
    }
}
