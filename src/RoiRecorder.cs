using OpenCvSharp;
using System.Collections.Concurrent;
using System.Text.Json;
using SavedRoiData = System.Collections.Generic.Dictionary<string, Garden.RoiRecorder.RoiData>;

namespace Garden
{
    public class RoiRecorder : Recorder
    {
        public class RoiData
        {
            public int x { get; set; }
            public int y { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public int frameWidth { get; set; }
            public int frameHeight { get; set; }
            public string roiType { get; set; } = "template";
            public int? clickOffsetX { get; set; } = null;
            public int? clickOffsetY { get; set; } = null;
            public List<ReadArea> readAreas { get; set; } = new();

            public class ReadArea
            {
                public string name { get; set; } = "";
                public int x { get; set; }
                public int y { get; set; }
                public int width { get; set; }
                public int height { get; set; }
            }
        }

        private readonly string _saveDirectory;
        private readonly ConcurrentQueue<string> _commandQueue;
        private int _startX, _startY;
        private int _currentX, _currentY;
        private bool _hasStartPoint = false;
        private bool _isWaitingForInput = false;
        private enum CaptureMode { None, ClickPoint, BoundingBox }
        private volatile CaptureMode _captureMode = CaptureMode.None;
        private volatile bool _captureReady = false;
        private Mat? _currentFrame = null;

        public bool IsRecording => _isRecording;

        public RoiRecorder(string saveDirectory, ConcurrentQueue<string> commandQueue) : base(WindowType.CapturedFrame)
        {
            _saveDirectory = saveDirectory;
            _commandQueue = commandQueue;
        }

        public void StartRecording()
        {
            if (!_isRecording)
            {
                _hasStartPoint = false;
                base.StartRecording();
                Console.WriteLine("ROI recording started.");
            }
            else
            {
                Console.WriteLine("ROI recording is already active.");
            }
        }

        public override void StopRecording()
        {
            if (_isRecording)
            {
                base.StopRecording();
                _hasStartPoint = false;
                Console.WriteLine("ROI recording stopped.");
            }
        }

        public void SetCurrentFrame(Mat frame)
        {
            _currentFrame?.Dispose();
            _currentFrame = frame.Clone();
        }

        public bool IsWaitingForInput => _isWaitingForInput;

        public Rect? GetCurrentRoi()
        {
            if (!_hasStartPoint || !_isRecording)
                return null;

            int x = Math.Min(_startX, _currentX);
            int y = Math.Min(_startY, _currentY);
            int width = Math.Abs(_currentX - _startX);
            int height = Math.Abs(_currentY - _startY);

            if (width > 0 && height > 0)
                return new Rect(x, y, width, height);

            return null;
        }

        protected override void OnMouseClick(object? sender, MouseEventReporter.MouseEvent e)
        {
            if (!_isRecording || _currentFrame == null) return;

            if (e.IsMouseDown)
            {
                _startX = e.X;
                _startY = e.Y;
                _currentX = e.X;
                _currentY = e.Y;
                _hasStartPoint = true;
                if (_captureMode == CaptureMode.ClickPoint) { _captureReady = true; }
                else if (_captureMode == CaptureMode.None) { Console.WriteLine($"ROI start point: ({_startX}, {_startY})"); }
            }
            else if (_hasStartPoint && _captureMode != CaptureMode.ClickPoint)
            {
                int endX = e.X;
                int endY = e.Y;
                int x = Math.Min(_startX, endX);
                int y = Math.Min(_startY, endY);
                int width = Math.Abs(endX - _startX);
                int height = Math.Abs(endY - _startY);

                if (width > 0 && height > 0)
                {
                    if (_captureMode == CaptureMode.BoundingBox) { _currentX = endX; _currentY = endY; _captureReady = true; }
                    else
                    {
                        Rect roi = new Rect(x, y, width, height);
                        Mat roiMat = new Mat(_currentFrame, roi);
                        int frameWidth = _currentFrame.Width;
                        int frameHeight = _currentFrame.Height;
                        _ = Task.Run(() => PromptAndSaveRoi(roiMat, x, y, width, height, frameWidth, frameHeight));
                    }
                }
                _hasStartPoint = false;
            }
        }

        private (int startX, int startY, int endX, int endY) WaitForCapture(CaptureMode mode)
        {
            _hasStartPoint = false;
            _captureReady = false;
            _captureMode = mode;
            while (!_captureReady) { Thread.Sleep(50); }
            _captureMode = CaptureMode.None;
            return (_startX, _startY, _currentX, _currentY);
        }

        private void PromptAndSaveRoi(Mat roiMat, int x, int y, int width, int height, int frameWidth, int frameHeight)
        {
            Console.Write("Enter ROI name: ");
            _isWaitingForInput = true;

            string? roiName = null;
            while (roiName == null)
            {
                if (_commandQueue.TryDequeue(out var input)) { roiName = input; }
                else { Thread.Sleep(50); }
            }

            if (string.IsNullOrWhiteSpace(roiName))
            {
                _isWaitingForInput = false;
                Console.WriteLine("ROI name cannot be empty. ROI discarded.");
                roiMat.Dispose();
                return;
            }

            Console.Write("Contour detection? (y/n): ");
            string? typeInput = null;
            while (typeInput == null)
            {
                if (_commandQueue.TryDequeue(out var input)) { typeInput = input; }
                else { Thread.Sleep(50); }
            }

            string roiType = typeInput.Trim().ToLower() == "y" ? "contour" : "template";

            Console.Write("Set click point? (y/n): ");
            string? clickPointInput = null;
            while (clickPointInput == null)
            {
                if (_commandQueue.TryDequeue(out var input)) { clickPointInput = input; }
                else { Thread.Sleep(50); }
            }

            _isWaitingForInput = false;

            int? clickOffsetX = null;
            int? clickOffsetY = null;
            if (clickPointInput.Trim().ToLower() == "y")
            {
                Console.WriteLine("Click the target point anywhere on the frame...");
                var (sx, sy, _, _) = WaitForCapture(CaptureMode.ClickPoint);
                clickOffsetX = sx - x;
                clickOffsetY = sy - y;
                Console.WriteLine($"Click point set at offset ({clickOffsetX}, {clickOffsetY})");
            }

            var readAreas = new List<RoiData.ReadArea>();
            _isWaitingForInput = true;
            Console.Write("Add read area? (y/n): ");
            string? addReadAreaInput = null;
            while (addReadAreaInput == null)
            {
                if (_commandQueue.TryDequeue(out var input)) { addReadAreaInput = input; }
                else { Thread.Sleep(50); }
            }

            while (addReadAreaInput?.Trim().ToLower() == "y")
            {
                _isWaitingForInput = false;
                Console.WriteLine("Draw read area on frame (drag to select)...");
                var (raStartX, raStartY, raEndX, raEndY) = WaitForCapture(CaptureMode.BoundingBox);
                int raX = Math.Min(raStartX, raEndX) - x;
                int raY = Math.Min(raStartY, raEndY) - y;
                int raW = Math.Abs(raEndX - raStartX);
                int raH = Math.Abs(raEndY - raStartY);

                _isWaitingForInput = true;
                Console.Write("Enter read area name: ");
                string? readAreaName = null;
                while (readAreaName == null)
                {
                    if (_commandQueue.TryDequeue(out var input)) { readAreaName = input; }
                    else { Thread.Sleep(50); }
                }

                if (!string.IsNullOrWhiteSpace(readAreaName))
                {
                    readAreas.Add(new RoiData.ReadArea
                    {
                        name = readAreaName,
                        x = raX,
                        y = raY,
                        width = raW,
                        height = raH
                    });
                    Console.WriteLine($"Read area '{readAreaName}' added at offset ({raX}, {raY}) [{raW}x{raH}]");
                }
                else
                {
                    Console.WriteLine("Read area discarded (empty name).");
                }

                Console.Write("Add another read area? (y/n): ");
                addReadAreaInput = null;
                while (addReadAreaInput == null)
                {
                    if (_commandQueue.TryDequeue(out var input)) { addReadAreaInput = input; }
                    else { Thread.Sleep(50); }
                }
            }
            _isWaitingForInput = false;

            string filePath = Path.Combine(_saveDirectory, $"{roiName}.png");

            Cv2.ImWrite(filePath, roiMat);
            Console.WriteLine($"ROI saved to {filePath} (type: {roiType})");

            roiMat.Dispose();
            SaveRoiData(roiName, roiType, clickOffsetX, clickOffsetY, readAreas, x, y, width, height, frameWidth, frameHeight);
        }

        private void SaveRoiData(string roiName, string roiType, int? clickOffsetX, int? clickOffsetY, List<RoiData.ReadArea> readAreas, int x, int y, int width, int height, int frameWidth, int frameHeight)
        {
            string roiDataPath = Path.Combine(_saveDirectory, "roi_metadata.json");

            Dictionary<string, RoiData> savedRoiData;
            if (File.Exists(roiDataPath))
            {
                string jsonString = File.ReadAllText(roiDataPath);
                savedRoiData = JsonSerializer.Deserialize<Dictionary<string, RoiData>>(jsonString) ?? new();
            }
            else
            {
                savedRoiData = new();
            }

            savedRoiData[roiName] = new RoiData
            {
                roiType = roiType,
                clickOffsetX = clickOffsetX,
                clickOffsetY = clickOffsetY,
                readAreas = readAreas,
                x = x,
                y = y,
                width = width,
                height = height,
                frameWidth = frameWidth,
                frameHeight = frameHeight
            };
            Console.WriteLine($"Overwriting existing ROI: {roiName}");

            var options = new JsonSerializerOptions { WriteIndented = true };
            string updatedJson = JsonSerializer.Serialize(savedRoiData, options);
            File.WriteAllText(roiDataPath, updatedJson);

            Console.WriteLine($"Metadata updated in {roiDataPath}");
        }

        protected override void OnMouseMove(object? sender, MouseEventReporter.MouseEvent e)
        {
            if (!_isRecording || !_hasStartPoint) return;

            _currentX = e.X;
            _currentY = e.Y;
        }

        public void RemoveRoi(string roiName)
        {
            string roiDataPath = Path.Combine(_saveDirectory, "roi_metadata.json");

            if (!File.Exists(roiDataPath))
            {
                Console.WriteLine($"ROI metadata file not found: {roiDataPath}");
                return;
            }

            try
            {
                string jsonString = File.ReadAllText(roiDataPath);
                var savedRoiData = JsonSerializer.Deserialize<Dictionary<string, RoiData>>(jsonString) ?? new();

                if (savedRoiData.ContainsKey(roiName))
                {
                    savedRoiData.Remove(roiName);

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string updatedJson = JsonSerializer.Serialize(savedRoiData, options);
                    File.WriteAllText(roiDataPath, updatedJson);

                    string imagePath = Path.Combine(_saveDirectory, $"{roiName}.png");
                    if (File.Exists(imagePath)) { File.Delete(imagePath); }
                    Console.WriteLine($"Removed ROI '{roiName}'");
                }
                else
                {
                    Console.WriteLine($"ROI '{roiName}' not found in metadata");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error removing ROI: {ex.Message}");
                throw;
            }
        }

        public void ListRois()
        {
            string roiDataPath = Path.Combine(_saveDirectory, "roi_metadata.json");

            if (!File.Exists(roiDataPath))
            {
                Console.WriteLine("No ROIs found (metadata file doesn't exist)");
                return;
            }

            try
            {
                string jsonString = File.ReadAllText(roiDataPath);
                var savedRoiData = JsonSerializer.Deserialize<Dictionary<string, RoiData>>(jsonString) ?? new();

                if (savedRoiData.Count == 0)
                {
                    Console.WriteLine("No ROIs found");
                    return;
                }

                Console.WriteLine($"\nAvailable ROIs ({savedRoiData.Count}):");
                Console.WriteLine("==========================================");
                foreach (var (name, roi) in savedRoiData)
                {
                    Console.WriteLine($"  {name} [{roi.width}x{roi.height}] ({roi.roiType}){(roi.clickOffsetX.HasValue ? $" [click@{roi.clickOffsetX},{roi.clickOffsetY}]" : "")}");
                }
                Console.WriteLine("==========================================\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing ROIs: {ex.Message}");
                throw;
            }
        }
    }
}
