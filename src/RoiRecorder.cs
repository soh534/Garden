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
        private readonly string _imageSaveDirectory;
        private readonly BlockingCollection<string> _promptInput = new();
        private readonly CancellationTokenSource _cts = new();
        private int _startX, _startY;
        private int _currentX, _currentY;
        private bool _hasStartPoint = false;
        private volatile bool _isPrompting = false;
        private string? _pendingName = null;
        private enum CaptureMode { None, ClickPoint, BoundingBox }
        private volatile CaptureMode _captureMode = CaptureMode.None;
        private volatile bool _captureReady = false;
        private Mat? _currentFrame = null;

        private record RoiCapture(Mat RoiMat, Mat FullFrame, int X, int Y, int Width, int Height, int FrameWidth, int FrameHeight, string? PendingName);
        private readonly BlockingCollection<RoiCapture> _captureQueue = new(boundedCapacity: 1);
        private RoiCapture? _pendingRestart = null;
        private volatile CancellationTokenSource? _restartCts = null;

        public bool IsRecording => _isRecording;
        public bool IsPrompting => _isPrompting;
        public void FeedInput(string input) => _promptInput.Add(input);

        public RoiRecorder(string saveDirectory, string imageSaveDirectory) : base(WindowType.CapturedFrame)
        {
            _saveDirectory = saveDirectory;
            _imageSaveDirectory = imageSaveDirectory;
            new Thread(WorkerLoop) { IsBackground = true, Name = "RoiRecorder.Worker" }.Start();
        }

        private void WorkerLoop()
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    RoiCapture? capture;
                    try { capture = _captureQueue.Take(_cts.Token); }
                    catch (OperationCanceledException) { break; }

                    while (capture != null && !_cts.Token.IsCancellationRequested)
                    {
                        _restartCts = new CancellationTokenSource();
                        _isPrompting = true;
                        bool restarted = false;
                        try
                        {
                            PromptAndSaveRoi(capture.RoiMat, capture.FullFrame, capture.X, capture.Y, capture.Width, capture.Height, capture.FrameWidth, capture.FrameHeight, capture.PendingName, _restartCts.Token);
                        }
                        catch (OperationCanceledException) when (_restartCts.IsCancellationRequested)
                        {
                            restarted = true;
                        }
                        finally
                        {
                            _isPrompting = false;
                            _restartCts.Dispose();
                            _restartCts = null;
                        }

                        if (restarted)
                        {
                            capture = Interlocked.Exchange(ref _pendingRestart, null);
                            if (capture != null) { Console.WriteLine("Restarting with new selection..."); }
                        }
                        else
                        {
                            if (capture.PendingName != null) { StopRecording(); }
                            capture = null;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void StartRecording(string? name = null)
        {
            if (!_isRecording)
            {
                _pendingName = name;
                _hasStartPoint = false;
                base.StartRecording();
                Console.WriteLine($"ROI recording started{(name != null ? $" (will save as '{name}')" : "")}.");
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

        private (int x, int y) ScaleToFrame(int dispX, int dispY)
        {
            IntPtr hWnd = WindowManager.Instance.GetWindowHandle(WindowType.CapturedFrame);
            Win32Api.GetClientRect(hWnd, out Win32Api.RECT rect);
            return InputManager.DisplayToPhone(dispX, dispY, rect.Right, rect.Bottom);
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
                int width  = Math.Abs(endX - _startX);
                int height = Math.Abs(endY - _startY);

                if (width > 0 && height > 0)
                {
                    if (_captureMode == CaptureMode.BoundingBox) { _currentX = endX; _currentY = endY; _captureReady = true; }
                    else
                    {
                        var (fx,  fy)  = ScaleToFrame(x, y);
                        var (fx2, fy2) = ScaleToFrame(x + width, y + height);
                        int fw = fx2 - fx, fh = fy2 - fy;
                        Rect roi   = new Rect(fx, fy, fw, fh);
                        Mat roiMat = new Mat(_currentFrame, roi);
                        int frameWidth  = _currentFrame.Width;
                        int frameHeight = _currentFrame.Height;
                        var pendingName = _pendingName;
                        var fullFrameSnapshot = _currentFrame.Clone();
                        var capture = new RoiCapture(roiMat, fullFrameSnapshot, fx, fy, fw, fh, frameWidth, frameHeight, pendingName);

                        if (_isPrompting)
                        {
                            var old = Interlocked.Exchange(ref _pendingRestart, capture);
                            old?.RoiMat.Dispose();
                            old?.FullFrame.Dispose();
                            _restartCts?.Cancel();
                            Console.WriteLine("Selection updated, restarting...");
                        }
                        else if (!_captureQueue.TryAdd(capture))
                        {
                            Console.WriteLine("ROI recording busy, finish current ROI first.");
                            roiMat.Dispose();
                            fullFrameSnapshot.Dispose();
                        }
                    }
                }
                _hasStartPoint = false;
            }
        }

        private (int startX, int startY, int endX, int endY) WaitForCapture(CaptureMode mode, CancellationToken restartToken)
        {
            _hasStartPoint = false;
            _captureReady = false;
            _captureMode = mode;
            while (!_captureReady)
            {
                restartToken.ThrowIfCancellationRequested();
                Thread.Sleep(50);
            }
            _captureMode = CaptureMode.None;
            return (_startX, _startY, _currentX, _currentY);
        }

        private void PromptAndSaveRoi(Mat roiMat, Mat fullFrame, int x, int y, int width, int height, int frameWidth, int frameHeight, string? pendingName, CancellationToken restartToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, restartToken);
            CancellationToken token = linked.Token;

            string roiName;
            if (pendingName != null)
            {
                roiName = pendingName;
                Console.WriteLine($"ROI name: {roiName}");
            }
            else
            {
                Console.Write("Enter ROI name: ");
                roiName = _promptInput.Take(token);
            }

            if (string.IsNullOrWhiteSpace(roiName))
            {
                Console.WriteLine("ROI name cannot be empty. ROI discarded.");
                roiMat.Dispose();
                return;
            }

            Console.Write("Contour detection? (y/n): ");
            string typeInput = _promptInput.Take(token);

            string roiType = typeInput.Trim().ToLower() == "y" ? "contour" : "template";

            Console.Write("Set click point? (y/n): ");
            string clickPointInput = _promptInput.Take(token);

            int? clickOffsetX = null;
            int? clickOffsetY = null;
            if (clickPointInput.Trim().ToLower() == "y")
            {
                Console.WriteLine("Click the target point anywhere on the frame...");
                var (sx, sy, _, _) = WaitForCapture(CaptureMode.ClickPoint, restartToken);
                var (fsx, fsy) = ScaleToFrame(sx, sy);
                clickOffsetX = fsx - x;
                clickOffsetY = fsy - y;
                Console.WriteLine($"Click point set at offset ({clickOffsetX}, {clickOffsetY})");
            }

            var readAreas = new List<RoiData.ReadArea>();
            Console.Write("Add read area? (y/n): ");
            string? addReadAreaInput = _promptInput.Take(token);

            while (addReadAreaInput?.Trim().ToLower() == "y")
            {
                Console.WriteLine("Draw read area on frame (drag to select)...");
                var (raStartX, raStartY, raEndX, raEndY) = WaitForCapture(CaptureMode.BoundingBox, restartToken);
                var (fx1, fy1) = ScaleToFrame(Math.Min(raStartX, raEndX), Math.Min(raStartY, raEndY));
                var (fx2, fy2) = ScaleToFrame(Math.Max(raStartX, raEndX), Math.Max(raStartY, raEndY));
                int raX = fx1 - x;
                int raY = fy1 - y;
                int raW = fx2 - fx1;
                int raH = fy2 - fy1;

                Console.Write("Enter read area name: ");
                string? readAreaName = _promptInput.Take(token);

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
                addReadAreaInput = _promptInput.Take(token);
            }

            string filePath = Path.Combine(_saveDirectory, $"{roiName}.png");

            Cv2.ImWrite(filePath, roiMat);
            Console.WriteLine($"ROI saved to {filePath} (type: {roiType})");
            Directory.CreateDirectory(_imageSaveDirectory);
            string framePath = Path.Combine(_imageSaveDirectory, $"frame_{roiName}.png");
            Cv2.ImWrite(framePath, fullFrame);
            Console.WriteLine($"Frame saved to {framePath}");

            roiMat.Dispose();
            fullFrame.Dispose();
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
                    string framePath = Path.Combine(_imageSaveDirectory, $"frame_{roiName}.png");
                    if (File.Exists(framePath)) { File.Delete(framePath); }
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

        public override void Dispose()
        {
            _cts.Cancel();
            _captureQueue.CompleteAdding();
            _promptInput.CompleteAdding();
            base.Dispose();
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
