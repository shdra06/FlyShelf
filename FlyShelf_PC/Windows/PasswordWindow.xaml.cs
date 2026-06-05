using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FlyShelf.ViewModels;
using MicaWPF.Controls;

namespace FlyShelf.Windows
{
    public partial class PasswordWindow : MicaWindow
    {
        private readonly ClipboardItem _item;
        private bool _isRevealed = false;

        public PasswordWindow(ClipboardItem item, bool focusLabel)
        {
            InitializeComponent();
            FlyShelf.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            _item = item;

            // Load data
            LabelInput.Text = _item.FileName ?? "";
            string pass = _item.RawContent ?? "";
            PasswordMaskedBox.Password = pass;
            PasswordPlainTextBox.Text = pass;

            // Setup focus
            Loaded += (s, e) =>
            {
                if (focusLabel)
                {
                    LabelInput.Focus();
                    LabelInput.SelectAll();
                }
                else
                {
                    PasswordMaskedBox.Focus();
                }
            };
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
        {
            _isRevealed = !_isRevealed;
            if (_isRevealed)
            {
                PasswordPlainTextBox.Text = PasswordMaskedBox.Password;
                PasswordMaskedBox.Visibility = Visibility.Collapsed;
                PasswordPlainTextBox.Visibility = Visibility.Visible;
                EyeIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.EyeOff24;
            }
            else
            {
                PasswordMaskedBox.Password = PasswordPlainTextBox.Text;
                PasswordPlainTextBox.Visibility = Visibility.Collapsed;
                PasswordMaskedBox.Visibility = Visibility.Visible;
                EyeIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Eye24;
            }
        }

        private void CopyPassword_Click(object sender, RoutedEventArgs e)
        {
            string currentPassword = _isRevealed ? PasswordPlainTextBox.Text : PasswordMaskedBox.Password;
            if (string.IsNullOrEmpty(currentPassword)) return;

            try
            {
                if (FlyShelf.Classes.ClipboardHelper.SafeSetText(currentPassword))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Password copied! 🔑");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Clipboard busy — try again");
                }

                // Visual feedback on the icon
                CopyIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Checkmark24;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (s, ev) =>
                {
                    CopyIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Copy24;
                    timer.Stop();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Failed to copy: {ex.Message}");
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            string newLabel = LabelInput.Text.Trim();
            string newPassword = _isRevealed ? PasswordPlainTextBox.Text : PasswordMaskedBox.Password;

            if (string.IsNullOrEmpty(newLabel))
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Label cannot be empty.");
                return;
            }

            _item.FileName = newLabel;
            _item.RawContent = newPassword;
            _item.GeneratePasswordIcon(); // Refresh the icon representation

            DialogResult = true;
            Close();
        }
    }
}
