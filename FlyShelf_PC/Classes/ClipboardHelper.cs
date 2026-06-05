using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public static class ClipboardHelper
    {
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
                            System.Threading.Thread.Sleep(15);
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
                            System.Threading.Thread.Sleep(15);
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
                            System.Threading.Thread.Sleep(15);
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
                            System.Threading.Thread.Sleep(15);
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
