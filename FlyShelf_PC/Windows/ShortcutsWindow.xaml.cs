using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FlyShelf.Classes;
using MicaWPF.Controls;

namespace FlyShelf.Windows
{
    public partial class ShortcutsWindow : MicaWindow
    {
        private NotifyCollectionChangedEventHandler _shortcutsChangedHandler;

        public ShortcutsWindow()
        {
            InitializeComponent();
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            NativeMethods.ApplyWindowBackdropAndBackground(this);

            // Bind the observable collection
            ShortcutsList.ItemsSource = ShortcutManager.Shortcuts;
            _shortcutsChangedHandler = (s, e) => UpdateUI();
            ShortcutManager.Shortcuts.CollectionChanged += _shortcutsChangedHandler;
            UpdateUI();

            // Esc to close
            this.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Suppress red DWM window border and re-apply theme with valid hwnd
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int colorNone = NativeMethods.DWMWA_COLOR_DARK_GRAY;
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
                }
            }
            catch { } // Best-effort: failure is acceptable
            NativeMethods.ApplyWindowBackdropAndBackground(this);
        }

        // ═══════════════════════════════════════════════════════
        // ADD SHORTCUT
        // ═══════════════════════════════════════════════════════

        private void AddShortcut_Click(object sender, RoutedEventArgs e)
        {
            string trigger = TriggerInput.Text?.Trim() ?? "";
            string label = LabelInput.Text?.Trim() ?? "";
            string expansion = ExpansionInput.Text?.Trim() ?? "";

            // Validate trigger
            if (string.IsNullOrEmpty(trigger) || trigger.Length < 2)
            {
                ToastWindow.ShowToast("Trigger must be at least 2 characters (e.g. /adh)");
                return;
            }
            if (!trigger.StartsWith('/'))
                trigger = "/" + trigger;

            // Validate label
            if (string.IsNullOrEmpty(label))
            {
                ToastWindow.ShowToast("Please enter a label name.");
                return;
            }

            // Validate expansion
            if (string.IsNullOrEmpty(expansion))
            {
                ToastWindow.ShowToast("Please enter expansion text.");
                return;
            }

            // Check max limit
            if (ShortcutManager.Shortcuts.Count >= ShortcutManager.MaxShortcuts)
            {
                ToastWindow.ShowToast($"Maximum {ShortcutManager.MaxShortcuts} shortcuts reached.");
                return;
            }

            // Check for duplicate trigger
            if (ShortcutManager.Shortcuts.Any(s => s.Trigger.Equals(trigger, StringComparison.OrdinalIgnoreCase)))
            {
                ToastWindow.ShowToast($"Trigger '{trigger}' already exists.");
                return;
            }

            var shortcut = new TextShortcut
            {
                Trigger = trigger,
                Label = label,
                Expansion = expansion
            };

            bool added = ShortcutManager.Add(shortcut);
            if (added)
            {
                TriggerInput.Text = "";
                LabelInput.Text = "";
                ExpansionInput.Text = "";
                ToastWindow.ShowToast("Shortcut added! ✦");
            }
            else
            {
                ToastWindow.ShowToast("Failed to add shortcut.");
            }
        }

        // ═══════════════════════════════════════════════════════
        // DELETE SHORTCUT
        // ═══════════════════════════════════════════════════════

        private void DeleteShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is TextShortcut shortcut)
            {
                ShortcutManager.Remove(shortcut.Id);
                ToastWindow.ShowToast($"Shortcut '{shortcut.Trigger}' removed.");
            }
        }

        // ═══════════════════════════════════════════════════════
        // EDIT SHORTCUT (inline)
        // ═══════════════════════════════════════════════════════

        private string _editOriginalTrigger = "";
        private string _editOriginalLabel = "";
        private string _editOriginalExpansion = "";

        private void EditShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is TextShortcut shortcut)
            {
                // Store original values for cancel/revert
                _editOriginalTrigger = shortcut.Trigger;
                _editOriginalLabel = shortcut.Label;
                _editOriginalExpansion = shortcut.Expansion;

                shortcut.IsEditing = true;
            }
        }

        private void SaveEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is TextShortcut shortcut)
            {
                // Validate
                if (string.IsNullOrWhiteSpace(shortcut.Trigger) || shortcut.Trigger.Trim().Length < 2)
                {
                    ToastWindow.ShowToast("Trigger must be at least 2 characters.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(shortcut.Label))
                {
                    ToastWindow.ShowToast("Label cannot be empty.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(shortcut.Expansion))
                {
                    ToastWindow.ShowToast("Expansion text cannot be empty.");
                    return;
                }

                // Ensure trigger starts with /
                if (!shortcut.Trigger.StartsWith('/'))
                    shortcut.Trigger = "/" + shortcut.Trigger;

                shortcut.IsEditing = false;
                ShortcutManager.Save();
                ToastWindow.ShowToast("Shortcut updated! ✦");
            }
        }

        private void CancelEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is TextShortcut shortcut)
            {
                // Revert to original values
                shortcut.Trigger = _editOriginalTrigger;
                shortcut.Label = _editOriginalLabel;
                shortcut.Expansion = _editOriginalExpansion;
                shortcut.IsEditing = false;
            }
        }

        private void ToggleAddMenuBtn_Click(object sender, RoutedEventArgs e)
        {
            if (AddShortcutPanel.Visibility == Visibility.Visible)
            {
                AddShortcutPanel.Visibility = Visibility.Collapsed;
                ToggleAddMenuIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Add24;
                ToggleAddMenuBtn.ToolTip = "Add New Shortcut";
            }
            else
            {
                AddShortcutPanel.Visibility = Visibility.Visible;
                ToggleAddMenuIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Dismiss24;
                ToggleAddMenuBtn.ToolTip = "Close Add Panel";
                TriggerInput.Focus();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            FlyShelf.Classes.SmoothScrollFeature.Detach(this);
            ShortcutManager.Shortcuts.CollectionChanged -= _shortcutsChangedHandler;
            base.OnClosed(e);
        }

        // ═══════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════

        private void UpdateUI()
        {
            ShortcutCountLabel.Text = $"{ShortcutManager.Shortcuts.Count}/{ShortcutManager.MaxShortcuts}";
            EmptyState.Visibility = ShortcutManager.Shortcuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
