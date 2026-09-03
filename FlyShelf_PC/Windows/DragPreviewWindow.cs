// ---------------------------------------------------------------
// DragPreviewWindow — Floating thumbnail card during drag-out
// Shows a refined preview card with thumbnail + filename that
// follows the cursor closely with native 0ms latency, like Windows
// File Explorer with high-DPI scaling and polished visuals.
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
        private const int SW_HIDE = 0;

        // ═══ Card Sizing ═══
        private const double ThumbnailSize = 56;
        private const double CardMaxWidth = 240;
        private const double CardCornerRadius = 10;

        // Cursor offset — just below-right of cursor
        private const int CursorOffsetX = 12;
        private const int CursorOffsetY = 14;

        private readonly Border _rootCard;
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _isClosed;
        private System.Windows.Threading.DispatcherTimer? _safetyTimer;
        private double _dpiX = 1.0;
        private double _dpiY = 1.0;

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
        }

        /// <summary>
        /// WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE —
        /// fully click-through, no taskbar, no focus steal.
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            if (_hwnd != IntPtr.Zero)
            {
                int extStyle = Classes.NativeMethods.GetWindowLong(_hwnd, GWL_EXSTYLE);
                Classes.NativeMethods.SetWindowLong(_hwnd, GWL_EXSTYLE,
                    extStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                _dpiX = source.CompositionTarget.TransformFromDevice.M11;
                _dpiY = source.CompositionTarget.TransformFromDevice.M22;
            }
        }

        /// <summary>
        /// Safely close the drag preview — immediately removes from screen
        /// without delay or lingering animations.
        /// </summary>
        public void SafeClose()
        {
            if (_isClosed) return;
            _isClosed = true;

            if (_safetyTimer != null)
            {
                _safetyTimer.Stop();
                _safetyTimer = null;
            }

            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(_hwnd, SW_HIDE);
                }
                Visibility = Visibility.Collapsed;
                Content = null;
                Close();
            }
            catch { /* Window may already be disposed */ }
        }

        /// <summary>
        /// Start a safety timer — if SafeClose is never called (e.g. drag
        /// thread hangs), the preview self-destructs after 4 seconds.
        /// </summary>
        public void StartSafetyTimer()
        {
            if (_safetyTimer != null)
            {
                _safetyTimer.Stop();
                _safetyTimer = null;
            }

            _safetyTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4)
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
        /// Updates position to track cursor. Uses native Win32 SetWindowPos
        /// directly on the HWND handle for zero-latency, 0-CPU tracking without
        /// triggering WPF layout cycles.
        /// </summary>
        public void UpdatePosition(int screenX, int screenY)
        {
            if (_isClosed) return;

            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = new WindowInteropHelper(this).Handle;
            }

            if (_hwnd != IntPtr.Zero)
            {
                int x = screenX + CursorOffsetX;
                int y = screenY + CursorOffsetY;
                NativeMethods.SetWindowPos(_hwnd, 0, x, y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS);
            }
            else
            {
                Left = screenX * _dpiX + CursorOffsetX;
                Top = screenY * _dpiY + CursorOffsetY;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Card Builder — thumbnail + filename card
        // ═══════════════════════════════════════════════════════════════

        private Border BuildCard(ClipboardItem item, int selectedCount)
        {
            bool isImageType = item.ItemType == ClipboardItemType.Image ||
                               item.ItemType == ClipboardItemType.QRCode;

            UIElement cardContent;

            if (isImageType)
            {
                cardContent = BuildImageThumbnailCard(item, selectedCount);
            }
            else
            {
                cardContent = BuildFileCard(item, selectedCount);
            }

            var card = new Border
            {
                MaxWidth = CardMaxWidth,
                CornerRadius = new CornerRadius(CardCornerRadius),
                Background = Helpers.BrushHelper.Frozen(Color.FromArgb(240, 20, 20, 28)),
                BorderBrush = Helpers.BrushHelper.Frozen(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1.0),
                ClipToBounds = true,
                Child = cardContent,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 3,
                    Opacity = 0.5,
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
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
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
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accentBar = new Border
            {
                Background = Helpers.BrushHelper.Frozen(Color.FromArgb(180, accentColor.R, accentColor.G, accentColor.B)),
                CornerRadius = new CornerRadius(2, 0, 0, 2),
                Width = 3
            };
            Grid.SetColumn(accentBar, 0);
            outerGrid.Children.Add(accentBar);

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 7, 12, 7)
            };

            panel.Children.Add(BuildIcon(item));

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var displayName = GetDisplayName(item);
            textStack.Children.Add(new TextBlock
            {
                Text = displayName,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = Helpers.BrushHelper.Frozen(ThemeColors.LightSlate),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160
            });

            var typeName = item.ItemType switch
            {
                ClipboardItemType.Pdf => "PDF",
                ClipboardItemType.Document => "Document",
                ClipboardItemType.Presentation => "Presentation",
                ClipboardItemType.Video => "Video",
                ClipboardItemType.Audio => "Audio",
                ClipboardItemType.Archive => "Archive",
                ClipboardItemType.Code => !string.IsNullOrEmpty(item.Extension) ? item.Extension : "Code",
                ClipboardItemType.Url => "Link",
                ClipboardItemType.Folder => "Folder",
                ClipboardItemType.Text => "Text",
                _ => !string.IsNullOrEmpty(item.Extension) ? item.Extension : "File"
            };

            var typePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            typePanel.Children.Add(new TextBlock
            {
                Text = typeName,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Helpers.BrushHelper.Frozen(accentColor)
            });

            var sizeInfo = !string.IsNullOrEmpty(item.FormattedSize) ? item.FormattedSize
                : !string.IsNullOrEmpty(item.RawContent) ? $"{item.RawContent.Length:N0} chars"
                : null;

            if (sizeInfo != null)
            {
                typePanel.Children.Add(new TextBlock
                {
                    Text = $" · {sizeInfo}",
                    FontSize = 9.5,
                    Foreground = Helpers.BrushHelper.Frozen(ThemeColors.SlateGray)
                });
            }

            if (selectedCount > 1)
            {
                typePanel.Children.Add(new TextBlock
                {
                    Text = $" · {selectedCount} items",
                    FontSize = 9.5,
                    Foreground = Helpers.BrushHelper.Frozen(ThemeColors.SlateGray)
                });
            }

            textStack.Children.Add(typePanel);
            panel.Children.Add(textStack);
            Grid.SetColumn(panel, 1);
            outerGrid.Children.Add(panel);

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
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(7),
                Background = Helpers.BrushHelper.Frozen(bgColor),
                ClipToBounds = true
            };

            if (item.ItemType is ClipboardItemType.Image or ClipboardItemType.QRCode && item.Icon != null)
            {
                var img = new Image
                {
                    Source = item.Icon,
                    Width = 34,
                    Height = 34,
                    Stretch = Stretch.UniformToFill
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
                iconBorder.Child = img;
            }
            else if (item.Icon != null && item.ItemType is not ClipboardItemType.Text and not ClipboardItemType.Code)
            {
                iconBorder.Child = MakeSmallIcon(item.Icon);
            }
            else
            {
                iconBorder.Child = MakeVectorIcon(iconType, accentColor);
            }

            return iconBorder;
        }

        private static string GetDisplayName(ClipboardItem item)
        {
            if (!string.IsNullOrEmpty(item.FilePath))
                return System.IO.Path.GetFileName(item.FilePath);

            if (!string.IsNullOrEmpty(item.RawContent))
            {
                var preview = item.RawContent.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
                return preview.Length > 36 ? string.Concat(preview.AsSpan(0, 36), "…") : preview;
            }

            return item.ItemType.ToString("G");
        }

        private static UIElement MakeVectorIcon(string iconType, Color accentColor)
        {
            var path = new System.Windows.Shapes.Path
            {
                Width = 18,
                Height = 18,
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

        private static UIElement MakeSmallIcon(BitmapSource icon)
        {
            var img = new Image
            {
                Source = icon,
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
            return img;
        }

        private static UIElement BuildCountBadge(int count)
        {
            var badge = new Border
            {
                MinWidth = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Helpers.BrushHelper.Frozen(Color.FromRgb(59, 130, 246)),
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

        private Border? _pathModeBadge;
        private Border? _pathOverlay;
        private static readonly Brush _pathModeBorderBrush = Helpers.BrushHelper.Frozen(Color.FromArgb(200, 137, 180, 250));
        private static readonly Brush _defaultBorderBrush = Helpers.BrushHelper.Frozen(Color.FromArgb(50, 255, 255, 255));

        /// <summary>
        /// Toggles the Ctrl+Drag path mode visual.
        /// Shows a full-card overlay with the file path text, with a smooth transition.
        /// </summary>
        public void SetPathMode(bool isPathMode, string? filePath = null)
        {
            if (_isClosed) return;

            try
            {
                if (isPathMode)
                {
                    _rootCard.BorderBrush = _pathModeBorderBrush;
                    _rootCard.BorderThickness = new Thickness(1.5);

                    // ── Create full path overlay on first activation ──
                    if (_pathOverlay == null && _rootCard.Child is Panel panel)
                    {
                        // Determine display path
                        string displayPath = filePath ?? "";
                        if (string.IsNullOrEmpty(displayPath))
                            displayPath = "📋 Path Mode";

                        // Path overlay — covers the entire card content
                        _pathOverlay = new Border
                        {
                            Background = Helpers.BrushHelper.Frozen(Color.FromArgb(245, 20, 22, 35)),
                            CornerRadius = new CornerRadius(CardCornerRadius - 1),
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment = VerticalAlignment.Stretch,
                            Padding = new Thickness(10, 8, 10, 8),
                            Opacity = 0,
                        };

                        var overlayContent = new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                        };

                        // "📋 Path" header
                        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                        headerRow.Children.Add(new TextBlock
                        {
                            Text = "📋",
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 5, 0),
                        });
                        headerRow.Children.Add(new TextBlock
                        {
                            Text = "Drop as Path",
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Helpers.BrushHelper.Frozen(Color.FromArgb(230, 137, 180, 250)),
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                        overlayContent.Children.Add(headerRow);

                        // File path text
                        overlayContent.Children.Add(new TextBlock
                        {
                            Text = displayPath,
                            FontSize = 10,
                            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                            Foreground = Helpers.BrushHelper.Frozen(Color.FromArgb(220, 220, 230, 255)),
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = CardMaxWidth - 24,
                            Opacity = 0.9,
                        });

                        _pathOverlay.Child = overlayContent;
                        panel.Children.Add(_pathOverlay);
                    }

                    if (_pathOverlay != null)
                    {
                        _pathOverlay.Visibility = Visibility.Visible;

                        // Smooth fade-in
                        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
                        {
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                        };
                        _pathOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    }

                    // Also keep the badge for redundancy
                    if (_pathModeBadge == null && _rootCard.Child is Panel badgePanel)
                    {
                        _pathModeBadge = new Border
                        {
                            Background = Helpers.BrushHelper.Frozen(Color.FromArgb(220, 137, 180, 250)),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(5, 2, 5, 2),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Margin = new Thickness(4, 0, 0, 4),
                            Child = new TextBlock
                            {
                                Text = "📋 Path",
                                FontSize = 9,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = Helpers.BrushHelper.Frozen(Color.FromRgb(20, 20, 28))
                            }
                        };
                        badgePanel.Children.Add(_pathModeBadge);
                    }
                    if (_pathModeBadge != null) _pathModeBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    _rootCard.BorderBrush = _defaultBorderBrush;
                    _rootCard.BorderThickness = new Thickness(1.0);

                    // Smooth fade-out for overlay
                    if (_pathOverlay != null)
                    {
                        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100))
                        {
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                        };
                        fadeOut.Completed += (s, e) =>
                        {
                            if (_pathOverlay != null)
                                _pathOverlay.Visibility = Visibility.Collapsed;
                        };
                        _pathOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                    }

                    if (_pathModeBadge != null) _pathModeBadge.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }
    }
}
