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
            double logicalX = 0;
            double logicalY = 0;
            
            if (FlyShelf.Classes.NativeMethods.GetCursorPos(out var pt))
            {
                logicalX = pt.X;
                logicalY = pt.Y;
                try
                {
                    var monitor = FlyShelf.Classes.Utils.MonitorUtil.GetMonitorWithCursor();
                    double scaleX = monitor.dpiX / 96.0;
                    double scaleY = monitor.dpiY / 96.0;
                    if (scaleX > 0 && scaleY > 0)
                    {
                        logicalX = pt.X / scaleX;
                        logicalY = pt.Y / scaleY;
                    }
                }
                catch { }
            }
            else
            {
                // Fallback to PointToScreen if GetCursorPos fails
                try
                {
                    var point = PointToScreen(e.GetPosition(this));
                    logicalX = point.X;
                    logicalY = point.Y;
                }
                catch
                {
                    // Sane fallback
                    logicalX = System.Windows.SystemParameters.PrimaryScreenWidth / 2;
                    logicalY = System.Windows.SystemParameters.PrimaryScreenHeight / 2;
                }
            }

            FlyShelf.Classes.Logger.LogAction("TELEMETRY", $"Widget left click received, screen point=({logicalX}, {logicalY})");

            // Summon the bigger clipboard MainWindow consistently
            if (_mainWindow != null)
            {
                bool isMode1 = false;
                if (_mainWindow.DataContext is FlyShelf.ViewModels.FlyShelfViewModel vm && vm.CurrentMode == 1)
                {
                    isMode1 = true;
                }

                if (_mainWindow.IsSummoned && isMode1)
                {
                    _mainWindow.AnimateAndHide();
                }
                else
                {
                    _mainWindow.ShowNearPosition(logicalX, logicalY, 1, false, false);
                }
            }
        }
    }
}
