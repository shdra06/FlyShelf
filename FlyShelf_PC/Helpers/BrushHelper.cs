using System.Windows.Media;

namespace FlyShelf.Helpers
{
    /// <summary>
    /// Shared helper for creating frozen (thread-safe, GC-friendly) brushes.
    /// </summary>
    internal static class BrushHelper
    {
        internal static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
