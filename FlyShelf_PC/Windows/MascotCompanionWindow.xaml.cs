using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class MascotCompanionWindow : Window
    {
        public enum CompanionState
        {
            Idle,
            Walking,
            Falling,
            Dragging,
            Action
        }

        private CompanionState _state = CompanionState.Falling;
        private DispatcherTimer _physicsTimer = null!;
        private double _velocityX = 0;
        private double _velocityY = 0;
        private double _walkSpeed = 1.5;
        private Random _random = new();

        // Dragging & Throwing tracking
        private Point _lastMousePosition;
        private DateTime _lastMouseTime;

        // Action timer
        private DateTime _actionEndTime = DateTime.MinValue;

        public MascotCompanionWindow()
        {
            InitializeComponent();
            
            // Setup context menu
            var menu = new ContextMenu();
            var openItem = new MenuItem { Header = "Open Clipboard" };
            openItem.Click += (s, e) =>
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (Application.Current.MainWindow is MainWindow mainWin)
                    {
                        mainWin.OpenHubWindow();
                    }
                });
            };
            var settingsItem = new MenuItem { Header = "Disable Desktop Pet" };
            settingsItem.Click += (s, e) =>
            {
                SettingsManager.Current.EnableDesktopMascot = false;
            };
            menu.Items.Add(openItem);
            menu.Items.Add(settingsItem);
            this.ContextMenu = menu;

            // Register mouse handlers on root grid for transparency click-through
            RootGrid.MouseLeftButtonDown += Grid_MouseLeftButtonDown;
            RootGrid.MouseLeftButtonUp += Grid_MouseLeftButtonUp;

            // Wire active theme changes to reload animations immediately
            ThemeManager.Instance.ActiveThemeChanged += OnActiveThemeChanged;

            // Subscribe to AnimationTriggerService to react to copy, delete, and search events
            AnimationTriggerService.Instance.AnimationRequested += OnGlobalAnimationRequested;
        }

        private void OnGlobalAnimationRequested(object? sender, AnimationRequestEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (e.IsStop)
                {
                    if (e.TriggerName == "search" && _state == CompanionState.Action)
                    {
                        _state = CompanionState.Falling;
                        PlayThemeAnimation("idle");
                    }
                }
                else
                {
                    // Map triggers to states/actions
                    if (e.TriggerName == "copy" || e.TriggerName == "insert")
                    {
                        TriggerAction("copy", e.Animation?.DurationMs > 0 ? e.Animation.DurationMs : 1200);
                    }
                    else if (e.TriggerName == "delete")
                    {
                        TriggerAction("delete", e.Animation?.DurationMs > 0 ? e.Animation.DurationMs : 1200);
                    }
                    else if (e.TriggerName == "search")
                    {
                        // Search is ongoing, set action with long duration until stopped
                        TriggerAction("search", 30000); 
                    }
                }
            });
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Position at bottom-left of screen initially
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Left + 100;
            this.Top = workArea.Bottom - this.Height - 100;

            // Start physics tick (approx 30 FPS)
            _physicsTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _physicsTimer.Tick += PhysicsTimer_Tick;
            _physicsTimer.Start();

            PlayThemeAnimation("idle");
        }

        private void OnActiveThemeChanged(ThemePackage? newTheme)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // Reload animation for the current state
                RefreshStateAnimation();
            });
        }

        private void PhysicsTimer_Tick(object? sender, EventArgs e)
        {
            var workArea = SystemParameters.WorkArea;
            double width = this.Width;
            double height = this.Height;

            switch (_state)
            {
                case CompanionState.Idle:
                    // 1% chance per tick to start walking
                    if (_random.NextDouble() < 0.01)
                    {
                        _walkSpeed = _random.Next(0, 2) == 0 ? 1.5 : -1.5;
                        _state = CompanionState.Walking;
                        PlayThemeAnimation("walking");
                    }
                    break;

                case CompanionState.Walking:
                    double newLeft = this.Left + _walkSpeed;
                    
                    // Boundary check
                    if (newLeft < workArea.Left)
                    {
                        newLeft = workArea.Left;
                        _walkSpeed = -_walkSpeed;
                    }
                    else if (newLeft > workArea.Right - width)
                    {
                        newLeft = workArea.Right - width;
                        _walkSpeed = -_walkSpeed;
                    }

                    this.Left = newLeft;
                    MascotAnimator.SetFlipped(_walkSpeed < 0);

                    // 1.5% chance per tick to stop and rest
                    if (_random.NextDouble() < 0.015)
                    {
                        _state = CompanionState.Idle;
                        PlayThemeAnimation("idle");
                    }
                    break;

                case CompanionState.Falling:
                    // Apply gravity constant
                    _velocityY += 0.6;

                    double nextLeft = this.Left + _velocityX;
                    double nextTop = this.Top + _velocityY;

                    // Side wall bounce
                    if (nextLeft < workArea.Left)
                    {
                        nextLeft = workArea.Left;
                        _velocityX = -_velocityX * 0.5; // Dampen horizontal bounce
                    }
                    else if (nextLeft > workArea.Right - width)
                    {
                        nextLeft = workArea.Right - width;
                        _velocityX = -_velocityX * 0.5;
                    }

                    // Ceiling collision
                    if (nextTop < workArea.Top)
                    {
                        nextTop = workArea.Top;
                        _velocityY = 0;
                    }

                    // Floor collision (landing)
                    if (nextTop > workArea.Bottom - height)
                    {
                        nextTop = workArea.Bottom - height;
                        
                        // Bounce if falling fast
                        if (_velocityY > 4)
                        {
                            _velocityY = -_velocityY * 0.35; // Damped bounce
                            _velocityX *= 0.7; // Friction
                        }
                        else
                        {
                            _velocityY = 0;
                            _velocityX = 0;
                            _state = CompanionState.Idle;
                            PlayThemeAnimation("idle");
                        }
                    }

                    this.Left = nextLeft;
                    this.Top = nextTop;
                    
                    if (Math.Abs(_velocityX) > 0.1)
                    {
                        MascotAnimator.SetFlipped(_velocityX < 0);
                    }
                    break;

                case CompanionState.Dragging:
                    // Position is managed directly by WPF DragMove modal loop.
                    // We record last position/time in mouse handlers to compute release velocity.
                    break;

                case CompanionState.Action:
                    if (DateTime.Now > _actionEndTime)
                    {
                        _state = CompanionState.Falling; // Let physics settle it
                        PlayThemeAnimation("falling");
                    }
                    else
                    {
                        // Apply any active action velocity
                        double actionLeft = this.Left + _velocityX;
                        if (actionLeft < workArea.Left) actionLeft = workArea.Left;
                        if (actionLeft > workArea.Right - width) actionLeft = workArea.Right - width;
                        this.Left = actionLeft;

                        if (Math.Abs(_velocityX) > 0.1)
                        {
                            MascotAnimator.SetFlipped(_velocityX < 0);
                        }
                    }
                    break;
            }
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _state = CompanionState.Dragging;
                PlayThemeAnimation("idle");

                _lastMousePosition = PointToScreen(e.GetPosition(this));
                _lastMouseTime = DateTime.Now;

                try
                {
                    this.DragMove();

                    // DragMove returned: user released mouse
                    var releasePos = PointToScreen(Mouse.GetPosition(this));
                    var releaseTime = DateTime.Now;
                    double dt = (releaseTime - _lastMouseTime).TotalSeconds;

                    if (dt > 0.02)
                    {
                        // Convert displacement per second to displacement per 33ms tick
                        _velocityX = (releasePos.X - _lastMousePosition.X) / dt * 0.033;
                        _velocityY = (releasePos.Y - _lastMousePosition.Y) / dt * 0.033;

                        // Cap launch speed to prevent pet from flying off screen
                        _velocityX = Math.Clamp(_velocityX, -25, 25);
                        _velocityY = Math.Clamp(_velocityY, -25, 25);
                    }
                    else
                    {
                        _velocityX = 0;
                        _velocityY = 0;
                    }

                    _state = CompanionState.Falling;
                    PlayThemeAnimation("falling");
                }
                catch { } // Best-effort: failure is acceptable
            }
        }

        private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_state == CompanionState.Dragging)
            {
                _velocityX = 0;
                _velocityY = 0;
                _state = CompanionState.Falling;
                PlayThemeAnimation("falling");
            }
        }

        public void TriggerAction(string actionType, int durationMs = 1200)
        {
            _state = CompanionState.Action;
            _actionEndTime = DateTime.Now.AddMilliseconds(durationMs);
            PlayThemeAnimation(actionType);

            var workArea = SystemParameters.WorkArea;
            
            // Move pet closer to bottom-left (spawning area of clipboard) when action is copy/insert
            if (actionType == "copy" || actionType == "insert")
            {
                _velocityX = (this.Left > workArea.Left + 150) ? -3.5 : 0;
            }
            else
            {
                _velocityX = 0;
            }
            _velocityY = 0;
        }

        private void RefreshStateAnimation()
        {
            switch (_state)
            {
                case CompanionState.Idle:
                    PlayThemeAnimation("idle");
                    break;
                case CompanionState.Walking:
                    PlayThemeAnimation("walking");
                    break;
                case CompanionState.Falling:
                    PlayThemeAnimation("falling");
                    break;
                case CompanionState.Action:
                    // Action keeps its set animation until end time
                    break;
                case CompanionState.Dragging:
                    // Dragging animation is managed by mouse handlers — preserve current animation
                    break;
                default:
                    PlayThemeAnimation("idle");
                    break;
            }
        }

        private void PlayThemeAnimation(string stateName)
        {
            try
            {
                ThemeAnimation? anim = null;

                if (stateName == "walking" || stateName == "running")
                {
                    anim = ThemeManager.Instance.GetAnimation("running") 
                           ?? ThemeManager.Instance.GetAnimation("idle");
                }
                else if (stateName == "falling")
                {
                    // Look for fall/crouch animation, fallback to idle
                    anim = ThemeManager.Instance.GetAnimation("falling") 
                           ?? ThemeManager.Instance.GetAnimation("idle");
                }
                else if (stateName == "copy" || stateName == "insert")
                {
                    anim = ThemeManager.Instance.GetAnimation("insert") 
                           ?? ThemeManager.Instance.GetAnimation("copy") 
                           ?? ThemeManager.Instance.GetAnimation("idle");
                }
                else if (stateName == "delete")
                {
                    anim = ThemeManager.Instance.GetAnimation("header_reaction") 
                           ?? ThemeManager.Instance.GetAnimation("delete") 
                           ?? ThemeManager.Instance.GetAnimation("idle");
                }
                else
                {
                    anim = ThemeManager.Instance.GetAnimation(stateName);
                }

                if (anim != null && !string.IsNullOrEmpty(anim.ResolvedFilePath))
                {
                    MascotAnimator.PlayAnimation(anim);
                    Logger.LogAction("COMPANION", $"Playing theme animation: '{stateName}' (file: {Path.GetFileName(anim.ResolvedFilePath)})");
                }
                else
                {
                    // Fallback to flyshelf-default assets on disk
                    string defaultDir = Path.Combine(ThemeManager.Instance.ThemesDirectory, "flyshelf-default");
                    string file = stateName == "delete" ? "sprites/delete.gif" : "sprites/idle.gif";
                    string fullPath = Path.Combine(defaultDir, file);
                    if (File.Exists(fullPath))
                    {
                        MascotAnimator.PlayAnimation(fullPath, 128, 128, stateName != "delete");
                    }
                    else
                    {
                        MascotAnimator.StopAnimation();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("COMPANION_ANIM_ERR", ex.ToString());
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            ThemeManager.Instance.ActiveThemeChanged -= OnActiveThemeChanged;
            AnimationTriggerService.Instance.AnimationRequested -= OnGlobalAnimationRequested;
            _physicsTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
