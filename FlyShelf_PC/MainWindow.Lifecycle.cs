using FlyShelf.ViewModels;
using MicaWPF.Controls;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace FlyShelf
{
    public partial class MainWindow : MicaWindow
    {
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_isAnimatingHide) return;
            // Guard: don't fight QuickLook for focus
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .Any(w => w is FlyShelf.Windows.QuickLookWindow && w.IsActive)) return;
            this.Opacity = 1.0;
            
            // Explicitly set DWM border color on activation to prevent DWM/MicaWindow from resetting it to system accent
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int cn = DWMWA_COLOR_DARK_GRAY;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                }
            }
            catch { }
        }


        /// <summary>
        /// Auto-hide the clipboard shelf when user clicks elsewhere (e.g. to type in another app).
        /// Respects persistent mode and prevents accidental dismissal during the first 400ms after spawn.
        /// </summary>
        private void MicaWindow_Deactivated(object sender, EventArgs e)
        {
            // Don't auto-hide in persistent/docked mode (taskbar widget click)
            if (_isPersistentMode) return;

            // Guard: Don't dismiss if the window JUST appeared (prevents flicker from focus races)
            if ((DateTime.Now - _spawnTime).TotalMilliseconds < 100) return;

            // Don't dismiss while user is mid-drag
            if (_isDragHovering) return;

            // Don't dismiss if focus went to our own QuickLook window
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .Any(w => w is FlyShelf.Windows.QuickLookWindow && w.IsActive)) return;

            // Auto-hide when user clicks away
            if (this.IsVisible)
            {
                AnimateAndHide();
            }
        }

        /// <summary>
        /// Native Win32 callback triggered when the active foreground window changes globally.
        /// Handles auto-dismissing FlyShelf when shown in a non-activated / non-focus-stealing state.
        /// </summary>
        private void ForegroundChangedCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd != IntPtr.Zero)
            {
                // Get thread/process ID of the new foreground window
                GetWindowThreadProcessId(hwnd, out uint focusedProcId);
                uint currProcId = (uint)System.Environment.ProcessId;

                // Cache the last active external window, filtering out our own app, taskbar, desktop, and standard system Windows Core UI
                if (focusedProcId != currProcId)
                {
                    var sbClass = new System.Text.StringBuilder(256);
                    GetClassName(hwnd, sbClass, 256);
                    string clsName = sbClass.ToString();
                    if (clsName != "Shell_TrayWnd" && 
                        clsName != "Shell_SecondaryTrayWnd" && 
                        clsName != "WorkerW" && 
                        clsName != "Progman" && 
                        clsName != "Windows.UI.Core.CoreWindow" &&
                        clsName != "MultitaskingViewFrame")
                    {
                        _lastActiveExternalWindow = hwnd;
                    }
                }
            }

            if (_isPersistentMode) return;

            // Don't auto-dismiss during first 250ms of spawn to avoid startup focus race transitions
            if ((DateTime.Now - _spawnTime).TotalMilliseconds < 250) return;

            if (_isDragHovering) return;

            // Get thread/process ID of the new foreground window
            GetWindowThreadProcessId(hwnd, out uint focusedProcessId);
            uint currentProcessId = (uint)System.Environment.ProcessId;

            // If the focused window belongs to our own app (e.g. MainWindow, HubWindow, QuickLook), do not dismiss
            if (focusedProcessId == currentProcessId) return;

            // Foreground changed to another app (browser, editor, desktop, etc.)! Auto-dismiss FlyShelf!
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (this.IsVisible && !_isAnimatingHide)
                {
                    AnimateAndHide();
                }
            });
        }
        private bool _isPersistentMode = false;
        private bool _isAnimatingHide = false;

        /// <summary>Fast appear animation on inner content (preserves Mica glass).</summary>
        private void PlayShowAnimation()
        {
            RootContent.RenderTransformOrigin = new Point(0.5, 1);
            RootContent.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(0.97, 0.97), new TranslateTransform(0, 6) }
            };
            RootContent.Opacity = 0;

            // GPU Optimization: Cache the visual tree as a flat texture during the animation
            // to bypass expensive re-rasterization of frosted glass and heavy card rendering.
            RootContent.CacheMode = new BitmapCache { EnableClearType = false, RenderAtScale = 1.0 };

            var showEaseOut = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = showEaseOut };
            var scaleIn = new System.Windows.Media.Animation.DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = showEaseOut };
            var slideIn = new System.Windows.Media.Animation.DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = showEaseOut };

            fadeIn.Completed += (s, e) =>
            {
                RootContent.CacheMode = null; // Restore crisp Cleartype rendering on visual completion
            };

            RootContent.BeginAnimation(OpacityProperty, fadeIn);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
            ((TransformGroup)RootContent.RenderTransform).Children[1].BeginAnimation(TranslateTransform.YProperty, slideIn);
        }

        /// <summary>Fast dismiss animation on inner content, then hides window.</summary>
        private static readonly TimeSpan _hideAnimDuration = TimeSpan.FromMilliseconds(100);
        private static readonly System.Windows.Media.Animation.CubicEase _hideEaseIn = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };

        // PERF: Deferred mascot/GIF resume timer — mascot starts 1s after spawn, not during spawn
        private System.Windows.Threading.DispatcherTimer? _mascotDelayTimer;

        private void AnimateAndHide()
        {
            if (_isAnimatingHide || !this.IsVisible) return;
            _isAnimatingHide = true;
            _lastActualHeight = this.ActualHeight;

            // PERF: Cancel any pending mascot delay so it doesn't fire after hide
            _mascotDelayTimer?.Stop();

            // PERF: Immediately STOP (not pause) mascot and wallpaper GIF to drop CPU/GPU to zero instantly
            try
            {
                MascotIdle.StopAnimation();
                var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                animator?.Pause();
            }
            catch { }

            // Clear PDF merge selections so they don't persist on reopen
            DismissMergeState();
            CloseSearch();

            RootContent.RenderTransformOrigin = new Point(0.5, 1);
            RootContent.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) }
            };

            // GPU Optimization: Cache the visual tree as a flat texture during the dismiss animation
            RootContent.CacheMode = new BitmapCache { EnableClearType = false, RenderAtScale = 1.0 };

            var scaleOutX = new System.Windows.Media.Animation.DoubleAnimation(1, 0.97, _hideAnimDuration) { EasingFunction = _hideEaseIn };
            var scaleOutY = new System.Windows.Media.Animation.DoubleAnimation(1, 0.97, _hideAnimDuration) { EasingFunction = _hideEaseIn };
            var slideOut = new System.Windows.Media.Animation.DoubleAnimation(0, 5, _hideAnimDuration) { EasingFunction = _hideEaseIn };

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, _hideAnimDuration) { EasingFunction = _hideEaseIn };
            fadeOut.Completed += (s, e) =>
            {
                try
                {
                    this.Hide();
                    RootContent.BeginAnimation(OpacityProperty, null);
                    RootContent.Opacity = 1;
                    RootContent.RenderTransform = null;
                    RootContent.CacheMode = null;
                }
                catch { }
                _isAnimatingHide = false;
            };

            RootContent.BeginAnimation(OpacityProperty, fadeOut);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, scaleOutX);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, scaleOutY);
            ((TransformGroup)RootContent.RenderTransform).Children[1].BeginAnimation(TranslateTransform.YProperty, slideOut);
        }
        private DateTime _spawnTime = DateTime.MinValue;
        private IntPtr _previousForegroundWindow = IntPtr.Zero;
        private static int _clipboardWriteRefCount = 0;
        internal static bool _isWritingClipboard
        {
            get => System.Threading.Volatile.Read(ref _clipboardWriteRefCount) > 0;
            set
            {
                if (value)
                {
                    System.Threading.Interlocked.Increment(ref _clipboardWriteRefCount);
                }
                else
                {
                    int current;
                    do
                    {
                        current = _clipboardWriteRefCount;
                        if (current <= 0) break;
                    } while (System.Threading.Interlocked.CompareExchange(ref _clipboardWriteRefCount, current - 1, current) != current);
                }
            }
        }
        private static readonly object _timerLock = new object();
        private static System.Threading.Timer? _clipboardWriteResetTimer;
        
        internal static void SetWritingClipboard(bool value)
        {
            if (value)
            {
                System.Threading.Interlocked.Increment(ref _clipboardWriteRefCount);
                lock (_timerLock)
                {
                    _clipboardWriteResetTimer?.Dispose();
                    _clipboardWriteResetTimer = new System.Threading.Timer(_ =>
                    {
                        if (System.Threading.Volatile.Read(ref _clipboardWriteRefCount) > 0)
                        {
                            Classes.Logger.LogAction("CLIPBOARD", "⚠️ _isWritingClipboard was stuck true — auto-reset after 2s safety timeout");
                            System.Threading.Interlocked.Exchange(ref _clipboardWriteRefCount, 0);
                        }
                    }, null, 2000, System.Threading.Timeout.Infinite);
                }
            }
            else
            {
                int current;
                do
                {
                    current = _clipboardWriteRefCount;
                    if (current <= 0) break;
                } while (System.Threading.Interlocked.CompareExchange(ref _clipboardWriteRefCount, current - 1, current) != current);

                if (System.Threading.Volatile.Read(ref _clipboardWriteRefCount) == 0)
                {
                    lock (_timerLock)
                    {
                        _clipboardWriteResetTimer?.Dispose();
                        _clipboardWriteResetTimer = null;
                    }
                }
            }
        }

        private IntPtr GetTargetForegroundWindow()
        {
            IntPtr ptr = GetForegroundWindow();
            
            var sb = new System.Text.StringBuilder(256);
            GetClassName(ptr, sb, 256);
            string className = sb.ToString();

            if (className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd" || className == "WorkerW" || className == "Progman")
            {
                // Quick bypass: If we have a cached valid/visible external window, return it instantly!
                if (_lastActiveExternalWindow != IntPtr.Zero && IsWindow(_lastActiveExternalWindow) && IsWindowVisible(_lastActiveExternalWindow))
                {
                    return _lastActiveExternalWindow;
                }

                IntPtr target = IntPtr.Zero;
                uint currentProcessId = GetCurrentProcessId();
                EnumWindows((wnd, param) =>
                {
                    if (IsWindowVisible(wnd))
                    {
                        uint processId;
                        GetWindowThreadProcessId(wnd, out processId);
                        if (processId != currentProcessId)
                        {
                            GetClassName(wnd, sb, 256);
                            string cName = sb.ToString();
                            if (cName != "Shell_TrayWnd" && cName != "Shell_SecondaryTrayWnd" && cName != "WorkerW" && cName != "Progman")
                            {
                                GetWindowText(wnd, sb, 256);
                                if (sb.Length > 0 && sb.ToString() != "FlyShelf" && sb.ToString() != "Program Manager")
                                {
                                    target = wnd;
                                    return false; 
                                }
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);
                if (target != IntPtr.Zero) return target;
            }
            
            return ptr;
        }

        private DateTime _lastSortContextTime = DateTime.MinValue;

        private bool _borderColorSet = false;

        public void ShowNearPosition(double targetX, double targetY, int mode = 0, bool isPersistent = false, bool stealFocus = true)
        {
            CloseSearch();
            CloseEmojiPicker();
            // PERF: Use cached foreground window — avoids expensive EnumWindows P/Invoke scan
            _previousForegroundWindow = _lastActiveExternalWindow != IntPtr.Zero && IsWindow(_lastActiveExternalWindow)
                ? _lastActiveExternalWindow
                : GetForegroundWindow();

            _spawnTime = DateTime.Now;
            _isPersistentMode = isPersistent;

            // Abort hide animation if one is actively running
            if (_isAnimatingHide)
            {
                _isAnimatingHide = false;
                try
                {
                    RootContent.BeginAnimation(OpacityProperty, null);
                    if (RootContent.RenderTransform is TransformGroup tg)
                    {
                        tg.Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        tg.Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        tg.Children[1].BeginAnimation(TranslateTransform.YProperty, null);
                    }
                }
                catch { }
            }

            if (this.IsVisible)
            {
                this.Hide(); 
            }

            // PERF: Removed ShowInTaskbar toggle — it destroys/recreates the Win32 HWND (200-500ms penalty)

            _viewModel.CurrentMode = mode;
            this.MaxHeight = _viewModel.CurrentFlyShelfMaxHeight;
            this.Width = _viewModel.CurrentFlyShelfWidth;

            // Always reset selection to the first item when showing/opening the shelf
            if (_viewModel.DroppedItems.Count > 0)
            {
                ShelfListView.SelectedIndex = 0;
            }

            // Always scroll to the very top so the shelf never opens at a previous scroll offset
            {
                var sv = GetShelfScrollViewer();
                sv?.ScrollToTop();
            }

            // Force a deterministic height so the window doesn't bounce around with SizeToContent
            if (mode == 0)
            {
                // Mini mode: let content drive height, capped by MaxHeight
                this.SizeToContent = SizeToContent.Height;
                this.Height = double.NaN;
            }
            else
            {
                // Mode 1/2: use the stored height exactly — no content-driven fluctuation
                this.SizeToContent = SizeToContent.Manual;
                this.Height = _viewModel.CurrentFlyShelfMaxHeight;
            }

            var workArea = SystemParameters.WorkArea;
            double safeWidth = double.IsNaN(this.Width) ? 360 : this.Width;
            if (safeWidth <= 0) safeWidth = 320;

            double rawX = targetX - (safeWidth / 2);
            if (rawX + safeWidth > workArea.Left + workArea.Width - 16)
                rawX = workArea.Left + workArea.Width - safeWidth - 16;
            if (rawX < workArea.Left + 16)
                rawX = workArea.Left + 16;

            double rawY = targetY - 16;
            if (rawY > workArea.Top + workArea.Height - 16)
                rawY = workArea.Top + workArea.Height - 16;
            
            _lockedBottomEdge = rawY;
            _isEdgeLocked = true;

            this.Left = rawX;
            // PERF: Use estimated height — removed expensive this.Measure() that forced full visual tree layout (300-800ms)
            // The Dispatcher callback below adjusts position after ActualHeight resolves naturally
            double estimatedHeight = mode == 0 
                ? (_lastActualHeight > 0 ? _lastActualHeight : (double.IsNaN(this.Height) ? FlyShelf.Classes.SettingsManager.Current.MiniFormHeight : this.Height)) 
                : _viewModel.CurrentFlyShelfMaxHeight;
            this.Top = _lockedBottomEdge - estimatedHeight - 20;

            this.ShowActivated = stealFocus;
            RootContent.Opacity = 0;
            this.Show();
            if (stealFocus) this.Activate();

            // PERF: Cache DWM border attribute — only set once, never changes
            if (!_borderColorSet)
            {
                int cn = DWMWA_COLOR_DARK_GRAY;
                DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                _borderColorSet = true;
            }

            // Use Dispatcher callback to adjust position after the first layout pass completes.
            Dispatcher.InvokeAsync(() =>
            {
                if (this.ActualHeight > 0 && Math.Abs(this.ActualHeight - estimatedHeight) > 1)
                {
                    // Push it 20px dynamically upward to completely avoid taskbar z-index clipping!
                    this.Top = _lockedBottomEdge - this.ActualHeight - 20; 
                    
                    if (this.Top < workArea.Top)
                    {
                        this.Top = workArea.Top + 20;
                    }
                }

                // Play the appear animation ONLY after the window is perfectly settled at its final position.
                // This eliminates the position shift glitch during the fade/scale animation.
                PlayShowAnimation();

                // PERF: Do NOT resume mascot/GIF immediately — let the clipboard spawn lag-free first.
                // Defer mascot + wallpaper GIF start by 1 second so old laptops don't stutter on spawn.
                _mascotDelayTimer?.Stop();
                if (_mascotDelayTimer == null)
                {
                    _mascotDelayTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(1000)
                    };
                    _mascotDelayTimer.Tick += (s, ev) =>
                    {
                        _mascotDelayTimer.Stop();
                        if (!this.IsVisible || _isAnimatingHide) return; // Window was dismissed before timer fired
                        try
                        {
                            var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                            animator?.Play();
                            MascotIdle.ResumePlayback();
                            Classes.AnimationTriggerService.Instance.StartIdleAnimation();
                        }
                        catch { }
                    };
                }
                _mascotDelayTimer.Start();

                // Trigger visible high-quality render after 1s of opening
                if (_scrollHighQualityTimer == null)
                {
                    _scrollHighQualityTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(1000)
                    };
                    _scrollHighQualityTimer.Tick += (s, ev) =>
                    {
                        _scrollHighQualityTimer.Stop();
                        RenderVisibleThumbnails();
                    };
                }
                else
                {
                    _scrollHighQualityTimer.Stop();
                }
                _scrollHighQualityTimer.Start();
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            int currentToken = ++_spawnToken;

            // Give keyboard focus to the ListView so arrow keys + Enter work immediately
            // PERF: Defer focus to Background priority so it runs after layout + animation
            if (stealFocus)
            {
                Dispatcher.InvokeAsync(() => FocusFirstItemContainer(),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void FocusFirstItemContainer()
        {
            if (_viewModel.DroppedItems.Count == 0) return;

            if (ShelfListView.SelectedIndex < 0)
                ShelfListView.SelectedIndex = 0;

            int index = ShelfListView.SelectedIndex;
            
            // If the containers are already generated, focus immediately:
            var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
            if (container != null)
            {
                container.Focus();
                Keyboard.Focus(container);
                ShelfListView.ScrollIntoView(container);
            }
            else
            {
                // Otherwise, register event handler to focus as soon as they are ready:
                EventHandler? statusHandler = null;
                statusHandler = (s, ev) =>
                {
                    if (ShelfListView.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    {
                        ShelfListView.ItemContainerGenerator.StatusChanged -= statusHandler;
                        Dispatcher.InvokeAsync(() =>
                        {
                            var lazyContainer = ShelfListView.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
                            if (lazyContainer != null)
                            {
                                lazyContainer.Focus();
                                Keyboard.Focus(lazyContainer);
                                ShelfListView.ScrollIntoView(lazyContainer);
                            }
                            else
                            {
                                ShelfListView.Focus();
                            }
                        }, System.Windows.Threading.DispatcherPriority.Input);
                    }
                };
                ShelfListView.ItemContainerGenerator.StatusChanged += statusHandler;
                ShelfListView.Focus();
            }
        }

        private void ShelfListView_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0) return;

            var now = DateTime.UtcNow;
            double elapsedMs = (now - _lastScrollTime).TotalMilliseconds;
            _lastScrollTime = now;

            double change = Math.Abs(e.VerticalChange);
            double instVelocity = elapsedMs > 0 ? change / elapsedMs : change;

            if (elapsedMs > 500)
            {
                _scrollVelocity = instVelocity;
            }
            else
            {
                // Smooth the velocity using an EMA (exponential moving average)
                _scrollVelocity = 0.7 * _scrollVelocity + 0.3 * instVelocity;
            }

            // Mark that active scrolling is happening, and suppress hover buttons immediately
            _viewModel.IsScrolling = true;
            _viewModel.AllowHover = false;

            // Start or reset the timer to reset IsScrolling back to false after a delay
            if (_scrollDecayTimer == null)
            {
                _scrollDecayTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200) // Reset 200ms after scroll activity stops
                };
                _scrollDecayTimer.Tick += (s, ev) =>
                {
                    _scrollDecayTimer.Stop();
                    _viewModel.IsScrolling = false;
                    _scrollVelocity = 0;
                    // Do not set AllowHover = true here; keep it false until the user physically moves the mouse!
                };
            }
            else
            {
                _scrollDecayTimer.Stop();
            }

            _scrollDecayTimer.Start();

            // Start or reset the 1s stoppage timer to load visible high-quality thumbnails
            if (_scrollHighQualityTimer == null)
            {
                _scrollHighQualityTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1000)
                };
                _scrollHighQualityTimer.Tick += (s, ev) =>
                {
                    _scrollHighQualityTimer.Stop();
                    RenderVisibleThumbnails();
                };
            }
            else
            {
                _scrollHighQualityTimer.Stop();
            }

            _scrollHighQualityTimer.Start();
        }

        private void ShelfListView_MouseLeave(object sender, MouseEventArgs e)
        {
            // Reset AllowHover back to true when the mouse leaves the list view area entirely
            _viewModel.AllowHover = true;
        }

        private void RenderVisibleThumbnails()
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var sv = GetShelfScrollViewer();
                    if (sv == null) return;

                    double viewportWidth = sv.ViewportWidth;
                    double viewportHeight = sv.ViewportHeight;
                    if (viewportHeight <= 0 || viewportWidth <= 0) return;

                    Rect viewportRect = new Rect(0, 0, viewportWidth, viewportHeight);
                    int count = _viewModel.DroppedItems.Count;

                    for (int i = 0; i < count; i++)
                    {
                        var item = _viewModel.DroppedItems[i];
                        if (item == null) continue;

                        // Only process image items that have not loaded high quality and are not currently loading
                        if (item.ItemType != ClipboardItemType.Image) continue;
                        if (item.IsLoadedHighQuality || item.IsLoadingHighQuality) continue;

                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                        if (container == null || !container.IsLoaded) continue;

                        int currentIndex = i;

                        try
                        {
                            GeneralTransform transform = container.TransformToAncestor(sv);
                            Rect bounds = transform.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

                            if (viewportRect.IntersectsWith(bounds))
                            {
                                // Trigger high-quality 300px thumbnail rendering
                                item.IsLoadingHighQuality = true;
                                string filePath = item.FilePath;

                                _ = System.Threading.Tasks.Task.Run(() =>
                                {
                                    try
                                    {
                                        var bmp = ViewModels.FlyShelfViewModel.LoadImageThumbnail(filePath, 300);
                                        if (bmp != null)
                                        {
                                            Dispatcher.InvokeAsync(() =>
                                            {
                                                item.Icon = bmp;
                                                item.IsLoadedHighQuality = true;
                                                item.IsLoadingHighQuality = false;

                                                // Smooth cubic ease-out fade-in transition
                                                var element = ShelfListView.ItemContainerGenerator.ContainerFromIndex(currentIndex) as FrameworkElement;
                                                if (element != null && element.IsLoaded)
                                                {
                                                    var img = FindVisualChild<Image>(element, "ItemIcon");
                                                    if (img != null)
                                                    {
                                                        var anim = new System.Windows.Media.Animation.DoubleAnimation
                                                        {
                                                            From = 0.2,
                                                            To = 1.0,
                                                            Duration = TimeSpan.FromMilliseconds(400),
                                                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                                                        };
                                                        img.BeginAnimation(UIElement.OpacityProperty, anim);
                                                    }
                                                }
                                            }, System.Windows.Threading.DispatcherPriority.Background);
                                        }
                                        else
                                        {
                                            Dispatcher.InvokeAsync(() =>
                                            {
                                                item.IsLoadingHighQuality = false;
                                            });
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Classes.Logger.LogAction("SCROLL_LOAD_FAIL", $"Failed to load 300px thumbnail: {ex.Message}");
                                        Dispatcher.InvokeAsync(() =>
                                        {
                                            item.IsLoadingHighQuality = false;
                                        });
                                    }
                                });
                            }
                        }
                        catch
                        {
                            // Soft fail if coordinates transformation fails during rapid UI updates
                        }
                    }
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("SCROLL_LOAD_ERR", $"Error in RenderVisibleThumbnails: {ex.Message}");
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

    }
}
