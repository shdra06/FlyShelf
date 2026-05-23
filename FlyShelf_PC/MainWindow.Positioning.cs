using FlyShelf.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf
{
    /// <summary>
    /// MainWindow partial — Window Positioning, Spawn Logic, Focus Management, Scroll & Thumbnails.
    /// Contains: ShowNearPosition, FocusFirstItemContainer, ShelfListView_ScrollChanged,
    ///           ShelfListView_MouseLeave, RenderVisibleThumbnails, SuspendThemeAnimations, ResumeThemeAnimations.
    /// </summary>
    public partial class MainWindow
    {
        private DateTime _lastSortContextTime = DateTime.MinValue;

        private bool _borderColorSet = false;

        public void ShowNearPosition(double targetX, double targetY, int mode = 0, bool isPersistent = false, bool stealFocus = true)
        {
            Classes.Logger.LogAction("TELEMETRY", $"ShowNearPosition entered, mode={mode}, isPersistent={isPersistent}, stealFocus={stealFocus}");
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
            if (Classes.SettingsManager.Current.EnableSummonAnimations)
            {
                // Start invisible — the show animation fade-in masks the initial UI layout
                RootContent.Opacity = 0;
            }
            else
            {
                RootContent.Opacity = 1.0;
                RootContent.RenderTransform = null;
            }
            this.Show();

            // ═══ CRITICAL SCROLL RESET — must happen between Show() and Activate() ═══
            // ScrollToTop/ScrollToVerticalOffset are NO-OPS on a hidden window because the
            // ScrollViewer's visual tree is not rendered and ignores offset commands.
            // After Show(), the visual tree is live — we MUST force the layout and reset
            // the scroll offset BEFORE Activate() fires the Activated event, which triggers
            // FocusFirstItemContainer(). If the offset is still stale when FocusFirstItemContainer
            // runs, ContainerFromIndex(0) returns null (index 0 is off-viewport), the else-branch
            // calls ShelfListView.Focus(), and WPF's Selector.OnGotKeyboardFocus auto-scrolls
            // to an unpredictable position.
            try
            {
                // Clear any stale SmoothScroll animation target that would fight our reset
                Classes.SmoothScroll.ResetScrollState(GetShelfScrollViewer());

                if (ShelfListView.Items.Count > 0)
                    ShelfListView.SelectedIndex = 0;

                // PERF: Use InvalidateArrange instead of UpdateLayout.
                // UpdateLayout forces a SYNCHRONOUS full-tree layout pass (300-500ms with 500 items).
                // InvalidateArrange just marks the tree dirty — the next render tick does the layout
                // asynchronously. ScrollToVerticalOffset(0) sets the pending offset which takes effect
                // when the layout happens naturally.
                var sv = GetShelfScrollViewer();
                if (sv != null)
                {
                    sv.ScrollToVerticalOffset(0);
                    sv.ScrollToTop();
                    sv.InvalidateArrange();
                }
            }
            catch { }

            if (stealFocus) this.Activate();

            if (Classes.SettingsManager.Current.EnableSummonAnimations)
            {
                // Play the appear animation IMMEDIATELY — masks the initial UI layout/render.
                PlayShowAnimation();
            }
            else
            {
                RootContent.Opacity = 1.0;
                RootContent.RenderTransform = null;
            }

            // Final safety-net: after all async layout passes complete, force scroll to top one more time
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var sv = GetShelfScrollViewer();
                    if (sv != null)
                    {
                        sv.ScrollToVerticalOffset(0);
                        sv.ScrollToTop();
                    }

                    if (ShelfListView.Items.Count > 0)
                    {
                        ShelfListView.SelectedIndex = 0;

                        // Focus the first container if already generated
                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
                        if (container != null)
                        {
                            container.Focus();
                            Keyboard.Focus(container);
                        }
                    }
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Loaded);

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
                Classes.Logger.LogAction("TELEMETRY", "ShowNearPosition Loaded callback executed (Layout rendering complete)");
                if (this.ActualHeight > 0 && Math.Abs(this.ActualHeight - estimatedHeight) > 1)
                {
                    // Push it 20px dynamically upward to completely avoid taskbar z-index clipping!
                    this.Top = _lockedBottomEdge - this.ActualHeight - 20; 
                    
                    if (this.Top < workArea.Top)
                    {
                        this.Top = workArea.Top + 20;
                    }
                }

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
                if (index == 0)
                {
                    var sv = GetShelfScrollViewer();
                    sv?.ScrollToTop();
                }
                else
                {
                    ShelfListView.ScrollIntoView(container);
                }
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
                                if (index == 0)
                                {
                                    var sv = GetShelfScrollViewer();
                                    sv?.ScrollToTop();
                                }
                                else
                                {
                                    ShelfListView.ScrollIntoView(lazyContainer);
                                }
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

        private bool _themeAnimationsSuspended = false;

        public void SuspendThemeAnimations()
        {
            if (_themeAnimationsSuspended) return;
            _themeAnimationsSuspended = true;
            try
            {
                MascotIdle.PausePlayback();
                var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                animator?.Pause();
            }
            catch { }
        }

        public void ResumeThemeAnimations()
        {
            if (!_themeAnimationsSuspended) return;
            _themeAnimationsSuspended = false;
            try
            {
                if (this.IsVisible && !_isAnimatingHide && Classes.SettingsManager.Current.ThemeAnimationsEnabled)
                {
                    MascotIdle.ResumePlayback();
                    var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                    animator?.Play();
                }
            }
            catch { }
        }
    }
}
