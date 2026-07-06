// ---------------------------------------------------------------
// WindowHelper — Ensures all spawned windows appear in foreground
// Provides ShowInForeground and ShowDialogInForeground helpers
// to prevent windows from opening behind the main window.
// ---------------------------------------------------------------
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Helper class to ensure windows always appear in the foreground.
    /// Solves the common WPF issue where Show() or ShowDialog() opens
    /// windows behind the calling window.
    /// </summary>
    public static class WindowHelper
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        /// <summary>
        /// Shows a non-modal window and ensures it appears in the foreground.
        /// Uses the Topmost trick: temporarily set Topmost=true, show, activate, then reset.
        /// </summary>
        public static void ShowInForeground(Window window)
        {
            if (window == null) return;

            window.Topmost = true;
            window.Show();
            window.Activate();
            window.Focus();
            window.Topmost = false;

            // Belt-and-suspenders: use Win32 API to force foreground
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        /// <summary>
        /// Shows a modal dialog and ensures it appears in the foreground.
        /// Sets Owner if possible (to the active WPF window), and uses Topmost trick.
        /// </summary>
        /// <param name="dialog">The window to show as a dialog.</param>
        /// <param name="owner">Optional owner window. If null, tries Application.Current.MainWindow.</param>
        /// <returns>The dialog result.</returns>
        public static bool? ShowDialogInForeground(Window dialog, Window owner = null)
        {
            if (dialog == null) return null;

            // Try to set Owner for proper Z-order and modal behavior
            try
            {
                if (owner != null && owner.IsLoaded && owner.IsVisible)
                {
                    dialog.Owner = owner;
                }
                else
                {
                    // Try to find a suitable owner from active windows
                    var activeWindow = GetActiveOwnableWindow();
                    if (activeWindow != null)
                    {
                        dialog.Owner = activeWindow;
                    }
                }
            }
            catch { } // Best-effort: some windows may not accept Owner

            // Ensure the dialog starts centered if no explicit position is set
            if (dialog.WindowStartupLocation == WindowStartupLocation.Manual
                && dialog.Left == 0 && dialog.Top == 0)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            // Topmost trick to ensure visibility
            dialog.Topmost = true;
            dialog.Loaded += (s, e) =>
            {
                dialog.Topmost = false;
                dialog.Activate();
                dialog.Focus();
            };

            return dialog.ShowDialog();
        }

        /// <summary>
        /// Finds the best available ownable window (visible, loaded, not the dialog itself).
        /// Prefers HubWindow if visible, then MainWindow.
        /// </summary>
        private static Window GetActiveOwnableWindow()
        {
            try
            {
                // Prefer an active visible window
                foreach (Window w in Application.Current.Windows)
                {
                    if (w.IsVisible && w.IsLoaded && w is not Windows.ToastWindow)
                    {
                        return w;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
