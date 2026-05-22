// ═══════════════════════════════════════════════════════════════════
// SpriteAnimator — WPF UserControl for rendering GIF sprite animations.
// Uses XamlAnimatedGif (already in project) for GIF playback.
// Supports hot-loading from disk, play/stop, and flip transforms.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using XamlAnimatedGif;
using FlyShelf.Classes;

namespace FlyShelf.Controls
{
    public partial class SpriteAnimator : UserControl
    {
        private string _currentFilePath = "";
        private string _assignedTrigger = "";

        public SpriteAnimator()
        {
            InitializeComponent();
        }

        // ═══ Dependency Properties ═══

        /// <summary>Width of the sprite display.</summary>
        public double SpriteWidth
        {
            get => (double)GetValue(SpriteWidthProperty);
            set => SetValue(SpriteWidthProperty, value);
        }
        public static readonly DependencyProperty SpriteWidthProperty =
            DependencyProperty.Register(nameof(SpriteWidth), typeof(double), typeof(SpriteAnimator),
                new PropertyMetadata(48.0));

        /// <summary>Height of the sprite display.</summary>
        public double SpriteHeight
        {
            get => (double)GetValue(SpriteHeightProperty);
            set => SetValue(SpriteHeightProperty, value);
        }
        public static readonly DependencyProperty SpriteHeightProperty =
            DependencyProperty.Register(nameof(SpriteHeight), typeof(double), typeof(SpriteAnimator),
                new PropertyMetadata(48.0));

        // ═══ Public API ═══

        /// <summary>
        /// The animation trigger name this animator is responsible for (e.g., "idle", "delete").
        /// Set this in XAML or code-behind to auto-wire to AnimationTriggerService.
        /// </summary>
        public string AssignedTrigger
        {
            get => _assignedTrigger;
            set
            {
                _assignedTrigger = value;
                WireToTriggerService();
            }
        }

        /// <summary>
        /// Load and play a GIF animation from an absolute file path.
        /// </summary>
        public void PlayAnimation(string filePath, int width = 0, int height = 0, bool loop = true)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                StopAnimation();
                return;
            }

            if (filePath == _currentFilePath && (SpriteImage.Source != null || AnimationBehavior.GetSourceUri(SpriteImage) != null))
            {
                // Already playing/loaded this animation, just ensure it's playing and visible
                ResumePlayback();
                AnimatorRoot.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                // Update dimensions if specified
                if (width > 0) SpriteWidth = width;
                if (height > 0) SpriteHeight = height;

                string ext = Path.GetExtension(filePath).ToLowerInvariant();

                if (ext == ".gif")
                {
                    // Use XamlAnimatedGif for GIF playback
                    var uri = new Uri(filePath, UriKind.Absolute);
                    AnimationBehavior.SetSourceUri(SpriteImage, uri);
                    AnimationBehavior.SetRepeatBehavior(SpriteImage,
                        loop ? System.Windows.Media.Animation.RepeatBehavior.Forever
                             : new System.Windows.Media.Animation.RepeatBehavior(1));

                    // For one-shot animations, listen for completion
                    if (!loop)
                    {
                        AnimationBehavior.AddAnimationCompletedHandler(SpriteImage, OnOneShotCompleted);
                    }
                }
                else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp")
                {
                    // Static image fallback
                    AnimationBehavior.SetSourceUri(SpriteImage, null);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    SpriteImage.Source = bitmap;
                }

                _currentFilePath = filePath;
                AnimatorRoot.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.LogAction("SPRITE", $"Failed to play animation: {ex.Message}");
                StopAnimation();
            }
        }

        /// <summary>
        /// Play an animation from a ThemeAnimation config object.
        /// </summary>
        public void PlayAnimation(ThemeAnimation anim)
        {
            if (anim == null || string.IsNullOrEmpty(anim.ResolvedFilePath))
            {
                StopAnimation();
                return;
            }

            PlayAnimation(anim.ResolvedFilePath, anim.Width, anim.Height, anim.Loop);
        }

        /// <summary>
        /// Pause the current animated GIF playback.
        /// </summary>
        public void PausePlayback()
        {
            try
            {
                var animator = AnimationBehavior.GetAnimator(SpriteImage);
                animator?.Pause();
            }
            catch { }
        }

        /// <summary>
        /// Resume the current animated GIF playback.
        /// </summary>
        public void ResumePlayback()
        {
            try
            {
                var animator = AnimationBehavior.GetAnimator(SpriteImage);
                animator?.Play();
            }
            catch { }
        }

        /// <summary>
        /// Stop the current animation and hide the sprite.
        /// </summary>
        public void StopAnimation()
        {
            try
            {
                AnimationBehavior.SetSourceUri(SpriteImage, null);
                SpriteImage.Source = null;
                AnimatorRoot.Visibility = Visibility.Collapsed;
                _currentFilePath = "";
            }
            catch { }
        }

        /// <summary>
        /// Flip the sprite horizontally (for directional movement).
        /// </summary>
        public void SetFlipped(bool flipped)
        {
            SpriteFlip.ScaleX = flipped ? -1 : 1;
        }

        /// <summary>
        /// Whether an animation is currently playing.
        /// </summary>
        public bool IsPlaying => AnimatorRoot.Visibility == Visibility.Visible
                                 && !string.IsNullOrEmpty(_currentFilePath);

        // ═══════════════════════════════════════════════════════════════
        // INTERNAL: Auto-wire to AnimationTriggerService
        // ═══════════════════════════════════════════════════════════════

        private void WireToTriggerService()
        {
            // Subscribe to animation requests matching our trigger
            AnimationTriggerService.Instance.AnimationRequested += OnAnimationRequested;
            AnimationTriggerService.Instance.AllAnimationsStop += OnAllStop;
        }

        private void OnAnimationRequested(object? sender, AnimationRequestEventArgs e)
        {
            if (e.TriggerName != _assignedTrigger) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (e.IsStop)
                {
                    StopAnimation();
                }
                else if (e.Animation != null)
                {
                    PlayAnimation(e.Animation);
                }
            });
        }

        private void OnAllStop()
        {
            Dispatcher.InvokeAsync(() => StopAnimation());
        }

        private void OnOneShotCompleted(object? sender, RoutedEventArgs e)
        {
            // One-shot animation completed — hide after brief pause
            Dispatcher.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(200);
                // Don't hide if a new animation was started in the meantime
                if (_assignedTrigger == "delete" || _assignedTrigger == "copy")
                {
                    StopAnimation();
                }
            });

            // Remove the handler to prevent memory leaks
            AnimationBehavior.RemoveAnimationCompletedHandler(SpriteImage, OnOneShotCompleted);
        }
    }
}
