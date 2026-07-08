// ---------------------------------------------------------------
// TransferConverters — IValueConverter and IMultiValueConverter
// implementations for the Transfer Manager window
// ---------------------------------------------------------------
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>Converts byte count (long) to formatted file size string (e.g. "12.4 MB").</summary>
    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
                return LanTransferSession.FormatBytes(bytes);
            if (value is double d)
                return LanTransferSession.FormatBytes((long)d);
            return "—";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Converts bytes-per-second (double) to formatted speed string (e.g. "45.2 MB/s").</summary>
    public class SpeedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double bps)
                return LanTransferSession.FormatSpeed(bps);
            return "—";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Maps TransferState enum to a theme-appropriate SolidColorBrush.</summary>
    public class StateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TransferState state)
            {
                return state switch
                {
                    TransferState.Queued => new SolidColorBrush(Color.FromRgb(156, 163, 175)),       // Gray
                    TransferState.Connecting => new SolidColorBrush(Color.FromRgb(96, 165, 250)),     // Blue
                    TransferState.Transferring => new SolidColorBrush(Color.FromRgb(74, 222, 128)),   // Green
                    TransferState.Paused => new SolidColorBrush(Color.FromRgb(251, 191, 36)),         // Amber
                    TransferState.Completed => new SolidColorBrush(Color.FromRgb(34, 197, 94)),       // Green
                    TransferState.Failed => new SolidColorBrush(Color.FromRgb(248, 113, 113)),        // Red
                    TransferState.Cancelled => new SolidColorBrush(Color.FromRgb(156, 163, 175)),     // Gray
                    _ => new SolidColorBrush(Color.FromRgb(156, 163, 175))
                };
            }
            return new SolidColorBrush(Colors.Gray);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Standard bool → Visibility converter.</summary>
    public class BoolToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility.Visible;
    }

    /// <summary>Inverse bool → Visibility converter.</summary>
    public class InverseBoolToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility.Collapsed;
    }

    /// <summary>Compares value to parameter for equality → Visibility.</summary>
    public class EqualityToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null && parameter == null) return Visibility.Visible;
            if (value == null) return Visibility.Collapsed;
            return value.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Converts TransferDirection to arrow icon string.</summary>
    public class TransferDirectionToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TransferDirection dir)
                return dir == TransferDirection.Send ? "📤" : "📥";
            return "📁";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// IMultiValueConverter that computes progress bar fill width from (Value, Maximum, ActualWidth).
    /// Used inside the ProgressBar ControlTemplate.
    /// </summary>
    public class ProgressWidthConverter : IMultiValueConverter
    {
        public static readonly ProgressWidthConverter Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 3
                && values[0] is double val
                && values[1] is double max
                && values[2] is double width
                && max > 0)
            {
                double ratio = Math.Min(1.0, Math.Max(0.0, val / max));
                return ratio * width;
            }
            return 0.0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
