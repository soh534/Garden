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
            public int? clickOffsetX { get; set; } = null;
            public int? clickOffsetY { get; set; } = null;
            public List<ReadArea> readAreas { get; set; } = new();
            public bool fixedLocation { get; set; } = false;

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
        private bool _pendingFixed = false;
        private enum CaptureMode { None, ClickPoint, BoundingBox }
        private volatile CaptureMode _captureMode = CaptureMode.None;
        private volatile bool _captureReady = false;
        private Mat? _currentFrame = null;
        private readonly object _currentFrameLock = new();

        private record RoiCapture(Mat RoiMat, Mat FullFrame, int X, int Y, int Width, int Height, string? PendingName, bool Fixed);
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
                            PromptAndSaveRoi(capture.RoiMat, capture.FullFrame, capture.X, capture.Y, capture.Width, capture.Height, capture.PendingName, capture.Fixed, _restartCts.Token);
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

        public void StartRecording(string? name = null, bool fixedLocation = false)
        {
            if (!_isRecording)
            {
                _pendingName = name;
                _pendingFixed = fixedLocation;
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
            lock (_currentFrameLock)
            {
                _currentFrame?.Dispose();
                _currentFrame = frame.Clone();
            }
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
                        var pendingName = _pendingName;
                        var fullFrameSnapshot = _currentFrame.Clone();
                        var capture = new RoiCapture(roiMat, fullFrameSnapshot, fx, fy, fw, fh, pendingName, _pendingFixed);

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

        private void PromptAndSaveRoi(Mat roiMat, Mat fullFrame, int x, int y, int width, int height, string? pendingName, bool fixedLocation, CancellationToken restartToken)
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
            Console.WriteLine($"ROI saved to {filePath}");
            Directory.CreateDirectory(_imageSaveDirectory);
            string framePath = Path.Combine(_imageSaveDirectory, $"frame_{roiName}.png");
            Cv2.ImWrite(framePath, fullFrame);
            Console.WriteLine($"Frame saved to {framePath}");

            roiMat.Dispose();
            fullFrame.Dispose();
            SaveRoiData(roiName, clickOffsetX, clickOffsetY, readAreas, x, y, width, height, fixedLocation);
        }

        private void SaveRoiData(string roiName, int? clickOffsetX, int? clickOffsetY, List<RoiData.ReadArea> readAreas, int x, int y, int width, int height, bool fixedLocation)
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

            bool existed = savedRoiData.ContainsKey(roiName);
            savedRoiData[roiName] = new RoiData
            {
                clickOffsetX = clickOffsetX,
                clickOffsetY = clickOffsetY,
                readAreas = readAreas,
                x = x,
                y = y,
                width = width,
                height = height,
                fixedLocation = fixedLocation
            };
            Console.WriteLine(existed ? $"Overwriting existing ROI: {roiName}" : $"Saved new ROI: {roiName}");

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

        public void RenameRoi(string oldName, string newName)
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
                if (!savedRoiData.ContainsKey(oldName))
                {
                    Console.WriteLine($"ROI '{oldName}' not found in metadata");
                    return;
                }
                if (savedRoiData.ContainsKey(newName))
                {
                    Console.WriteLine($"ROI '{newName}' already exists; choose another name");
                    return;
                }
                savedRoiData[newName] = savedRoiData[oldName];
                savedRoiData.Remove(oldName);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(roiDataPath, JsonSerializer.Serialize(savedRoiData, options));

                string oldImg = Path.Combine(_saveDirectory, $"{oldName}.png");
                string newImg = Path.Combine(_saveDirectory, $"{newName}.png");
                if (File.Exists(oldImg)) { File.Move(oldImg, newImg); }
                string oldFrame = Path.Combine(_imageSaveDirectory, $"frame_{oldName}.png");
                string newFrame = Path.Combine(_imageSaveDirectory, $"frame_{newName}.png");
                if (File.Exists(oldFrame)) { File.Move(oldFrame, newFrame); }
                Console.WriteLine($"Renamed ROI '{oldName}' -> '{newName}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error renaming ROI: {ex.Message}");
                throw;
            }
        }

        // ---- editing already-saved ROIs in place. Each patches roi_metadata.json;
        // the detector's FileSystemWatcher reloads it live. ----

        private bool LoadRoi(string roiName, out SavedRoiData saved, out RoiData roi)
        {
            saved = new();
            roi = null!;
            string path = Path.Combine(_saveDirectory, "roi_metadata.json");
            if (!File.Exists(path)) { Console.WriteLine("roi_metadata.json not found"); return false; }
            saved = JsonSerializer.Deserialize<SavedRoiData>(File.ReadAllText(path)) ?? new();
            if (!saved.TryGetValue(roiName, out roi!)) { Console.WriteLine($"ROI '{roiName}' not found in metadata"); return false; }
            return true;
        }

        private void SaveRois(SavedRoiData saved)
        {
            string path = Path.Combine(_saveDirectory, "roi_metadata.json");
            File.WriteAllText(path, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Metadata updated in {path}");
        }

        public void ToggleFixed(string roiName)
        {
            if (!LoadRoi(roiName, out var saved, out var roi)) { return; }
            roi.fixedLocation = !roi.fixedLocation;
            SaveRois(saved);
            Console.WriteLine($"'{roiName}' fixedLocation = {roi.fixedLocation}");
        }

        public void RemoveReadArea(string roiName, string areaName)
        {
            if (!LoadRoi(roiName, out var saved, out var roi)) { return; }
            if (roi.readAreas.RemoveAll(a => a.name == areaName) == 0)
            {
                Console.WriteLine($"read area '{areaName}' not found on '{roiName}'");
                return;
            }
            SaveRois(saved);
            Console.WriteLine($"removed read area '{areaName}' from '{roiName}'");
        }

        // Capture-based edits run on their own thread: enable the mouse hooks
        // (base.StartRecording), capture a click/drag with the same WaitForCapture
        // the recorder uses, patch the one field, then stop.
        public void EditClickPoint(string roiName)
        {
            if (_isRecording || _isPrompting) { Console.WriteLine("Finish the current recording/edit first."); return; }
            if (!LoadRoi(roiName, out var saved, out var roi)) { return; }
            int boxX = roi.x, boxY = roi.y;
            base.StartRecording();   // install the WH_MOUSE_LL hook on THIS (message-pump) thread
            Console.WriteLine($"Click the new target point for '{roiName}' on the frame...");
            new Thread(() =>
            {
                using var cts = new CancellationTokenSource();
                try
                {
                    var (sx, sy, _, _) = WaitForCapture(CaptureMode.ClickPoint, cts.Token);
                    var (fx, fy) = ScaleToFrame(sx, sy);
                    roi.clickOffsetX = fx - boxX;
                    roi.clickOffsetY = fy - boxY;
                    SaveRois(saved);
                    Console.WriteLine($"'{roiName}' click point set at offset ({roi.clickOffsetX}, {roi.clickOffsetY})");
                }
                catch (Exception ex) { Console.WriteLine($"clickpoint edit failed: {ex.Message}"); }
                finally { StopRecording(); }
            }) { IsBackground = true, Name = "RoiRecorder.EditClickPoint" }.Start();
        }

        public void AddReadArea(string roiName)
        {
            if (_isRecording || _isPrompting) { Console.WriteLine("Finish the current recording/edit first."); return; }
            if (!LoadRoi(roiName, out var saved, out var roi)) { return; }
            int boxX = roi.x, boxY = roi.y;
            base.StartRecording();   // install the WH_MOUSE_LL hook on THIS (message-pump) thread
            Console.WriteLine($"Drag the read-area box for '{roiName}' on the frame...");
            new Thread(() =>
            {
                using var cts = new CancellationTokenSource();
                try
                {
                    var (sx, sy, ex, ey) = WaitForCapture(CaptureMode.BoundingBox, cts.Token);
                    var (fx1, fy1) = ScaleToFrame(Math.Min(sx, ex), Math.Min(sy, ey));
                    var (fx2, fy2) = ScaleToFrame(Math.Max(sx, ex), Math.Max(sy, ey));
                    int rx = fx1 - boxX, ry = fy1 - boxY, rw = fx2 - fx1, rh = fy2 - fy1;
                    if (rw <= 0 || rh <= 0) { Console.WriteLine("read area too small; aborted"); return; }

                    _isPrompting = true;
                    Console.Write("Enter read area name: ");
                    string name = _promptInput.Take(cts.Token);
                    _isPrompting = false;
                    if (string.IsNullOrWhiteSpace(name)) { Console.WriteLine("empty name; aborted"); return; }

                    roi.readAreas.Add(new RoiData.ReadArea { name = name, x = rx, y = ry, width = rw, height = rh });
                    SaveRois(saved);
                    Console.WriteLine($"added read area '{name}' to '{roiName}' at offset ({rx},{ry}) [{rw}x{rh}]");
                }
                catch (Exception ex) { Console.WriteLine($"readarea add failed: {ex.Message}"); }
                finally { _isPrompting = false; StopRecording(); }
            }) { IsBackground = true, Name = "RoiRecorder.AddReadArea" }.Start();
        }

        // Re-draw the box + re-crop the template; recompute clickpoint/read-area
        // offsets against the new box origin so they keep their absolute position.
        public void EditBox(string roiName)
        {
            if (_isRecording || _isPrompting) { Console.WriteLine("Finish the current recording/edit first."); return; }
            if (!LoadRoi(roiName, out var saved, out var roi)) { return; }
            int oldX = roi.x, oldY = roi.y;
            base.StartRecording();   // install the WH_MOUSE_LL hook on THIS (message-pump) thread
            Console.WriteLine($"Drag the new bounding box for '{roiName}' on the frame...");
            new Thread(() =>
            {
                using var cts = new CancellationTokenSource();
                try
                {
                    var (sx, sy, ex, ey) = WaitForCapture(CaptureMode.BoundingBox, cts.Token);
                    var (fx1, fy1) = ScaleToFrame(Math.Min(sx, ex), Math.Min(sy, ey));
                    var (fx2, fy2) = ScaleToFrame(Math.Max(sx, ex), Math.Max(sy, ey));
                    int nx = fx1, ny = fy1, nw = fx2 - fx1, nh = fy2 - fy1;

                    Mat? frame;
                    lock (_currentFrameLock) { frame = _currentFrame?.Clone(); }
                    if (frame == null) { Console.WriteLine("no frame available; aborted"); return; }
                    using (frame)
                    {
                        nx = Math.Max(0, Math.Min(nx, frame.Width - 1));
                        ny = Math.Max(0, Math.Min(ny, frame.Height - 1));
                        nw = Math.Min(nw, frame.Width - nx);
                        nh = Math.Min(nh, frame.Height - ny);
                        if (nw <= 0 || nh <= 0) { Console.WriteLine("box too small / out of bounds; aborted"); return; }
                        using var sub = new Mat(frame, new Rect(nx, ny, nw, nh));
                        Cv2.ImWrite(Path.Combine(_saveDirectory, $"{roiName}.png"), sub);
                    }

                    // keep clickpoint + read areas at the same absolute position
                    if (roi.clickOffsetX.HasValue) { roi.clickOffsetX = (oldX + roi.clickOffsetX.Value) - nx; }
                    if (roi.clickOffsetY.HasValue) { roi.clickOffsetY = (oldY + roi.clickOffsetY.Value) - ny; }
                    foreach (var ra in roi.readAreas)
                    {
                        ra.x = (oldX + ra.x) - nx;
                        ra.y = (oldY + ra.y) - ny;
                    }
                    roi.x = nx; roi.y = ny; roi.width = nw; roi.height = nh;
                    SaveRois(saved);
                    Console.WriteLine($"'{roiName}' box redrawn to ({nx},{ny}) [{nw}x{nh}]; template re-cropped");
                }
                catch (Exception ex) { Console.WriteLine($"box edit failed: {ex.Message}"); }
                finally { StopRecording(); }
            }) { IsBackground = true, Name = "RoiRecorder.EditBox" }.Start();
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
                    Console.WriteLine($"  {name} [{roi.width}x{roi.height}]{(roi.fixedLocation ? " [fixed]" : "")}{(roi.clickOffsetX.HasValue ? $" [click@{roi.clickOffsetX},{roi.clickOffsetY}]" : "")}");
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
