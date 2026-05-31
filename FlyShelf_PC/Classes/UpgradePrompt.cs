// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// UpgradePrompt â€” Shows upgrade dialogs when free-tier limits are hit.
// Also provides the license activation dialog.
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;


namespace FlyShelf.Classes
{
    public static class UpgradePrompt
    {
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        // LIMIT REACHED PROMPTS
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        public static void ShowPdfMergeLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "PDF Merge Limit Reached",
                $"You've used {LicenseManager.FREE_PDF_MERGE_DAILY}/{LicenseManager.FREE_PDF_MERGE_DAILY} free PDF merges today.",
                "Upgrade to FlyShelf Pro for unlimited PDF merges!",
                "ðŸ“„",
                owner);
        }

        public static void ShowPdfSaveLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "PDF Save Limit Reached",
                $"You've used {LicenseManager.FREE_PDF_SAVE_DAILY}/{LicenseManager.FREE_PDF_SAVE_DAILY} free PDF page extractions today.",
                "Upgrade to FlyShelf Pro for unlimited page extraction!",
                "ðŸ“„",
                owner);
        }

        public static void ShowDocConvertLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Document Conversion Limit Reached",
                $"You've used {LicenseManager.FREE_DOC_CONVERT_DAILY}/{LicenseManager.FREE_DOC_CONVERT_DAILY} free document conversions today.",
                "Upgrade to FlyShelf Pro for unlimited conversions!",
                "â™»ï¸",
                owner);
        }

        public static void ShowImageToPdfLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Image â†’ PDF Limit Reached",
                $"You've used {LicenseManager.FREE_IMAGE_TO_PDF_DAILY}/{LicenseManager.FREE_IMAGE_TO_PDF_DAILY} free image-to-PDF conversions today.",
                "Upgrade to FlyShelf Pro for unlimited conversions!",
                "ðŸ–¼ï¸",
                owner);
        }

        public static void ShowQrScanLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "QR Scan Limit Reached",
                $"You've used {LicenseManager.FREE_QR_SCAN_DAILY}/{LicenseManager.FREE_QR_SCAN_DAILY} free QR scans today.",
                "Upgrade to FlyShelf Pro for unlimited QR scanning!",
                "ðŸ“·",
                owner);
        }

        public static void ShowOcrLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "OCR Limit Reached",
                $"You've used {LicenseManager.FREE_OCR_DAILY}/{LicenseManager.FREE_OCR_DAILY} free OCR extractions today.",
                "Upgrade to FlyShelf Pro for unlimited text extraction!",
                "ðŸ”",
                owner);
        }

        public static void ShowTableExtractLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Table Extraction Limit Reached",
                $"You've used {LicenseManager.FREE_TABLE_EXTRACT_DAILY}/{LicenseManager.FREE_TABLE_EXTRACT_DAILY} free table extractions today.",
                "Upgrade to FlyShelf Pro for unlimited table extraction!",
                "ðŸ“Š",
                owner);
        }

        public static void ShowThemeLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Custom Themes â€” Pro Feature",
                "Custom themes are available for FlyShelf Pro users.",
                "Upgrade to unlock all themes including Glass UI!",
                "ðŸŽ¨",
                owner);
        }

        public static void ShowCustomWallpaperLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Custom Wallpaper â€” Pro Feature",
                "Setting a custom clipboard wallpaper is a Pro feature.",
                "Upgrade to FlyShelf Pro to personalize your wallpaper!",
                "ðŸ–¼ï¸",
                owner);
        }

        public static void ShowCloudflareLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Global Sync â€” Pro Feature",
                "Cloudflare tunnel (internet-wide sync) is available for FlyShelf Pro users.",
                "Upgrade to sync your clipboard across the internet!",
                "ðŸŒ",
                owner);
        }

        public static void ShowPinLimit()
        {
            FlyShelf.Windows.ToastWindow.ShowToast($"ðŸ“Œ Pin limit reached ({LicenseManager.FREE_PIN_LIMIT} max). Upgrade to Pro for unlimited pins!");
        }

        public static void ShowTodoLimit()
        {
            FlyShelf.Windows.ToastWindow.ShowToast($"âœ… To-do limit reached ({LicenseManager.FREE_TODO_DAILY} items/day). Upgrade to Pro for unlimited!");
        }

        public static void ShowNoteHistoryLimit()
        {
            FlyShelf.Windows.ToastWindow.ShowToast($"ðŸ“ Notes older than {LicenseManager.FREE_NOTE_DAYS} days are only available in Pro.");
        }

        public static void ShowCustomSnifferLimit()
        {
            FlyShelf.Windows.ToastWindow.ShowToast("ðŸ“ Custom sniffer folders are a Pro feature. Upgrade to add more folders!");
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        // ACTIVATION DIALOG
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        /// <summary>
        /// Shows the license key activation dialog. Returns true if activation succeeded.
        /// </summary>
        public static bool ShowActivationDialog(Window? owner = null)
        {
            var resolvedOwner = ResolveActiveOwner(owner);

            var dialog = new Window
            {
                Title = "Activate FlyShelf Pro",
                Width = 440,
                Height = 280,
                WindowStartupLocation = resolvedOwner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = resolvedOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E))
            };

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title
            var title = new TextBlock
            {
                Text = "ðŸ”‘ Enter Your License Key",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            // Subtitle
            var subtitle = new TextBlock
            {
                Text = "Paste your FlyShelf Pro license key below:",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(subtitle, 1);
            grid.Children.Add(subtitle);

            // Key input
            var keyInput = new TextBox
            {
                FontSize = 15,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x6A)),
                BorderThickness = new Thickness(1),
                MaxLength = 27, // FS-PRO-XXXX-XXXX-XXXX-XXXX
                Margin = new Thickness(0, 0, 0, 8)
            };
            // Placeholder text
            keyInput.Text = "";
            keyInput.ToolTip = "Format: FS-PRO-XXXX-XXXX-XXXX-XXXX";
            Grid.SetRow(keyInput, 2);
            grid.Children.Add(keyInput);

            // Status label
            var statusLabel = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(statusLabel, 3);
            grid.Children.Add(statusLabel);

            // "Buy a License Key" link
            var buyLink = new TextBlock
            {
                FontSize = 12,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 12)
            };
            buyLink.Inlines.Add(new System.Windows.Documents.Run("ðŸ›’ ")
            {
                FontSize = 13
            });
            var linkRun = new System.Windows.Documents.Run("Don't have a key? Buy FlyShelf Pro (â‚¹299)")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
                TextDecorations = TextDecorations.Underline
            };
            buyLink.Inlines.Add(linkRun);
            buyLink.MouseLeftButtonUp += (s, ev) =>
            {
                try
                {
                    string deviceId = FlyShelf.Classes.SettingsManager.Current.DeviceId ?? "";
#if MSIX_STORE
                    FlyShelf.Windows.ToastWindow.ShowToast("â„¹ï¸ Pro upgrade is available at https://fly-shelf.vercel.app/");
#else
                    string paymentUrl = $"https://fly-shelf.vercel.app/pricing.html?deviceId={Uri.EscapeDataString(deviceId)}";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = paymentUrl,
                        UseShellExecute = true
                    });
#endif
                }
                catch { }
            };
            Grid.SetRow(buyLink, 4);
            grid.Children.Add(buyLink);

            // Buttons panel
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x4E)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            cancelBtn.Click += (s, e) => dialog.DialogResult = false;

            var activateBtn = new Button
            {
                Content = "âœ“ Activate",
                Padding = new Thickness(20, 8, 20, 8),
                FontWeight = FontWeights.SemiBold,
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x8B, 0x5C, 0xF6),
                    Color.FromRgb(0x63, 0x66, 0xF1),
                    45),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            activateBtn.Click += (s, e) =>
            {
                string key = keyInput.Text.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    statusLabel.Text = "âš  Please enter a license key.";
                    statusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                    return;
                }

                if (LicenseManager.ActivateLicense(key))
                {
                    statusLabel.Text = "âœ… License activated! Welcome to FlyShelf Pro!";
                    statusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    FlyShelf.Windows.ToastWindow.ShowToast("ðŸŽ‰ FlyShelf Pro activated! All features unlocked!");
                    dialog.DialogResult = true;
                }
                else
                {
                    statusLabel.Text = "âŒ Invalid license key. Please check and try again.";
                    statusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                }
            };

            buttonPanel.Children.Add(cancelBtn);
            buttonPanel.Children.Add(activateBtn);
            Grid.SetRow(buttonPanel, 5);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;
            return dialog.ShowDialog() == true;
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        // INTERNAL â€” Generic limit dialog builder
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private static Window? ResolveActiveOwner(Window? owner)
        {
            if (owner != null && owner.IsVisible)
            {
                return owner;
            }

            try
            {
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    if (app.Dispatcher.CheckAccess())
                    {
                        var resolved = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                                       ?? (app.MainWindow != null && app.MainWindow.IsVisible ? app.MainWindow : null);
                        if (resolved != null) return resolved;
                    }
                    else
                    {
                        return app.Dispatcher.Invoke(() =>
                            app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                            ?? (app.MainWindow != null && app.MainWindow.IsVisible ? app.MainWindow : null));
                    }
                }
            }
            catch { }

            return null;
        }

        private static void ShowLimitDialog(string title, string message, string upgradeMessage, string emoji, Window? owner)
        {
            var resolvedOwner = ResolveActiveOwner(owner);

            MessageBoxResult result;
            if (resolvedOwner != null)
            {
                result = MessageBox.Show(
                    resolvedOwner,
                    $"{emoji} {message}\n\n{upgradeMessage}\n\nYour daily limits will reset at midnight.\n\nWould you like to enter a license key to upgrade?",
                    $"FlyShelf â€” {title}",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
            }
            else
            {
                result = MessageBox.Show(
                    $"{emoji} {message}\n\n{upgradeMessage}\n\nYour daily limits will reset at midnight.\n\nWould you like to enter a license key to upgrade?",
                    $"FlyShelf â€” {title}",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
            }

            if (result == MessageBoxResult.Yes)
            {
                ShowActivationDialog(resolvedOwner);
            }
        }
    }
}

