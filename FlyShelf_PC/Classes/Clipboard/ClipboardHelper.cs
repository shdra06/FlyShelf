using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public static class ClipboardHelper
    {
        /// <summary>
        /// Custom clipboard format used as a signature to identify clipboard writes from FlyShelf.
        /// The watcher checks for this tag to deduplicate (prevent loops) while still allowing
        /// new content (OCR results, file paths) to appear as clipboard cards.
        /// </summary>
        internal const string FLYSHELF_INTERNAL_FORMAT = "FlyShelf_Internal_v1";

        public static bool SafeSetText(string text, bool suppressEcho = true, int echoDelayMs = 500)
        {
            return ExecuteOnDispatcher(() =>
            {
                if (suppressEcho)
                {
                    FlyShelf.MainWindow.SetWritingClipboard(true);
                }

                bool success = false;
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(text);
                        success = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("CLIPBOARD_ERROR", $"SafeSetText failed (attempt {retry + 1}): {ex.Message}");
                        if (retry < 2)
                        {
                            // M1 FIX: Reduced from 15ms to 5ms. Can't use async here because
                            // callers go through Dispatcher.Invoke which requires synchronous execution.
                            System.Threading.Thread.Sleep(5);
                        }
                    }
                }

                if (suppressEcho)
                {
                    ReleaseEchoGuardWithDelay(echoDelayMs);
                }

                return success;
            });
        }

        /// <summary>
        /// Copies text to clipboard WITHOUT suppressing echo, so the clipboard watcher
        /// will create a new card for this content. Adds a FlyShelf_Internal signature
        /// so the watcher can detect duplicates and prevent loops.
        /// Use for: OCR results, Copy File Path, Copy to Clipboard — content the user
        /// explicitly wants to see in their clipboard history.
        /// </summary>
        public static bool SafeSetTextAllowCapture(string text)
        {
            return ExecuteOnDispatcher(() =>
            {
                bool success = false;
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        var dataObj = new DataObject();
                        dataObj.SetData(DataFormats.Text, text);
                        dataObj.SetData(DataFormats.UnicodeText, text);
                        dataObj.SetData(FLYSHELF_INTERNAL_FORMAT, "1"); // Signature tag
                        System.Windows.Clipboard.SetDataObject(dataObj, true);
                        success = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("CLIPBOARD_ERROR", $"SafeSetTextAllowCapture failed (attempt {retry + 1}): {ex.Message}");
                        if (retry < 2)
                        {
                            // M1 FIX: Reduced from 15ms to 5ms. Can't use async here because
                            // callers go through Dispatcher.Invoke which requires synchronous execution.
                            System.Threading.Thread.Sleep(5);
                        }
                    }
                }
                return success;
            });
        }

        public static bool SafeSetFileDropList(StringCollection files, bool suppressEcho = true, int echoDelayMs = 500)
        {
            return ExecuteOnDispatcher(() =>
            {
                if (suppressEcho)
                {
                    FlyShelf.MainWindow.SetWritingClipboard(true);
                }

                bool success = false;
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetFileDropList(files);
                        success = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("CLIPBOARD_ERROR", $"SafeSetFileDropList failed (attempt {retry + 1}): {ex.Message}");
                        if (retry < 2)
                        {
                            // M1 FIX: Reduced from 15ms to 5ms. Can't use async here because
                            // callers go through Dispatcher.Invoke which requires synchronous execution.
                            System.Threading.Thread.Sleep(5);
                        }
                    }
                }

                if (suppressEcho)
                {
                    ReleaseEchoGuardWithDelay(echoDelayMs);
                }

                return success;
            });
        }

        public static bool SafeSetDataObject(object data, bool copy, bool suppressEcho = true, int echoDelayMs = 500)
        {
            return ExecuteOnDispatcher(() =>
            {
                if (suppressEcho)
                {
                    FlyShelf.MainWindow.SetWritingClipboard(true);
                }

                bool success = false;
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetDataObject(data, copy);
                        success = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("CLIPBOARD_ERROR", $"SafeSetDataObject failed (attempt {retry + 1}): {ex.Message}");
                        if (retry < 2)
                        {
                            // M1 FIX: Reduced from 15ms to 5ms. Can't use async here because
                            // callers go through Dispatcher.Invoke which requires synchronous execution.
                            System.Threading.Thread.Sleep(5);
                        }
                    }
                }

                if (suppressEcho)
                {
                    ReleaseEchoGuardWithDelay(echoDelayMs);
                }

                return success;
            });
        }

        public static bool SafeSetImage(BitmapSource image, bool suppressEcho = true, int echoDelayMs = 500)
        {
            return ExecuteOnDispatcher(() =>
            {
                if (suppressEcho)
                {
                    FlyShelf.MainWindow.SetWritingClipboard(true);
                }

                bool success = false;
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetImage(image);
                        success = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("CLIPBOARD_ERROR", $"SafeSetImage failed (attempt {retry + 1}): {ex.Message}");
                        if (retry < 2)
                        {
                            // M1 FIX: Reduced from 15ms to 5ms. Can't use async here because
                            // callers go through Dispatcher.Invoke which requires synchronous execution.
                            System.Threading.Thread.Sleep(5);
                        }
                    }
                }

                if (suppressEcho)
                {
                    ReleaseEchoGuardWithDelay(echoDelayMs);
                }

                return success;
            });
        }

        private static T ExecuteOnDispatcher<T>(Func<T> action)
        {
            var app = Application.Current;
            if (app == null)
            {
                return action();
            }

            if (app.Dispatcher.CheckAccess())
            {
                return action();
            }
            else
            {
                return app.Dispatcher.Invoke(action);
            }
        }

        private static void ReleaseEchoGuardWithDelay(int delayMs)
        {
            if (delayMs <= 0)
            {
                FlyShelf.MainWindow.SetWritingClipboard(false);
            }
            else
            {
                Task.Delay(delayMs).ContinueWith(_ => FlyShelf.MainWindow.SetWritingClipboard(false));
            }
        }
    }
}
