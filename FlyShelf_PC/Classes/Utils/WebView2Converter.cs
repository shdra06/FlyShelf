using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FlyShelf.Classes
{
    public static class WebView2Converter
    {
        /// <summary>
        /// Converts Markdown content to a beautifully formatted PDF using an offscreen Edge WebView2.
        /// Runs entirely on the WPF UI thread but does not block since WebView2 rendering is out-of-process.
        /// </summary>
        public static async Task<bool> ConvertMarkdownToPdfAsync(string markdownContent, string outputPath)
        {
            var tcs = new TaskCompletionSource<bool>();

            // Ensure execution on the WPF UI thread
            if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                return await System.Windows.Application.Current.Dispatcher.Invoke(async () => 
                    await ConvertMarkdownToPdfAsync(markdownContent, outputPath)
                );
            }

            Window tempWindow = null;
            WebView2 webView = null;
            try
            {
                webView = new WebView2();

                // Create a hidden host window to force HWND allocation and enable background rendering
                tempWindow = new Window
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Opacity = 0,
                    Left = -10000,
                    Top = -10000
                };
                tempWindow.Content = webView;
                tempWindow.Show();

                // Create a isolated temp directory for WebView2 user data to avoid file access permission issues
                string userDataFolder = Path.Combine(Path.GetTempPath(), "FlyShelf_WebView2_" + Guid.NewGuid().ToString().Substring(0, 8));
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                // Configure offscreen WebView settings
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                // Listen for completion signal from JS in the template
                webView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        string msg = e.TryGetWebMessageAsString();
                        if (msg == "RENDER_COMPLETE")
                        {
                            tcs.TrySetResult(true);
                        }
                        else if (msg != null && msg.StartsWith("RENDER_ERROR:"))
                        {
                            tcs.TrySetException(new Exception(msg.Substring(13)));
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                };

                // Compile markdown + JS libraries + CSS into styled HTML
                string html = MarkdownTemplate.GetHtml(markdownContent);

                // Load the HTML content
                webView.NavigateToString(html);

                // Set a 15-second timeout for the rendering process
                var timeoutTask = Task.Delay(15000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException("Markdown rendering in WebView2 timed out.");
                }

                // If exception was thrown inside the webview message handler, rethrow it here
                if (tcs.Task.IsFaulted && tcs.Task.Exception != null)
                {
                    throw tcs.Task.Exception.InnerException ?? tcs.Task.Exception;
                }

                // Generate PDF Print Settings
                var printSettings = webView.CoreWebView2.Environment.CreatePrintSettings();
                printSettings.Orientation = CoreWebView2PrintOrientation.Portrait;
                printSettings.PageWidth = 8.27;  // A4 Width in inches
                printSettings.PageHeight = 11.69; // A4 Height in inches
                printSettings.MarginTop = 0.78;   // 20mm margin
                printSettings.MarginBottom = 0.78;
                printSettings.MarginLeft = 0.59;  // 15mm margin
                printSettings.MarginRight = 0.59;
                printSettings.ShouldPrintBackgrounds = true; // Required for colored code blocks & table rows
                printSettings.ShouldPrintHeaderAndFooter = true;
                printSettings.HeaderTitle = ""; // Empty string suppresses header title
                printSettings.FooterUri = ""; // Empty string suppresses footer URL (avoids showing path)

                // Print the rendered webpage to PDF
                await webView.CoreWebView2.PrintToPdfAsync(outputPath, printSettings);

                // Clean up UserDataFolder after success
                try
                {
                    if (Directory.Exists(userDataFolder))
                    {
                        Directory.Delete(userDataFolder, true);
                    }
                }
                catch { }

                return File.Exists(outputPath);
            }
            catch (Exception ex)
            {
                Logger.LogAction("WEBVIEW2_MD_CONVERT", $"WebView2 conversion failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (webView != null)
                {
                    try { webView.Dispose(); } catch { }
                }
                if (tempWindow != null)
                {
                    try { tempWindow.Close(); } catch { }
                }
            }
        }
    }
}
