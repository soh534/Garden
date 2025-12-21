using OpenCvSharp;
using System.Collections.Concurrent;
using System.Text.Json;
using SavedRoiData = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Garden.RoiRecorder.RoiData>>;

namespace Garden
{
    public class RoiRecorder : Recorder
    {
        public class RoiData
        {
            public string name { get; set; } = "";
            public int x { get; set; }
            public int y { get; set; }
            public int width { get; set; }
            public int height { get; set; }
        }

        private readonly string _saveDirectory;
        private readonly ConcurrentQueue<string> _commandQueue;
        private string? _currentStateName = null;
        private int _startX, _startY;
        private int _currentX, _currentY;
        private bool _hasStartPoint = false;
        private bool _isWaitingForInput = false;
        private Mat? _currentFrame = null;

        public bool IsRecording => _isRecording;

        public RoiRecorder(string saveDirectory, ConcurrentQueue<string> commandQueue) : base(WindowType.CapturedFrame)
        {
            _saveDirectory = saveDirectory;
            _commandQueue = commandQueue;
        }

        public void StartRecording(string stateName)
        {
            if (!_isRecording)
            {
                _currentStateName = stateName;
                _hasStartPoint = false;
                base.StartRecording();
                Console.WriteLine($"ROI recording started for state: {stateName}");
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
                _currentStateName = null;
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
            if (!_isRecording || _currentStateName == null || _currentFrame == null) return;

            if (e.IsMouseDown)
            {
                // Store start position
                _startX = e.X;
                _startY = e.Y;
                _currentX = e.X;
                _currentY = e.Y;
                _hasStartPoint = true;
                Console.WriteLine($"ROI start point: ({_startX}, {_startY})");
            }
            else if (_hasStartPoint)
            {
                // Mouse up - calculate ROI rectangle
                int endX = e.X;
                int endY = e.Y;

                int x = Math.Min(_startX, endX);
                int y = Math.Min(_startY, endY);
                int width = Math.Abs(endX - _startX);
                int height = Math.Abs(endY - _startY);

                if (width > 0 && height > 0)
                {
                    // Extract ROI
                    Rect roi = new Rect(x, y, width, height);
                    Mat roiMat = new Mat(_currentFrame, roi);

                    // Process on background thread to avoid blocking hook
                    _ = Task.Run(() => PromptAndSaveRoi(roiMat, _currentStateName, x, y, width, height));
                }

                // Reset for next ROI (stay in recording mode)
                _hasStartPoint = false;
            }
        }

        private void PromptAndSaveRoi(Mat roiMat, string stateName, int x, int y, int width, int height)
        {
            Console.Write("Enter ROI name: ");

            // Signal that we're waiting for input
            _isWaitingForInput = true;

            // Wait for input from command queue
            string? roiName = null;
            while (roiName == null)
            {
                if (_commandQueue.TryDequeue(out var input))
                {
                    roiName = input;
                }
                else
                {
                    Thread.Sleep(50);
                }
            }

            _isWaitingForInput = false;

            if (!string.IsNullOrWhiteSpace(roiName))
            {
                string stateDirectory = Path.Combine(_saveDirectory, stateName);
                Directory.CreateDirectory(stateDirectory);
                string filename = $"{roiName}.png";
                string filePath = Path.Combine(stateDirectory, filename);

                // Save original ROI
                Cv2.ImWrite(filePath, roiMat);
                Console.WriteLine($"ROI saved to {filePath}");

                // Save metadata
                SaveRoiData(stateName, roiName, x, y, width, height);
            }
            else
            {
                Console.WriteLine("ROI name cannot be empty. ROI discarded.");
            }

            roiMat.Dispose();
        }

        private void SaveRoiData(string stateName, string roiName, int x, int y, int width, int height)
        {
            string roiDataPath = Path.Combine(_saveDirectory, "roi_metadata.json");

            // Load existing metadata or create new
            SavedRoiData savedRoiData;
            if (File.Exists(roiDataPath))
            {
                string jsonString = File.ReadAllText(roiDataPath);
                savedRoiData = JsonSerializer.Deserialize<SavedRoiData>(jsonString) ?? new SavedRoiData();
            }
            else
            {
                savedRoiData = new SavedRoiData();
            }

            // Add or update state entry
            if (!savedRoiData.ContainsKey(stateName))
            {
                savedRoiData[stateName] = new List<RoiData>();
            }

            // Check if ROI with same name already exists
            string roiFileName = $"{roiName}.png";
            var existingRoi = savedRoiData[stateName].FirstOrDefault(r => r.name == roiFileName);

            if (existingRoi != null)
            {
                // Overwrite existing ROI
                existingRoi.x = x;
                existingRoi.y = y;
                existingRoi.width = width;
                existingRoi.height = height;
                Console.WriteLine($"Overwriting existing ROI: {roiFileName}");
            }
            else
            {
                // Add new ROI metadata
                savedRoiData[stateName].Add(new RoiData
                {
                    name = roiFileName,
                    x = x,
                    y = y,
                    width = width,
                    height = height
                });
            }

            // Save back to file
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

        public void RemoveState(string stateName)
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
                var savedRoiData = JsonSerializer.Deserialize<SavedRoiData>(jsonString) ?? new SavedRoiData();

                if (savedRoiData.ContainsKey(stateName))
                {
                    // Remove state from metadata
                    savedRoiData.Remove(stateName);

                    // Save updated metadata
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string updatedJson = JsonSerializer.Serialize(savedRoiData, options);
                    File.WriteAllText(roiDataPath, updatedJson);

                    // Delete state directory
                    string stateDirectory = Path.Combine(_saveDirectory, stateName);
                    if (Directory.Exists(stateDirectory))
                    {
                        Directory.Delete(stateDirectory, true);
                        Console.WriteLine($"Removed ROI state '{stateName}' and deleted directory");
                    }
                    else
                    {
                        Console.WriteLine($"Removed ROI state '{stateName}' from metadata");
                    }
                }
                else
                {
                    Console.WriteLine($"ROI state '{stateName}' not found in metadata");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error removing ROI state: {ex.Message}");
            }
        }

        public void ListStates()
        {
            string roiDataPath = Path.Combine(_saveDirectory, "roi_metadata.json");

            if (!File.Exists(roiDataPath))
            {
                Console.WriteLine("No ROI states found (metadata file doesn't exist)");
                return;
            }

            try
            {
                string jsonString = File.ReadAllText(roiDataPath);
                var savedRoiData = JsonSerializer.Deserialize<SavedRoiData>(jsonString) ?? new SavedRoiData();

                if (savedRoiData.Count == 0)
                {
                    Console.WriteLine("No ROI states found");
                    return;
                }

                Console.WriteLine($"\nAvailable ROI states ({savedRoiData.Count}):");
                Console.WriteLine("==========================================");
                foreach (var state in savedRoiData)
                {
                    Console.WriteLine($"  {state.Key} ({state.Value.Count} ROIs)");
                    foreach (var roi in state.Value)
                    {
                        Console.WriteLine($"    - {roi.name} [{roi.width}x{roi.height}]");
                    }
                }
                Console.WriteLine("==========================================\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing ROI states: {ex.Message}");
            }
        }
    }
}
