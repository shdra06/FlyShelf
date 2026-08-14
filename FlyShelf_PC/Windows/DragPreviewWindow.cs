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
using FlyShelf.Helpers;
using FlyShelf.Classes;

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

        // Win32 desktop invalidation to clear ghost artifacts — P/Invoke centralized in NativeMethods.cs
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_ERASE = 0x0004;

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
                    new ScaleTransform(0.8, 0.8),
                    new TranslateTransform(0, 6)
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
        /// Pop-in animation (180ms) with BackEase overshoot for "picked up" feel.
        /// </summary>
        private void PlayEntranceAnimation()
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(180));
            // [FIX DRAG-ANIM]: BackEase with slight overshoot for "picked up" feel
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.15 };
            var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };

            _rootCard.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 0.95, duration) { EasingFunction = fadeEase });

            var scaleTransform = ((TransformGroup)_rootCard.RenderTransform).Children[0] as ScaleTransform;
            scaleTransform?.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.8, 1.0, duration) { EasingFunction = ease });
            scaleTransform?.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.8, 1.0, duration) { EasingFunction = ease });

            var translateTransform = ((TransformGroup)_rootCard.RenderTransform).Children[1] as TranslateTransform;
            translateTransform?.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, duration) { EasingFunction = ease });

            // Elevate shadow during drag for depth effect
            if (_rootCard.Effect is DropShadowEffect shadow)
            {
                shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
                    new DoubleAnimation(6, 16, duration) { EasingFunction = fadeEase });
                shadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty,
                    new DoubleAnimation(2, 6, duration) { EasingFunction = fadeEase });
            }
        }

        private bool _isClosed;
        private System.Windows.Threading.DispatcherTimer? _safetyTimer;

        /// <summary>
        /// Safely close the drag preview — plays a micro exit animation (80ms),
        /// then aggressively removes from DWM compositor to prevent ghost artifacts.
        /// </summary>
        public void SafeClose()
        {
            if (_isClosed) return;
            _isClosed = true;

            try
            {
                // Attempt a quick 80ms exit animation before aggressive cleanup
                if (_rootCard.Opacity > 0 && Visibility == Visibility.Visible)
                {
                    var duration = new Duration(TimeSpan.FromMilliseconds(100));
                    var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

                    var fadeOut = new DoubleAnimation(0, duration) { EasingFunction = ease };
                    var tg = _rootCard.RenderTransform as TransformGroup;
                    var scaleTransform = tg?.Children[0] as ScaleTransform;
                    var translateTransform = tg?.Children[1] as TranslateTransform;

                    if (scaleTransform != null)
                    {
                        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
                            new DoubleAnimation(0.92, duration) { EasingFunction = ease });
                        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
                            new DoubleAnimation(0.92, duration) { EasingFunction = ease });
                    }

                    // [FIX DRAG-ANIM]: Slide down slightly on exit for "dropped" feel
                    translateTransform?.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(5, duration) { EasingFunction = ease });

                    // Reduce shadow on exit
                    if (_rootCard.Effect is DropShadowEffect shadow)
                    {
                        shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
                            new DoubleAnimation(3, duration) { EasingFunction = ease });
                    }

                    fadeOut.Completed += (_, _) => PerformAggressiveCleanup();
                    _rootCard.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                    return;
                }
            }
            catch { /* Fall through to instant cleanup */ }

            PerformAggressiveCleanup();
        }

        /// <summary>
        /// Performs the aggressive DWM cleanup — called after exit animation completes
        /// or immediately if animation is skipped.
        /// </summary>
        private void PerformAggressiveCleanup()
        {
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
                    NativeMethods.ShowWindow(hwnd, SW_HIDE);

                // 6. Force desktop repaint
                NativeMethods.InvalidateRect(IntPtr.Zero, IntPtr.Zero, true);
                NativeMethods.RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
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
            // Stop any previously running safety timer to prevent leaks
            if (_safetyTimer != null)
            {
                _safetyTimer.Stop();
                _safetyTimer = null;
            }

            _safetyTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            _safetyTimer.Tick += (s, e) =>
            {
                _safetyTimer?.Stop();
                _safetyTimer = null;
                if (!_isClosed) SafeClose();
            };
            _safetyTimer.Start();
        }

        /// <summary>
        /// Override OnClosed to ensure all resources are cleaned up.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            if (_safetyTimer != null)
            {
                _safetyTimer.Stop();
                _safetyTimer = null;
            }
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
                CornerRadius = new CornerRadius(12), // [FIX DRAG-ANIM]: Rounder corners for modern look
                Background = Helpers.BrushHelper.Frozen(Color.FromArgb(235, 20, 20, 28)), // Slightly more transparent
                BorderBrush = Helpers.BrushHelper.Frozen(Color.FromArgb(35, 255, 255, 255)),
                BorderThickness = new Thickness(0.8),
                ClipToBounds = true,
                Child = cardContent,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 6,
                    ShadowDepth = 2,
                    Opacity = 0.55, // Slightly deeper shadow
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
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                grid.Children.Add(img);
            }
            else
            {
                grid.Children.Add(new Border
                {
                    Width = 120, Height = 90,
                    Background = Helpers.BrushHelper.Frozen(Color.FromArgb(40, 255, 255, 255)),
                    Child = MakeVectorIcon("Image", Color.FromRgb(52, 211, 153))
                });
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
                    Foreground = Helpers.BrushHelper.Frozen(ThemeColors.LightSlate),
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
            // Type accent color for left bar
            var accentColor = item.ItemType switch
            {
                ClipboardItemType.Pdf => Color.FromRgb(239, 68, 68),
                ClipboardItemType.Document => Color.FromRgb(59, 130, 246),
                ClipboardItemType.Presentation => Color.FromRgb(245, 158, 11),
                ClipboardItemType.Video => Color.FromRgb(168, 85, 247),
                ClipboardItemType.Audio => Color.FromRgb(236, 72, 153),
                ClipboardItemType.Archive => Color.FromRgb(245, 158, 11),
                ClipboardItemType.Code => Color.FromRgb(16, 185, 129),
                ClipboardItemType.Url => Color.FromRgb(59, 130, 246),
                ClipboardItemType.Folder => Color.FromRgb(245, 158, 11),
                _ => Color.FromRgb(100, 116, 139)
            };

            var outerGrid = new Grid();
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });  // Accent bar
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Content

            // Left accent bar
            var accentBar = new Border
            {
                Background = Helpers.BrushHelper.Frozen(Color.FromArgb(180, accentColor.R, accentColor.G, accentColor.B)),
                CornerRadius = new CornerRadius(2, 0, 0, 2),
                Width = 3
            };
            Grid.SetColumn(accentBar, 0);
            outerGrid.Children.Add(accentBar);

            // Content panel
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(6, 6, 10, 6)
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
                Foreground = Helpers.BrushHelper.Frozen(ThemeColors.LightSlate),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 140
            };
            textStack.Children.Add(nameBlock);

            // Secondary label: type dot size — with accent color for the type name
            var typeInfo = GetTypeInfo(item, selectedCount);
            if (!string.IsNullOrEmpty(typeInfo))
            {
                var typePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
                
                // Type name in accent color
                var typeName = item.ItemType switch
                {
                    ClipboardItemType.Pdf => "PDF",
                    ClipboardItemType.Document => "Doc",
                    ClipboardItemType.Code => "Code",
                    ClipboardItemType.Url => "Link",
                    ClipboardItemType.Archive => "Archive",
                    ClipboardItemType.Video => "Video",
                    ClipboardItemType.Audio => "Audio",
                    ClipboardItemType.Folder => "Folder",
                    ClipboardItemType.Text => "Text",
                    ClipboardItemType.Presentation => "Slides",
                    _ => "File"
                };

                typePanel.Children.Add(new TextBlock
                {
                    Text = typeName,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Helpers.BrushHelper.Frozen(accentColor)
                });

                // Size/info after dot separator
                var sizeInfo = !string.IsNullOrEmpty(item.FormattedSize) ? item.FormattedSize
                    : !string.IsNullOrEmpty(item.RawContent) ? $"{item.RawContent.Length:N0} chars"
                    : null;

                if (sizeInfo != null)
                {
                    typePanel.Children.Add(new TextBlock
                    {
                        Text = $" · {sizeInfo}",
                        FontSize = 9,
                        Foreground = Helpers.BrushHelper.Frozen(ThemeColors.SlateGray)
                    });
                }

                if (selectedCount > 1)
                {
                    typePanel.Children.Add(new TextBlock
                    {
                        Text = $" · {selectedCount} items",
                        FontSize = 9,
                        Foreground = Helpers.BrushHelper.Frozen(ThemeColors.SlateGray)
                    });
                }

                textStack.Children.Add(typePanel);
            }

            panel.Children.Add(textStack);
            Grid.SetColumn(panel, 1);
            outerGrid.Children.Add(panel);

            // Wrap in grid for count badge
            if (selectedCount > 1)
            {
                var wrapper = new Grid();
                wrapper.Children.Add(outerGrid);
                wrapper.Children.Add(BuildCountBadge(selectedCount));
                return wrapper;
            }

            return outerGrid;
        }

        /// <summary>
        /// Builds the icon element based on item type.
        /// </summary>
        private UIElement BuildIcon(ClipboardItem item)
        {
            // Type-specific colors
            var (bgColor, accentColor, iconType) = item.ItemType switch
            {
                ClipboardItemType.Pdf => (Color.FromArgb(25, 239, 68, 68), Color.FromRgb(248, 113, 113), "Pdf"),
                ClipboardItemType.Document => (Color.FromArgb(25, 59, 130, 246), Color.FromRgb(96, 165, 250), "Doc"),
                ClipboardItemType.Presentation => (Color.FromArgb(25, 245, 158, 11), Color.FromRgb(251, 191, 36), "Ppt"),
                ClipboardItemType.Video => (Color.FromArgb(25, 168, 85, 247), Color.FromRgb(192, 132, 252), "Video"),
                ClipboardItemType.Audio => (Color.FromArgb(25, 236, 72, 153), Color.FromRgb(244, 114, 182), "Audio"),
                ClipboardItemType.Archive => (Color.FromArgb(25, 245, 158, 11), Color.FromRgb(251, 191, 36), "Archive"),
                ClipboardItemType.Code => (Color.FromArgb(25, 16, 185, 129), Color.FromRgb(52, 211, 153), "Code"),
                ClipboardItemType.Url => (Color.FromArgb(25, 59, 130, 246), Color.FromRgb(96, 165, 250), "Link"),
                ClipboardItemType.Folder => (Color.FromArgb(25, 245, 158, 11), Color.FromRgb(251, 191, 36), "Folder"),
                ClipboardItemType.Image or ClipboardItemType.QRCode => (Color.FromArgb(25, 16, 185, 129), Color.FromRgb(52, 211, 153), "Image"),
                ClipboardItemType.Text => (Color.FromArgb(20, 148, 163, 184), Color.FromRgb(148, 163, 184), "Text"),
                _ => (Color.FromArgb(20, 148, 163, 184), Color.FromRgb(148, 163, 184), "Text")
            };

            var iconBorder = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = Helpers.BrushHelper.Frozen(bgColor),
                ClipToBounds = true
            };

            // Use actual item thumbnail if available (images, PDFs with previews)
            if (item.ItemType is ClipboardItemType.Image or ClipboardItemType.QRCode && item.Icon != null)
            {
                var img = new Image
                {
                    Source = item.Icon,
                    Width = 36,
                    Height = 36,
                    Stretch = Stretch.UniformToFill
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                iconBorder.Child = img;
            }
            else if (item.Icon != null && item.ItemType is not ClipboardItemType.Text and not ClipboardItemType.Code)
            {
                // File types with system icons
                iconBorder.Child = MakeSmallIcon(item.Icon);
            }
            else
            {
                // Vector icon fallback
                iconBorder.Child = MakeVectorIcon(iconType, accentColor);
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

            // [FIX DD-3]: Use cached FormattedSize instead of FileInfo I/O on UI thread
            if (!string.IsNullOrEmpty(item.FormattedSize))
            {
                parts.Add(item.FormattedSize);
            }
            else if (!string.IsNullOrEmpty(item.FilePath))
            {
                // Fallback: file size not yet computed
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
        /// Creates a proper vector icon for the icon border, using WPF Path geometries.
        /// </summary>
        private static UIElement MakeVectorIcon(string iconType, Color accentColor)
        {
            var path = new System.Windows.Shapes.Path
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Helpers.BrushHelper.Frozen(accentColor)
            };

            path.Data = iconType switch
            {
                "Text" => Geometry.Parse("M3 5.5A2.5 2.5 0 015.5 3h9A2.5 2.5 0 0117 5.5v9a2.5 2.5 0 01-2.5 2.5h-9A2.5 2.5 0 013 14.5v-9zM6 7.25a.75.75 0 01.75-.75h6.5a.75.75 0 010 1.5h-6.5A.75.75 0 016 7.25zm0 3a.75.75 0 01.75-.75h6.5a.75.75 0 010 1.5h-6.5A.75.75 0 016 10.25zm0 3a.75.75 0 01.75-.75h3.5a.75.75 0 010 1.5h-3.5a.75.75 0 01-.75-.75z"),
                "Pdf" => Geometry.Parse("M4 4a2 2 0 012-2h5.586a1 1 0 01.707.293l4.414 4.414a1 1 0 01.293.707V18a2 2 0 01-2 2H6a2 2 0 01-2-2V4zm4.5 6a.5.5 0 00-.5.5v4a.5.5 0 001 0v-1.5h1a1.5 1.5 0 000-3H8.5zm1 2H9v-1h.5a.5.5 0 010 1z"),
                "Code" => Geometry.Parse("M8.066 4.266a.75.75 0 00-1.132-.984l-4.5 5.25a.75.75 0 000 .984l4.5 5.25a.75.75 0 101.132-.984L4.148 10l3.918-4.734zm3.868-.984a.75.75 0 10-1.132.984L14.852 10l-4.05 4.734a.75.75 0 001.132.984l4.5-5.25a.75.75 0 000-.984l-4.5-5.202z"),
                "Doc" => Geometry.Parse("M4 4a2 2 0 012-2h5.586a1 1 0 01.707.293l4.414 4.414a1 1 0 01.293.707V18a2 2 0 01-2 2H6a2 2 0 01-2-2V4zm3 4.25a.75.75 0 01.75-.75h4.5a.75.75 0 010 1.5h-4.5A.75.75 0 017 8.25zm0 3a.75.75 0 01.75-.75h4.5a.75.75 0 010 1.5h-4.5a.75.75 0 01-.75-.75zm0 3a.75.75 0 01.75-.75h2.5a.75.75 0 010 1.5h-2.5a.75.75 0 01-.75-.75z"),
                "Link" => Geometry.Parse("M7.775 3.275a3.5 3.5 0 014.95 0l.5.5a3.5 3.5 0 01.39 4.547.75.75 0 11-1.233-.852 2 2 0 00-.222-2.63l-.5-.5a2 2 0 00-2.83 0l-2 2a2 2 0 000 2.83l.25.25a.75.75 0 11-1.06 1.06l-.25-.25a3.5 3.5 0 010-4.95l2-2zm6.417 6.36a.75.75 0 01.058 1.06l-.25.25a3.5 3.5 0 01-4.95 0l-.5-.5a3.5 3.5 0 01-.39-4.547.75.75 0 011.233.852 2 2 0 00.222 2.63l.5.5a2 2 0 002.83 0l2-2a.75.75 0 011.06-.058z"),
                "Folder" => Geometry.Parse("M2 6a2 2 0 012-2h3.172a2 2 0 011.414.586l.828.828A2 2 0 0010.828 6H16a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z"),
                "Video" => Geometry.Parse("M2 6a2 2 0 012-2h8a2 2 0 012 2v2l3.293-3.293A1 1 0 0118 5.414v9.172a1 1 0 01-1.707.707L14 12v2a2 2 0 01-2 2H4a2 2 0 01-2-2V6z"),
                "Audio" => Geometry.Parse("M10 3.75a.75.75 0 00-1.264-.546L5.203 6H3.75A1.75 1.75 0 002 7.75v4.5c0 .966.784 1.75 1.75 1.75h1.453l3.533 2.796A.75.75 0 0010 16.25v-12.5zM15.22 5.22a.75.75 0 011.06 0c2.96 2.96 2.96 7.76 0 10.72a.75.75 0 11-1.06-1.06 6 6 0 000-8.49.75.75 0 010-1.06zm-2.12 2.12a.75.75 0 011.06 0 4 4 0 010 5.66.75.75 0 01-1.06-1.06 2.5 2.5 0 000-3.54.75.75 0 010-1.06z"),
                "Archive" => Geometry.Parse("M3 5a2 2 0 012-2h10a2 2 0 012 2v2a2 2 0 01-2 2H5a2 2 0 01-2-2V5zm4.5 1a.5.5 0 01.5-.5h4a.5.5 0 010 1H8a.5.5 0 01-.5-.5zM5 11a2 2 0 00-2 2v2a2 2 0 002 2h10a2 2 0 002-2v-2a2 2 0 00-2-2H5zm3 2.5a.5.5 0 01.5-.5h3a.5.5 0 010 1h-3a.5.5 0 01-.5-.5z"),
                "Ppt" => Geometry.Parse("M4 4a2 2 0 012-2h5.586a1 1 0 01.707.293l4.414 4.414a1 1 0 01.293.707V18a2 2 0 01-2 2H6a2 2 0 01-2-2V4zm3 5.5a.5.5 0 01.5-.5h5a.5.5 0 01.5.5v4a.5.5 0 01-.5.5h-5a.5.5 0 01-.5-.5v-4zm2 5.5v1h2v-1H9z"),
                _ => Geometry.Parse("M9 2a1 1 0 00-.894.553L6.382 6H3a1 1 0 000 2h.341l.949 8.525A2 2 0 006.278 18h7.444a2 2 0 001.988-1.475L16.659 8H17a1 1 0 000-2h-3.382l-1.724-3.447A1 1 0 0011 2H9z")
            };

            return path;
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
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
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
                Background = Helpers.BrushHelper.Frozen(Color.FromRgb(59, 130, 246)), // Blue
                BorderBrush = Helpers.BrushHelper.Frozen(Color.FromArgb(180, 20, 20, 28)),
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

        // ═══════════════════════════════════════════════════════════════
        // Ctrl+Drag Path Mode Visual Indicator
        // ═══════════════════════════════════════════════════════════════

        private Border _pathModeBadge;
        private static readonly Brush _pathModeBorderBrush = Helpers.BrushHelper.Frozen(Color.FromArgb(160, 137, 180, 250));
        private static readonly Brush _defaultBorderBrush = Helpers.BrushHelper.Frozen(Color.FromArgb(35, 255, 255, 255));

        /// <summary>
        /// Toggles path mode visual indicator on the drag preview.
        /// When path mode is active, shows a "📋 Path" badge on the card
        /// and tints the border to indicate the drag payload is the file path.
        /// Uses opacity animation instead of Add/Remove to avoid layout invalidation.
        /// </summary>
        public void SetPathMode(bool isPathMode)
        {
            if (_isClosed) return;

            try
            {
                var animDuration = new Duration(TimeSpan.FromMilliseconds(100));
                var animEase = new CubicEase { EasingMode = EasingMode.EaseOut };

                if (isPathMode)
                {
                    // Tint border to indicate path mode
                    _rootCard.BorderBrush = _pathModeBorderBrush;
                    _rootCard.BorderThickness = new Thickness(1.5);

                    // Pre-create path mode badge on first use, hidden with Opacity = 0
                    if (_pathModeBadge == null && _rootCard.Child is Panel panel)
                    {
                        _pathModeBadge = new Border
                        {
                            Background = Helpers.BrushHelper.Frozen(Color.FromArgb(220, 137, 180, 250)),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(5, 2, 5, 2),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Margin = new Thickness(4, 0, 0, 4),
                            Opacity = 0,
                            Child = new TextBlock
                            {
                                Text = "Path",
                                FontSize = 9,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = Helpers.BrushHelper.Frozen(Color.FromRgb(20, 20, 28))
                            }
                        };
                        panel.Children.Add(_pathModeBadge);
                    }

                    // Animate badge opacity in
                    _pathModeBadge?.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(1, animDuration) { EasingFunction = animEase });
                }
                else
                {
                    // Revert border
                    _rootCard.BorderBrush = _defaultBorderBrush;
                    _rootCard.BorderThickness = new Thickness(0.8);

                    // Animate badge opacity out
                    _pathModeBadge?.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0, animDuration) { EasingFunction = animEase });
                }
            }
            catch { } // Best-effort: visual feedback is non-critical
        }
    }
}
