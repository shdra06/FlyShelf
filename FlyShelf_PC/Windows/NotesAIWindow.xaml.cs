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
        private readonly string _actionType; // "Summarize", "Rewrite", "Organize", "Expand", "Explain", "Actions", "AutoTag", "Translate:XXX"

        public NotesAIWindow(string originalText, string actionType)
        {
            InitializeComponent();
            _originalText = originalText;
            _actionType = actionType;

            // Set titles
            string displayAction = actionType.StartsWith("Translate:", StringComparison.OrdinalIgnoreCase)
                ? $"Translate → {actionType.Substring(10)}"
                : actionType;
            HeaderTitle.Text = $"{displayAction} Note";
            SubtitleText.Text = $"Powered by {AiProviderService.Instance.ActiveProviderName}";
            LoadingText.Text = $"AI is working on your {displayAction.ToLower()}...";

            // Kick off generation
            Loaded += NotesAIWindow_Loaded;
        }

        private async void NotesAIWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string result = string.Empty;

                await Task.Run(async () =>
                {
                    if (_actionType.StartsWith("Translate:", StringComparison.OrdinalIgnoreCase))
                    {
                        var lang = _actionType.Substring(10);
                        result = await AiProviderService.Instance.TranslateAsync(_originalText, lang);
                    }
                    else if (_actionType.Equals("Summarize", StringComparison.OrdinalIgnoreCase))
                    {
                        result = await AiProviderService.Instance.SummarizeAsync(_originalText);
                    }
                    else if (_actionType.Equals("Rewrite", StringComparison.OrdinalIgnoreCase))
                    {
                        result = await AiProviderService.Instance.RewriteAsync(_originalText);
                    }
                    else if (_actionType.Equals("Organize", StringComparison.OrdinalIgnoreCase))
                    {
                        result = await AiProviderService.Instance.OrganizeAsync(_originalText);
                    }
                    else if (_actionType.Equals("Expand", StringComparison.OrdinalIgnoreCase))
                    {
                        result = await AiProviderService.Instance.ExpandAsync(_originalText);
                    }
                    else if (_actionType.Equals("Explain", StringComparison.OrdinalIgnoreCase))
                    {
                        result = await AiProviderService.Instance.ExplainAsync(_originalText);
                    }
                    else if (_actionType.Equals("Actions", StringComparison.OrdinalIgnoreCase))
                    {
                        result = await AiProviderService.Instance.ExtractActionsAsync(_originalText);
                    }
                    else if (_actionType.Equals("AutoTag", StringComparison.OrdinalIgnoreCase))
                    {
                        result = await AiProviderService.Instance.AutoTagAsync(_originalText);
                    }
                });

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
                if (ClipboardHelper.SafeSetText(ResultTextBox.Text))
                    ToastWindow.ShowToast("📋 Suggestion copied to clipboard.");
                else
                    ToastWindow.ShowToast("⚠️ Clipboard busy — try again.");
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

