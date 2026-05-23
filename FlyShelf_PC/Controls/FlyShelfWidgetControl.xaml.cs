using System.Windows.Controls;
using System.Windows.Input;

namespace FlyShelf.Controls
{
    public partial class FlyShelfWidgetControl : UserControl
    {
        private MainWindow? _mainWindow;

        public FlyShelfWidgetControl()
        {
            InitializeComponent();
        }

        public void SetMainWindow(MainWindow window)
        {
            _mainWindow = window;
        }

        public (double Width, double Height) CalculateSize(double dpiScale)
        {
            return (80, 36); 
        }

        private void WidgetGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mainWindow != null)
            {
                // Widget click toggles the Main Clipboard overlay (MainWindow in Medium Mode/Mode 1) at cursor
                _mainWindow.ToggleMainClipboard();
            }
        }
    }
}
