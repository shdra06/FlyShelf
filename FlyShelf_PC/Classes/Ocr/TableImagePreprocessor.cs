using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FlyShelf.Classes
{
    public static class TableImagePreprocessor
    {
        // COM interface to access raw pixel buffer of SoftwareBitmap in .NET
        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }

        /// <summary>
        /// Scales and letterboxes an image stream into a 640x640 RGB float tensor [1, 3, 640, 640].
        /// </summary>
        public static async Task<(float[] Tensor, float Scale, int PadX, int PadY)> PreprocessForYoloAsync(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            using (var randomAccessStream = stream.AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                uint origW = decoder.OrientedPixelWidth;
                uint origH = decoder.OrientedPixelHeight;

                // Calculate letterboxing dimensions
                float scale = Math.Min(640f / origW, 640f / origH);
                int scaledW = (int)Math.Round(origW * scale);
                int scaledH = (int)Math.Round(origH * scale);
                int padX = (640 - scaledW) / 2;
                int padY = (640 - scaledH) / 2;

                // Native WinRT scaling
                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)scaledW,
                    ScaledHeight = (uint)scaledH,
                    InterpolationMode = BitmapInterpolationMode.Linear
                };

                using (var scaledBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb))
                {
                    // Allocate 640x640 NCHW tensor, default filled with 114.0f/255.0f (YOLOv8 gray padding)
                    float[] tensor = new float[1 * 3 * 640 * 640];
                    float padValue = 114f / 255f;
                    Array.Fill(tensor, padValue);

                    using (var buffer = scaledBitmap.LockBuffer(BitmapBufferAccessMode.Read))
                    using (var reference = buffer.CreateReference())
                    {
                        unsafe
                        {
                            var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                            byteAccess.GetBuffer(out byte* dataBytes, out uint capacity);
                            var layout = buffer.GetPlaneDescription(0);

                            for (int y = 0; y < scaledH; y++)
                            {
                                int rowOffset = layout.StartIndex + layout.Stride * y;
                                for (int x = 0; x < scaledW; x++)
                                {
                                    int pxIdx = rowOffset + 4 * x;
                                    byte b = dataBytes[pxIdx + 0];
                                    byte g = dataBytes[pxIdx + 1];
                                    byte r = dataBytes[pxIdx + 2];

                                    int tensorY = padY + y;
                                    int tensorX = padX + x;

                                    // NCHW layout index calculations
                                    int rIdx = (0 * 640 * 640) + (tensorY * 640) + tensorX;
                                    int gIdx = (1 * 640 * 640) + (tensorY * 640) + tensorX;
                                    int bIdx = (2 * 640 * 640) + (tensorY * 640) + tensorX;

                                    tensor[rIdx] = r / 255.0f;
                                    tensor[gIdx] = g / 255.0f;
                                    tensor[bIdx] = b / 255.0f;
                                }
                            }
                        }
                    }
                    return (tensor, scale, padX, padY);
                }
            }
        }

        /// <summary>
        /// Crops a cell region from the original image, resizes it to 48px height, and creates an RGB float tensor [1, 3, 48, W].
        /// </summary>
        public static async Task<(float[] Tensor, int Width)> PreprocessCellCropAsync(string filePath, float origX, float origY, float origW, float origH)
        {
            using (var stream = File.OpenRead(filePath))
            using (var randomAccessStream = stream.AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                uint imageWidth = decoder.OrientedPixelWidth;
                uint imageHeight = decoder.OrientedPixelHeight;

                // Add small pixel padding for cleaner OCR (guarding against boundaries)
                int pad = 2;
                int cropX = Math.Max(0, (int)origX - pad);
                int cropY = Math.Max(0, (int)origY - pad);
                int cropW = Math.Min((int)imageWidth - cropX, (int)origW + (pad * 2));
                int cropH = Math.Min((int)imageHeight - cropY, (int)origH + (pad * 2));

                if (cropW <= 0 || cropH <= 0) return (null, 0);

                // Target size height is fixed at 48px, width scales proportionally
                int targetH = 48;
                int targetW = (int)Math.Max(8, Math.Round(cropW * (48.0 / cropH)));

                // Single-pass WIC Crop + Resize
                var transform = new BitmapTransform
                {
                    Bounds = new BitmapBounds
                    {
                        X = (uint)cropX,
                        Y = (uint)cropY,
                        Width = (uint)cropW,
                        Height = (uint)cropH
                    },
                    ScaledWidth = (uint)targetW,
                    ScaledHeight = (uint)targetH,
                    InterpolationMode = BitmapInterpolationMode.Linear
                };

                using (var croppedBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb))
                {
                    float[] tensor = new float[1 * 3 * targetH * targetW];

                    using (var buffer = croppedBitmap.LockBuffer(BitmapBufferAccessMode.Read))
                    using (var reference = buffer.CreateReference())
                    {
                        unsafe
                        {
                            var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                            byteAccess.GetBuffer(out byte* dataBytes, out uint capacity);
                            var layout = buffer.GetPlaneDescription(0);

                            for (int y = 0; y < targetH; y++)
                            {
                                int rowOffset = layout.StartIndex + layout.Stride * y;
                                for (int x = 0; x < targetW; x++)
                                {
                                    int pxIdx = rowOffset + 4 * x;
                                    byte b = dataBytes[pxIdx + 0];
                                    byte g = dataBytes[pxIdx + 1];
                                    byte r = dataBytes[pxIdx + 2];

                                    int rIdx = (0 * targetH * targetW) + (y * targetW) + x;
                                    int gIdx = (1 * targetH * targetW) + (y * targetW) + x;
                                    int bIdx = (2 * targetH * targetW) + (y * targetW) + x;

                                    // PP-OCRv4 normalization: (px / 255.0 - 0.5) / 0.5 = (px / 127.5) - 1.0
                                    tensor[rIdx] = (r / 127.5f) - 1.0f;
                                    tensor[gIdx] = (g / 127.5f) - 1.0f;
                                    tensor[bIdx] = (b / 127.5f) - 1.0f;
                                }
                            }
                        }
                    }
                    return (tensor, targetW);
                }
            }
        }
    }
}
