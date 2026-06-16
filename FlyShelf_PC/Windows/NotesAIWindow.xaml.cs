using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class NotesAIWindow : MicaWPF.Controls.MicaWindow
    {
        public string ResultText { get; private set; } = string.Empty;
        public bool IsApplied { get; private set; } = false;

        private readonly string _originalText;
        private readonly string _actionType; // "Summarize", "Rewrite", "Organize"
        private readonly bool _useWindowsAI;

        public NotesAIWindow(string originalText, string actionType, bool useWindowsAI = true)
        {
            InitializeComponent();
            _originalText = originalText;
            _actionType = actionType;
            _useWindowsAI = useWindowsAI;

            // Set titles
            string engineLabel = _useWindowsAI ? "" : " (Offline)";
            HeaderTitle.Text = $"{actionType} Note{engineLabel}";
            LoadingText.Text = _useWindowsAI
                ? $"AI is working on your {actionType.ToLower()}..."
                : $"Processing your {actionType.ToLower()}...";

            // Kick off generation
            Loaded += NotesAIWindow_Loaded;
        }

        private async void NotesAIWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string result = string.Empty;

                if (_useWindowsAI)
                {
                    // Run on thread pool to keep UI responsive
                    await Task.Run(async () =>
                    {
                        if (_actionType.Equals("Summarize", StringComparison.OrdinalIgnoreCase))
                        {
                            result = await WindowsAIService.Instance.SummarizeAsync(_originalText);
                        }
                        else if (_actionType.Equals("Rewrite", StringComparison.OrdinalIgnoreCase))
                        {
                            result = await WindowsAIService.Instance.RewriteAsync(_originalText);
                        }
                        else if (_actionType.Equals("Organize", StringComparison.OrdinalIgnoreCase))
                        {
                            result = await WindowsAIService.Instance.OrganizeAsync(_originalText);
                        }
                    });
                }
                else
                {
                    // Offline extractive processing — instant, < 1 MB RAM
                    await Task.Run(() =>
                    {
                        if (_actionType.Equals("Summarize", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OfflineTextProcessor.Summarize(_originalText);
                        }
                        else if (_actionType.Equals("Rewrite", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OfflineTextProcessor.Rewrite(_originalText);
                        }
                        else if (_actionType.Equals("Organize", StringComparison.OrdinalIgnoreCase))
                        {
                            result = OfflineTextProcessor.Organize(_originalText);
                        }
                    });
                }

                // Show result in UI
                LoadingView.Visibility = Visibility.Collapsed;
                ResultView.Visibility = Visibility.Visible;
                ResultTextBox.Text = result;
                ResultTextBox.Focus();
                ResultTextBox.SelectAll();

                ApplyBtn.IsEnabled = true;
                CopyBtn.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LoadingView.Visibility = Visibility.Collapsed;
                ResultView.Visibility = Visibility.Visible;
                ResultTextBox.Text = $"⚠️ Error processing note:\n\n{ex.Message}";
                ResultTextBox.IsReadOnly = true;
                ApplyBtn.IsEnabled = false;
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(ResultTextBox.Text))
            {
                Clipboard.SetText(ResultTextBox.Text);
                ToastWindow.ShowToast("📋 Suggestion copied to clipboard.");
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            ResultText = ResultTextBox.Text;
            IsApplied = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

