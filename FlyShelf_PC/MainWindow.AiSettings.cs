// ---------------------------------------------------------------
// MainWindow — AI Settings Panel Coordinator (Thin Shell)
// Decomposition Phase 1: All settings UI logic moved to
// Controls/AiSettingsControl.xaml.cs. This file only handles
// panel open/close coordination and mutual exclusion.
// ---------------------------------------------------------------
using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private bool _isAiSettingsActive;

        private void AiSettingsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isAiSettingsActive)
                CloseAiSettingsPanel();
            else
                OpenAiSettingsPanel();
        }

        private void OpenAiSettingsPanel()
        {
            // Close other modes
            if (_isNotesActive) CloseNotesPanel(immediate: true);
            if (_isTodoActive) CloseTodoPanel(immediate: true);
            if (_isResearchActive) CloseResearchPanel(immediate: true);
            if (_isSearchActive) CloseSearch(switchingPanel: true);
            if (_isFilterBarActive) ToggleFilterBar(false);

            _isAiSettingsActive = true;
            Title = "AI Settings";

            // Hide clipboard, show AI settings
            ShelfListView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            AiSettingsPanel.Visibility = Visibility.Visible;

            // Populate current values (delegated to UserControl)
            AiSettingsContent.Populate();

            // Swap AI button to clipboard icon
            AiSettingsToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24 };
            AiSettingsToggleBtn.ToolTip = "Back to Clipboard";

            // Fade in
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            AiSettingsPanel.BeginAnimation(UIElement.OpacityProperty, null); // Clear stacked animation clocks
            AiSettingsPanel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        private void CloseAiSettingsPanel(bool immediate = false)
        {
            if (!_isAiSettingsActive) return;
            _isAiSettingsActive = false;
            Title = "FlyShelf";

            // Restore button
            AiSettingsToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Sparkle24 };
            AiSettingsToggleBtn.ToolTip = "AI Settings";

            if (immediate)
            {
                AiSettingsPanel.Visibility = Visibility.Collapsed;
                AiSettingsPanel.Opacity = 0;
                ShelfListView.Visibility = Visibility.Visible;
                return;
            }

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s, e) =>
            {
                AiSettingsPanel.Visibility = Visibility.Collapsed;
                ShelfListView.Visibility = Visibility.Visible;
            };
            AiSettingsPanel.BeginAnimation(UIElement.OpacityProperty, null); // Clear stacked animation clocks
            AiSettingsPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }
}
