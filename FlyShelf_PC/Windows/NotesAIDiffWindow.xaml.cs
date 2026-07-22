using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class NotesAIDiffWindow : MicaWPF.Controls.MicaWindow
    {
        public string ImprovedText { get; private set; } = string.Empty;
        public bool IsApplied { get; private set; } = false;

        private readonly string _originalText;

        public NotesAIDiffWindow(string originalText)
        {
            InitializeComponent();
            _originalText = originalText;
            OriginalTextBox.Text = originalText;

            SubtitleText.Text = $"Powered by {AiProviderService.Instance.ActiveProviderName}";

            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                string improved = "";
                string summary = "";

                await Task.Run(async () =>
                {
                    // Get improved version
                    improved = await AiProviderService.Instance.GenerateAsync(
                        _originalText,
                        "You are a writing assistant. Improve the following note: fix grammar, improve clarity, better structure and formatting. " +
                        "Return ONLY the improved text without any explanations or preamble.",
                        maxTokens: 4096);
                });

                // Now get change summary
                await Task.Run(async () =>
                {
                    try
                    {
                        summary = await AiProviderService.Instance.GenerateAsync(
                            $"Original:\n{_originalText}\n\nImproved:\n{improved}",
                            "Compare the original and improved text. List the changes in 1-3 short bullet points. " +
                            "Example: '• Fixed 2 grammar errors\n• Restructured for clarity\n• Added bullet formatting'. " +
                            "Be very concise. Return ONLY the bullet points.",
                            maxTokens: 200);
                    }
                    catch { summary = "AI improvements applied."; }
                });

                // Show results
                LoadingView.Visibility = Visibility.Collapsed;
                ContentView.Visibility = Visibility.Visible;
                ImprovedTextBox.Text = improved;
                ChangeSummaryText.Text = summary;
                KeepBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                LoadingView.Visibility = Visibility.Collapsed;
                ContentView.Visibility = Visibility.Visible;
                ChangeSummaryText.Text = $"⚠️ Error: {ex.Message}";
                ImprovedTextBox.Text = _originalText;
                KeepBtn.IsEnabled = false;
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Keep_Click(object sender, RoutedEventArgs e)
        {
            ImprovedText = ImprovedTextBox.Text;
            IsApplied = true;
            DialogResult = true;
            Close();
        }

        private void Discard_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
