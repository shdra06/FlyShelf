using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FlyShelf.Classes
{
    public class OnnxTableDetector : IDisposable
    {
        private InferenceSession _session;
        private readonly string _modelPath;
        private readonly object _lock = new object();

        public bool IsLoaded => _session != null;

        public OnnxTableDetector()
        {
            _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "table_detect.onnx");
        }

        public void Initialize()
        {
            lock (_lock)
            {
                if (_session != null) return;

                if (!File.Exists(_modelPath))
                    throw new FileNotFoundException($"Table detect ONNX model not found at: {_modelPath}");

                // CPU-only optimization options
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = 2 // Restrict core usage to avoid desktop stutter
                };
                
                _session = new InferenceSession(_modelPath, options);
            }
        }

        public async Task<TableGrid> DetectTableStructureAsync(string filePath)
        {
            if (_session == null) Initialize();

            // Load and preprocess image into float array
            var (tensorData, scale, padX, padY) = await TableImagePreprocessor.PreprocessForYoloAsync(filePath);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("images", new DenseTensor<float>(tensorData, new int[] { 1, 3, 640, 640 }))
            };

            List<DetectedBox> candidates = new List<DetectedBox>();
            int features = 8;

            lock (_lock)
            {
                using (var outputs = _session.Run(inputs))
                {
                    var resultTensor = outputs.First().AsTensor<float>(); // Shape: [1, features, 8400]
                    
                    float[] data = resultTensor.ToArray();
                    int numBoxes = resultTensor.Dimensions[2]; // 8400
                    features = resultTensor.Dimensions[1];    // 6 or 8

                    for (int col = 0; col < numBoxes; col++)
                    {
                        float maxConf = 0f;
                        int bestClass = 0;

                        if (features >= 6)
                        {
                            // Class scores start at index 4
                            for (int c = 4; c < features; c++)
                            {
                                float score = data[c * numBoxes + col];
                                if (score > maxConf)
                                {
                                    maxConf = score;
                                    bestClass = c - 4;
                                }
                            }
                        }

                        if (maxConf > 0.40f) // Conf threshold
                        {
                            float cx = data[0 * numBoxes + col];
                            float cy = data[1 * numBoxes + col];
                            float w = data[2 * numBoxes + col];
                            float h = data[3 * numBoxes + col];

                            // Box coordinates on 640x640 canvas
                            float left = cx - w / 2f;
                            float top = cy - h / 2f;

                            // Scale back to original coordinates
                            float origX = (left - padX) / scale;
                            float origY = (top - padY) / scale;
                            float origW = w / scale;
                            float origH = h / scale;

                            candidates.Add(new DetectedBox
                            {
                                X = origX,
                                Y = origY,
                                Width = origW,
                                Height = origH,
                                Confidence = maxConf,
                                ClassId = bestClass
                            });
                        }
                    }
                }
            }

            // Apply Non-Maximum Suppression (NMS) to remove duplicates
            List<DetectedBox> filtered = ApplyNMS(candidates, 0.40f);

            // Separate cell detections (for table boundaries: class 0/1; for structures: class 0/3)
            List<DetectedBox> cells;
            if (features == 6)
            {
                cells = filtered.Where(b => b.ClassId == 0 || b.ClassId == 1).ToList();
            }
            else
            {
                cells = filtered.Where(b => b.ClassId == 0 || b.ClassId == 3).ToList();
            }

            if (cells.Count == 0) return null;

            return ClusterGrid(cells, features);
        }

        private List<DetectedBox> ApplyNMS(List<DetectedBox> boxes, float iouThreshold)
        {
            var sorted = boxes.OrderByDescending(b => b.Confidence).ToList();
            var kept = new List<DetectedBox>();
            var active = new bool[sorted.Count];
            Array.Fill(active, true);

            for (int i = 0; i < sorted.Count; i++)
            {
                if (!active[i]) continue;
                kept.Add(sorted[i]);

                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (!active[j]) continue;

                    // Compute IoU
                    float intersectionX = Math.Max(sorted[i].X, sorted[j].X);
                    float intersectionY = Math.Max(sorted[i].Y, sorted[j].Y);
                    float intersectionW = Math.Max(0, Math.Min(sorted[i].Right, sorted[j].Right) - intersectionX);
                    float intersectionH = Math.Max(0, Math.Min(sorted[i].Bottom, sorted[j].Bottom) - intersectionY);

                    float interArea = intersectionW * intersectionH;
                    float unionArea = (sorted[i].Width * sorted[i].Height) + (sorted[j].Width * sorted[j].Height) - interArea;

                    float iou = interArea / unionArea;
                    if (iou > iouThreshold)
                    {
                        active[j] = false; // Discard overlapping box
                    }
                }
            }

            return kept;
        }

        /// <summary>
        /// Group boxes vertically into rows and horizontally into columns to make a structured table grid.
        /// </summary>
        private TableGrid ClusterGrid(List<DetectedBox> cells, int features)
        {
            var rowsList = new List<List<DetectedBox>>();

            // Sort cells top-to-bottom
            var sortedCells = cells.OrderBy(c => c.CenterY).ToList();

            foreach (var cell in sortedCells)
            {
                bool added = false;
                foreach (var row in rowsList)
                {
                    float rowMinY = row.Min(c => c.Y);
                    float rowMaxBottom = row.Max(c => c.Bottom);
                    float rowH = rowMaxBottom - rowMinY;

                    // Calculate overlap ratio
                    float overlap = Math.Max(0, Math.Min(cell.Bottom, rowMaxBottom) - Math.Max(cell.Y, rowMinY));
                    float minH = Math.Min(cell.Height, rowH);

                    if (minH > 0 && (overlap / minH) > 0.40f) // 40% vertical overlap threshold
                    {
                        row.Add(cell);
                        added = true;
                        break;
                    }
                }

                if (!added)
                {
                    rowsList.Add(new List<DetectedBox> { cell });
                }
            }

            // Order rows top-to-bottom
            rowsList = rowsList.OrderBy(r => r.Average(c => c.CenterY)).ToList();

            // Sort cells inside each row left-to-right
            for (int r = 0; r < rowsList.Count; r++)
            {
                rowsList[r] = rowsList[r].OrderBy(c => c.X).ToList();
            }

            int numRows = rowsList.Count;
            int numCols = rowsList.Max(r => r.Count);

            // Establish column partitions based on the row with the most cells
            var referenceRow = rowsList.OrderByDescending(r => r.Count).First();
            var colSeparators = new List<float>();
            for (int i = 0; i < referenceRow.Count - 1; i++)
            {
                colSeparators.Add((referenceRow[i].Right + referenceRow[i + 1].X) / 2f);
            }

            var grid = new DetectedBox[numRows, numCols];

            // Assign cells to column indices using separators
            for (int r = 0; r < numRows; r++)
            {
                var rowCells = rowsList[r];
                foreach (var cell in rowCells)
                {
                    int colIdx = 0;
                    for (int s = 0; s < colSeparators.Count; s++)
                    {
                        if (cell.CenterX > colSeparators[s])
                        {
                            colIdx = s + 1;
                        }
                        else break;
                    }

                    if (colIdx >= numCols) colIdx = numCols - 1;

                    // Put the box in the grid if empty
                    grid[r, colIdx] = cell;
                }
            }

            // Fill empty cells with placeholder coordinate bounds to avoid null references
            for (int r = 0; r < numRows; r++)
            {
                for (int c = 0; c < numCols; c++)
                {
                    if (grid[r, c] == null)
                    {
                        grid[r, c] = new DetectedBox { X = 0, Y = 0, Width = 0, Height = 0, ClassId = 0, Confidence = 0 };
                    }
                }
            }

            return new TableGrid
            {
                Rows = numRows,
                Cols = numCols,
                Cells = grid,
                ModelFeatures = features
            };
        }

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public class DetectedBox
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float Confidence { get; set; }
        public int ClassId { get; set; }
        
        public float Right => X + Width;
        public float Bottom => Y + Height;
        public float CenterX => X + Width / 2f;
        public float CenterY => Y + Height / 2f;
    }

    public class TableGrid
    {
        public int Rows { get; set; }
        public int Cols { get; set; }
        public DetectedBox[,] Cells { get; set; }
        public int ModelFeatures { get; set; }
    }
}
