using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Collections.Generic;
using FlyShelf.Helpers;

namespace FlyShelf.Windows
{
    public partial class OnboardingWindow : Window
    {
        private int _currentStep = 0;
        private const int TOTAL_STEPS = 6;
        private string _selectedThemeMode = "desktop";
        private readonly StackPanel[] _stepPanels;
        private readonly System.Windows.Shapes.Ellipse[] _dots;

        public OnboardingWindow()
        {
            InitializeComponent();
            _stepPanels = new[] { Step1, Step2, Step3, Step4, Step5, Step6 };
            _dots = new[] { Dot1, Dot2, Dot3, Dot4, Dot5, Dot6 };
            ShowStep(0);
            BuildOnboardingKeycaps();
        }

        private void ShowStep(int index)
        {
            _currentStep = index;
            for (int i = 0; i < TOTAL_STEPS; i++)
            {
                _stepPanels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
                _dots[i].Fill = i <= index
                    ? new SolidColorBrush(ThemeColors.IndigoAccent)  // #6366F1
                    : new SolidColorBrush(Color.FromRgb(55, 65, 81));   // #374151
                _dots[i].Width = i == index ? 10 : 6;
                _dots[i].Height = i == index ? 10 : 6;
            }

            // Update button text
            NextBtnText.Text = index == TOTAL_STEPS - 1 ? "Get Started" : "Next →";

            // Step 3: Auto-enable widget
            if (index == 2)
            {
                Classes.SettingsManager.Current.EnableTaskbarWidget = true;
            }

            // Fade-in animation for current step
            var panel = _stepPanels[index];
            panel.Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            panel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        private void NextBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_currentStep < TOTAL_STEPS - 1)
            {
                ShowStep(_currentStep + 1);
            }
            else
            {
                CompleteOnboarding();
            }
        }

        private void Skip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            CompleteOnboarding();
        }

        private void ThemeCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string mode)
            {
                _selectedThemeMode = mode;

                // Update visual selection
                var selectedGlow = new LinearGradientBrush(
                    mode == "mica"
                        ? Color.FromRgb(37, 99, 235)   // blue
                        : Color.FromRgb(217, 119, 6),  // amber
                    mode == "mica"
                        ? Color.FromRgb(124, 58, 237)  // purple
                        : ThemeColors.WarningAmber, // amber-light
                    new Point(0, 0), new Point(1, 1));
                var inactiveBrush = new SolidColorBrush(Color.FromRgb(42, 45, 63)); // #2A2D3E

                ThemeCard_Mica.BorderBrush = mode == "mica" ? selectedGlow : inactiveBrush;
                ThemeCard_Desktop.BorderBrush = mode == "desktop" ? selectedGlow : inactiveBrush;
            }
        }

        private void BuildOnboardingKeycaps()
        {
            if (HotkeyKeycapsPanel == null) return;
            HotkeyKeycapsPanel.Children.Clear();
            var display = Classes.SettingsManager.Current.HotkeyDisplayString;
            var parts = display.Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    HotkeyKeycapsPanel.Children.Add(new TextBlock
                    {
                        Text = "+",
                        FontSize = 24, FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), // #64748B
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(14, 0, 14, 0)
                    });
                }
                var keycap = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 10, 16, 10),
                    Background = new SolidColorBrush(Color.FromRgb(42, 45, 62)), // #2A2D3E
                    BorderBrush = new SolidColorBrush(Color.FromRgb(58, 61, 78)), // #3A3D4E
                    BorderThickness = new Thickness(1),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius = 8, Opacity = 0.3, ShadowDepth = 2,
                        Direction = 270, Color = Colors.Black
                    },
                    Child = new TextBlock
                    {
                        Text = parts[i],
                        FontSize = 24, FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250)), // #A78BFA
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                HotkeyKeycapsPanel.Children.Add(keycap);
            }

            // Update description text
            if (HotkeyDescriptionText != null)
                HotkeyDescriptionText.Text = $"Press {display} anytime to summon or dismiss your clipboard shelf. You can change this in Settings.";
        }

        private void CompleteOnboarding()
        {
            Classes.SettingsManager.Current.HasCompletedOnboarding = true;
            Classes.SettingsManager.Current.EnableTaskbarWidget = true;

            // Apply selected theme mode
            Classes.SettingsManager.Current.ThemeDisplayMode = _selectedThemeMode;

            Classes.SettingsManager.Save();
            this.DialogResult = true;
            this.Close();
        }

        // Allow dragging the window
        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { this.DragMove(); } catch { } // Best-effort: failure is acceptable
        }
    }
}
