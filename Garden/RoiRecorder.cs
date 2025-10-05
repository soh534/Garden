using OpenCvSharp;
using System.Collections.Concurrent;

namespace Garden
{
    public class RoiRecorder : Recorder
    {
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
                    _ = Task.Run(() => PromptAndSaveRoi(roiMat, _currentStateName));
                }

                // Reset for next ROI (stay in recording mode)
                _hasStartPoint = false;
            }
        }

        private void PromptAndSaveRoi(Mat roiMat, string stateName)
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

                Cv2.ImWrite(filePath, roiMat);
                Console.WriteLine($"ROI saved to {filePath}");
            }
            else
            {
                Console.WriteLine("ROI name cannot be empty. ROI discarded.");
            }

            roiMat.Dispose();
        }

        protected override void OnMouseMove(object? sender, MouseEventReporter.MouseEvent e)
        {
            if (!_isRecording || !_hasStartPoint) return;

            _currentX = e.X;
            _currentY = e.Y;
        }
    }
}
