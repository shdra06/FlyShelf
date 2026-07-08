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

namespace FlyShelf.Classes
{
    // ═══════════════════════════════════════════════════════════
    // DATA MODEL
    // ═══════════════════════════════════════════════════════════

    public class TextShortcut : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        private string _trigger = "";
        public string Trigger
        {
            get => _trigger;
            set { if (_trigger != value) { _trigger = value; OnPropertyChanged(nameof(Trigger)); } }
        }

        private string _label = "";
        public string Label
        {
            get => _label;
            set { if (_label != value) { _label = value; OnPropertyChanged(nameof(Label)); } }
        }

        private string _expansion = "";
        public string Expansion
        {
            get => _expansion;
            set { if (_expansion != value) { _expansion = value; OnPropertyChanged(nameof(Expansion)); } }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // UI-only property for inline editing — not serialized
        private bool _isEditing;
        [JsonIgnore]
        public bool IsEditing
        {
            get => _isEditing;
            set { if (_isEditing != value) { _isEditing = value; OnPropertyChanged(nameof(IsEditing)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ═══════════════════════════════════════════════════════════
    // MANAGER (static singleton, same pattern as NoteManager)
    // ═══════════════════════════════════════════════════════════

    public static class ShortcutManager
    {
        public static int MaxShortcuts => LicenseManager.IsPro ? 50 : 5;

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
                    var json = File.ReadAllText(path);
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
                            File.Move(tempPath, path, true);
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
