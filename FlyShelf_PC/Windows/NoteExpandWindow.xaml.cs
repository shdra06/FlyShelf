using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf.Windows
{
    /// <summary>
    /// Sticky-note style expanded view for freeform notes.
    /// Binds to a FreeformSection and syncs text changes back in real-time.
    /// </summary>
    public partial class NoteExpandWindow : Window
    {
        private readonly FlyShelf.Classes.FreeformSection _section;
        private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
        private bool _isPinned = true;
        private bool _isDirty = false;

        public NoteExpandWindow(FlyShelf.Classes.FreeformSection section, string dayLabel = "Note")
        {
            InitializeComponent();
            _section = section ?? throw new ArgumentNullException(nameof(section));

            _saveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                SaveContent();
            };

            HeaderTitle.Text = dayLabel;
            NoteTextBox.Text = _section.Content ?? "";
            UpdateWordCount();

            // Reset dirty status after initial load
            _isDirty = false;
            _saveTimer.Stop();
            if (FooterStatus != null) FooterStatus.Text = "Ready";
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                NoteTextBox.Focus();
                NoteTextBox.CaretIndex = NoteTextBox.Text.Length;
            }
            catch { }
        }

        // ═══ TEXT EDITING ═══

        private void NoteTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _isDirty = true;
            UpdateWordCount();
            if (FooterStatus != null) FooterStatus.Text = "Editing...";
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        private void SaveContent()
        {
            if (!_isDirty) return;
            _section.Content = NoteTextBox.Text;
            _isDirty = false;
            if (FooterStatus != null) FooterStatus.Text = "✓ Saved";
            try { FlyShelf.Classes.NoteManager.SaveNow(); } catch { }
        }

        private void UpdateWordCount()
        {
            if (WordCountBadge == null || CharCountLabel == null) return;
            var text = NoteTextBox.Text ?? "";
            var charCount = text.Length;
            var wordCount = string.IsNullOrWhiteSpace(text)
                ? 0
                : text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

            WordCountBadge.Text = $"{wordCount} word{(wordCount == 1 ? "" : "s")}";
            CharCountLabel.Text = $"{charCount} char{(charCount == 1 ? "" : "s")}";
        }

        // ═══ HEADER BUTTONS ═══

        private void CopyBtn_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(NoteTextBox.Text))
                {
                    Clipboard.SetText(NoteTextBox.Text);
                    FooterStatus.Text = "✓ Copied";
                }
            }
            catch { }
            e.Handled = true;
        }

        private void PinBtn_Click(object sender, MouseButtonEventArgs e)
        {
            _isPinned = !_isPinned;
            Topmost = _isPinned;
            PinIcon.Symbol = _isPinned
                ? Wpf.Ui.Controls.SymbolRegular.Pin24
                : Wpf.Ui.Controls.SymbolRegular.PinOff24;
            FooterStatus.Text = _isPinned ? "📌 Pinned" : "Unpinned";
            e.Handled = true;
        }

        private void CloseBtn_Click(object sender, MouseButtonEventArgs e)
        {
            Close();
            e.Handled = true;
        }

        // ═══ WINDOW CHROME ═══

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // Double-click toggles between compact and expanded
                if (Height < 500)
                { Width = 520; Height = 600; }
                else
                { Width = 360; Height = 420; }
            }
            else
            {
                try { DragMove(); } catch { }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _saveTimer.Stop();
            SaveContent();
        }

        // ═══ KEYBOARD SHORTCUTS ═══

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                _saveTimer.Stop();
                SaveContent();
                FooterStatus.Text = "✓ Saved";
                e.Handled = true;
            }
        }
    }
}
