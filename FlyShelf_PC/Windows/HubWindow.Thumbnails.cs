// ═══════════════════════════════════════════════════════════════════════
// HubWindow.Thumbnails.cs — Hub thumbnail lazy rendering: scroll-based
// viewport visibility detection and async image loading for History tab.
// Part of the HubWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlyShelf.Classes;
using FlyShelf.ViewModels;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        // ═══════════════════════════════════════════════════════════════════════
        // Hub Thumbnail Rendering — Scroll-based lazy load for History tab
        // ═══════════════════════════════════════════════════════════════════════

        private void HubListView_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0) return;

            // Debounce: start or reset the 30ms timer to render visible thumbnails when scroll stops
            if (_hubScrollHighQualityTimer == null)
            {
                _hubScrollHighQualityTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _hubScrollHighQualityTimer.Tick += (s, ev) =>
                {
                    _hubScrollHighQualityTimer.Stop();
                    RenderHubVisibleThumbnails();
                };
            }
            else
            {
                _hubScrollHighQualityTimer.Stop();
            }
            _hubScrollHighQualityTimer.Start();
        }

        private ScrollViewer? GetHubScrollViewer()
        {
            if (HubListView == null) return null;
            if (VisualTreeHelper.GetChildrenCount(HubListView) == 0) return null;

            // Try the standard WPF pattern first: Border → ScrollViewer
            var border = VisualTreeHelper.GetChild(HubListView, 0) as System.Windows.Controls.Decorator;
            if (border?.Child is ScrollViewer sv1)
                return sv1;

            // Fallback: walk the visual tree recursively to find any ScrollViewer descendant.
            // This handles cases where the ListView template is wrapped (e.g., VSCodeScrollViewer style).
            return FindDescendantScrollViewer(HubListView);
        }

        private static ScrollViewer? FindDescendantScrollViewer(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv) return sv;
                var found = FindDescendantScrollViewer(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Walks all visible HubListView containers and loads 300px image thumbnails
        /// for any Image/QRCode items whose Icon has been evicted (null).
        /// Does NOT evict — eviction is handled by OptimizeMemoryUsage on window close.
        /// </summary>
        private int _hubThumbnailRetryCount = 0;
        private static void ThumbDiag(string msg)
        {
            Logger.LogAction("HUB_THUMB", msg);
        }


        private void RenderHubVisibleThumbnails()
        {
            ThumbDiag($"RENDER CALLED (before dispatch)");
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!this.IsVisible) { ThumbDiag("SKIP: window not visible"); return; }
                    if (HistoryGrid == null || HistoryGrid.Visibility != Visibility.Visible) { ThumbDiag("SKIP: HistoryGrid not visible"); return; }
                    if (HubListView == null) { ThumbDiag("SKIP: HubListView is null"); return; }

                    // Skip UpdateLayout() here — it forces a synchronous layout pass
                    // that fights CompositionTarget.Rendering for UI thread time.
                    // The VirtualizingStackPanel has already generated containers by this point.

                    if (HubListView.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    {
                        // Containers not ready yet — retry with a small delay (up to 10 times)
                        if (_hubThumbnailRetryCount < 10)
                        {
                            _hubThumbnailRetryCount++;
                            var retryTimer = new System.Windows.Threading.DispatcherTimer
                            {
                                Interval = TimeSpan.FromMilliseconds(100)
                            };
                            retryTimer.Tick += (s, ev) =>
                            {
                                retryTimer.Stop();
                                RenderHubVisibleThumbnails();
                            };
                            retryTimer.Start();
                        }
                        return;
                    }
                    _hubThumbnailRetryCount = 0;
                    ThumbDiag($"RENDER START: items={HubListView.Items.Count}, containers=Generated");

                    var sv = GetHubScrollViewer();
                    if (sv == null) { ThumbDiag("ABORT: GetHubScrollViewer returned null"); return; }

                    double viewportWidth = sv.ViewportWidth;
                    double viewportHeight = sv.ViewportHeight;

                    int count = HubListView.Items.Count;
                    int loadedCount = 0;
                    int imageCount = 0;
                    int skippedHasIcon = 0;
                    int skippedLoading = 0;

                    // If viewport dimensions are valid, use viewport-based visibility check
                    if (viewportHeight > 0 && viewportWidth > 0)
                    {
                        // Prefetch overdraw: expand viewport by 300px top and bottom
                        Rect viewportRect = new Rect(0, -300, viewportWidth, viewportHeight + 600);

                        for (int i = 0; i < count; i++)
                        {
                            var item = HubListView.Items[i] as ClipboardItem;
                            if (item == null) continue;

                            // Only process image and QR code items
                            if (item.ItemType != ClipboardItemType.Image && item.ItemType != ClipboardItemType.QRCode)
                                continue;

                            imageCount++;

                            // Skip if already loaded or currently loading
                            if (item.Icon != null) { skippedHasIcon++; continue; }
                            if (item.IsLoadingHighQuality) { skippedLoading++; continue; }

                            var container = HubListView.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                            if (container == null || !container.IsLoaded) continue;

                            bool isVisible = false;
                            try
                            {
                                GeneralTransform transform = container.TransformToAncestor(sv);
                                Rect bounds = transform.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                                isVisible = viewportRect.IntersectsWith(bounds);
                            }
                            catch { /* container not fully in visual tree */ }

                            if (!isVisible) continue;

                            LoadHubThumbnailAsync(item);
                            loadedCount++;
                        }
                    }
                    else
                    {
                        ThumbDiag($"VIEWPORT INVALID: w={viewportWidth}, h={viewportHeight}");
                    }

                    ThumbDiag($"SCAN: images={imageCount}, hasIcon={skippedHasIcon}, loading={skippedLoading}, viewportLoaded={loadedCount}");

                    // Fallback: if no thumbnails were loaded via viewport check, force-load the first
                    // batch of image items. This handles the initial open where transforms may not be ready.
                    if (loadedCount == 0)
                    {
                        int batchLoaded = 0;
                        for (int i = 0; i < count && batchLoaded < 20; i++)
                        {
                            var item = HubListView.Items[i] as ClipboardItem;
                            if (item == null) continue;

                            if (item.ItemType != ClipboardItemType.Image && item.ItemType != ClipboardItemType.QRCode)
                                continue;

                            if (item.Icon != null || item.IsLoadingHighQuality)
                                continue;

                            LoadHubThumbnailAsync(item);
                            batchLoaded++;
                        }
                        ThumbDiag($"FALLBACK: loaded {batchLoaded} items");
                    }
                    else
                    {
                        ThumbDiag($"VIEWPORT: loaded {loadedCount} items");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("HUB_THUMB_ERR", $"Error in RenderHubVisibleThumbnails: {ex.Message}");
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Loads a 300px thumbnail for the given item on a background thread and updates Icon on the UI thread.
        /// </summary>
        private void LoadHubThumbnailAsync(ClipboardItem item)
        {
            item.IsLoadingHighQuality = true;
            string filePath = item.FilePath;
            ThumbDiag($"LOAD START: {System.IO.Path.GetFileName(filePath)}, exists={System.IO.File.Exists(filePath)}");

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var bmp = FlyShelfViewModel.LoadImageThumbnail(filePath, 300);
                    if (bmp != null)
                    {
                        ThumbDiag($"LOAD OK: {System.IO.Path.GetFileName(filePath)}, w={bmp.PixelWidth}, h={bmp.PixelHeight}");
                        Dispatcher.InvokeAsync(() =>
                        {
                            item.Icon = bmp;
                            item.IsLoadedHighQuality = true;
                            item.IsLoadingHighQuality = false;
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    else
                    {
                        ThumbDiag($"LOAD NULL: {System.IO.Path.GetFileName(filePath)}");
                        Dispatcher.InvokeAsync(() => { item.IsLoadingHighQuality = false; });
                    }
                }
                catch
                {
                    Dispatcher.InvokeAsync(() => { item.IsLoadingHighQuality = false; });
                }
            });
        }
    }
}
