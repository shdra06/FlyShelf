// ---------------------------------------------------------------
// DragPreviewWindow — Floating thumbnail card during drag-out
// Shows a refined preview card with thumbnail + filename that
// follows the cursor closely, like Windows File Explorer but with
// a premium polished look including rounded corners and shadows.
// Uses a borderless, click-through, topmost WPF Window.
// ---------------------------------------------------------------
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FlyShelf.ViewModels;

namespace FlyShelf.Windows
{
    /// <summary>
    /// A lightweight borderless topmost window that displays a compact
    /// thumbnail preview of the dragged item(s). Fully click-through
    /// via WS_EX_TRANSPARENT so it never interferes with drop targets.
    /// </summary>
    public sealed class DragPreviewWindow : Window
    {
        // ═══ Win32 Constants ═══
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        // Win32 desktop invalidation to clear ghost artifacts
        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_ERASE = 0x0004;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_HIDE = 0;

        // ═══ Card Sizing ═══
        private const double ThumbnailSize = 56;      // Thumbnail square size
        private const double CardMaxWidth = 220;       // Max card width
        private const double CardCornerRadius = 10;

        // Cursor offset — right at cursor tip like Explorer
        // Negative values compensate for DropShadowEffect padding around the card
        private const double CursorOffsetX = -2;
        private const double CursorOffsetY = 2;

        private readonly Border _rootCard;

        /// <summary>
        /// Creates the drag preview for one or more clipboard items.
        /// </summary>
        public DragPreviewWindow(ClipboardItem primaryItem, int selectedCount)
        {
            // ═══ Window Configuration ═══
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;

            // Build the card UI
            _rootCard = BuildCard(primaryItem, selectedCount);
            Content = _rootCard;

            // Start invisible for entrance animation
            _rootCard.Opacity = 0;
            _rootCard.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(0.85, 0.85),
                    new TranslateTransform(0, 3)
                }
            };
            _rootCard.RenderTransformOrigin = new Point(0, 0);
        }

        /// <summary>
        /// WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE —
        /// fully click-through, no taskbar, no focus steal.
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int extStyle = Classes.NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
            Classes.NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE,
                extStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            PlayEntranceAnimation();
        }

        /// <summary>
        /// Quick pop-in animation (120ms).
        /// </summary>
        private void PlayEntranceAnimation()
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(120));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            _rootCard.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 0.95, duration) { EasingFunction = ease });

            var scaleTransform = ((TransformGroup)_rootCard.RenderTransform).Children[0] as ScaleTransform;
            scaleTransform?.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.85, 1.0, duration) { EasingFunction = ease });
            scaleTransform?.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.85, 1.0, duration) { EasingFunction = ease });

            var translateTransform = ((TransformGroup)_rootCard.RenderTransform).Children[1] as TranslateTransform;
            translateTransform?.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(3, 0, duration) { EasingFunction = ease });
        }

        private bool _isClosed;

        /// <summary>
        /// Safely close the drag preview — aggressively removes from DWM compositor
        /// to prevent ghost artifacts on screen.
        /// </summary>
        public void SafeClose()
        {
            if (_isClosed) return;
            _isClosed = true;

            try
            {
                // 1. Stop ALL running animations immediately — frozen frames cause ghosts
                _rootCard.BeginAnimation(UIElement.OpacityProperty, null);
                var tg = _rootCard.RenderTransform as TransformGroup;
                if (tg != null)
                {
                    (tg.Children[0] as ScaleTransform)?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    (tg.Children[0] as ScaleTransform)?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    (tg.Children[1] as TranslateTransform)?.BeginAnimation(TranslateTransform.YProperty, null);
                }

                // 2. Make fully invisible
                _rootCard.Opacity = 0;
                Opacity = 0;

                // 3. Move completely off-screen so DWM drops the composited frame
                Left = -10000;
                Top = -10000;
                Width = 0;
                Height = 0;

                // 4. Clear the visual tree
                Content = null;
                Visibility = Visibility.Collapsed;

                // 5. Hide the Win32 window immediately
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    ShowWindow(hwnd, SW_HIDE);

                // 6. Force desktop repaint
                InvalidateRect(IntPtr.Zero, IntPtr.Zero, true);
                RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_ERASE);

                // 7. Close on next dispatcher frame (allows compositor to flush)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { Close(); } catch { } // Best-effort: failure is acceptable
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { /* Window may already be disposed */ }
        }

        /// <summary>
        /// Start a safety timer — if SafeClose is never called (e.g. drag
        /// thread hangs), the preview self-destructs after 8 seconds.
        /// </summary>
        public void StartSafetyTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (!_isClosed) SafeClose();
            };
            timer.Start();
        }

        /// <summary>
        /// Override OnClosed to ensure all resources are cleaned up.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            Content = null;
            base.OnClosed(e);
        }

        /// <summary>
        /// Updates position to track cursor. DPI-aware.
        /// Positioned just below-right of cursor like File Explorer.
        /// </summary>
        public void UpdatePosition(int screenX, int screenY)
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var dpiX = source.CompositionTarget.TransformFromDevice.M11;
                var dpiY = source.CompositionTarget.TransformFromDevice.M22;
                Left = screenX * dpiX + CursorOffsetX;
                Top = screenY * dpiY + CursorOffsetY;
            }
            else
            {
                Left = screenX + CursorOffsetX;
                Top = screenY + CursorOffsetY;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Card Builder — thumbnail + filename card
        // ═══════════════════════════════════════════════════════════════

        private Border BuildCard(ClipboardItem item, int selectedCount)
        {
            bool isImageType = item.ItemType == ClipboardItemType.Image ||
                               item.ItemType == ClipboardItemType.QRCode;
            bool hasFile = !string.IsNullOrEmpty(item.FilePath);
            bool hasText = !string.IsNullOrEmpty(item.RawContent);

            UIElement cardContent;

            if (isImageType)
            {
                // ─── Image: full thumbnail card ───
                cardContent = BuildImageThumbnailCard(item, selectedCount);
            }
            else
            {
                // ─── File/Text: icon + name horizontal layout ───
                cardContent = BuildFileCard(item, selectedCount);
            }

            // Outer card — rounded with shadow
            var card = new Border
            {
                MaxWidth = CardMaxWidth,
                CornerRadius = new CornerRadius(CardCornerRadius),
                Background = new SolidColorBrush(Color.FromArgb(245, 22, 22, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(0.5),
                ClipToBounds = true,
                Child = cardContent,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 6,
                    ShadowDepth = 2,
                    Opacity = 0.45,
                    Color = Colors.Black,
                    Direction = 270
                }
            };

            return card;
        }

        /// <summary>
        /// Image thumbnail card — fills the card with the image, with filename overlay at bottom.
        /// </summary>
        private UIElement BuildImageThumbnailCard(ClipboardItem item, int selectedCount)
        {
            var grid = new Grid();

            // Image thumbnail (fill the card)
            if (item.Icon != null)
            {
                var img = new Image
                {
                    Source = item.Icon,
                    Width = 120,
                    Height = 90,
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
                grid.Children.Add(img);
            }
            else
            {
                grid.Children.Add(MakeEmojiIcon("🖼️", 120, 90));
            }

            // Filename overlay at bottom (dark gradient bar)
            var fileName = GetDisplayName(item);
            if (!string.IsNullOrEmpty(fileName))
            {
                var overlayBorder = new Border
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = new LinearGradientBrush(
                        Color.FromArgb(0, 0, 0, 0),
                        Color.FromArgb(200, 0, 0, 0),
                        90),
                    Padding = new Thickness(8, 12, 8, 6)
                };
                overlayBorder.Child = new TextBlock
                {
                    Text = fileName,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 110
                };
                grid.Children.Add(overlayBorder);
            }

            // Multi-select badge
            if (selectedCount > 1)
                grid.Children.Add(BuildCountBadge(selectedCount));

            return grid;
        }

        /// <summary>
        /// File/text card — icon left + filename/preview right.
        /// </summary>
        private UIElement BuildFileCard(ClipboardItem item, int selectedCount)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 6, 10, 6)
            };

            // Icon (left)
            var iconElement = BuildIcon(item);
            panel.Children.Add(iconElement);

            // Text area (right)
            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                MaxWidth = 140
            };

            // Primary label: filename or content preview
            var displayName = GetDisplayName(item);
            var nameBlock = new TextBlock
            {
                Text = displayName,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 140
            };
            textStack.Children.Add(nameBlock);

            // Secondary label: type or size info
            var typeInfo = GetTypeInfo(item, selectedCount);
            if (!string.IsNullOrEmpty(typeInfo))
            {
                var typeBlock = new TextBlock
                {
                    Text = typeInfo,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    Margin = new Thickness(0, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                textStack.Children.Add(typeBlock);
            }

            panel.Children.Add(textStack);

            // Wrap in grid for count badge
            if (selectedCount > 1)
            {
                var wrapper = new Grid();
                wrapper.Children.Add(panel);
                wrapper.Children.Add(BuildCountBadge(selectedCount));
                return wrapper;
            }

            return panel;
        }

        /// <summary>
        /// Builds the icon element based on item type.
        /// </summary>
        private UIElement BuildIcon(ClipboardItem item)
        {
            // Container for the icon
            var iconBorder = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                ClipToBounds = true
            };

            switch (item.ItemType)
            {
                case ClipboardItemType.Image:
                case ClipboardItemType.QRCode:
                    if (item.Icon != null)
                    {
                        var img = new Image
                        {
                            Source = item.Icon,
                            Width = 36,
                            Height = 36,
                            Stretch = Stretch.UniformToFill
                        };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
                        iconBorder.Child = img;
                    }
                    else
                    {
                        iconBorder.Child = MakeCenteredEmoji("🖼️");
                    }
                    break;

                case ClipboardItemType.Pdf:
                    iconBorder.Child = item.Icon != null ? MakeSmallIcon(item.Icon) : MakeCenteredEmoji("📕");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 239, 68, 68));
                    break;

                case ClipboardItemType.Document:
                    iconBorder.Child = item.Icon != null ? MakeSmallIcon(item.Icon) : MakeCenteredEmoji("📄");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 59, 130, 246));
                    break;

                case ClipboardItemType.Presentation:
                    iconBorder.Child = item.Icon != null ? MakeSmallIcon(item.Icon) : MakeCenteredEmoji("📊");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 245, 158, 11));
                    break;

                case ClipboardItemType.Video:
                    iconBorder.Child = item.Icon != null ? MakeSmallIcon(item.Icon) : MakeCenteredEmoji("🎬");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 168, 85, 247));
                    break;

                case ClipboardItemType.Audio:
                    iconBorder.Child = item.Icon != null ? MakeSmallIcon(item.Icon) : MakeCenteredEmoji("🎵");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 236, 72, 153));
                    break;

                case ClipboardItemType.Archive:
                    iconBorder.Child = item.Icon != null ? MakeSmallIcon(item.Icon) : MakeCenteredEmoji("📦");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 245, 158, 11));
                    break;

                case ClipboardItemType.Code:
                    iconBorder.Child = item.Icon != null ? MakeSmallIcon(item.Icon) : MakeCenteredEmoji("💻");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 16, 185, 129));
                    break;

                case ClipboardItemType.Url:
                    iconBorder.Child = MakeCenteredEmoji("🔗");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 59, 130, 246));
                    break;

                case ClipboardItemType.Folder:
                    iconBorder.Child = MakeCenteredEmoji("📁");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 245, 158, 11));
                    break;

                case ClipboardItemType.Text:
                default:
                    iconBorder.Child = MakeCenteredEmoji("📋");
                    iconBorder.Background = new SolidColorBrush(Color.FromArgb(30, 148, 163, 184));
                    break;
            }

            return iconBorder;
        }

        // ═══════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets a human-readable display name for the item.
        /// </summary>
        private static string GetDisplayName(ClipboardItem item)
        {
            if (!string.IsNullOrEmpty(item.FilePath))
                return System.IO.Path.GetFileName(item.FilePath);

            if (!string.IsNullOrEmpty(item.RawContent))
            {
                var preview = item.RawContent.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
                return preview.Length > 40 ? string.Concat(preview.AsSpan(0, 40), "…") : preview;
            }

            return item.ItemType.ToString("G");
        }

        /// <summary>
        /// Gets secondary info text (type + size or count).
        /// </summary>
        private static string GetTypeInfo(ClipboardItem item, int selectedCount)
        {
            var parts = new System.Collections.Generic.List<string>();

            // Type name
            parts.Add(item.ItemType switch
            {
                ClipboardItemType.Image => "Image",
                ClipboardItemType.Pdf => "PDF",
                ClipboardItemType.Document => "Document",
                ClipboardItemType.Code => "Code",
                ClipboardItemType.Url => "Link",
                ClipboardItemType.Archive => "Archive",
                ClipboardItemType.Video => "Video",
                ClipboardItemType.Audio => "Audio",
                ClipboardItemType.Folder => "Folder",
                ClipboardItemType.Text => "Text",
                _ => "File"
            });

            // File size
            if (!string.IsNullOrEmpty(item.FilePath))
            {
                try
                {
                    var fi = new FileInfo(item.FilePath);
                    if (fi.Exists)
                    {
                        parts.Add(fi.Length switch
                        {
                            < 1024 => $"{fi.Length} B",
                            < 1024 * 1024 => $"{fi.Length / 1024.0:F0} KB",
                            _ => $"{fi.Length / (1024.0 * 1024.0):F1} MB"
                        });
                    }
                }
                catch { /* ignore */ }
            }
            else if (!string.IsNullOrEmpty(item.RawContent))
            {
                parts.Add($"{item.RawContent.Length} chars");
            }

            // Multi-select
            if (selectedCount > 1)
                parts.Add($"{selectedCount} items");

            return string.Join(" · ", parts);
        }

        /// <summary>
        /// Full-card emoji placeholder (for image thumbnails when icon is null).
        /// </summary>
        private static UIElement MakeEmojiIcon(string emoji, double width, double height)
        {
            return new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Child = new TextBlock
                {
                    Text = emoji,
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        /// <summary>
        /// Small centered emoji for the icon border.
        /// </summary>
        private static UIElement MakeCenteredEmoji(string emoji)
        {
            return new TextBlock
            {
                Text = emoji,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
        }

        /// <summary>
        /// Small system icon (24x24) centered in the icon border.
        /// </summary>
        private static UIElement MakeSmallIcon(BitmapSource icon)
        {
            var img = new Image
            {
                Source = icon,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);
            return img;
        }

        /// <summary>
        /// Small circular count badge for multi-select (top-right).
        /// </summary>
        private static UIElement BuildCountBadge(int count)
        {
            var badge = new Border
            {
                MinWidth = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 20, 20, 28)),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -2, 0),
                Child = new TextBlock
                {
                    Text = count.ToString(CultureInfo.InvariantCulture),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            return badge;
        }
    }
}
