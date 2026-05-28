using FlyShelf.ViewModels;
using MicaWPF.Controls;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FlyShelf.Windows
{
    public partial class MiniShelfWindow : MicaWindow
    {
        private readonly FlyShelfViewModel _viewModel;
        private bool _isCurrentlySummoned = false;
        private bool _isAnimatingHide = false;
        private bool _isShowAnimating = false;
        private double _lastActualHeight = 0;
        private IntPtr _previousForegroundWindow = IntPtr.Zero;
        private IntPtr _lastActiveExternalWindow = IntPtr.Zero;
        private DateTime _spawnTime = DateTime.MinValue;
        private string _loadedWallpaperPathInMini = "";

        // P/Invoke declarations
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const int KEYEVENTF_KEYUP = 0x0002;
        private const int VK_CONTROL = 0x11;
        private const int VK_V = 0x56;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_COLOR_DARK_GRAY = 0x002D2D2D;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        public bool IsSummoned => _isCurrentlySummoned;

        public MiniShelfWindow(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
            this.DataContext = viewModel;
            InitializeComponent();

            // Start offscreen
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Left = -20000;
            this.Top = -20000;
            this.Opacity = 0;

            this.Loaded += MiniShelf_Loaded;
            this.Deactivated += MiniShelf_Deactivated;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
                SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }
        }

        private void MiniShelf_Loaded(object sender, RoutedEventArgs e)
        {
            // Set DWM border color
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int borderColor = DWMWA_COLOR_DARK_GRAY;
                DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }

            // Win10 fallback: Mica doesn't exist
            if (Environment.OSVersion.Version.Build < 22000)
            {
                this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
            }

            // Apply theming from current settings
            ApplyCurrentTheme();

            // Attach smooth scrolling
            Classes.SmoothScroll.AttachToWindow(this, Classes.SmoothScroll.ClipboardProfile);
        }

        private void ApplyCurrentTheme()
        {
            string displayMode = Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica";

            if (displayMode == "desktop" || displayMode == "theme")
            {
                string wpPath = Classes.SettingsManager.Current.ClipboardWallpaperPath;
                if (string.IsNullOrEmpty(wpPath) || !System.IO.File.Exists(wpPath))
                {
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                    WallpaperBg.Source = null;
                    WallpaperBg.Visibility = Visibility.Collapsed;
                    _loadedWallpaperPathInMini = "";
                    return;
                }

                if (wpPath == _loadedWallpaperPathInMini)
                {
                    WallpaperBg.Visibility = Visibility.Visible;
                    return; // Already loaded!
                }

                try
                {
                    _loadedWallpaperPathInMini = wpPath;
                    string ext = System.IO.Path.GetExtension(wpPath).ToLowerInvariant();
                    bool isGif = ext == ".gif";

                    if (isGif)
                    {
                        // ═══ LIVE ANIMATED GIF WALLPAPER ═══
                        WallpaperBg.Source = null;
                        var uri = new Uri(wpPath, UriKind.Absolute);
                        XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, uri);
                        XamlAnimatedGif.AnimationBehavior.SetRepeatBehavior(WallpaperBg,
                            System.Windows.Media.Animation.RepeatBehavior.Forever);
                        WallpaperBg.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        // ═══ STATIC PNG/JPG WALLPAPER ═══
                        XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null); // Clear any GIF

                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(wpPath, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 1200; // High-quality decoded resolution
                        bmp.EndInit();
                        bmp.Freeze();

                        // Set unblurred initially to prevent flash
                        WallpaperBg.Source = bmp;
                        WallpaperBg.Visibility = Visibility.Visible;

                        var capturedPath = wpPath;
                        var bmpToBlur = bmp;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                // Pre-render a premium soft blur at radius 15 on background thread
                                var blurredBg = MainWindow.PreBlurBitmap(bmpToBlur, 15);
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (_loadedWallpaperPathInMini != capturedPath) return; // Stale
                                    WallpaperBg.Source = blurredBg;
                                });
                            }
                            catch { }
                        });
                    }
                }
                catch
                {
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                    WallpaperBg.Source = null;
                    WallpaperBg.Visibility = Visibility.Collapsed;
                    _loadedWallpaperPathInMini = "";
                }
            }
            else
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                WallpaperBg.Source = null;
                WallpaperBg.Visibility = Visibility.Collapsed;
                _loadedWallpaperPathInMini = "";
            }
        }

        private void MiniShelf_Deactivated(object sender, EventArgs e)
        {
            // Grace period: Don't auto-dismiss within 600ms of spawning.
            // The shake gesture holds the left mouse button on another window,
            // which immediately reclaims focus and fires Deactivated. Without this
            // grace period the mini shelf spawns and vanishes instantly.
            if ((DateTime.Now - _spawnTime).TotalMilliseconds < 600)
                return;

            // Auto-hide when clicking elsewhere (unlike MainWindow which stays)
            if (_isCurrentlySummoned && !_isAnimatingHide)
            {
                AnimateAndHide();
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_isAnimatingHide || _isShowAnimating) return;
            this.Opacity = 1.0;

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int cn = DWMWA_COLOR_DARK_GRAY;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Shows the mini shelf near the specified logical screen position.
        /// </summary>
        public void ShowNearPosition(double targetX, double targetY)
        {
            _spawnTime = DateTime.Now;

            // Cache foreground window for paste-back
            _previousForegroundWindow = _lastActiveExternalWindow != IntPtr.Zero && IsWindow(_lastActiveExternalWindow)
                ? _lastActiveExternalWindow
                : GetForegroundWindow();

            // Ensure window is shown
            if (!this.IsVisible)
                this.Show();

            if (this.WindowState == WindowState.Minimized)
                this.WindowState = WindowState.Normal;

            // Apply sizing
            this.Width = 360;
            this.MaxHeight = Classes.SettingsManager.Current.MiniFormHeight > 0
                ? Classes.SettingsManager.Current.MiniFormHeight
                : 500;
            this.SizeToContent = SizeToContent.Height;
            this.Height = double.NaN;
            this.UpdateLayout();

            // Calculate position
            var workArea = SystemParameters.WorkArea;
            double safeWidth = 360;

            double rawX = targetX - (safeWidth / 2);
            if (rawX + safeWidth > workArea.Left + workArea.Width - 16)
                rawX = workArea.Left + workArea.Width - safeWidth - 16;
            if (rawX < workArea.Left + 16)
                rawX = workArea.Left + 16;

            double realHeight = this.ActualHeight > 0 ? this.ActualHeight :
                (_lastActualHeight > 0 ? _lastActualHeight : 400);
            if (realHeight <= 0 || double.IsNaN(realHeight))
                realHeight = 400;

            double rawY = targetY - 16;
            double minBottomEdge = workArea.Top + realHeight + 36;
            double maxBottomEdge = workArea.Top + workArea.Height - 16;
            if (rawY < minBottomEdge) rawY = minBottomEdge;
            if (rawY > maxBottomEdge) rawY = maxBottomEdge;

            // Reset opacity and animation
            this.Opacity = 0;
            this.BeginAnimation(OpacityProperty, null);
            RootContent.Opacity = 1;
            RootContent.RenderTransform = null;

            _isCurrentlySummoned = true;
            if (Classes.SettingsManager.Current.EnableSummonAnimations)
                _isShowAnimating = true;

            // Scroll to top
            try
            {
                var sv = FindVisualChild<ScrollViewer>(ShelfListView);
                if (sv != null && sv.VerticalOffset > 0)
                {
                    sv.ScrollToVerticalOffset(0);
                    sv.ScrollToTop();
                }
            }
            catch { }

            this.Activate();

            // Play show animation
            if (Classes.SettingsManager.Current.EnableSummonAnimations)
                PlayShowAnimation();
            else
                this.Opacity = 1.0;

            // Move onscreen
            this.Left = rawX;
            double computedTop = rawY - realHeight - 20;
            if (computedTop < workArea.Top + 16)
                computedTop = workArea.Top + 16;
            if (computedTop + realHeight > workArea.Top + workArea.Height - 16)
                computedTop = workArea.Top + workArea.Height - realHeight - 16;
            this.Top = computedTop;

            // DWM border
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int cn = DWMWA_COLOR_DARK_GRAY;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Background);

            // Refresh theme
            ApplyCurrentTheme();

            Classes.Logger.LogAction("MINI_SHELF", $"Shown at ({this.Left}, {this.Top})");
        }

        /// <summary>
        /// Toggles visibility of the mini shelf.
        /// </summary>
        public void ToggleVisibility(double targetX, double targetY)
        {
            if (_isCurrentlySummoned && !_isAnimatingHide)
            {
                AnimateAndHide();
            }
            else
            {
                ShowNearPosition(targetX, targetY);
            }
        }

        private void PlayShowAnimation()
        {
            _isShowAnimating = true;
            RootContent.RenderTransform = new TranslateTransform(0, 10);

            var opacityAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            opacityAnim.Completed += (s, e) => _isShowAnimating = false;

            var slideInAnim = new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            this.BeginAnimation(OpacityProperty, opacityAnim);
            RootContent.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideInAnim);
        }

        public void AnimateAndHide()
        {
            if (!_isCurrentlySummoned) return;

            _isAnimatingHide = false;
            _lastActualHeight = this.ActualHeight;
            _isCurrentlySummoned = false;

            try
            {
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                RootContent.Opacity = 1;
                if (RootContent.RenderTransform is TranslateTransform tt)
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                RootContent.RenderTransform = null;

                Dispatcher.InvokeAsync(() =>
                {
                    this.Left = -20000;
                    this.Top = -20000;
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        // Click to paste
        private async void ShelfListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if ((DateTime.Now - _spawnTime).TotalMilliseconds < 300)
            {
                e.Handled = true;
                return;
            }

            if (e.OriginalSource is DependencyObject sourceElement)
            {
                if (sourceElement is System.Windows.Controls.Primitives.ButtonBase ||
                    FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(sourceElement) != null)
                    return;
            }

            var listView = sender as ListView;
            if (listView == null) return;
            var itemContainer = ItemsControl.ContainerFromElement(listView, e.OriginalSource as DependencyObject) as ListViewItem;

            if (itemContainer?.DataContext is ClipboardItem clipboardObj)
            {
                await CopyItemAndPaste(clipboardObj);
                e.Handled = true;
            }
        }

        private void ShelfListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ShelfListView.SelectedItem is ClipboardItem item)
                item.Execute();
        }

        private async System.Threading.Tasks.Task CopyItemAndPaste(ClipboardItem clipboardObj)
        {
            try { _viewModel.MoveItemToTop(clipboardObj); } catch { }

            try
            {
                MainWindow.SetWritingClipboard(true);

                if (!string.IsNullOrEmpty(clipboardObj.FilePath))
                {
                    var dataObj = new DataObject();
                    var dropList = new System.Collections.Specialized.StringCollection();
                    dropList.Add(clipboardObj.FilePath);
                    dataObj.SetFileDropList(dropList);
                    if (!string.IsNullOrEmpty(clipboardObj.RawContent))
                    {
                        dataObj.SetData(DataFormats.Text, clipboardObj.RawContent);
                        dataObj.SetData(DataFormats.UnicodeText, clipboardObj.RawContent);
                    }
                    else
                    {
                        dataObj.SetData(DataFormats.StringFormat, clipboardObj.FilePath);
                        dataObj.SetData(DataFormats.Text, clipboardObj.FilePath);
                    }
                    dataObj.SetData("FileNameW", new string[] { clipboardObj.FilePath });
                    dataObj.SetData("FileName", new string[] { clipboardObj.FilePath });

                    if (clipboardObj.ItemType == ClipboardItemType.Image)
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(clipboardObj.FilePath);
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 1024;
                            bmp.EndInit();
                            bmp.Freeze();
                            dataObj.SetImage(bmp);
                        }
                        catch { }
                    }

                    byte[] moveEffect = new byte[] { 5, 0, 0, 0 };
                    using (var dropEffect = new System.IO.MemoryStream())
                    {
                        dropEffect.Write(moveEffect, 0, moveEffect.Length);
                        dataObj.SetData("Preferred DropEffect", dropEffect);
                    }

                    for (int retry = 0; retry < 3; retry++)
                    {
                        try { Clipboard.SetDataObject(dataObj, true); break; }
                        catch { await System.Threading.Tasks.Task.Delay(15); }
                    }
                }
                else if (!string.IsNullOrEmpty(clipboardObj.RawContent))
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        try { Clipboard.SetText(clipboardObj.RawContent); break; }
                        catch { await System.Threading.Tasks.Task.Delay(15); }
                    }
                }
            }
            catch { }

            AnimateAndHide();

            await System.Threading.Tasks.Task.Delay(80);

            if (_previousForegroundWindow != IntPtr.Zero)
            {
                var sbTitle = new System.Text.StringBuilder(256);
                GetWindowText(_previousForegroundWindow, sbTitle, 256);
                string contextTitle = sbTitle.ToString();
                if (!string.IsNullOrWhiteSpace(contextTitle))
                    clipboardObj.AssociatedContextTitle = contextTitle;

                SetForegroundWindow(_previousForegroundWindow);
                await System.Threading.Tasks.Task.Delay(50);

                if (GetForegroundWindow() != _previousForegroundWindow)
                {
                    SetForegroundWindow(_previousForegroundWindow);
                    await System.Threading.Tasks.Task.Delay(30);
                }
            }

            keybd_event(VK_CONTROL, 0, 0, 0);
            keybd_event(VK_V, 0, 0, 0);
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            AnimateAndHide();
        }

        private void OpenHub_Click(object sender, RoutedEventArgs e)
        {
            AnimateAndHide();
            // Find the MainWindow and open the Hub
            if (Application.Current.MainWindow is MainWindow mainWin)
            {
                mainWin.OpenHubWindow();
            }
        }

        // Helper to find visual children
        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T found) return found;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }
    }
}
