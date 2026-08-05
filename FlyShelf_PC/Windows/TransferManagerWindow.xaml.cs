// ---------------------------------------------------------------
// TransferManagerWindow.xaml.cs — Code-behind for Transfer Manager
// Thin code-behind: singleton pattern, drag-drop, keyboard shortcuts
// ---------------------------------------------------------------
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FlyShelf.Classes;
using FlyShelf.ViewModels;

namespace FlyShelf.Windows
{
    public partial class TransferManagerWindow : MicaWPF.Controls.MicaWindow
    {
        // ═══ Singleton ═══
        private static TransferManagerWindow? _instance;
        private readonly TransferManagerViewModel _vm;

        public TransferManagerWindow()
        {
            InitializeComponent();
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            NativeMethods.ApplyWindowBackdropAndBackground(this);

            _vm = new TransferManagerViewModel();
            DataContext = _vm;
        }

        /// <summary>
        /// Singleton pattern — show existing window or create new one.
        /// </summary>
        public static void ShowOrActivate()
        {
            if (_instance != null && _instance.IsLoaded)
            {
                if (_instance.WindowState == WindowState.Minimized)
                    _instance.WindowState = WindowState.Normal;
                _instance._vm.Resume();
                WindowHelper.ShowInForeground(_instance);
                Classes.SmoothScrollFeature.Attach(_instance);
                return;
            }

            _instance = new TransferManagerWindow();
            WindowHelper.ShowInForeground(_instance);
        }

        // ═══ Window Closing — hide instead of destroy (singleton) ═══

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            FlyShelf.Classes.SmoothScrollFeature.Detach(this);
            // Hide instead of closing so we can re-show later
            e.Cancel = true;
            _vm.Cleanup();
            Hide();
        }

        /// <summary>
        /// Call this on app shutdown to actually dispose the singleton.
        /// </summary>
        public static void ForceClose()
        {
            if (_instance != null)
            {
                _instance._vm.Cleanup();
                _instance.Closing -= _instance.Window_Closing;
                _instance.Close();
                _instance = null;
            }
        }

        // ═══ Drag-Drop Handlers ═══

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;

                // Show overlay
                DropOverlay.Visibility = Visibility.Visible;

                // Update target text
                var selectedPeer = _vm.SelectedPeer;
                DropTargetText.Text = selectedPeer != null
                    ? $"Will send to {selectedPeer.DeviceName}"
                    : "Select a peer device first";
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    if (_vm.SelectedPeer == null)
                    {
                        ToastWindow.ShowToast("Select a peer device first to send files");
                        return;
                    }

                    _vm.HandleFileDrop(files);
                    ToastWindow.ShowToast($"Sending {files.Length} file(s) to {_vm.SelectedPeer.DeviceName}");
                }
            }
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        // ═══ Filter Tab Clicks ═══

        private void FilterAll_Click(object sender, RoutedEventArgs e) => _vm.FilterMode = "All";
        private void FilterActive_Click(object sender, RoutedEventArgs e) => _vm.FilterMode = "Active";
        private void FilterCompleted_Click(object sender, RoutedEventArgs e) => _vm.FilterMode = "Completed";
        private void FilterFailed_Click(object sender, RoutedEventArgs e) => _vm.FilterMode = "Failed";

        // ═══ Keyboard Shortcuts ═══

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            var selected = _vm.SelectedTransfer;

            switch (e.Key)
            {
                case Key.Space:
                    // Toggle pause/resume on selected transfer
                    if (selected != null)
                    {
                        if (selected.CanPause)
                            _vm.PauseCommand.Execute(selected);
                        else if (selected.CanResume)
                            _vm.ResumeCommand.Execute(selected);
                    }
                    e.Handled = true;
                    break;

                case Key.Delete:
                    // Cancel selected transfer
                    if (selected?.CanCancel == true)
                        _vm.CancelCommand.Execute(selected);
                    e.Handled = true;
                    break;

                case Key.O:
                    // Open file location for completed
                    if (Keyboard.Modifiers == ModifierKeys.Control && selected?.IsCompleted == true)
                    {
                        _vm.OpenFileLocationCommand.Execute(selected);
                        e.Handled = true;
                    }
                    break;

                case Key.Escape:
                    Hide();
                    e.Handled = true;
                    break;
            }
        }
    }
}
