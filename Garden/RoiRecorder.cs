using OpenCvSharp;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Garden
{
    public class RoiRecorder : Recorder
    {
        public class RoiMetadata
        {
            public string name { get; set; } = "";
            public int x { get; set; }
            public int y { get; set; }
            public int width { get; set; }
            public int height { get; set; }
        }

        public class RoiMetadataFile
        {
            public Dictionary<string, List<RoiMetadata>> states { get; set; } = new();
        }
        private readonly string _saveDirectory;
        private readonly ConcurrentQueue<string> _commandQueue;
        private string? _currentStateName = null;
        private int _startX, _startY;
        private int _currentX, _currentY;
        private bool _hasStartPoint = false;
        private bool _isWaitingForInput = false;
        private Mat? _currentFrame = null;

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

        public OpenCvSharp.Rect? GetCurrentRoi()
        {
            if (!_hasStartPoint || !_isRecording)
                return null;

            int x = Math.Min(_startX, _currentX);
            int y = Math.Min(_startY, _currentY);
            int width = Math.Abs(_currentX - _startX);
            int height = Math.Abs(_currentY - _startY);

            if (width > 0 && height > 0)
                return new OpenCvSharp.Rect(x, y, width, height);

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

                // Convert to grayscale and apply edge detection
                Mat gray = new Mat();
                Cv2.CvtColor(roiMat, gray, ColorConversionCodes.BGR2GRAY);
                Mat edges = new Mat();
                Cv2.Canny(gray, edges, 50, 150);

                // Save edge-detected version
                string edgeFilename = $"{roiName}_edges.png";
                string edgeFilePath = Path.Combine(stateDirectory, edgeFilename);
                Cv2.ImWrite(edgeFilePath, edges);
                Console.WriteLine($"Edge ROI saved to {edgeFilePath}");

                gray.Dispose();
                edges.Dispose();

                // Save metadata
                SaveRoiMetadata(stateName, roiName, x, y, width, height);
            }
            else
            {
                Console.WriteLine("ROI name cannot be empty. ROI discarded.");
            }

            roiMat.Dispose();
        }

        private void SaveRoiMetadata(string stateName, string roiName, int x, int y, int width, int height)
        {
            string metadataPath = Path.Combine(_saveDirectory, "roi_metadata.json");

            // Load existing metadata or create new
            RoiMetadataFile metadataFile;
            if (File.Exists(metadataPath))
            {
                string jsonString = File.ReadAllText(metadataPath);
                metadataFile = JsonSerializer.Deserialize<RoiMetadataFile>(jsonString) ?? new RoiMetadataFile();
            }
            else
            {
                metadataFile = new RoiMetadataFile();
            }

            // Add or update state entry
            if (!metadataFile.states.ContainsKey(stateName))
            {
                metadataFile.states[stateName] = new List<RoiMetadata>();
            }

            // Check if ROI with same name already exists
            string roiFileName = $"{roiName}.png";
            var existingRoi = metadataFile.states[stateName].FirstOrDefault(r => r.name == roiFileName);

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
                metadataFile.states[stateName].Add(new RoiMetadata
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
            string updatedJson = JsonSerializer.Serialize(metadataFile, options);
            File.WriteAllText(metadataPath, updatedJson);

            Console.WriteLine($"Metadata updated in {metadataPath}");
        }

        protected override void OnMouseMove(object? sender, MouseEventReporter.MouseEvent e)
        {
            if (!_isRecording || !_hasStartPoint) return;

            _currentX = e.X;
            _currentY = e.Y;
        }

        public void RemoveState(string stateName)
        {
            string metadataPath = Path.Combine(_saveDirectory, "roi_metadata.json");

            if (!File.Exists(metadataPath))
            {
                Console.WriteLine($"ROI metadata file not found: {metadataPath}");
                return;
            }

            try
            {
                string jsonString = File.ReadAllText(metadataPath);
                RoiMetadataFile metadataFile = JsonSerializer.Deserialize<RoiMetadataFile>(jsonString) ?? new RoiMetadataFile();

                if (metadataFile.states.ContainsKey(stateName))
                {
                    // Remove state from metadata
                    metadataFile.states.Remove(stateName);

                    // Save updated metadata
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string updatedJson = JsonSerializer.Serialize(metadataFile, options);
                    File.WriteAllText(metadataPath, updatedJson);

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
    }
}
