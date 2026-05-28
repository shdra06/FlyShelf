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
            if (mode == 0)
            {
                EnsureClipboardMode();
            }
            CloseSearch();
            CloseEmojiPicker();

            // Increment spawn token at the very beginning of the summon sequence.
            // This immediately invalidates any active or pending dismiss/hide animation callbacks.
            int currentToken = ++_spawnToken;

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
                    // Set base opacity to 0 and clear animation clocks immediately.
                    // This forces WPF to evaluate the opacity as 0.
                    this.Opacity = 0;
                    this.BeginAnimation(OpacityProperty, null);
                    if (RootContent.RenderTransform is TranslateTransform tt)
                    {
                        tt.BeginAnimation(TranslateTransform.YProperty, null);
                    }
                    RootContent.Opacity = 1;
                    RootContent.RenderTransform = null;

                    // Defer offscreen move to let WPF commit the 0% opacity frame onscreen first
                    Dispatcher.InvokeAsync(() => HideWindowInternal(), System.Windows.Threading.DispatcherPriority.Background);
                }
                catch { }
            }

            if (_isCurrentlySummoned)
            {
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                RootContent.Opacity = 1;
                RootContent.RenderTransform = null;
                
                // Defer offscreen move to let WPF commit the 0% opacity frame onscreen first
                Dispatcher.InvokeAsync(() => HideWindowInternal(), System.Windows.Threading.DispatcherPriority.Background);
            }

            // CRITICAL: Always pre-apply the target mode's width and layout mode offscreen.
            // This ensures WPF processes the size change while the window is still offscreen,
            // preventing the "half-rendered" flash where the window appears at the old mode's width.
            _isSuppressingSizeSync = true;
            try
            {
                _viewModel.CurrentMode = mode;
                this.Width = _viewModel.CurrentFlyShelfWidth;
                this.MaxHeight = _viewModel.CurrentFlyShelfMaxHeight;
                if (mode == 0)
                {
                    if (this.SizeToContent != SizeToContent.Height)
                        this.SizeToContent = SizeToContent.Height;
                    if (!double.IsNaN(this.Height))
                        this.Height = double.NaN;
                }
                else
                {
                    if (this.SizeToContent != SizeToContent.Manual)
                        this.SizeToContent = SizeToContent.Manual;
                    this.Height = _viewModel.CurrentFlyShelfMaxHeight;
                }
                this.UpdateLayout();
            }
            finally
            {
                _isSuppressingSizeSync = false;
            }

            // ALWAYS defer the positioning, activation, and summon animation to Background priority.
            // This guarantees that WPF renders the 0% opacity frame offscreen, fully committing 
            // the 0% transparent state to DWM, BEFORE the window is positioned back onscreen.
            // This completely eliminates any repositioning or rapid re-summon flashes/double-spawns!
            Dispatcher.InvokeAsync(() =>
            {
                // Verify this summon hasn't been superseded by a newer summon in the meantime
                if (_spawnToken != currentToken)
                {
                    Classes.Logger.LogAction("TELEMETRY", $"ShowNearPosition deferred callback bypassed: token changed ({currentToken} -> {_spawnToken})");
                    return;
                }

                ShowNearPositionInternal(targetX, targetY, mode, isPersistent, stealFocus);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ShowNearPositionInternal(double targetX, double targetY, int mode, bool isPersistent, bool stealFocus)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }

            // PERF: Removed ShowInTaskbar toggle — it destroys/recreates the Win32 HWND (200-500ms penalty)

            _isSuppressingSizeSync = true;
            try
            {
                _viewModel.CurrentMode = mode;
                this.MaxHeight = _viewModel.CurrentFlyShelfMaxHeight;
                this.Width = _viewModel.CurrentFlyShelfWidth;

                // Force a deterministic height so the window doesn't bounce around with SizeToContent
                if (mode == 0)
                {
                    // Mini mode: let content drive height, capped by MaxHeight
                    if (this.SizeToContent != SizeToContent.Height)
                        this.SizeToContent = SizeToContent.Height;
                    if (!double.IsNaN(this.Height))
                        this.Height = double.NaN;
                }
                else
                {
                    // Mode 1/2: use the stored height exactly — no content-driven fluctuation
                    if (this.SizeToContent != SizeToContent.Manual)
                        this.SizeToContent = SizeToContent.Manual;
                    if (this.Height != (double)_viewModel.CurrentFlyShelfMaxHeight)
                        this.Height = _viewModel.CurrentFlyShelfMaxHeight;
                }

                UpdateToolbarButtonsVisibility();
                this.UpdateLayout();
            }
            finally
            {
                _isSuppressingSizeSync = false;
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
            // Clamp bottom: window bottom edge must stay above the bottom of the work area
            if (rawY > workArea.Top + workArea.Height - 16)
                rawY = workArea.Top + workArea.Height - 16;

            // PERF: Use hardcoded cached physical height on startup or already-resolved ActualHeight.
            // This completely eliminates dynamic spawning height calculations and spawning jumps!
            double realHeight = this.ActualHeight > 0 ? this.ActualHeight : 
                (_lastActualHeight > 0 ? _lastActualHeight : 
                (double.IsNaN(this.Height) ? FlyShelf.Classes.SettingsManager.Current.MiniFormHeight : this.Height));

            // SAFETY: If all height caches are empty/zero (e.g. very first summon before layout),
            // use a sane default so the minBottomEdge clamp actually works.
            if (realHeight <= 0 || double.IsNaN(realHeight))
                realHeight = 400;

            // CRITICAL FIX: Ensure the bottom edge is far enough down so the full window
            // fits within the work area. Without this, shaking near the top of the screen
            // pushes the window's Top above workArea.Top, rendering it half off-screen.
            double minBottomEdge = workArea.Top + realHeight + 36; // 16px top margin + 20px bottom offset
            // Also cap the bottom edge so the window doesn't extend below the work area
            double maxBottomEdge = workArea.Top + workArea.Height - 16;
            if (rawY < minBottomEdge)
                rawY = minBottomEdge;
            if (rawY > maxBottomEdge)
                rawY = maxBottomEdge;

            _lockedBottomEdge = rawY;
            _isEdgeLocked = false; // Lock the edge AFTER all positioning has been completed at the end of the method!

            this.ShowActivated = stealFocus;

            // ═══ CRITICAL: ANTI-BLACK-BOX SPAWN SEQUENCE ═══
            // DWM renders the raw HWND surface (black rectangle) independently of WPF content.
            // If we move the window onscreen BEFORE the opacity animation starts, DWM shows
            // 1-3 frames of black before WPF's opacity transitions from 0→1.
            //
            // Solution: Keep the window OFFSCREEN during all prep work, start the opacity
            // animation while still offscreen, then move onscreen as the very last step.
            // This way the first DWM-visible frame already has the animation clock running
            // at ~0% opacity (invisible), and the content gracefully fades in.

            // 1. Reset opacity and animation clocks while still offscreen
            this.Opacity = 0;
            this.BeginAnimation(OpacityProperty, null);
            RootContent.Opacity = 1;
            RootContent.RenderTransform = null;

            _isCurrentlySummoned = true;
            if (Classes.SettingsManager.Current.EnableSummonAnimations)
                _isShowAnimating = true; // Guard BEFORE move — prevents OnActivated from flashing opacity to 1.0

            // 2. Scroll reset while still offscreen (no visual impact)
            try
            {
                Classes.SmoothScroll.ResetScrollState(GetShelfScrollViewer());
                if (ShelfListView.Items.Count > 0)
                    ShelfListView.SelectedIndex = 0;
                var sv = GetShelfScrollViewer();
                if (sv != null && sv.VerticalOffset > 0)
                {
                    sv.ScrollToVerticalOffset(0);
                    sv.ScrollToTop();
                    sv.InvalidateArrange();
                }
            }
            catch { }

            // 3. Activate while still offscreen — this is the slow Win32 call that causes
            //    layout stalls. Doing it offscreen means the stall is invisible.
            if (stealFocus) this.Activate();

            // 4. Start the opacity animation BEFORE moving onscreen.
            //    The animation clock starts ticking from opacity=0. When we move the window
            //    onscreen in step 5, the first DWM-composited frame will already be at ~0% opacity.
            if (Classes.SettingsManager.Current.EnableSummonAnimations)
            {
                PlayShowAnimation();
            }
            else
            {
                this.Opacity = 1.0;
            }

            // 5. LAST STEP: Move onscreen. By now the animation is running at near-0% opacity,
            //    so DWM will composite a fully transparent frame — no black box flash.
            this.Left = rawX;
            double computedTop = _lockedBottomEdge - realHeight - 20;
            // Full bounds clamp: keep entire window within the visible work area
            if (computedTop < workArea.Top + 16)
                computedTop = workArea.Top + 16;
            if (computedTop + realHeight > workArea.Top + workArea.Height - 16)
                computedTop = workArea.Top + workArea.Height - realHeight - 16;
            this.Top = computedTop;

            _isEdgeLocked = true; // Lock the edge AFTER all positioning has been completed!

            // Explicitly set DWM border color on each summon to prevent OS/MicaWPF composition resets.
            // PERF: Defer to Background priority so it runs after the spawn animation is fully started.
            Dispatcher.InvokeAsync(() =>
            {
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
            }, System.Windows.Threading.DispatcherPriority.Background);

            // Use Dispatcher callback to adjust position after the first layout pass completes.
            Dispatcher.InvokeAsync(() =>
            {
                Classes.Logger.LogAction("TELEMETRY", "ShowNearPosition Loaded callback executed (Layout rendering complete)");
                if (this.ActualHeight > 0 && Math.Abs(this.ActualHeight - realHeight) > 1)
                {
                    double newTop = _lockedBottomEdge - this.ActualHeight - 20;
                    
                    // Full bounds clamp: keep entire window within the visible work area
                    if (newTop < workArea.Top + 16)
                        newTop = workArea.Top + 16;
                    if (newTop + this.ActualHeight > workArea.Top + workArea.Height - 16)
                        newTop = workArea.Top + workArea.Height - this.ActualHeight - 16;
                    
                    this.Top = newTop;
                }

                // PERF: Do NOT resume mascot/GIF immediately — let the clipboard spawn lag-free first.
                // Defer mascot + wallpaper GIF start to perfectly sync with the end of the 1000ms appear transition.
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
                        if (!_isCurrentlySummoned || _isAnimatingHide) return; // Window was dismissed before timer fired
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

                // Trigger visible high-quality render almost instantly after opening (20ms)
                if (_scrollHighQualityTimer == null)
                {
                    _scrollHighQualityTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(20)
                    };
                    _scrollHighQualityTimer.Tick += (s, ev) =>
                    {
                        _scrollHighQualityTimer.Stop();
                        RenderVisibleThumbnails(onlyFirstTen: false);
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
            // DELETION SCROLL GUARD: If we're in the middle of deleting an item,
            // the VirtualizingStackPanel is recalculating extents during its layout pass.
            // Intercept the scroll change and immediately correct the offset to keep
            // the anchor card pinned. This fires synchronously during the layout pass,
            // so no frame is ever rendered with the wrong offset.
            if (_isDeletionScrollGuardActive && e.VerticalChange != 0)
            {
                try
                {
                    var sv = GetShelfScrollViewer();
                    if (sv != null && _deletionAnchorIndex >= 0 && _deletionAnchorIndex < ShelfListView.Items.Count)
                    {
                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(_deletionAnchorIndex) as ListViewItem;
                        if (container != null)
                        {
                            var transform = container.TransformToAncestor(this);
                            var currentPos = transform.Transform(new Point(0, 0));
                            double drift = currentPos.Y - _deletionAnchorTargetY;
                            _deletionLog?.Add($"  GUARD HIT: scrollΔ={e.VerticalChange:+0.0;-0.0}  anchorY={currentPos.Y:F1}  drift={drift:+0.0;-0.0}px  offset={sv.VerticalOffset:F2}  extent={sv.ExtentHeight:F2}");
                            if (Math.Abs(drift) > 0.5)
                            {
                                double correctedOffset = sv.VerticalOffset + drift;
                                correctedOffset = Math.Max(0, Math.Min(correctedOffset, sv.ScrollableHeight));
                                sv.ScrollToVerticalOffset(correctedOffset);
                                _deletionLog?.Add($"  GUARD FIX: → {correctedOffset:F2}");
                            }
                        }
                    }
                }
                catch { }
                return; // Don't process velocity/scrolling state during deletion
            }

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

            // Active Scrolling Throttling: trigger prefetch rendering every 80ms while actively scrolling.
            // PERF: Only load first-10 images during active scroll — skip heavy BitmapImage decoding
            // for older images to eliminate scroll jitter. Full load happens when scroll stops (30ms timer).
            if ((DateTime.Now - _lastScrollRenderTime).TotalMilliseconds >= 80)
            {
                _lastScrollRenderTime = DateTime.Now;
                RenderVisibleThumbnails(onlyFirstTen: true);
            }

            // Start or reset the snappier 30ms stoppage timer to load visible high-quality thumbnails instantly when scroll stops
            if (_scrollHighQualityTimer == null)
            {
                _scrollHighQualityTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(30)
                };
                _scrollHighQualityTimer.Tick += (s, ev) =>
                {
                    _scrollHighQualityTimer.Stop();
                    RenderVisibleThumbnails(onlyFirstTen: false);
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

        private void RenderVisibleThumbnails(bool onlyFirstTen = false)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!this.IsVisible) return;

                    // Guard: Ensure containers are fully generated before evaluating visibility or eviction
                    if (ShelfListView.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    {
                        return;
                    }

                    // Start the 1-second periodic eviction background timer if not already running
                    if (_evictionBackgroundTimer == null)
                    {
                        _evictionBackgroundTimer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(1)
                        };
                        _evictionBackgroundTimer.Tick += (s, ev) =>
                        {
                            if (!this.IsVisible) return;
                            RenderVisibleThumbnails(onlyFirstTen: false);
                        };
                        _evictionBackgroundTimer.Start();
                    }

                    var sv = GetShelfScrollViewer();
                    if (sv == null) return;

                    double viewportWidth = sv.ViewportWidth;
                    double viewportHeight = sv.ViewportHeight;
                    if (viewportHeight <= 0 || viewportWidth <= 0) return;

                    // Prefetch overdraw: expand viewport vertically by 300px on top and bottom to proactively load adjacent images before they scroll into view
                    Rect viewportRect = new Rect(0, -300, viewportWidth, viewportHeight + 600);
                    int count = ShelfListView.Items.Count;

                    int imageCount = 0;
                    bool anyEvicted = false;

                    for (int i = 0; i < count; i++)
                    {
                        var item = ShelfListView.Items[i] as ClipboardItem;
                        if (item == null) continue;

                        // Only process image and QR code items
                        if (item.ItemType != ClipboardItemType.Image && item.ItemType != ClipboardItemType.QRCode) continue;

                        imageCount++;
                        bool isFirst10Images = imageCount <= 10;

                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                        bool isVisible = false;

                        if (container != null && container.IsLoaded)
                        {
                            try
                            {
                                GeneralTransform transform = container.TransformToAncestor(sv);
                                Rect bounds = transform.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                                isVisible = viewportRect.IntersectsWith(bounds);
                            }
                            catch
                            {
                                // Soft fail if transform fails (e.g. not fully in visual tree yet)
                            }
                        }

                        if (isVisible)
                        {
                            // On-Screen / Visible / Prefetch Zone: Reset eviction timer
                            item.LeftViewportTime = null;

                            // Load 300px thumbnail if not loaded/loading.
                            // During active scrolling (onlyFirstTen=true), skip non-first-10 images
                            // to avoid BitmapImage decode stutters that cause scroll jitter.
                            if (!item.IsLoadedHighQuality && !item.IsLoadingHighQuality && (!onlyFirstTen || isFirst10Images))
                            {
                                item.IsLoadingHighQuality = true;
                                string filePath = item.FilePath;
                                int currentIndex = i;

                                _ = System.Threading.Tasks.Task.Run(() =>
                                {
                                    try
                                    {
                                        var bmp = ViewModels.FlyShelfViewModel.LoadImageThumbnail(filePath, 300);
                                        if (bmp != null)
                                        {
                                            Dispatcher.InvokeAsync(() =>
                                            {
                                                // Coalesce / discard if it was evicted or cancelled in the meantime
                                                if (!item.IsLoadingHighQuality) return;

                                                item.Icon = bmp;
                                                item.IsLoadedHighQuality = true;
                                                item.IsLoadingHighQuality = false;

                                                // Smooth cubic ease-out fade-in transition (150ms for ultra-responsive feel)
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
                                                            Duration = TimeSpan.FromMilliseconds(150),
                                                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                                                        };
                                                        img.BeginAnimation(UIElement.OpacityProperty, anim);
                                                    }
                                                }
                                            }, System.Windows.Threading.DispatcherPriority.Normal);
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
                        else
                        {
                            // Off-Screen / Scrolled Out: Skip eviction if pinned OR is one of the first 10 images
                            if (item.IsPinned || isFirst10Images)
                            {
                                item.LeftViewportTime = null;
                                continue;
                            }

                            // Evict thumbnail ONLY after it has stayed offscreen for at least 10 seconds
                            if (item.Icon != null || item.IsLoadedHighQuality || item.IsLoadingHighQuality)
                            {
                                if (item.LeftViewportTime == null)
                                {
                                    // Record the timestamp when the item first left the viewport
                                    item.LeftViewportTime = DateTime.Now;
                                }
                                else if ((DateTime.Now - item.LeftViewportTime.Value).TotalSeconds >= 10)
                                {
                                    // 10 seconds have elapsed offscreen — actively evict to free RAM
                                    item.Icon = null;
                                    item.IsLoadedHighQuality = false;
                                    item.IsLoadingHighQuality = false;
                                    item.LeftViewportTime = null;
                                    anyEvicted = true;
                                }
                            }
                            else
                            {
                                item.LeftViewportTime = null;
                            }
                        }
                    }

                    if (anyEvicted)
                    {
                        // Force a non-blocking background Gen 2 Garbage Collection to immediately reclaim unmanaged bitmap memory and return it to the OS
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            System.GC.Collect(2, System.GCCollectionMode.Forced, false);
                        });
                    }
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("SCROLL_LOAD_ERR", $"Error in RenderVisibleThumbnails: {ex.Message}");
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
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
                if (_isCurrentlySummoned && !_isAnimatingHide && Classes.SettingsManager.Current.ThemeAnimationsEnabled)
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
