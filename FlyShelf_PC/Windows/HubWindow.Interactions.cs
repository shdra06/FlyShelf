using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.ViewModels;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild && tChild.Name == name) return tChild;
                T? deeper = FindVisualChild<T>(child, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        internal void OpenExplorer_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                _viewModel.OpenFileLocationCommand.Execute(item);
                e.Handled = true;
            }
        }

        internal void ConvertToZip_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                if (item.HasZipArchive)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("📦 Zip already exists!");
                    return;
                }
                FlyShelf.Windows.ToastWindow.ShowToast("📦 Creating zip archive...");
                item.CreateZipArchive();
            }
        }

        internal void SyncZipLan_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                if (!item.HasZipArchive)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Create a zip first!");
                    return;
                }
                _ = item.SyncZipViaLanAsync();
            }
        }

        internal void SanitizeUrlSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                if (item.ItemType == FlyShelf.ViewModels.ClipboardItemType.Url && !string.IsNullOrEmpty(item.RawContent))
                {
                    try
                    {
                        string original = item.RawContent;
                        var rxUtmClean = new System.Text.RegularExpressions.Regex(
                            @"(?<=&|\?)(utm_source|utm_medium|utm_campaign|utm_term|utm_content|gclid|fbclid|_gl|msclkid|mc_eid|ig_shid)=[^&]*&?", 
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        
                        string cleanUrl = rxUtmClean.Replace(original, string.Empty).TrimEnd('?', '&');
                        if (cleanUrl != original)
                        {
                            item.RawContent = cleanUrl;
                            item.FileName = cleanUrl;
                            
                            FlyShelf.Classes.ClipboardHelper.SafeSetText(cleanUrl, suppressEcho: true, echoDelayMs: 500);
                            _viewModel.PersistHistoryPublic();
                            FlyShelf.Windows.ToastWindow.ShowToast("URL Sanitized & Copied! 🛡️");
                            FlyShelf.Classes.Logger.LogAction("URL_SANITY", $"Successfully stripped tracking metrics from URL. Result: {cleanUrl}");
                        }
                        else
                        {
                            FlyShelf.Windows.ToastWindow.ShowToast("URL is already clean! ✨");
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("URL_SANITY_ERR", $"Sanitization failed: {ex.Message}");
                    }
                }
            }
        }

        internal void MakePasswordSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                item.IsPassword = true;
                item.Extension = "PASSWORD";
                if (string.IsNullOrEmpty(item.FileName) || item.FileName == item.RawContent)
                {
                    item.FileName = "Protected Password";
                }
                item.GeneratePasswordIcon();
                FlyShelf.Windows.ToastWindow.ShowToast("Locked as password card! 🔒");

                _viewModel.PersistHistoryPublic();
            }
        }

        internal void RenamePasswordSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                FlyShelf.Windows.ToastWindow.ShowToast("Please use the main window to rename passwords for now.");
            }
        }

        internal void SmartActionSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                if (item.SmartActionType == "CompileAndRun")
                {
                    item.CompileAndRunNative();
                }
                else if (item.SmartActionType == "OpenPDF" || item.SmartActionType == "JoinMeeting" || item.SmartActionType == "OpenBrowser")
                {
                    string target = item.SmartActionType == "OpenPDF" ? item.FilePath : item.RawContent;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { } // Best-effort: failure is acceptable
                }
                else if (item.SmartActionType == "OpenMap")
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "bingmaps:?q=" + Uri.EscapeDataString(item.RawContent), UseShellExecute = true }); } catch { } // Best-effort: failure is acceptable
                }
            }
        }

        internal void RunTerminalSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                if (item.ItemType == ClipboardItemType.Code)
                {
                    item.RunInTerminal();
                }
                e.Handled = true;
            }
        }

        internal async void RotateImageSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                if (string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

                System.Windows.Controls.Image targetImage = null;
                try
                {
                    string filePath = item.FilePath;

                    targetImage = FindVisualChild<System.Windows.Controls.Image>(fe, "ItemIcon");

                    if (targetImage != null)
                    {
                        var rotateTransform = new System.Windows.Media.RotateTransform(0, targetImage.ActualWidth / 2, targetImage.ActualHeight / 2);
                        targetImage.RenderTransform = rotateTransform;
                        var rotateAnim = new System.Windows.Media.Animation.DoubleAnimation
                        {
                            From = 0,
                            To = 90,
                            Duration = TimeSpan.FromMilliseconds(300),
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                        };
                        rotateTransform.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, rotateAnim);
                    }

                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        byte[] fileBytes = System.IO.File.ReadAllBytes(filePath);
                        var original = new System.Windows.Media.Imaging.BitmapImage();
                        using (var ms = new System.IO.MemoryStream(fileBytes))
                        {
                            original.BeginInit();
                            original.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            original.StreamSource = ms;
                            original.EndInit();
                            original.Freeze();
                        }

                        var rotated = new System.Windows.Media.Imaging.TransformedBitmap(original, new System.Windows.Media.RotateTransform(90));
                        rotated.Freeze();

                        string ext = System.IO.Path.GetExtension(filePath).ToLower(CultureInfo.InvariantCulture);
                        System.Windows.Media.Imaging.BitmapEncoder encoder;
                        if (ext == ".png") encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        else if (ext == ".bmp") encoder = new System.Windows.Media.Imaging.BmpBitmapEncoder();
                        else encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };

                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rotated));

                        using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            encoder.Save(fs);
                        }
                    });

                    await System.Threading.Tasks.Task.Delay(320);

                    var freshBitmap = await System.Threading.Tasks.Task.Run(() =>
                    {
                        byte[] freshBytes = System.IO.File.ReadAllBytes(filePath);
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        using (var ms = new System.IO.MemoryStream(freshBytes))
                        {
                            bmp.BeginInit();
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                            bmp.Freeze();
                        }
                        return bmp;
                    });

                    if (targetImage != null)
                        targetImage.RenderTransform = null;

                    item.Icon = freshBitmap;
                    FlyShelf.Classes.Logger.LogAction("ROTATE", "Rotated 90° in-place (Alt UI): " + System.IO.Path.GetFileName(filePath));
                }
                catch (Exception ex)
                {
                    if (targetImage != null)
                        targetImage.RenderTransform = null;
                    FlyShelf.Classes.Logger.LogAction("ROTATE", "Failed: " + ex.Message);
                }
            }
        }

        internal void QuickLookSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                var mainWin = System.Windows.Application.Current.MainWindow as MainWindow;
                mainWin?.ShowQuickLookForItem(item);
                e.Handled = true;
            }
        }
    }
}
