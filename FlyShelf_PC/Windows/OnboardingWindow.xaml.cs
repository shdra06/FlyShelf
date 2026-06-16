using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        }

        private void ShowStep(int index)
        {
            _currentStep = index;
            for (int i = 0; i < TOTAL_STEPS; i++)
            {
                _stepPanels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
                _dots[i].Fill = i <= index
                    ? new SolidColorBrush(Color.FromRgb(99, 102, 241))  // #6366F1
                    : new SolidColorBrush(Color.FromRgb(55, 65, 81));   // #374151
                _dots[i].Width = i == index ? 10 : 6;
                _dots[i].Height = i == index ? 10 : 6;
            }

            // Update button text
            NextBtnText.Text = index == TOTAL_STEPS - 1 ? "🚀 Get Started" : "Next →";

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
                        : Color.FromRgb(245, 158, 11), // amber-light
                    new Point(0, 0), new Point(1, 1));
                var inactiveBrush = new SolidColorBrush(Color.FromRgb(42, 45, 63)); // #2A2D3E

                ThemeCard_Mica.BorderBrush = mode == "mica" ? selectedGlow : inactiveBrush;
                ThemeCard_Desktop.BorderBrush = mode == "desktop" ? selectedGlow : inactiveBrush;
            }
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
            try { this.DragMove(); } catch { }
        }
    }
}
