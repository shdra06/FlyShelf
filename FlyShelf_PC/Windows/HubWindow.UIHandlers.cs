// ═══════════════════════════════════════════════════════════════════════
// HubWindow.UIHandlers.cs — Settings UI click handlers: incognito mode,
// size reset/steppers/preview, widget alignment buttons, and widget toggle.
// Part of the HubWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Windows;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        // ═══ Incognito Mode ═══

        private void IncognitoToggle_Click(object sender, RoutedEventArgs e)
        {
            if (Classes.IncognitoManager.IsIncognito)
            {
                Classes.IncognitoManager.DisableIncognito();
                UpdateIncognitoUI();
                ToastWindow.ShowToast("Clipboard monitoring resumed");
                return;
            }

            // Get selected duration
            int hours = 1;
            if (IncognitoDurationCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selected && selected.Tag != null)
            {
                if (int.TryParse(selected.Tag.ToString(), out int h))
                    hours = h;
            }

            // v7.2 FREE: Pro gate temporarily bypassed — uncomment to re-enable
            // if (hours >= 6 && !LicenseManager.IsPro)
            // {
            //     ToastWindow.ShowToast("6+ hour incognito requires Pro!");
            //     UpgradePrompt.ShowActivationDialog(this);
            //     return;
            // }

            Classes.IncognitoManager.EnableIncognito(hours);
            UpdateIncognitoUI();
            ToastWindow.ShowToast($"Incognito enabled for {hours}h");
        }

        internal void UpdateIncognitoUI()
        {
            if (IncognitoToggleBtn == null) return;

            if (Classes.IncognitoManager.IsIncognito)
            {
                IncognitoToggleBtn.Content = "Disable";
                IncognitoToggleBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Danger;
                if (IncognitoDurationCombo != null) IncognitoDurationCombo.IsEnabled = false;

                string remaining = Classes.IncognitoManager.RemainingTimeText;
                if (!string.IsNullOrEmpty(remaining) && IncognitoStatusText != null)
                {
                    IncognitoStatusText.Text = $"Active  {remaining}";
                    IncognitoStatusText.Visibility = Visibility.Visible;
                }
            }
            else
            {
                IncognitoToggleBtn.Content = "Enable";
                IncognitoToggleBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Caution;
                if (IncognitoDurationCombo != null) IncognitoDurationCombo.IsEnabled = true;
                if (IncognitoStatusText != null) IncognitoStatusText.Visibility = Visibility.Collapsed;
            }
        }

        // ═══ Size Reset & Steppers ═══

        private void ResetClipboardSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MediumFormWidth = 360;
            SettingsManager.Current.MediumFormHeight = 380;
            SettingsManager.Save();
        }

        private void ResetFlyShelfSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MiniFormWidth = 260;
            SettingsManager.Current.MiniFormHeight = 260;
            SettingsManager.Save();
        }

        private void SizingLockedCard_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ToastWindow.ShowToast("Unlock Premium to use this option!");
            UpgradePrompt.ShowActivationDialog(this);
            e.Handled = true;
        }

        // Clipboard +/- steppers
        private void ClipW_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormWidth = Math.Min(500, SettingsManager.Current.MediumFormWidth + 5); PreviewClipboardSize_Click(null, null); }
        private void ClipW_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormWidth = Math.Max(200, SettingsManager.Current.MediumFormWidth - 5); PreviewClipboardSize_Click(null, null); }
        private void ClipH_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormHeight = Math.Min(700, SettingsManager.Current.MediumFormHeight + 5); PreviewClipboardSize_Click(null, null); }
        private void ClipH_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormHeight = Math.Max(300, SettingsManager.Current.MediumFormHeight - 5); PreviewClipboardSize_Click(null, null); }

        private void ClipboardSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (this.IsLoaded) PreviewClipboardSize_Click(null, null);
        }

        // FlyShelf +/- steppers
        private void DropW_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormWidth = Math.Min(400, SettingsManager.Current.MiniFormWidth + 5); PreviewFlyShelfSize_Click(null, null); }
        private void DropW_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormWidth = Math.Max(180, SettingsManager.Current.MiniFormWidth - 5); PreviewFlyShelfSize_Click(null, null); }
        private void DropH_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormHeight = Math.Min(350, SettingsManager.Current.MiniFormHeight + 5); PreviewFlyShelfSize_Click(null, null); }
        private void DropH_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormHeight = Math.Max(100, SettingsManager.Current.MiniFormHeight - 5); PreviewFlyShelfSize_Click(null, null); }

        private void FlyShelfSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (this.IsLoaded) PreviewFlyShelfSize_Click(null, null);
        }

        // Live Preview buttons
        private void PreviewClipboardSize_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var mainWin = Application.Current.MainWindow as MainWindow;
                if (mainWin != null)
                {
                    // Apply the new size to the clipboard popup (mode=1), not the mini FlyShelf
                    mainWin.Width = SettingsManager.Current.MediumFormWidth;
                    mainWin.Height = SettingsManager.Current.MediumFormHeight;
                    var screen = SystemParameters.WorkArea;
                    mainWin.ShowNearPosition(screen.Width / 2, screen.Height / 2, 1, false, false);
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void PreviewFlyShelfSize_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var mainWin = Application.Current.MainWindow as MainWindow;
                if (mainWin != null)
                {
                    // Apply the new size to the mini FlyShelf (mode=0, Mouse Shake mini)
                    mainWin.Width = SettingsManager.Current.MiniFormWidth;
                    mainWin.Height = SettingsManager.Current.MiniFormHeight;
                    var screen = SystemParameters.WorkArea;
                    mainWin.ShowNearPosition(screen.Width / 2, screen.Height / 2, 0, false, false);
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        // ═══ Widget Alignment & Positioning ═══

        private void UpdateAlignButtonsVisualState()
        {
            if (AlignAutoBtn == null || AlignLeftBtn == null || AlignStartBtn == null || AlignTrayBtn == null || AlignCustomBtn == null)
                return;

            int align = SettingsManager.Current.WidgetTaskbarAlignment;
            
            // Set appearance of active button to Primary, others to Secondary
            AlignAutoBtn.Appearance = align == -1 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            AlignLeftBtn.Appearance = align == 0 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            AlignStartBtn.Appearance = align == 1 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            AlignTrayBtn.Appearance = align == 2 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            AlignCustomBtn.Appearance = align == 3 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;

            // Show/hide relevant sliders
            if (PixelOffsetContainer != null)
                PixelOffsetContainer.Visibility = align != 3 ? Visibility.Visible : Visibility.Collapsed;
            if (PercentagePositionContainer != null)
                PercentagePositionContainer.Visibility = align == 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AlignAuto_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.WidgetTaskbarAlignment = -1;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void AlignLeft_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.WidgetTaskbarAlignment = 0;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void AlignStart_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.WidgetTaskbarAlignment = 1;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void AlignTray_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.WidgetTaskbarAlignment = 2;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void AlignCustom_Click(object sender, RoutedEventArgs e)
        {
            int currentOffset = SettingsManager.Current.WidgetHorizontalOffset;
            // Reset to center (50%) if the current offset is out of range for percentage mode,
            // or if it's 0 (which places widget behind Start button — invisible)
            if (currentOffset <= 0 || currentOffset > 100)
            {
                SettingsManager.Current.WidgetHorizontalOffset = 50; // default to center (50%)
            }
            SettingsManager.Current.WidgetTaskbarAlignment = 3;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void TaskbarWidgetToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Force-update Widget Positioning section visibility from code-behind
            // as a robust fallback in case the XAML BooleanToVisibilityConverter binding doesn't fire
            if (WidgetPositioningSection != null)
            {
                WidgetPositioningSection.Visibility = SettingsManager.Current.EnableTaskbarWidget
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (SettingsManager.Current.EnableTaskbarWidget)
            {
                UpdateAlignButtonsVisualState();
            }
        }
    }
}
