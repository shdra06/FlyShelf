// ---------------------------------------------------------------
// NotesPanelControl.ContextMenus.cs — Context menus, templates,
// sort, export, and AI assistant dropdown/actions.
// Includes: bullet card 'More' menu, header dropdown menu,
// template definitions + application, legacy sort/export,
// and AI summarize/rewrite/organize/translate actions.
// Partial class split from NotesPanelControl.xaml.cs.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using FlyShelf.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Helpers;

namespace FlyShelf.Controls
{
    public partial class NotesPanelControl : UserControl
    {
        // ═══════════════════════════════════════════════════════════
        // TEMPLATES — Click handler & application logic
        // ═══════════════════════════════════════════════════════════

        // TODO: Deduplicate — these templates are also defined in the bullet context menu (NoteBulletMore_Click, ~line 2670)
        private void NotesTemplates_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe)
            {
                var menu = new ContextMenu();

                // Helper: colorful emoji menu item using Emoji.Wpf
                MenuItem EmojiMenuItem(string emoji, string label, (string header, string content)[] template)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    var emojiBlock = new Emoji.Wpf.TextBlock
                    {
                        Text = emoji, FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    };
                    var labelBlock = new TextBlock
                    {
                        Text = label, FontSize = 13,
                        Foreground = FrozenBrush(ThemeColors.CatppuccinText),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    sp.Children.Add(emojiBlock);
                    sp.Children.Add(labelBlock);
                    var mi = new MenuItem { Header = sp, Padding = new Thickness(4, 4, 4, 4) };
                    mi.Click += (s, ev) => ApplyNotesTemplateWithHeaders(template);
                    return mi;
                }

                menu.Items.Add(EmojiMenuItem("🛒", "Grocery List", new[] {
                    ("Dairy", "Milk, Eggs, Cheese, Yogurt"),
                    ("Produce", "Veggies, Fruits, Herbs"),
                    ("Pantry", "Bread, Rice, Pasta, Cereal"),
                    ("Frozen & Snacks", "")
                }));

                menu.Items.Add(EmojiMenuItem("💼", "Daily Standup", new[] {
                    ("Yesterday", ""),
                    ("Today", ""),
                    ("Blockers", ""),
                    ("Notes", "")
                }));

                menu.Items.Add(EmojiMenuItem("📝", "Meeting Notes", new[] {
                    ("Attendees", ""),
                    ("Agenda", ""),
                    ("Discussion", ""),
                    ("Action Items", ""),
                    ("Follow-up", "")
                }));

                menu.Items.Add(EmojiMenuItem("🏋️", "Workout Planner", new[] {
                    ("Warmup", "5 min cardio"),
                    ("Main Set", ""),
                    ("Cooldown", "Stretching & foam roll")
                }));

                menu.Items.Add(new Separator());

                menu.Items.Add(EmojiMenuItem("🎯", "Project Planning", new[] {
                    ("Goal", ""),
                    ("Tasks", ""),
                    ("Timeline", ""),
                    ("Risks & Mitigations", "")
                }));

                menu.Items.Add(EmojiMenuItem("📊", "Weekly Review", new[] {
                    ("Wins", ""),
                    ("Challenges", ""),
                    ("Lessons Learned", ""),
                    ("Next Week Priorities", "")
                }));

                menu.Items.Add(EmojiMenuItem("🧠", "Brain Dump", new[] {
                    ("Ideas", ""),
                    ("To Research", ""),
                    ("Questions", "")
                }));

                menu.Items.Add(EmojiMenuItem("📚", "Reading Notes", new[] {
                    ("Key Takeaways", ""),
                    ("Quotes", ""),
                    ("Reflections", "")
                }));

                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }

        private void ApplyNotesTemplateWithHeaders((string header, string content)[] items)
        {
            var targetDay = GetTargetDayForAdd();
            if (targetDay == null) return;

            if (targetDay.IsFreeformMode)
            {
                // In freeform mode, format as structured text
                var sb = new System.Text.StringBuilder();
                foreach (var (header, content) in items)
                {
                    sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"## {header}");
                    if (!string.IsNullOrEmpty(content))
                        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {content}");
                    sb.AppendLine();
                }
                // Append to last freeform section
                var lastSec = targetDay.FreeformSections.LastOrDefault();
                if (lastSec != null) lastSec.Content += sb.ToString();
                NoteManager.MarkDirty();
            }
            else
            {
                foreach (var (header, content) in items)
                {
                    var bullet = NoteManager.AddBullet(targetDay);
                    bullet.Header = header;
                    bullet.Content = content;
                    bullet.IsCollapsed = false; // Templates should start expanded
                }

                if (_selectedNoteDay == null && _selectedMonth != -1)
                {
                    RebuildSidebar();
                    SelectNoteMonth(_selectedMonth, _selectedYear);
                }
                else
                {
                    NotesBulletList.ItemsSource = null;
                    NotesBulletList.ItemsSource = targetDay.Bullets;
                }
            }
        }

        private void ApplyNotesTemplate(string[] lines)
        {
            var targetDay = GetTargetDayForAdd();
            if (targetDay == null) return;

            if (targetDay.IsFreeformMode)
            {
                string templateText = string.Join(Environment.NewLine, lines.Select(l => "• " + l)) + Environment.NewLine;
                // Append to last freeform section
                var lastSection = targetDay.FreeformSections.LastOrDefault();
                if (lastSection != null) lastSection.Content += templateText;
                NoteManager.MarkDirty();
            }
            else
            {
                foreach (var line in lines)
                {
                    var bullet = NoteManager.AddBullet(targetDay);
                    bullet.Content = line;
                }
                
                if (_selectedNoteDay == null && _selectedMonth != -1)
                {
                    RebuildSidebar();
                    SelectNoteMonth(_selectedMonth, _selectedYear);
                }
                else
                {
                    NotesBulletList.ItemsSource = null;
                    NotesBulletList.ItemsSource = targetDay.Bullets;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MORE MENU (consolidated dropdown for bullet cards)
        // ═══════════════════════════════════════════════════════════

        private void NoteBulletMore_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                // Close any existing menu first
                if (_activeNoteDropdownMenu != null)
                {
                    var wasForSameTarget = _activeNoteDropdownMenu.IsOpen && _activeNoteDropdownMenu.PlacementTarget == fe;
                    _activeNoteDropdownMenu.IsOpen = false;
                    _activeNoteDropdownMenu = null;
                    if (wasForSameTarget) return; // toggle OFF
                }

                // Guard against rapid re-open: StaysOpen=False closes the menu async
                // BEFORE this click handler fires, so the toggle above never triggers.
                // This timestamp guard catches that case.
                if ((DateTime.Now - _lastNoteDropdownCloseTime).TotalMilliseconds < 300)
                    return;

                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    // [BTN-18/BTN-19]: Reuse cached ContextMenu — all closures read bullet
                    // from _cachedBulletMoreMenu.Tag instead of capturing a local variable.
                    if (_cachedBulletMoreMenu != null)
                    {
                        _cachedBulletMoreMenu.Tag = bullet;

                        // ── Update dynamic state for the cached menu ──────────
                        // Pin item (index 0): header + icon change based on IsPinned
                        if (_cachedBulletMoreMenu.Items[0] is MenuItem pinItem)
                        {
                            pinItem.Header = bullet.IsPinned ? "Unpin" : "Pin to Top";
                            pinItem.Icon = MakeBulletMenuIcon(bullet.IsPinned ? "📌" : "📍", "#F59E0B");
                        }

                        // Tags submenu (index 2): update IsChecked + icons for preset tags
                        if (_cachedBulletMoreMenu.Items[2] is MenuItem cachedTagMenu)
                        {
                            for (int i = 0; i < s_notePresetTags.Length && i < cachedTagMenu.Items.Count; i++)
                            {
                                if (cachedTagMenu.Items[i] is MenuItem tagItem)
                                {
                                    bool hasTag = bullet.Tags.Contains(s_notePresetTags[i]);
                                    tagItem.IsChecked = hasTag;
                                    tagItem.Icon = hasTag
                                        ? MakeBulletMenuIcon("✓", "#22C55E")
                                        : MakeBulletMenuIcon("○", "#6B7280");
                                }
                            }
                        }

                        _cachedBulletMoreMenu.PlacementTarget = fe;
                        _activeNoteDropdownMenu = _cachedBulletMoreMenu;
                        _cachedBulletMoreMenu.IsOpen = true;
                        return;
                    }

                    // ── First-time build ──────────────────────────────────
                    var menu = new ContextMenu();
                    menu.Tag = bullet;

                    // Pin / Unpin  ── amber pin icon
                    var pin = new MenuItem { Header = bullet.IsPinned ? "Unpin" : "Pin to Top" };
                    pin.Icon = MakeBulletMenuIcon(bullet.IsPinned ? "📌" : "📍", "#F59E0B");
                    pin.Click += (s, ev) => { if (_cachedBulletMoreMenu?.Tag is NoteBullet b) { b.IsPinned = !b.IsPinned; NoteManager.MarkDirty(); } };
                    menu.Items.Add(pin);

                    // Color submenu  ── palette icon
                    var colorMenu = new MenuItem { Header = "Color" };
                    colorMenu.Icon = MakeBulletMenuIcon("🎨", "#EC4899");
                    var noteColors = new (string Hex, string Name)[]
                    {
                        ("#FF4444", "Red"), ("#F59E0B", "Amber"), ("#22C55E", "Green"),
                        ("#3B82F6", "Blue"), ("#8B5CF6", "Purple"), ("#EC4899", "Pink")
                    };
                    foreach (var (hex, name) in noteColors)
                    {
                        var mi = new MenuItem { Header = name };
                        mi.Icon = new Border
                        {
                            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
                            Background = FrozenBrush((Color)ColorConverter.ConvertFromString(hex))
                        };
                        string ch = hex;
                        mi.Click += (s, ev) => { if (_cachedBulletMoreMenu?.Tag is NoteBullet b) { b.Color = ch; NoteManager.MarkDirty(); } };
                        colorMenu.Items.Add(mi);
                    }
                    colorMenu.Items.Add(new Separator());
                    var clearColor = new MenuItem { Header = "Clear Color" };
                    clearColor.Icon = MakeBulletMenuIcon("✕", "#6B7280");
                    clearColor.Click += (s, ev) => { if (_cachedBulletMoreMenu?.Tag is NoteBullet b) { b.Color = ""; NoteManager.MarkDirty(); } };
                    colorMenu.Items.Add(clearColor);
                    menu.Items.Add(colorMenu);

                    // Tags submenu  ── cyan tag icon
                    var tagMenu = new MenuItem { Header = "Tags" };
                    tagMenu.Icon = MakeBulletMenuIcon("🏷", "#00D2FF");
                    string[] presetTags = s_notePresetTags;
                    foreach (var tag in presetTags)
                    {
                        bool hasTag = bullet.Tags.Contains(tag);
                        var mi = new MenuItem { Header = tag, IsChecked = hasTag };
                        mi.Icon = hasTag
                            ? MakeBulletMenuIcon("✓", "#22C55E")
                            : MakeBulletMenuIcon("○", "#6B7280");
                        string ct = tag;
                        mi.Click += (s, ev) =>
                        {
                            if (_cachedBulletMoreMenu?.Tag is NoteBullet b)
                            {
                                if (!b.Tags.Remove(ct)) b.Tags.Add(ct);
                                b.Tags = new List<string>(b.Tags);
                                NoteManager.MarkDirty();
                            }
                        };
                        tagMenu.Items.Add(mi);
                    }
                    tagMenu.Items.Add(new Separator());
                    var customTag = new MenuItem { Header = "Custom Tag..." };
                    customTag.Icon = MakeBulletMenuIcon("✏", "#8B5CF6");
                    customTag.Click += (s, ev) =>
                    {
                        if (_cachedBulletMoreMenu?.Tag is not NoteBullet b) return;
                        var popup = new System.Windows.Controls.Primitives.Popup
                        {
                            PlacementTarget = fe,
                            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                            StaysOpen = false, AllowsTransparency = true
                        };
                        var textBox = new TextBox
                        {
                            Width = 160, FontSize = 13, Padding = new Thickness(6, 4, 6, 4),
                            Background = FrozenBrush(ThemeColors.CatppuccinSurface),
                            Foreground = FrozenBrush(Colors.White),
                            BorderBrush = FrozenBrush(ThemeColors.VioletAccentA60),
                            CaretBrush = FrozenBrush(Colors.White)
                        };
                        textBox.KeyDown += (ts, te) =>
                        {
                            if (te.Key == Key.Enter && !string.IsNullOrWhiteSpace(textBox.Text))
                            {
                                te.Handled = true;
                                string newTag = textBox.Text.Trim();
                                if (_cachedBulletMoreMenu?.Tag is NoteBullet tb)
                                {
                                    if (!tb.Tags.Remove(newTag)) tb.Tags.Add(newTag);
                                    tb.Tags = new List<string>(tb.Tags);
                                    NoteManager.MarkDirty();
                                }
                                popup.IsOpen = false;
                            }
                            else if (te.Key == Key.Escape) { te.Handled = true; popup.IsOpen = false; }
                        };
                        popup.Child = new Border
                        {
                            Background = FrozenBrush(ThemeColors.CatppuccinSurface),
                            CornerRadius = new CornerRadius(6), Padding = new Thickness(4),
                            BorderBrush = FrozenBrush(ThemeColors.VioletAccentA40),
                            BorderThickness = new Thickness(1), Child = textBox
                        };
                        popup.IsOpen = true;
                        Dispatcher.InvokeAsync(() => { textBox.Focus(); Keyboard.Focus(textBox); },
                            System.Windows.Threading.DispatcherPriority.Input);
                    };
                    tagMenu.Items.Add(customTag);
                    menu.Items.Add(tagMenu);

                    menu.Items.Add(new Separator());

                    // Copy as Text  ── blue clipboard icon
                    var copyText = new MenuItem { Header = "Copy as Text" };
                    copyText.Icon = MakeBulletMenuIcon("📋", "#3B82F6");
                    copyText.Click += (s, ev) =>
                    {
                        if (_cachedBulletMoreMenu?.Tag is NoteBullet b)
                        {
                            string text = "";
                            if (!string.IsNullOrEmpty(b.Header)) text += b.Header + "\n";
                            if (!string.IsNullOrEmpty(b.Content)) text += b.Content;
                            if (!string.IsNullOrWhiteSpace(text)) Classes.ClipboardHelper.SafeSetText(text.Trim());
                        }
                    };
                    menu.Items.Add(copyText);

                    // Copy as Markdown  ── indigo markdown icon
                    var copyMd = new MenuItem { Header = "Copy as Markdown" };
                    copyMd.Icon = MakeBulletMenuIcon("📝", "#6366F1");
                    copyMd.Click += (s, ev) =>
                    {
                        if (_cachedBulletMoreMenu?.Tag is NoteBullet b)
                        {
                            string md = "";
                            if (!string.IsNullOrEmpty(b.Header)) md += $"## {b.Header}\n\n";
                            if (!string.IsNullOrEmpty(b.Content)) md += b.Content;
                            if (!string.IsNullOrWhiteSpace(md)) Classes.ClipboardHelper.SafeSetText(md.Trim());
                        }
                    };
                    menu.Items.Add(copyMd);

                    menu.Items.Add(new Separator());

                    // Set Reminder  ── amber bell icon
                    var reminderItem = new MenuItem { Header = "Set Reminder" };
                    reminderItem.Icon = MakeBulletMenuIcon("⏰", "#F59E0B");
                    reminderItem.Click += (s, ev) =>
                    {
                        if (_cachedBulletMoreMenu?.Tag is NoteBullet b)
                        {
                            string noteText = !string.IsNullOrEmpty(b.Header) ? b.Header :
                                               (!string.IsNullOrEmpty(b.Content) ? (b.Content.Length > 120 ? b.Content[..120] : b.Content) : "");

                            var (parsedTitle, calculatedDue) = Classes.NaturalLanguageReminderParser.Parse(noteText, DateTime.Now);

                            if (_selectedNoteDay != null && _selectedNoteDay.Date.Date > DateTime.Today && calculatedDue < _selectedNoteDay.Date.Date.AddHours(9))
                            {
                                calculatedDue = _selectedNoteDay.Date.Date.AddHours(9);
                            }

                            try { _activeReminderCreateWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                            var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow(parsedTitle, calculatedDue);
                            WindowHelper.ShowInForeground(reminderWindow);
                            _activeReminderCreateWindow = reminderWindow;
                        }
                    };
                    menu.Items.Add(reminderItem);

                    // Delete  ── red trash icon
                    var deleteItem = new MenuItem { Header = "Delete" };
                    deleteItem.Icon = MakeBulletMenuIcon("🗑", "#EF4444");
                    deleteItem.Foreground = FrozenBrush(ThemeColors.ErrorRed);
                    deleteItem.Click += (s, ev) =>
                    {
                        if (_cachedBulletMoreMenu?.Tag is NoteBullet b && _selectedNoteDay != null)
                        {
                            var result = MessageBox.Show("Are you sure you want to delete this bullet?", "Delete Bullet",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning);
                            if (result == MessageBoxResult.Yes)
                            {
                                NoteManager.DeleteBullet(_selectedNoteDay, b);
                            }
                        }
                    };
                    menu.Items.Add(deleteItem);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.Closed += (s, ev) => { _lastNoteDropdownCloseTime = DateTime.Now; if (_activeNoteDropdownMenu == menu) _activeNoteDropdownMenu = null; };
                    _cachedBulletMoreMenu = menu;
                    _activeNoteDropdownMenu = menu;
                    menu.IsOpen = true;
                }));
            }
        }

        // ── Helper: make a colored TextBlock icon for bullet menu ──────────
        private TextBlock MakeBulletMenuIcon(string glyph, string hexColor) => new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = FrozenBrush((Color)ColorConverter.ConvertFromString(hexColor))
        };

        // ═══════════════════════════════════════════════════════════
        // NOTES AI ASSISTANT (Summarize / Rewrite / Organize)
        // ═══════════════════════════════════════════════════════════

        private void NoteBulletAI_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                string textToProcess = !string.IsNullOrEmpty(bullet.Content) ? bullet.Content : bullet.Header;
                OpenNotesAIDropdown(fe, textToProcess, (newText) =>
                {
                    if (!string.IsNullOrEmpty(bullet.Content))
                    {
                        bullet.Content = newText;
                    }
                    else
                    {
                        bullet.Header = newText;
                    }
                    NoteManager.MarkDirty();
                });
            }
        }

        private void NotesFreeformAI_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is FreeformSection section)
            {
                // Snapshot for undo before AI modifies the text
                _notesUndoText = section.Content;
                _notesUndoSection = section;

                OpenNotesAIDropdown(fe, section.Content, (newText) =>
                {
                    section.Content = newText;
                    NoteManager.MarkDirty();

                    // Show the undo button now that AI has modified text
                    NotesUndoBtn.Visibility = Visibility.Visible;
                });
            }
        }

        private void OpenNotesAIDropdown(FrameworkElement target, string originalText, Action<string> onApplyText)
        {
            if (string.IsNullOrWhiteSpace(originalText))
            {
                Windows.ToastWindow.ShowToast("⚠️ Note is empty. Type something first!");
                return;
            }

            var menu = new ContextMenu();

            // Improve Writing (opens AI Diff window for grammar/clarity fix)
            var improve = new MenuItem { Header = "Improve Writing", FontWeight = FontWeights.SemiBold };
            improve.Click += (s, ev) =>
            {
                bool hasCloudKey = AiProviderService.Instance.HasCloudApiKey;
                if (!LicenseManager.IsPro && !hasCloudKey)
                {
                    UpgradePrompt.ShowNotesAILimit(GetMainWindow());
                    return;
                }

                var aiWindow = new FlyShelf.Windows.NotesAIDiffWindow(originalText);
                aiWindow.Owner = GetMainWindow();
                if (aiWindow.ShowDialog() == true && aiWindow.IsApplied)
                {
                    onApplyText(aiWindow.ImprovedText);
                }
            };
            menu.Items.Add(improve);
            menu.Items.Add(new Separator());

            var summarize = new MenuItem { Header = "Summarize" };
            summarize.Click += (s, ev) => RunNotesAIAction("Summarize", originalText, onApplyText);
            menu.Items.Add(summarize);

            var rewrite = new MenuItem { Header = "Rewrite" };
            rewrite.Click += (s, ev) => RunNotesAIAction("Rewrite", originalText, onApplyText);
            menu.Items.Add(rewrite);

            var organize = new MenuItem { Header = "Organize" };
            organize.Click += (s, ev) => RunNotesAIAction("Organize", originalText, onApplyText);
            menu.Items.Add(organize);

            menu.Items.Add(new Separator());

            // Translate submenu with language options
            var translate = new MenuItem { Header = "Translate" };
            var languages = new[] { "English", "Spanish", "French", "German", "Japanese", "Chinese", "Hindi", "Arabic", "Korean", "Portuguese" };
            foreach (var lang in languages)
            {
                var langItem = new MenuItem { Header = lang, Tag = $"Translate:{lang}" };
                langItem.Click += (s, ev) => RunNotesAIAction($"Translate:{lang}", originalText, onApplyText);
                translate.Items.Add(langItem);
            }
            menu.Items.Add(translate);

            var expand = new MenuItem { Header = "Expand" };
            expand.Click += (s, ev) => RunNotesAIAction("Expand", originalText, onApplyText);
            menu.Items.Add(expand);

            var explain = new MenuItem { Header = "Explain Simply" };
            explain.Click += (s, ev) => RunNotesAIAction("Explain", originalText, onApplyText);
            menu.Items.Add(explain);

            menu.Items.Add(new Separator());

            var actions = new MenuItem { Header = "Extract Actions" };
            actions.Click += (s, ev) => RunNotesAIAction("Actions", originalText, onApplyText);
            menu.Items.Add(actions);

            var autoTag = new MenuItem { Header = "Auto-Tag" };
            autoTag.Click += (s, ev) => RunNotesAIAction("AutoTag", originalText, onApplyText);
            menu.Items.Add(autoTag);

            menu.PlacementTarget = target;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void RunNotesAIAction(string actionType, string originalText, Action<string> onApplyText)
        {
            bool hasCloudKey = AiProviderService.Instance.HasCloudApiKey;

            // Allow if Pro OR if user has their own cloud API key
            if (!LicenseManager.IsPro && !hasCloudKey)
            {
                UpgradePrompt.ShowNotesAILimit(GetMainWindow());
                return;
            }

            // Cloud-only actions require an API key (no offline fallback)
            bool isCloudOnly = actionType.StartsWith("Translate:", StringComparison.OrdinalIgnoreCase)
                || actionType == "Expand" || actionType == "Explain"
                || actionType == "Actions" || actionType == "AutoTag";

            if (isCloudOnly && !hasCloudKey && !WindowsAIService.Instance.IsAvailable)
            {
                Windows.ToastWindow.ShowToast("⚠️ This feature requires an AI API key. Click ⚡ in Settings to configure.");
                return;
            }

            var mainWin = GetMainWindow();
            var aiWindow = new FlyShelf.Windows.NotesAIWindow(originalText, actionType);
            aiWindow.Owner = mainWin;
            if (aiWindow.ShowDialog() == true && aiWindow.IsApplied)
            {
                onApplyText(aiWindow.ResultText);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTES HEADER DROPDOWN MENU (Sort / Export / Templates)
        // ═══════════════════════════════════════════════════════════

        private void NotesHeaderMenu_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe)
            {
                // Close existing header menu (toggle OFF)
                if (_activeNotesHeaderMenu != null)
                {
                    var wasForSameTarget = _activeNotesHeaderMenu.IsOpen && _activeNotesHeaderMenu.PlacementTarget == fe;
                    _activeNotesHeaderMenu.IsOpen = false;
                    _activeNotesHeaderMenu = null;
                    if (wasForSameTarget) return;
                }

                // Guard against rapid re-open
                if ((DateTime.Now - _lastNotesHeaderMenuCloseTime).TotalMilliseconds < 300)
                    return;

                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    var menu = new ContextMenu();

                    // Helper: colored emoji icon (using Emoji.Wpf for full-color rendering)
                    Emoji.Wpf.TextBlock MI(string g, string c) => new Emoji.Wpf.TextBlock
                    {
                        Text = g,
                        FontSize = 13, VerticalAlignment = VerticalAlignment.Center
                    };

                    // ── Sort submenu ── cyan chart icon
                    var day = _selectedNoteDay;
                    if (day != null)
                    {
                        var sortMenu = new MenuItem { Header = "Sort Bullets" };
                        sortMenu.Icon = MI("📊", "#00D2FF");

                        var sortPinned = new MenuItem { Header = "Pinned First" };
                        sortPinned.Icon = MI("📌", "#F59E0B");
                        sortPinned.Click += (s, ev) =>
                        {
                            var sorted = day.Bullets.OrderByDescending(b => b.IsPinned).ThenBy(b => b.SortOrder).ToList();
                            ReplaceBullets(day, sorted);
                            NoteManager.MarkDirty();
                        };
                        sortMenu.Items.Add(sortPinned);

                        var sortAZ = new MenuItem { Header = "Header A-Z" };
                        sortAZ.Icon = MI("🔤", "#3B82F6");
                        sortAZ.Click += (s, ev) =>
                        {
                            var sorted = day.Bullets.OrderBy(b => b.Header ?? "").ThenBy(b => b.SortOrder).ToList();
                            ReplaceBullets(day, sorted);
                            NoteManager.MarkDirty();
                        };
                        sortMenu.Items.Add(sortAZ);

                        var sortEdited = new MenuItem { Header = "Last Edited" };
                        sortEdited.Icon = MI("🕐", "#8B5CF6");
                        sortEdited.Click += (s, ev) =>
                        {
                            var sorted = day.Bullets.OrderByDescending(b => b.LastEdited).ToList();
                            ReplaceBullets(day, sorted);
                            NoteManager.MarkDirty();
                        };
                        sortMenu.Items.Add(sortEdited);

                        var sortCreated = new MenuItem { Header = "Created" };
                        sortCreated.Icon = MI("📅", "#22C55E");
                        sortCreated.Click += (s, ev) =>
                        {
                            var sorted = day.Bullets.OrderByDescending(b => b.CreatedAt).ToList();
                            ReplaceBullets(day, sorted);
                            NoteManager.MarkDirty();
                        };
                        sortMenu.Items.Add(sortCreated);

                        menu.Items.Add(sortMenu);
                    }

                    // ── Export submenu ── blue clipboard icon
                    if (day != null)
                    {
                        var exportMenu = new MenuItem { Header = "Export" };
                        exportMenu.Icon = MI("📋", "#3B82F6");

                        var copyMd = new MenuItem { Header = "Copy as Markdown" };
                        copyMd.Icon = MI("📝", "#6366F1");
                        copyMd.Click += (s, ev) =>
                        {
                            string md = NoteManager.ExportToMarkdown(day);
                            if (!string.IsNullOrWhiteSpace(md)) Classes.ClipboardHelper.SafeSetText(md);
                        };
                        exportMenu.Items.Add(copyMd);

                        var copyTxt = new MenuItem { Header = "Copy as Text" };
                        copyTxt.Icon = MI("📋", "#3B82F6");
                        copyTxt.Click += (s, ev) =>
                        {
                            string txt = NoteManager.ExportToText(day);
                            if (!string.IsNullOrWhiteSpace(txt)) Classes.ClipboardHelper.SafeSetText(txt);
                        };
                        exportMenu.Items.Add(copyTxt);

                        menu.Items.Add(exportMenu);
                    }

                    menu.Items.Add(new Separator());

                    // ── Templates submenu ── amber document icon
                    // TODO: Deduplicate — these templates are also defined in NotesTemplates_Click (~line 1990)
                    var templatesMenu = new MenuItem { Header = "Templates" };
                    templatesMenu.Icon = MI("📄", "#F59E0B");

                    var tGrocery = new MenuItem { Header = "Grocery List" };
                    tGrocery.Icon = MI("🛒", "#22C55E");
                    tGrocery.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Dairy", "Milk, Eggs, Cheese, Yogurt"),
                        ("Produce", "Veggies, Fruits, Herbs"),
                        ("Pantry", "Bread, Rice, Pasta, Cereal"),
                        ("Frozen & Snacks", "")
                    });
                    templatesMenu.Items.Add(tGrocery);

                    var tStandup = new MenuItem { Header = "Daily Standup" };
                    tStandup.Icon = MI("💼", "#3B82F6");
                    tStandup.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Yesterday", ""),
                        ("Today", ""),
                        ("Blockers", ""),
                        ("Notes", "")
                    });
                    templatesMenu.Items.Add(tStandup);

                    var tMeeting = new MenuItem { Header = "Meeting Notes" };
                    tMeeting.Icon = MI("📝", "#6366F1");
                    tMeeting.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Attendees", ""),
                        ("Agenda", ""),
                        ("Discussion", ""),
                        ("Action Items", ""),
                        ("Follow-up", "")
                    });
                    templatesMenu.Items.Add(tMeeting);

                    var tWorkout = new MenuItem { Header = "Workout Planner" };
                    tWorkout.Icon = MI("🏋", "#EF4444");
                    tWorkout.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Warmup", "5 min cardio"),
                        ("Main Set", ""),
                        ("Cooldown", "Stretching & foam roll")
                    });
                    templatesMenu.Items.Add(tWorkout);

                    var tProject = new MenuItem { Header = "Project Planning" };
                    tProject.Icon = MI("📋", "#00D2FF");
                    tProject.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Goals", ""),
                        ("Tasks", ""),
                        ("Timeline", ""),
                        ("Resources", "")
                    });
                    templatesMenu.Items.Add(tProject);

                    var tBrainDump = new MenuItem { Header = "Brain Dump" };
                    tBrainDump.Icon = MI("🧠", "#EC4899");
                    tBrainDump.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Ideas", ""),
                        ("To Process", ""),
                        ("Follow Up", "")
                    });
                    templatesMenu.Items.Add(tBrainDump);

                    menu.Items.Add(templatesMenu);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.Closed += (s, ev) => { _lastNotesHeaderMenuCloseTime = DateTime.Now; if (_activeNotesHeaderMenu == menu) _activeNotesHeaderMenu = null; };
                    _activeNotesHeaderMenu = menu;
                    menu.IsOpen = true;
                }));
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTE SORT (legacy — now integrated into header dropdown)
        // ═══════════════════════════════════════════════════════════

        private void NoteSort_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var day = _selectedNoteDay;
            if (sender is FrameworkElement fe && day != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var menu = new ContextMenu();

                    var sortPinned = new MenuItem { Header = "📌 Pinned First" };
                    sortPinned.Click += (s, ev) =>
                    {
                        var sorted = day.Bullets.OrderByDescending(b => b.IsPinned).ThenBy(b => b.SortOrder).ToList();
                        ReplaceBullets(day, sorted);
                        NoteManager.MarkDirty();
                    };
                    menu.Items.Add(sortPinned);

                    var sortAZ = new MenuItem { Header = "🔤 Header A-Z" };
                    sortAZ.Click += (s, ev) =>
                    {
                        var sorted = day.Bullets.OrderBy(b => b.Header ?? "").ThenBy(b => b.SortOrder).ToList();
                        ReplaceBullets(day, sorted);
                        NoteManager.MarkDirty();
                    };
                    menu.Items.Add(sortAZ);

                    var sortEdited = new MenuItem { Header = "🕐 Last Edited" };
                    sortEdited.Click += (s, ev) =>
                    {
                        var sorted = day.Bullets.OrderByDescending(b => b.LastEdited).ToList();
                        ReplaceBullets(day, sorted);
                        NoteManager.MarkDirty();
                    };
                    menu.Items.Add(sortEdited);

                    var sortCreated = new MenuItem { Header = "📅 Created" };
                    sortCreated.Click += (s, ev) =>
                    {
                        var sorted = day.Bullets.OrderByDescending(b => b.CreatedAt).ToList();
                        ReplaceBullets(day, sorted);
                        NoteManager.MarkDirty();
                    };
                    menu.Items.Add(sortCreated);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }));
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTE EXPORT
        // ═══════════════════════════════════════════════════════════

        private void NoteExport_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && _selectedNoteDay != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var menu = new ContextMenu();

                    var copyMd = new MenuItem { Header = "📋 Copy as Markdown" };
                    copyMd.Click += (s, ev) =>
                    {
                        string md = NoteManager.ExportToMarkdown(_selectedNoteDay);
                        if (!string.IsNullOrWhiteSpace(md)) Classes.ClipboardHelper.SafeSetText(md);
                    };
                    menu.Items.Add(copyMd);

                    var copyTxt = new MenuItem { Header = "📋 Copy as Text" };
                    copyTxt.Click += (s, ev) =>
                    {
                        string txt = NoteManager.ExportToText(_selectedNoteDay);
                        if (!string.IsNullOrWhiteSpace(txt)) Classes.ClipboardHelper.SafeSetText(txt);
                    };
                    menu.Items.Add(copyTxt);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }));
            }
        }
    }
}
