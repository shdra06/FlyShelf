// ---------------------------------------------------------------
// ModernOcrEngine — Windows AI TextRecognizer (NPU-accelerated OCR)
//
// Attempts to use the modern Microsoft.Windows.AI.Imaging.TextRecognizer
// API that ships with Windows 11 24H2+ (Windows App SDK runtime).
// This engine offers dramatically better accuracy and speed than the
// legacy Windows.Media.Ocr.OcrEngine, especially on:
//   - Rotated/skewed text
//   - Colored/complex backgrounds
//   - Small or low-contrast text
//   - Multi-language content
//
// Uses WinRT COM activation via P/Invoke (RoGetActivationFactory)
// to avoid requiring any NuGet package references. All API calls
// go through dynamic/reflection to handle runtime absence gracefully.
//
// Fallback: Returns null when the API is unavailable, so callers
// can fall through to the legacy Windows.Media.Ocr pipeline.
//
// Used by: ClipboardItem.Ocr.cs (ExtractText, ScanForOcrTextAsync)
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Result from the modern OCR engine for a single recognized word.
    /// </summary>
    public struct OcrWordResult
    {
        public string Text;
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public double Confidence;
    }

    /// <summary>
    /// Wrapper around Microsoft.Windows.AI.Imaging.TextRecognizer using
    /// raw WinRT COM activation. No compile-time dependency on Windows App SDK.
    /// </summary>
    public static class ModernOcrEngine
    {
        // ═══════════════════════════════════════════════════════════════
        //  P/Invoke declarations for WinRT COM activation
        // ═══════════════════════════════════════════════════════════════

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void RoGetActivationFactory(
            [MarshalAs(UnmanagedType.HString)] string activatableClassId,
            [In] ref Guid iid,
            out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void RoActivateInstance(
            [MarshalAs(UnmanagedType.HString)] string activatableClassId,
            out IntPtr instance);

        // IInspectable IID — base interface for all WinRT objects
        private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");

        // ═══════════════════════════════════════════════════════════════
        //  Availability check (cached)
        // ═══════════════════════════════════════════════════════════════

        private static bool? _isAvailable;
        private static readonly object _lock = new();

        /// <summary>
        /// Whether the modern TextRecognizer API is available on this system.
        /// Checked once at first access and cached for the process lifetime.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable == null)
                {
                    lock (_lock)
                    {
                        if (_isAvailable == null)
                            _isAvailable = CheckAvailability();
                    }
                }
                return _isAvailable.Value;
            }
        }

        /// <summary>
        /// Probes for the modern TextRecognizer API by attempting WinRT activation.
        /// Returns false if the API doesn't exist or the model isn't ready.
        /// </summary>
        private static bool CheckAvailability()
        {
            try
            {
                // Step 1: Check if the WinRT type is registered on this OS
                // ApiInformation.IsTypePresent works for both in-box and framework WinRT types
                bool typePresent = global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent(
                    "Microsoft.Windows.AI.Imaging.TextRecognizer");

                if (!typePresent)
                {
                    Logger.LogAction("MODERN_OCR", "TextRecognizer type not present on this system");
                    return false;
                }

                // Step 2: Try to get the activation factory — this confirms the runtime is installed
                IntPtr factoryPtr = IntPtr.Zero;
                try
                {
                    var iid = IID_IInspectable;
                    RoGetActivationFactory("Microsoft.Windows.AI.Imaging.TextRecognizer", ref iid, out factoryPtr);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("MODERN_OCR", $"Activation factory unavailable: {ex.Message}");
                    return false;
                }
                finally
                {
                    if (factoryPtr != IntPtr.Zero)
                        Marshal.Release(factoryPtr);
                }

                Logger.LogAction("MODERN_OCR", "TextRecognizer API is available on this system ✓");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("MODERN_OCR", $"Availability check failed: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Core recognition method
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Recognizes text from a SoftwareBitmap using the modern AI TextRecognizer.
        /// Returns null if the API is unavailable or recognition fails, allowing
        /// the caller to fall back to the legacy Windows.Media.Ocr pipeline.
        /// 
        /// The bitmap should be in Bgra8/Premultiplied format (same as legacy OCR).
        /// </summary>
        #pragma warning disable CS1998 // Early-return path before await
        public static async Task<(string text, List<OcrWordResult> words)?> RecognizeAsync(
            SoftwareBitmap bitmap)
        {
            if (!IsAvailable)
                return null;

            try
            {
                return await Task.Run(() => RecognizeCore(bitmap));
            }
            catch (Exception ex)
            {
                Logger.LogAction("MODERN_OCR", $"Recognition failed, falling back to legacy: {ex.Message}");
                return null;
            }
        }
        #pragma warning restore CS1998

        /// <summary>
        /// Internal core that performs the actual WinRT COM calls.
        /// All API interaction goes through dynamic dispatch to avoid compile-time deps.
        /// </summary>
        private static (string text, List<OcrWordResult> words)? RecognizeCore(SoftwareBitmap bitmap)
        {
            // ── Step 1: Check model readiness via static method ──
            // TextRecognizer.GetReadyState() → AIFeatureReadyState enum
            dynamic readyState;
            try
            {
                var factoryObj = ActivateFactory("Microsoft.Windows.AI.Imaging.TextRecognizer");
                if (factoryObj == null)
                {
                    Logger.LogAction("MODERN_OCR", "Failed to activate TextRecognizer factory");
                    return null;
                }

                // GetReadyState is a static method exposed through the factory
                readyState = factoryObj.GetReadyState();
                string stateStr = readyState.ToString();

                if (stateStr != "Ready" && stateStr != "0")
                {
                    // Model not ready — could be NotReady, NotSupportedOnCurrentSystem, etc.
                    Logger.LogAction("MODERN_OCR", $"Model not ready: {stateStr}");

                    // Try EnsureReadyAsync to trigger model download if needed
                    // Use our synchronous WinRT async helper (polls with timeout)
                    try
                    {
                        dynamic ensureOp = factoryObj.EnsureReadyAsync();
                        var ensureResult = AwaitWinRTAsync(ensureOp);

                        if (ensureResult == null)
                        {
                            Logger.LogAction("MODERN_OCR", "EnsureReadyAsync timed out or failed, falling back to legacy");
                            return null;
                        }

                        // Re-check readiness
                        readyState = factoryObj.GetReadyState();
                        stateStr = readyState.ToString();
                        if (stateStr != "Ready" && stateStr != "0")
                        {
                            Logger.LogAction("MODERN_OCR", $"Model still not ready after EnsureReady: {stateStr}");
                            return null;
                        }
                    }
                    catch (Exception ensureEx)
                    {
                        Logger.LogAction("MODERN_OCR", $"EnsureReady failed: {ensureEx.Message}");
                        return null;
                    }
                }
            }
            catch (Exception readyEx)
            {
                Logger.LogAction("MODERN_OCR", $"GetReadyState failed: {readyEx.Message}");
                return null;
            }

            // ── Step 2: Create TextRecognizer instance via CreateAsync ──
            dynamic textRecognizer;
            try
            {
                var factoryObj = ActivateFactory("Microsoft.Windows.AI.Imaging.TextRecognizer");
                dynamic createOp = factoryObj.CreateAsync();
                textRecognizer = AwaitWinRTAsync(createOp);

                if (textRecognizer == null)
                {
                    Logger.LogAction("MODERN_OCR", "CreateAsync returned null");
                    return null;
                }
            }
            catch (Exception createEx)
            {
                Logger.LogAction("MODERN_OCR", $"CreateAsync failed: {createEx.Message}");
                return null;
            }

            // ── Step 3: Create ImageBuffer from SoftwareBitmap ──
            dynamic imageBuffer;
            try
            {
                // ImageBuffer.CreateBufferAttachedToBitmap(bitmap)
                // or ImageBuffer.CreateForSoftwareBitmap(bitmap) depending on SDK version
                var ibFactory = ActivateFactory("Microsoft.Graphics.Imaging.ImageBuffer");
                if (ibFactory == null)
                {
                    Logger.LogAction("MODERN_OCR", "Failed to activate ImageBuffer factory");
                    return null;
                }

                try
                {
                    imageBuffer = ibFactory.CreateBufferAttachedToBitmap(bitmap);
                }
                catch
                {
                    // Try alternate method name used in some SDK versions
                    imageBuffer = ibFactory.CreateForSoftwareBitmap(bitmap);
                }
            }
            catch (Exception ibEx)
            {
                Logger.LogAction("MODERN_OCR", $"ImageBuffer creation failed: {ibEx.Message}");
                return null;
            }

            // ── Step 4: Recognize text ──
            dynamic recognizedText;
            try
            {
                recognizedText = textRecognizer.RecognizeTextFromImage(imageBuffer);
            }
            catch (Exception recEx)
            {
                Logger.LogAction("MODERN_OCR", $"RecognizeTextFromImage failed: {recEx.Message}");
                return null;
            }

            // ── Step 5: Extract results ──
            try
            {
                var words = new List<OcrWordResult>();
                var lines = new List<string>();

                foreach (dynamic line in recognizedText.Lines)
                {
                    string lineText = (string)line.Text;
                    lines.Add(lineText);

                    foreach (dynamic word in line.Words)
                    {
                        string wordText = (string)word.Text;
                        double confidence = 1.0;
                        try { confidence = (double)word.Confidence; } catch { }

                        // BoundingBox is a BoundingBoxWithKeyPoints (polygon)
                        // Extract bounding rectangle from the polygon corners
                        double x = 0, y = 0, w = 0, h = 0;
                        try
                        {
                            dynamic bbox = word.BoundingBox;
                            // The BoundingBox has TopLeft, TopRight, BottomLeft, BottomRight
                            dynamic topLeft = bbox.TopLeft;
                            dynamic bottomRight = bbox.BottomRight;

                            x = (double)topLeft.X;
                            y = (double)topLeft.Y;

                            double x2 = (double)bottomRight.X;
                            double y2 = (double)bottomRight.Y;
                            w = x2 - x;
                            h = y2 - y;
                        }
                        catch
                        {
                            // Some SDK versions may use different property names
                            // Fall back to zero rect
                        }

                        words.Add(new OcrWordResult
                        {
                            Text = wordText,
                            X = x,
                            Y = y,
                            Width = w,
                            Height = h,
                            Confidence = confidence
                        });
                    }
                }

                string fullText = string.Join("\n", lines);

                if (string.IsNullOrWhiteSpace(fullText))
                {
                    Logger.LogAction("MODERN_OCR", "Recognition returned empty text");
                    return null;
                }

                Logger.LogAction("MODERN_OCR",
                    $"Successfully recognized {words.Count} words, {lines.Count} lines via AI TextRecognizer ✓");

                return (fullText, words);
            }
            catch (Exception extractEx)
            {
                Logger.LogAction("MODERN_OCR", $"Result extraction failed: {extractEx.Message}");
                return null;
            }
            finally
            {
                // Dispose the recognizer if it implements IDisposable
                try
                {
                    if (textRecognizer is IDisposable disposable)
                        disposable.Dispose();
                }
                catch { }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  WinRT COM helpers
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Activates a WinRT factory (static class) by its runtime class name.
        /// Returns a dynamic wrapper around the COM object, or null on failure.
        /// </summary>
        private static dynamic ActivateFactory(string runtimeClassName)
        {
            try
            {
                var iid = IID_IInspectable;
                RoGetActivationFactory(runtimeClassName, ref iid, out IntPtr factoryPtr);

                if (factoryPtr == IntPtr.Zero)
                    return null;

                var obj = Marshal.GetObjectForIUnknown(factoryPtr);
                Marshal.Release(factoryPtr);
                return obj;
            }
            catch (Exception ex)
            {
                Logger.LogAction("MODERN_OCR", $"Factory activation failed for {runtimeClassName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Synchronously waits for a WinRT IAsyncOperation and returns the result.
        /// Used for CreateAsync and other async factory methods.
        /// </summary>
        private static dynamic AwaitWinRTAsync(dynamic asyncOp)
        {
            // WinRT async operations implement IAsyncInfo
            // Poll until completed (with timeout)
            var startTime = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(30);

            while (true)
            {
                try
                {
                    // Check Status property: 0=Started, 1=Completed, 2=Error, 3=Canceled
                    var status = asyncOp.Status;
                    int statusInt = (int)status;

                    if (statusInt == 1) // Completed
                    {
                        return asyncOp.GetResults();
                    }
                    else if (statusInt >= 2) // Error or Canceled
                    {
                        try
                        {
                            var error = asyncOp.ErrorCode;
                            Logger.LogAction("MODERN_OCR", $"Async operation failed: status={statusInt}, error={error}");
                        }
                        catch { }
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("MODERN_OCR", $"Async poll error: {ex.Message}");
                    return null;
                }

                if (DateTime.UtcNow - startTime > timeout)
                {
                    Logger.LogAction("MODERN_OCR", "Async operation timed out after 30s");
                    return null;
                }

                System.Threading.Thread.Sleep(50);
            }
        }
    }
}
