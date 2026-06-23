using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;


namespace FlyShelf.Styles
{
    /// <summary>
    /// Code-behind for HubClipboardItemTemplate.xaml ResourceDictionary.
    /// Delegates all event handlers to the parent HubWindow instance.
    /// </summary>
    public partial class HubClipboardItemTemplate : ResourceDictionary
    {
        public HubClipboardItemTemplate()
        {
            InitializeComponent();
        }


        // ═══════════════════════════════════════════════════════════════════
        // HELPER — Find the parent HubWindow from any FrameworkElement
        // ═══════════════════════════════════════════════════════════════════

        private static Windows.HubWindow? FindHub(object sender)
        {
            if (sender is FrameworkElement fe)
                return Window.GetWindow(fe) as Windows.HubWindow;
            return null;
        }


        // ═══════════════════════════════════════════════════════════════════
        // CLICK HANDLERS — delegates to HubWindow
        // ═══════════════════════════════════════════════════════════════════

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            FindHub(sender)?.ItemCheckBox_Click(sender, e);
        }


        private void CopyColorHex_Click(object sender, RoutedEventArgs e)
        {
            FindHub(sender)?.CopyColorHex_Click(sender, e);
        }


        private void CopyColorRgb_Click(object sender, RoutedEventArgs e)
        {
            FindHub(sender)?.CopyColorRgb_Click(sender, e);
        }


        private void CopyColorHsl_Click(object sender, RoutedEventArgs e)
        {
            FindHub(sender)?.CopyColorHsl_Click(sender, e);
        }


        private void ContextMenu_SendToDevice_Click(object sender, RoutedEventArgs e)
        {
            FindHub(sender)?.ContextMenu_SendToDevice_Click(sender, e);
        }


        private void ExtractArchive_Click(object sender, RoutedEventArgs e)
        {
            FindHub(sender)?.ExtractArchive_Click(sender, e);
        }


        // ═══════════════════════════════════════════════════════════════════
        // MOUSE HANDLERS — delegates to HubWindow
        // ═══════════════════════════════════════════════════════════════════

        private void PinSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            FindHub(sender)?.PinSpecific_Click(sender, e);
        }


        private void DeleteSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            FindHub(sender)?.DeleteSpecific_Click(sender, e);
        }


        private void ExpandToggleSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            FindHub(sender)?.ExpandToggleSpecific_Click(sender, e);
        }


        private void SendToDevice_Click(object sender, MouseButtonEventArgs e)
        {
            FindHub(sender)?.SendToDevice_Click(sender, e);
        }

        private void RotateImageSpecific_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.RotateImageSpecific_Click(sender, e);
        private void SmartActionSpecific_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.SmartActionSpecific_Click(sender, e);
        private void RunTerminalSpecific_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.RunTerminalSpecific_Click(sender, e);
        private void OpenExplorer_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.OpenExplorer_Click(sender, e);
        private void ConvertToZip_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.ConvertToZip_Click(sender, e);
        private void SyncZipLan_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.SyncZipLan_Click(sender, e);
        private void SanitizeUrlSpecific_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.SanitizeUrlSpecific_Click(sender, e);
        private void MakePasswordSpecific_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.MakePasswordSpecific_Click(sender, e);
        private void RenamePasswordSpecific_Click(object sender, MouseButtonEventArgs e) => FindHub(sender)?.RenamePasswordSpecific_Click(sender, e);
    }
}
