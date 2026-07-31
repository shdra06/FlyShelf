// ---------------------------------------------------------------
// AiSettingsControl — Self-contained AI Settings UserControl
// Extracted from MainWindow.AiSettings.cs (Decomposition Phase 1).
// Contains all settings UI logic: populate, save, event handlers.
// MainWindow coordinates panel visibility via Open()/Close().
// ---------------------------------------------------------------
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlyShelf.Classes;

namespace FlyShelf.Controls
{
    public partial class AiSettingsControl : UserControl
    {
        private System.Windows.Threading.DispatcherTimer? _aiModelSaveTimer;

        public AiSettingsControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Populate all AI settings fields from SettingsManager.
        /// Called by MainWindow when the panel is opened.
        /// </summary>
        public void Populate()
        {
            var settings = SettingsManager.Current;

            // API Key — show masked version
            if (!string.IsNullOrEmpty(settings.AiApiKey))
            {
                string key = settings.AiApiKey;
                AiApiKeyBox.Text = key.Length > 8 ? string.Concat(key.AsSpan(0, 4), "...", key.AsSpan(key.Length - 4)) : "••••••••";
                AiApiKeyBox.Tag = "masked"; // Track that it's showing masked value
                AiApiKeyStatus.Text = "API key configured";
                AiApiKeyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
            else
            {
                AiApiKeyBox.Text = "";
                AiApiKeyBox.Tag = null;
                AiApiKeyStatus.Text = "No API key set — some features use local processing";
                AiApiKeyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            }

            // Provider radio buttons
            var provider = settings.AiProvider?.ToLowerInvariant() ?? "auto";
            AiProviderAuto.IsChecked = provider == "auto";
            AiProviderGemini.IsChecked = provider == "gemini";
            AiProviderOpenAI.IsChecked = provider == "openai";
            AiProviderClaude.IsChecked = provider == "claude";
            AiProviderWindows.IsChecked = provider == "windows";
            UpdateProviderStatus();

            // Default method radio buttons
            var method = settings.DefaultAiMethod?.ToLowerInvariant() ?? "auto";
            AiMethodAuto.IsChecked = method == "auto";
            AiMethodApi.IsChecked = method == "api";
            AiMethodLocal.IsChecked = method == "local";

            // Model override
            AiModelOverrideBox.Text = settings.AiModelOverride ?? "";

            // AI enabled toggle
            AiEnabledToggle.IsChecked = settings.AiEnabled;

            // Current status
            UpdateAiCurrentStatus();
        }

        private void UpdateProviderStatus()
        {
            string active = AiProviderService.Instance.ActiveProviderName;
            AiProviderStatus.Text = $"Active provider: {active}";
        }

        private void UpdateAiCurrentStatus()
        {
            var provider = AiProviderService.Instance.ActiveProviderName;
            bool hasKey = AiProviderService.Instance.HasCloudApiKey;
            bool winAi = WindowsAIService.Instance.IsAvailable;

            string status = $"Provider: {provider}";
            if (hasKey) status += "\n✅ Cloud API key configured";
            else status += "\n⚠️ No cloud API key";
            if (winAi) status += "\n✅ Windows AI available";
            else status += "\n⚪ Windows AI not available";
            status += $"\nAI Enabled: {(SettingsManager.Current.AiEnabled ? "Yes" : "No")}";

            AiCurrentStatus.Text = status;
        }

        // ═══ Event Handlers ═══

        private void AiApiKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            // When user starts typing in masked field, clear the mask
            if (AiApiKeyBox.Tag as string == "masked" && AiApiKeyBox.IsFocused)
            {
                AiApiKeyBox.Tag = null;
                AiApiKeyBox.Text = "";
            }
        }

        private void AiApiKeySave_Click(object sender, RoutedEventArgs e)
        {
            string newKey = AiApiKeyBox.Text?.Trim() ?? "";

            // Don't save the masked display value
            if (AiApiKeyBox.Tag as string == "masked") return;

            SettingsManager.Current.AiApiKey = newKey;
            SettingsManager.Save();

            if (!string.IsNullOrEmpty(newKey))
            {
                AiApiKeyBox.Text = newKey.Length > 8 ? string.Concat(newKey.AsSpan(0, 4), "...", newKey.AsSpan(newKey.Length - 4)) : "••••••••";
                AiApiKeyBox.Tag = "masked";
                AiApiKeyStatus.Text = "API key saved and encrypted!";
                AiApiKeyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                Windows.ToastWindow.ShowToast("API key saved!");
            }
            else
            {
                AiApiKeyStatus.Text = "API key cleared";
                AiApiKeyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                Windows.ToastWindow.ShowToast("API key cleared");
            }

            UpdateProviderStatus();
            UpdateAiCurrentStatus();
        }

        private void AiProvider_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                SettingsManager.Current.AiProvider = tag;
                SettingsManager.Save();
                UpdateProviderStatus();
                UpdateAiCurrentStatus();
            }
        }

        private void AiMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                SettingsManager.Current.DefaultAiMethod = tag;
                SettingsManager.Save();
            }
        }

        private void AiModelOverride_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Debounced save — save after user stops typing
            if (_aiModelSaveTimer == null)
            {
                _aiModelSaveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(800)
                };
                _aiModelSaveTimer.Tick += (s, ev) =>
                {
                    _aiModelSaveTimer.Stop();
                    SettingsManager.Current.AiModelOverride = AiModelOverrideBox.Text?.Trim() ?? "";
                    SettingsManager.Save();
                    UpdateProviderStatus();
                    UpdateAiCurrentStatus();
                };
            }
            _aiModelSaveTimer.Stop();
            _aiModelSaveTimer.Start();
        }

        private void AiEnabled_Changed(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.AiEnabled = AiEnabledToggle.IsChecked == true;
            SettingsManager.Save();
            UpdateAiCurrentStatus();
        }
    }
}
