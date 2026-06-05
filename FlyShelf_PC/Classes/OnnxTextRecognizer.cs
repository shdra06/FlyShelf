using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FlyShelf.Classes
{
    public class OnnxTextRecognizer : IDisposable
    {
        private InferenceSession _session;
        private string[] _charset;
        private readonly string _modelPath;
        private readonly string _keysPath;
        private readonly object _lock = new object();

        public bool IsLoaded => _session != null;

        public OnnxTextRecognizer()
        {
            _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "text_recognize.onnx");
            _keysPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "ppocr_keys_v1.txt");
        }

        public void Initialize()
        {
            lock (_lock)
            {
                if (_session != null) return;

                if (!File.Exists(_modelPath))
                    throw new FileNotFoundException($"OCR recognition model not found at: {_modelPath}");
                if (!File.Exists(_keysPath))
                    throw new FileNotFoundException($"Character keys file not found at: {_keysPath}");

                // Load character list
                var lines = File.ReadAllLines(_keysPath);
                _charset = lines;

                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = 2
                };
                
                _session = new InferenceSession(_modelPath, options);
            }
        }

        /// <summary>
        /// Crops a cell region from the file, resizes it, runs OCR inference, and returns the decoded string.
        /// </summary>
        public async Task<string> OCRCellAsync(string filePath, DetectedBox cellBox)
        {
            if (cellBox.Width <= 0 || cellBox.Height <= 0) return "";
            if (_session == null) Initialize();

            // Load, crop, and normalize cell crop to fixed height 48
            var (tensorData, cellW) = await TableImagePreprocessor.PreprocessCellCropAsync(
                filePath, cellBox.X, cellBox.Y, cellBox.Width, cellBox.Height);

            if (tensorData == null) return "";

            var inputs = new List<NamedOnnxValue>
            {
                // Dynamic width batch: x is input shape [1, 3, 48, cellW]
                NamedOnnxValue.CreateFromTensor("x", new DenseTensor<float>(tensorData, new int[] { 1, 3, 48, cellW }))
            };

            lock (_lock)
            {
                using (var outputs = _session.Run(inputs))
                {
                    var resultTensor = outputs.First().AsTensor<float>(); // Shape: [1, time_steps, num_classes]
                    
                    int timeSteps = resultTensor.Dimensions[1];
                    int numClasses = resultTensor.Dimensions[2];

                    float[] logits = resultTensor.ToArray();

                    return CTCGreedyDecode(logits, timeSteps, numClasses);
                }
            }
        }

        /// <summary>
        /// Greedy decoder for CTC sequence outputs.
        /// </summary>
        private string CTCGreedyDecode(float[] logits, int timeSteps, int numClasses)
        {
            var textBuilder = new System.Text.StringBuilder();
            int prevIdx = -1;

            for (int t = 0; t < timeSteps; t++)
            {
                int maxIdx = 0;
                float maxVal = float.MinValue;
                int timeStepOffset = t * numClasses;

                for (int c = 0; c < numClasses; c++)
                {
                    float val = logits[timeStepOffset + c];
                    if (val > maxVal)
                    {
                        maxVal = val;
                        maxIdx = c;
                    }
                }

                // 0 is the blank token in CTC
                if (maxIdx != 0 && maxIdx != prevIdx)
                {
                    // Map index to character list index (shift by -1 because index 0 is blank)
                    int charIdx = maxIdx - 1;
                    if (charIdx < _charset.Length)
                    {
                        textBuilder.Append(_charset[charIdx]);
                    }
                }

                prevIdx = maxIdx;
            }

            return textBuilder.ToString().Trim();
        }

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
