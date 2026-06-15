// ═══════════════════════════════════════════════════════════════════
// AnimationTriggerService — Event bridge between app actions and
// mascot sprite animations. Listens for delete/copy/search events
// and fires animation requests to the SpriteAnimator controls.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Event arguments for animation trigger requests.
    /// </summary>
    public class AnimationRequestEventArgs : EventArgs
    {
        /// <summary>Trigger name: "idle", "delete", "copy", "search", "running"</summary>
        public string TriggerName { get; set; } = "";

        /// <summary>The resolved animation config from the active theme.</summary>
        public ThemeAnimation? Animation { get; set; }

        /// <summary>Whether to stop the animation (e.g., search ended).</summary>
        public bool IsStop { get; set; } = false;
    }

    public class AnimationTriggerService
    {
        // ═══ Singleton ═══
        private static AnimationTriggerService? _instance;
        public static AnimationTriggerService Instance => _instance ??= new AnimationTriggerService();

        // ═══ Events ═══
        /// <summary>Fires when an animation should start playing.</summary>
        public event EventHandler<AnimationRequestEventArgs>? AnimationRequested;

        /// <summary>Fires when all animations should stop (theme disabled/changed).</summary>
        public event Action? AllAnimationsStop;

        // ═══ State ═══
        private bool _searchActive = false;
        private bool _isInitialized = false;
        private DateTime _lastActivity = DateTime.Now;

        private AnimationTriggerService() { }

        /// <summary>
        /// Initialize the trigger service. Call after ThemeManager.Initialize().
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            // Listen for theme changes
            ThemeManager.Instance.ActiveThemeChanged += OnThemeChanged;

            // Start idle animation if theme is active
            if (ThemeManager.Instance.ActiveTheme != null)
            {
                StartIdleAnimation();
            }

            Logger.LogAction("THEME", "AnimationTriggerService initialized");
        }

        // ═══════════════════════════════════════════════════════════════
        // PUBLIC: Trigger methods — call these from app event handlers
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Trigger the delete animation (one-shot, e.g., Cat scratch strike).
        /// </summary>
        public void OnDelete()
        {
            if (!IsThemeActive()) return;
            _lastActivity = DateTime.Now;

            var anim = ThemeManager.Instance.GetAnimation("delete");
            if (anim == null || string.IsNullOrEmpty(anim.ResolvedFilePath)) return;

            FireAnimation("delete", anim);

            // Auto-stop after duration (return to idle)
            int durationMs = anim.DurationMs > 0 ? anim.DurationMs : 1000;
            _ = ReturnToIdleAfter(durationMs);
        }

        /// <summary>
        /// Trigger the copy animation (one-shot, e.g., sparkle effect).
        /// </summary>
        public void OnCopy()
        {
            if (!IsThemeActive()) return;
            _lastActivity = DateTime.Now;

            var anim = ThemeManager.Instance.GetAnimation("copy");
            if (anim == null || string.IsNullOrEmpty(anim.ResolvedFilePath)) return;

            FireAnimation("copy", anim);

            int durationMs = anim.DurationMs > 0 ? anim.DurationMs : 600;
            _ = ReturnToIdleAfter(durationMs);
        }

        /// <summary>
        /// Trigger search animation (looping while search is active).
        /// </summary>
        public void OnSearchToggle(bool active)
        {
            if (!IsThemeActive()) return;
            _searchActive = active;
            _lastActivity = DateTime.Now;

            if (active)
            {
                var anim = ThemeManager.Instance.GetAnimation("search");
                if (anim != null && !string.IsNullOrEmpty(anim.ResolvedFilePath))
                {
                    FireAnimation("search", anim);
                }
            }
            else
            {
                // Stop search animation, return to idle
                AnimationRequested?.Invoke(this, new AnimationRequestEventArgs
                {
                    TriggerName = "search",
                    IsStop = true
                });
                StartIdleAnimation();
            }
        }

        /// <summary>
        /// Trigger the running animation (for bottom/corner placement).
        /// </summary>
        public void OnScrolling()
        {
            if (!IsThemeActive()) return;

            var anim = ThemeManager.Instance.GetAnimation("running");
            if (anim == null || string.IsNullOrEmpty(anim.ResolvedFilePath)) return;

            FireAnimation("running", anim);
        }

        /// <summary>
        /// Start the idle animation (continuous loop when clipboard is visible).
        /// </summary>
        public void StartIdleAnimation()
        {
            bool themeActive = IsThemeActive();
            Logger.LogAction("MASCOT", $"StartIdleAnimation: themeActive={themeActive}, animEnabled={SettingsManager.Current.ThemeAnimationsEnabled}, activeTheme='{ThemeManager.Instance.ActiveTheme?.Name ?? "null"}', searchActive={_searchActive}");
            
            if (!themeActive) return;
            if (_searchActive) return; // Don't override search animation

            var anim = ThemeManager.Instance.GetAnimation("idle");
            Logger.LogAction("MASCOT", $"StartIdleAnimation: idle anim={(anim != null ? "found" : "NULL")}, resolvedPath='{anim?.ResolvedFilePath ?? "N/A"}'");
            if (anim == null || string.IsNullOrEmpty(anim.ResolvedFilePath)) return;

            FireAnimation("idle", anim);
        }

        /// <summary>
        /// Stop all animations (e.g., when clipboard window hides).
        /// </summary>
        public void StopAll()
        {
            _searchActive = false;
            AllAnimationsStop?.Invoke();
        }

        // ═══════════════════════════════════════════════════════════════
        // INTERNAL
        // ═══════════════════════════════════════════════════════════════

        private bool IsThemeActive()
        {
            if (!SettingsManager.Current.ThemeAnimationsEnabled) return false;
            return ThemeManager.Instance.ActiveTheme != null;
        }

        private void FireAnimation(string triggerName, ThemeAnimation anim)
        {
            AnimationRequested?.Invoke(this, new AnimationRequestEventArgs
            {
                TriggerName = triggerName,
                Animation = anim,
                IsStop = false
            });
        }

        private async Task ReturnToIdleAfter(int delayMs)
        {
            await Task.Delay(delayMs);
            if (!_searchActive) // Don't override search with idle
            {
                StartIdleAnimation();
            }
        }

        private void OnThemeChanged(ThemePackage? newTheme)
        {
            AllAnimationsStop?.Invoke();

            if (newTheme != null)
            {
                // Restart idle animation with new theme
                StartIdleAnimation();
            }
        }

        public void Dispose()
        {
            ThemeManager.Instance.ActiveThemeChanged -= OnThemeChanged;
        }
    }
}
