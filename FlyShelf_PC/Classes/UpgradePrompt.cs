// ═══════════════════════════════════════════════════════════════════
// UpgradePrompt — Shows upgrade dialogs when free-tier limits are hit.
// Also provides the license activation dialog.
// ═══════════════════════════════════════════════════════════════════
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
        private static Window? _activeDialog;

        // ═════════════════════════════════════════════════════════════
        // LIMIT REACHED PROMPTS
        // ═════════════════════════════════════════════════════════════

        public static void ShowPdfMergeLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "PDF Merge Limit Reached",
                $"You've used {LicenseManager.FREE_PDF_MERGE_DAILY}/{LicenseManager.FREE_PDF_MERGE_DAILY} free PDF merges today.",
                "Upgrade to FlyShelf Pro for unlimited PDF merges!",
                "",
                owner);
        }

        public static void ShowPdfSaveLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "PDF Save Limit Reached",
                $"You've used {LicenseManager.FREE_PDF_SAVE_DAILY}/{LicenseManager.FREE_PDF_SAVE_DAILY} free PDF page extractions today.",
                "Upgrade to FlyShelf Pro for unlimited page extraction!",
                "",
                owner);
        }

        public static void ShowDocConvertLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Document Conversion Limit Reached",
                $"You've used {LicenseManager.FREE_DOC_CONVERT_DAILY}/{LicenseManager.FREE_DOC_CONVERT_DAILY} free document conversions today.",
                "Upgrade to FlyShelf Pro for unlimited conversions!",
                "",
                owner);
        }

        public static void ShowImageToPdfLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Image to PDF Limit Reached",
                $"You've used {LicenseManager.FREE_IMAGE_TO_PDF_DAILY}/{LicenseManager.FREE_IMAGE_TO_PDF_DAILY} free image-to-PDF conversions today.",
                "Upgrade to FlyShelf Pro for unlimited conversions!",
                "",
                owner);
        }

        public static void ShowQrScanLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "QR Scan Limit Reached",
                $"You've used {LicenseManager.FREE_QR_SCAN_DAILY}/{LicenseManager.FREE_QR_SCAN_DAILY} free QR scans today.",
                "Upgrade to FlyShelf Pro for unlimited QR scanning!",
                "",
                owner);
        }

        public static void ShowOcrLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "OCR Limit Reached",
                $"You've used {LicenseManager.FREE_OCR_DAILY}/{LicenseManager.FREE_OCR_DAILY} free OCR extractions today.",
                "Upgrade to FlyShelf Pro for unlimited text extraction!",
                "",
                owner);
        }

        public static void ShowNotesAILimit(Window? owner = null)
        {
            ShowLimitDialog(
                "AI Notes Assistant",
                "AI features (Summarize, Rewrite, Organize) are exclusive to FlyShelf Pro.",
                "Upgrade to FlyShelf Pro to unlock local AI notes enhancements!",
                "✨",
                owner);
        }


        public static void ShowTableExtractLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Table Extraction Limit Reached",
                $"You've used {LicenseManager.FREE_TABLE_EXTRACT_DAILY}/{LicenseManager.FREE_TABLE_EXTRACT_DAILY} free table extractions today.",
                "Upgrade to FlyShelf Pro for unlimited table extraction!",
                "",
                owner);
        }

        public static void ShowThemeLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Custom Themes - Pro Feature",
                "Custom themes are available for FlyShelf Pro users.",
                "Upgrade to unlock all themes including Glass UI!",
                "",
                owner);
        }

        public static void ShowCustomWallpaperLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Custom Wallpaper - Pro Feature",
                "Setting a custom clipboard wallpaper is a Pro feature.",
                "Upgrade to FlyShelf Pro to personalize your wallpaper!",
                "",
                owner);
        }

        public static void ShowCloudflareLimit(Window? owner = null)
        {
            ShowLimitDialog(
                "Global Sync - Pro Feature",
                "Cloudflare tunnel (internet-wide sync) is available for FlyShelf Pro users.",
                "Upgrade to sync your clipboard across the internet!",
                "",
                owner);
        }

        public static void ShowPinLimit()
        {
            FlyShelf.Windows.ToastWindow.ShowToast($"Pin limit reached ({LicenseManager.FREE_PIN_LIMIT} max). Upgrade to Pro for unlimited pins!");
        }

        public static void ShowTodoLimit()
        {
            FlyShelf.Windows.ToastWindow.ShowToast($"To-do limit reached ({LicenseManager.FREE_TODO_DAILY} items/day). Upgrade to Pro for unlimited!");
        }

        public static void ShowNoteHistoryLimit()
        {
            FlyShelf.Windows.ToastWindow.ShowToast($"Notes older than {LicenseManager.FREE_NOTE_DAYS} days are only available in Pro.");
        }

        public static void ShowCustomSnifferLimit()
        {
            FlyShelf.Windows.ToastWindow.ShowToast("Custom sniffer folders are a Pro feature. Upgrade to add more folders!");
        }

        // ═════════════════════════════════════════════════════════════
        // ACTIVATION DIALOG
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows the license key activation dialog. Returns true if activation succeeded.
        /// </summary>
        public static bool ShowActivationDialog(Window? owner = null)
        {
            if (_activeDialog != null && _activeDialog.IsLoaded)
            {
                _activeDialog.Activate();
                _activeDialog.Focus();
                return true;
            }

            var resolvedOwner = ResolveActiveOwner(owner);

            // Resolve theme brushes with fallbacks
            var app = System.Windows.Application.Current;
            var bgBrush = app?.TryFindResource("ThemeWindowFallback") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E));
            var fgBrush = app?.TryFindResource("ThemeTextPrimary") as Brush ?? Brushes.White;
            var fgSecondary = app?.TryFindResource("ThemeTextSecondary") as Brush ?? new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            var inputBgBrush = app?.TryFindResource("ThemeOverlayBgHover") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E));
            var inputBorderBrush = app?.TryFindResource("ThemeOverlayBorderStrong") as Brush ?? new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x6A));
            var accentBrush = app?.TryFindResource("ThemeAccent") as Brush ?? new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
            var cancelBgBrush = app?.TryFindResource("ThemeOverlayBg") as Brush ?? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x4E));
            var warningBrush = app?.TryFindResource("WarningColor") as Brush ?? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            var successBrush = app?.TryFindResource("SuccessColor") as Brush ?? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            var dangerBrush = app?.TryFindResource("DangerColor") as Brush ?? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

            // Extract accent color for gradient button
            Color accentColor = (accentBrush is SolidColorBrush scb) ? scb.Color : Color.FromRgb(0x8B, 0x5C, 0xF6);
            Color accentDark = Color.FromRgb(
                (byte)Math.Max(0, (int)accentColor.R - 30),
                (byte)Math.Min(255, (int)accentColor.G + 10),
                (byte)Math.Min(255, (int)accentColor.B));

            var dialog = new Window
            {
                Title = "Activate FlyShelf Pro",
                Width = 440,
                Height = 280,
                WindowStartupLocation = resolvedOwner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = resolvedOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = bgBrush
            };

            dialog.Closed += (s, e) => _activeDialog = null;
            _activeDialog = dialog;

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
                Text = "Enter Your License Key",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = fgBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            // Subtitle
            var subtitle = new TextBlock
            {
                Text = "Paste your FlyShelf Pro license key below:",
                FontSize = 12,
                Foreground = fgSecondary,
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
                Background = inputBgBrush,
                Foreground = fgBrush,
                BorderBrush = inputBorderBrush,
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
            var linkRun = new System.Windows.Documents.Run("Don't have a key? Buy FlyShelf Pro")
            {
                Foreground = accentBrush,
                TextDecorations = TextDecorations.Underline
            };
            buyLink.Inlines.Add(linkRun);
            buyLink.MouseLeftButtonUp += (s, ev) =>
            {
                OpenSecureCheckout(dialog);
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
                Background = cancelBgBrush,
                Foreground = fgBrush,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            cancelBtn.Click += (s, e) => dialog.Close();

            var activateBtn = new Button
            {
                Content = "Activate",
                Padding = new Thickness(20, 8, 20, 8),
                FontWeight = FontWeights.SemiBold,
                Background = new LinearGradientBrush(accentColor, accentDark, 45),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            activateBtn.Click += (s, e) =>
            {
                string key = keyInput.Text.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    statusLabel.Text = "Please enter a license key.";
                    statusLabel.Foreground = warningBrush;
                    return;
                }

                if (LicenseManager.ActivateLicense(key))
                {
                    statusLabel.Text = "License activated! Welcome to FlyShelf Pro!";
                    statusLabel.Foreground = successBrush;
                    FlyShelf.Windows.ToastWindow.ShowToast("FlyShelf Pro activated! All features unlocked!");
                    dialog.Close();
                }
                else
                {
                    statusLabel.Text = "Invalid license key. Please check and try again.";
                    statusLabel.Foreground = dangerBrush;
                }
            };

            buttonPanel.Children.Add(cancelBtn);
            buttonPanel.Children.Add(activateBtn);
            Grid.SetRow(buttonPanel, 5);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;
            dialog.Show();
            return true;
        }


        // ═════════════════════════════════════════════════════════════
        // INTERNAL - Generic limit dialog builder
        // ═════════════════════════════════════════════════════════════

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
            // Prevent stacking — if a limit dialog is already open, just bring it forward
            if (_activeDialog != null && _activeDialog.IsLoaded)
            {
                _activeDialog.Activate();
                _activeDialog.Focus();
                return;
            }

            var resolvedOwner = ResolveActiveOwner(owner);

            // ── Resolve theme brushes with fallbacks ──
            var app = System.Windows.Application.Current;
            var bgBrush = app?.TryFindResource("ThemeWindowFallback") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E));
            var fgBrush = app?.TryFindResource("ThemeTextPrimary") as Brush ?? Brushes.White;
            var fgSecondary = app?.TryFindResource("ThemeTextSecondary") as Brush ?? new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            var accentBrush = app?.TryFindResource("ThemeAccent") as Brush ?? new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
            var accentLightBrush = app?.TryFindResource("ThemeAccentLight") as Brush ?? new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));
            var overlayBg = app?.TryFindResource("ThemeOverlayBg") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E));
            var overlayBorder = app?.TryFindResource("ThemeOverlayBorderStrong") as Brush ?? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A));
            var warningBrush = app?.TryFindResource("WarningColor") as Brush ?? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

            // Extract accent color for gradients
            Color accentColor = (accentBrush is SolidColorBrush scb) ? scb.Color : Color.FromRgb(0x8B, 0x5C, 0xF6);
            Color accentLightColor = (accentLightBrush is SolidColorBrush slb) ? slb.Color : Color.FromRgb(0xA7, 0x8B, 0xFA);
            Color accentDarkColor = Color.FromRgb(
                (byte)Math.Max(0, (int)accentColor.R - 40),
                (byte)Math.Max(0, (int)accentColor.G - 20),
                (byte)Math.Min(255, (int)accentColor.B));

            // ── Create dialog window ──
            var dialog = new Window
            {
                Title = $"FlyShelf — {title}",
                Width = 420,
                Height = 360,
                WindowStartupLocation = resolvedOwner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = resolvedOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true
            };

            dialog.Closed += (s, e) => _activeDialog = null;
            _activeDialog = dialog;

            // ── Outer container with border + shadow ──
            var outerBorder = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = bgBrush,
                BorderBrush = overlayBorder,
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 30,
                    ShadowDepth = 4,
                    Opacity = 0.45,
                    Color = Colors.Black
                },
                Margin = new Thickness(16) // Space for the shadow
            };

            // Allow window drag from any empty area (but not from buttons)
            outerBorder.MouseLeftButtonDown += (s, e) => {
                if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
                // Walk up visual tree to check if inside a button
                var src = e.OriginalSource as DependencyObject;
                while (src != null)
                {
                    if (src is System.Windows.Controls.Primitives.ButtonBase || src is System.Windows.Controls.Button) return;
                    src = System.Windows.Media.VisualTreeHelper.GetParent(src);
                }
                try { dialog.DragMove(); } catch { }
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });    // Row 0: Accent stripe
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // Row 1: Content
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 2: Spacer
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // Row 3: Buttons

            // ── Row 0: Gradient accent stripe at the top ──
            var accentStripe = new Border
            {
                CornerRadius = new CornerRadius(14, 14, 0, 0),
                Background = new LinearGradientBrush(accentColor, accentLightColor, 0),
                Height = 6
            };
            Grid.SetRow(accentStripe, 0);
            rootGrid.Children.Add(accentStripe);

            // ── Row 1: Content area ──
            var contentPanel = new StackPanel { Margin = new Thickness(28, 22, 28, 0) };

            // Crown icon + Pro badge row
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };

            var crownIcon = new TextBlock
            {
                Text = "👑",
                FontSize = 28,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            headerRow.Children.Add(crownIcon);

            var proBadge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush(
                    Color.FromArgb(0x30, accentColor.R, accentColor.G, accentColor.B),
                    Color.FromArgb(0x15, accentLightColor.R, accentLightColor.G, accentLightColor.B), 45),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, accentColor.R, accentColor.G, accentColor.B)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            var proBadgeText = new TextBlock
            {
                Text = "PRO FEATURE",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = accentLightBrush
            };
            proBadge.Child = proBadgeText;
            headerRow.Children.Add(proBadge);
            contentPanel.Children.Add(headerRow);

            // Title
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = fgBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            contentPanel.Children.Add(titleBlock);

            // Message
            var messageBlock = new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = fgSecondary,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Margin = new Thickness(0, 0, 0, 16)
            };
            contentPanel.Children.Add(messageBlock);

            // Separator line
            var separator = new Border
            {
                Height = 1,
                Background = overlayBorder,
                Margin = new Thickness(0, 0, 0, 14),
                Opacity = 0.5
            };
            contentPanel.Children.Add(separator);

            // Upgrade pitch with bullet point
            var upgradeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var bulletIcon = new TextBlock
            {
                Text = "✨",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 8, 0)
            };
            upgradeRow.Children.Add(bulletIcon);
            var upgradeBlock = new TextBlock
            {
                Text = upgradeMessage,
                FontSize = 13,
                Foreground = fgBrush,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.Medium
            };
            upgradeRow.Children.Add(upgradeBlock);
            contentPanel.Children.Add(upgradeRow);

            // Daily reset hint
            var resetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
            var clockIcon = new TextBlock
            {
                Text = "🕐",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 8, 0)
            };
            resetRow.Children.Add(clockIcon);
            var resetBlock = new TextBlock
            {
                Text = "Daily limits reset at midnight",
                FontSize = 12,
                Foreground = fgSecondary,
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic
            };
            resetRow.Children.Add(resetBlock);
            contentPanel.Children.Add(resetRow);

            Grid.SetRow(contentPanel, 1);
            rootGrid.Children.Add(contentPanel);

            // ── Row 3: Button bar ──
            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(28, 0, 28, 20)
            };

            // Close / dismiss button
            var dismissBtn = new Button
            {
                Content = "Maybe Later",
                Padding = new Thickness(18, 9, 18, 9),
                Margin = new Thickness(0, 0, 10, 0),
                Background = overlayBg,
                Foreground = fgSecondary,
                BorderBrush = overlayBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FontSize = 13
            };
            dismissBtn.Click += (s, e) => dialog.Close();

            // Upgrade CTA button with gradient
            var upgradeBtn = new Button
            {
                Padding = new Thickness(22, 9, 22, 9),
                FontWeight = FontWeights.SemiBold,
                Background = new LinearGradientBrush(accentColor, accentDarkColor, 135),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 13
            };
            // Use a StackPanel for icon + text in button
            var upgradeBtnContent = new StackPanel { Orientation = Orientation.Horizontal };
            upgradeBtnContent.Children.Add(new TextBlock { Text = "🔑", FontSize = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            upgradeBtnContent.Children.Add(new TextBlock { Text = "Upgrade Now", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            upgradeBtn.Content = upgradeBtnContent;
            upgradeBtn.Click += (s, e) =>
            {
                dialog.Close();
                ShowActivationDialog(resolvedOwner);
            };

            buttonBar.Children.Add(dismissBtn);
            buttonBar.Children.Add(upgradeBtn);
            Grid.SetRow(buttonBar, 3);
            rootGrid.Children.Add(buttonBar);

            // ── Close button (X) in top-right corner ──
            var closeBtn = new Button
            {
                Content = "✕",
                Width = 28,
                Height = 28,
                FontSize = 12,
                Background = Brushes.Transparent,
                Foreground = fgSecondary,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Close"
            };
            closeBtn.Click += (s, e) => dialog.Close();
            Grid.SetRow(closeBtn, 0);
            Grid.SetRowSpan(closeBtn, 2);
            rootGrid.Children.Add(closeBtn);

            outerBorder.Child = rootGrid;
            dialog.Content = outerBorder;
            dialog.ShowDialog();
        }

        // ═════════════════════════════════════════════════════════════
        // SECURE CHECKOUT DISCLOSURE (Policy 10.8 Compliant)
        // ═════════════════════════════════════════════════════════════

        public static void OpenSecureCheckout(Window? owner = null)
        {
            try
            {
                var resolvedOwner = ResolveActiveOwner(owner);
                string deviceId = FlyShelf.Classes.SettingsManager.Current.DeviceId ?? "";
                string paymentUrl = $"https://fly-shelf.vercel.app/pricing.html?deviceId={Uri.EscapeDataString(deviceId)}";

                string msg = "Secure External Checkout\n\n" +
                             "You are proceeding to our secure payment gateway to complete your upgrade purchase:\n" +
                             "https://fly-shelf.vercel.app/pricing.html\n\n" +
                             "• This transaction is processed outside of the Microsoft Store.\n" +
                             "• Microsoft gift cards, store credits, and family safety controls (such as 'Ask to Buy') are not supported.\n" +
                             "• Customer support, billing queries, and refund requests are managed directly by FlyShelf.\n\n" +
                             "Would you like to open the secure checkout page in your browser?";

                MessageBoxResult result;
                if (resolvedOwner != null)
                {
                    result = MessageBox.Show(
                        resolvedOwner,
                        msg,
                        "FlyShelf - Secure Checkout",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                }
                else
                {
                    result = MessageBox.Show(
                        msg,
                        "FlyShelf - Secure Checkout",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                }

                if (result == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = paymentUrl,
                        UseShellExecute = true
                    });
                    FlyShelf.Windows.ToastWindow.ShowToast("🛒 Opening payment page in your browser...");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("LICENSE", $"Failed to open checkout: {ex.Message}");
                FlyShelf.Windows.ToastWindow.ShowToast("❌ Could not open browser. Please visit our website to upgrade.");
            }
        }
    }
}
