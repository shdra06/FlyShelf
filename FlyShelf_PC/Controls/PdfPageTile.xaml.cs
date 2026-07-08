using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace FlyShelf.Controls
{
    public partial class PdfPageTile : UserControl
    {
        public int PageIndex { get; set; }
        public string SourceFile { get; set; }
        public int Rotation { get; set; } = 0;

        public event EventHandler<int> RotateRequested;
        public event EventHandler<int> DeleteRequested;

        public PdfPageTile()
        {
            InitializeComponent();
        }

        public void SetThumbnail(BitmapImage bitmap)
        {
            PageThumbnail.Source = bitmap;
        }

        public void SetPageInfo(int pageNum, string sourceLabel, int rotation)
        {
            PageNumText.Text = pageNum.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SourceLabelText.Text = sourceLabel;
            Rotation = rotation;
            PageThumbnail.LayoutTransform = new System.Windows.Media.RotateTransform(rotation);
        }

        private void RotateBtn_Click(object sender, RoutedEventArgs e)
        {
            Rotation = (Rotation + 90) % 360;
            PageThumbnail.LayoutTransform = new System.Windows.Media.RotateTransform(Rotation);
            RotateRequested?.Invoke(this, PageIndex);
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            DeleteRequested?.Invoke(this, PageIndex);
        }
    }
}
