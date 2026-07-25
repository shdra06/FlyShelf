using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    /// <summary>
    /// AI provider setup popup — allows the user to select a provider,
    /// enter an API key, test the connection, and persist settings.
    /// Usage: new AiSetupPopup(ownerWindow).ShowDialog();
    /// </summary>
    public partial class AiSetupPopup : Window
    {
        private string _selectedProvider = "gemini";
        private bool _isKeyVisible = false;
        private bool _advancedExpanded = false;
        private bool _isTesting = false;

        // Accent colours per provider
        private static readonly SolidColorBrush GeminiBorder  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4285F4"));
        private static readonly SolidColorBrush OpenAIBorder  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10A37F"));
        private static readonly SolidColorBrush ClaudeBorder  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97757"));
        private static readonly SolidColorBrush DefaultBorder = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B"));

        public AiSetupPopup(Window owner = null)
        {
            InitializeComponent();
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            this.Closed += (s, e) => FlyShelf.Classes.SmoothScrollFeature.Detach(this);

            if (owner != null)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            // Allow dragging the window from anywhere on the card
            MainCard.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                    DragMove();
            };

            // Default: mask API key input
            apiKeyBox.Visibility = Visibility.Collapsed;
            apiKeyMaskedBox.Visibility = Visibility.Visible;

            // Default provider selection
            SelectProvider("gemini");
        }

        // ──────────────────── Window Loaded ────────────────────

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = SettingsManager.Current;

                // Restore previously saved provider
                if (!string.IsNullOrWhiteSpace(settings.AiProvider))
                {
                    SelectProvider(settings.AiProvider.ToLowerInvariant());
                }

                // Restore API key (decrypted via DPAPI property)
                if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
                {
                    apiKeyMaskedBox.Text = settings.AiApiKey;
                    apiKeyBox.Text = settings.AiApiKey;
                }

                // Restore model override
                if (!string.IsNullOrWhiteSpace(settings.AiModelOverride))
                {
                    modelOverrideBox.Text = settings.AiModelOverride;
                    _advancedExpanded = true;
                    modelOverrideBox.Visibility = Visibility.Visible;
                    advancedToggle.Content = "Advanced ▴";
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("AI SETUP", $"AiSetupPopup load error: {ex.Message}");
            }
        }

        // ──────────────────── Provider Selection ────────────────────

        private void GeminiCard_Click(object sender, MouseButtonEventArgs e) => SelectProvider("gemini");
        private void OpenAICard_Click(object sender, MouseButtonEventArgs e) => SelectProvider("openai");
        private void ClaudeCard_Click(object sender, MouseButtonEventArgs e) => SelectProvider("claude");

        private void SelectProvider(string provider)
        {
            _selectedProvider = provider;

            // Reset all cards
            geminiCard.BorderBrush = DefaultBorder;
            openaiCard.BorderBrush = DefaultBorder;
            claudeCard.BorderBrush = DefaultBorder;
            geminiCard.Effect = null;
            openaiCard.Effect = null;
            claudeCard.Effect = null;

            // Highlight selected card
            switch (provider)
            {
                case "gemini":
                    geminiCard.BorderBrush = GeminiBorder;
                    geminiCard.Effect = CreateGlow(GeminiBorder.Color);
                    break;
                case "openai":
                    openaiCard.BorderBrush = OpenAIBorder;
                    openaiCard.Effect = CreateGlow(OpenAIBorder.Color);
                    break;
                case "claude":
                    claudeCard.BorderBrush = ClaudeBorder;
                    claudeCard.Effect = CreateGlow(ClaudeBorder.Color);
                    break;
            }

            // Clear previous test result when provider changes
            testResultText.Visibility = Visibility.Collapsed;
        }

        private static DropShadowEffect CreateGlow(Color color)
        {
            return new DropShadowEffect
            {
                Color = color,
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.45,
                RenderingBias = RenderingBias.Quality
            };
        }

        // ──────────────────── API Key Visibility Toggle ────────────────────

        private void ToggleVisibility_Click(object sender, RoutedEventArgs e)
        {
            _isKeyVisible = !_isKeyVisible;

            if (_isKeyVisible)
            {
                // Show plain text
                apiKeyBox.Text = apiKeyMaskedBox.Text;
                apiKeyMaskedBox.Visibility = Visibility.Collapsed;
                apiKeyBox.Visibility = Visibility.Visible;
                apiKeyBox.Focus();
                toggleVisibilityBtn.Content = "🙈";
            }
            else
            {
                // Mask it
                apiKeyMaskedBox.Text = apiKeyBox.Text;
                apiKeyBox.Visibility = Visibility.Collapsed;
                apiKeyMaskedBox.Visibility = Visibility.Visible;
                apiKeyMaskedBox.Focus();
                toggleVisibilityBtn.Content = "👁";
            }
        }

        /// <summary>Keep the two text boxes in sync when the user types in the masked box.</summary>
        private void ApiKeyMaskedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Use the masking character for display
            if (!_isKeyVisible && apiKeyMaskedBox.IsFocused)
            {
                apiKeyBox.Text = apiKeyMaskedBox.Text;
            }
        }

        // ──────────────────── Get Free Key Link ────────────────────

        private void GetFreeKey_Click(object sender, RoutedEventArgs e)
        {
            string url = _selectedProvider switch
            {
                "gemini" => "https://aistudio.google.com/apikey",
                "openai" => "https://platform.openai.com/api-keys",
                "claude" => "https://console.anthropic.com/settings/keys",
                _ => "https://aistudio.google.com/apikey"
            };

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.LogAction("AI SETUP", $"Failed to open URL: {ex.Message}");
            }
        }

        // ──────────────────── Advanced Toggle ────────────────────

        private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
        {
            _advancedExpanded = !_advancedExpanded;
            modelOverrideBox.Visibility = _advancedExpanded ? Visibility.Visible : Visibility.Collapsed;
            advancedToggle.Content = _advancedExpanded ? "Advanced ▴" : "Advanced ▾";
        }

        // ──────────────────── Test Connection ────────────────────

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_isTesting) return;

            string key = GetCurrentApiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                ShowTestResult("❌ Please enter an API key first.", false);
                return;
            }

            _isTesting = true;
            testConnectionBtn.IsEnabled = false;
            testBtnText.Text = "Testing...";
            testResultText.Visibility = Visibility.Collapsed;

            try
            {
                // Temporarily apply the key so the service can use it
                SettingsManager.Current.AiProvider = _selectedProvider;
                SettingsManager.Current.AiApiKey = key;
                SettingsManager.Current.AiModelOverride = modelOverrideBox.Text;
                AiProviderService.Instance.ClearCache();

                var result = await Task.Run(() => AiProviderService.Instance.TestConnectionAsync()).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (result.success)
                        ShowTestResult($"✅ Connected to {result.provider} ({result.responseTimeMs}ms)", true);
                    else
                        ShowTestResult($"❌ Connection failed: {result.message}", false);
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    ShowTestResult($"❌ Connection failed: {ex.Message}", false));
                Logger.LogAction("AI SETUP", $"Test connection error: {ex.Message}");
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _isTesting = false;
                    testConnectionBtn.IsEnabled = true;
                    testBtnText.Text = "Test Connection";
                });
            }
        }

        private void ShowTestResult(string message, bool success)
        {
            testResultText.Text = message;
            testResultText.Foreground = success
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            testResultText.Visibility = Visibility.Visible;
        }

        // ──────────────────── Save ────────────────────

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string key = GetCurrentApiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                ShowTestResult("❌ Please enter an API key before saving.", false);
                return;
            }

            try
            {
                SettingsManager.Current.AiProvider = _selectedProvider;
                SettingsManager.Current.AiApiKey = key;   // triggers DPAPI encryption via AiApiKeyEncrypted
                SettingsManager.Current.AiModelOverride = modelOverrideBox.Text;
                AiProviderService.Instance.ClearCache();

                ToastWindow.ShowToast("✅ AI provider configured!");

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Logger.LogAction("AI SETUP", $"AiSetupPopup save error: {ex.Message}");
                ShowTestResult($"❌ Save failed: {ex.Message}", false);
            }
        }

        // ──────────────────── Close ────────────────────

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ──────────────────── Helpers ────────────────────

        /// <summary>Returns the current API key from whichever box is visible.</summary>
        private string GetCurrentApiKey()
        {
            return _isKeyVisible ? apiKeyBox.Text?.Trim() : apiKeyMaskedBox.Text?.Trim();
        }
    }
}
