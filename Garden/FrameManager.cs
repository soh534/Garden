using System.Runtime.InteropServices;

using OpenCvSharp;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Collections.Concurrent;
using NLog;
using Garden.Bots;

namespace Garden
{
    internal class FrameManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly LuaBot _bot;
        private readonly MouseEventRecorder _mouseRecorder;
        private readonly ActionPlayer _actionPlayer;
        private readonly RoiRecorder _roiRecorder;
        private readonly StateDetector _stateDetector;
        private readonly WindowPositionManager _windowPosManager;
        // You take a picture via
        // 1. open_a_terminal_here.bat
        // 2. adb exec-out screencap -p > file.png
        // 3. this saves file.png which is current phone screen under same path as open_a_terminal_here.bat

        private readonly string _imageSavePath;

        private const int TARGET_FRAME_TIME_MS = 33;

        // Profiling
        private readonly Stopwatch _sw = new();
        private double _msCapture, _msDetect, _msDraw;

        // Bot control
        private bool _isBotEnabled = false;
        public bool IsBotEnabled => _isBotEnabled;
        public void EnableBot() => _isBotEnabled = true;
        public void DisableBot() => _isBotEnabled = false;

        public FrameManager(string imageSavePath, LuaBot bot, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer, RoiRecorder roiRecorder, StateDetector stateDetector, WindowPositionManager windowPosManager)
        {
            _imageSavePath = imageSavePath;
            _bot = bot;
            _mouseRecorder = mouseRecorder;
            _actionPlayer = actionPlayer;
            _roiRecorder = roiRecorder;
            _stateDetector = stateDetector;
            _windowPosManager = windowPosManager;
        }

        public Mat CaptureWindow(IntPtr hWnd)
        {
            Win32Api.GetClientRect(hWnd, out Win32Api.RECT clientRect);
            Win32Api.POINT clientTopLeft = new Win32Api.POINT { X = 0, Y = 0 };
            Win32Api.ClientToScreen(hWnd, ref clientTopLeft);

            int width = clientRect.Right;
            int height = clientRect.Bottom;

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(clientTopLeft.X, clientTopLeft.Y, 0, 0, new System.Drawing.Size(width, height));
            }

            return OpenCvSharp.Extensions.BitmapConverter.ToMat(bmp);
        }

        internal void ProcessFrames(CancellationToken token, Process proc, ConcurrentQueue<string> commandQueue, ConcurrentQueue<ActionPlayer.MouseEvent> actionQueue)
        {
            IntPtr hWnd = WindowManager.Instance.GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                Logger.Error("Window not found!");
                return;
            }

            Cv2.NamedWindow("Captured Frame", WindowFlags.AutoSize);

            // Position window once — show dummy frame first so it has a real size
            using (Mat dummy = Mat.Zeros(100, 100, MatType.CV_8UC3))
            {
                Cv2.ImShow("Captured Frame", dummy);
                Cv2.WaitKey(1);
            }
            _windowPosManager.PositionAndAdvance(Win32Api.FindWindow(null, "Captured Frame"));

            var commandHandler = new CommandHandler(_imageSavePath, _mouseRecorder, _actionPlayer, actionQueue, _roiRecorder, this);

            while (!token.IsCancellationRequested && !proc.HasExited)
            {
                var frameStartTime = DateTime.Now;

                try
                {
                    _sw.Restart();
                    using Mat frame = CaptureWindow(hWnd);
                    _roiRecorder.SetCurrentFrame(frame);
                    _msCapture = _sw.Elapsed.TotalMilliseconds;

                    _sw.Restart();
                    if (!_roiRecorder.IsRecording)
                    {
                        _stateDetector.DetectState(frame);
                    }

                    if (_isBotEnabled && actionQueue.IsEmpty)
                    {
                        _bot.QueueStateResponse();
                    }

                    _actionPlayer.StepAction(frameStartTime);

                    // Process terminal commands (skip if ROI is waiting for input)
                    if (!_roiRecorder.IsWaitingForInput && commandQueue.TryDequeue(out var command))
                    {
                        bool shouldContinue = commandHandler.Handle(command, frame);
                        if (!shouldContinue)
                        {
                            return; // Exit ProcessFrames if quit command was received
                        }
                    }
                    _msDetect = _sw.Elapsed.TotalMilliseconds;

                    _sw.Restart();
                    DrawAction(frame);
                    DrawCreatingRoi(frame, _roiRecorder);
                    DrawDetectedRois(frame, _stateDetector, _roiRecorder);
                    DrawState(frame);
                    DrawProfiler(frame);
                    Cv2.ImShow("Captured Frame", frame);
                    int key = Cv2.WaitKey(1);
                    _msDraw = _sw.Elapsed.TotalMilliseconds;
                }
                catch (Exception e)
                {
                    Logger.Error($"Error capturing frame: {e.Message}");
                    throw;
                }

                // Calculate frame time and sleep for remaining time
                var frameElapsed = (DateTime.Now - frameStartTime).TotalMilliseconds;
                int sleepTime = Math.Max(0, TARGET_FRAME_TIME_MS - (int)frameElapsed);
                Thread.Sleep(sleepTime);
            }

            return;
        }

        private void DrawAction(Mat frame)
        {
            var cursorPos = _actionPlayer.CurrentCursorPosition;
            if (cursorPos.HasValue)
            {
                // Draw circle at interpolated position
                Cv2.Circle(frame, cursorPos.Value, 10, Scalar.Red, 2);
            }
        }

        private void DrawCreatingRoi(Mat frame, RoiRecorder roiRecorder)
        {
            var roi = roiRecorder.GetCurrentRoi();
            if (roi.HasValue)
            {
                Cv2.Rectangle(frame, roi.Value, Scalar.Green, 2);
            }
        }

        private void DrawDetectedRois(Mat frame, StateDetector stateDetector, RoiRecorder roiRecorder)
        {
            // Don't draw if in ROI recording mode
            if (roiRecorder.IsRecording) return;

            foreach (var roiDetectionInfo in stateDetector.RoiDetectionInfos)
            {
                Mat? roiMat = stateDetector.GetRoiMat(roiDetectionInfo.StateName, roiDetectionInfo.RoiName);
                if (roiMat == null) continue;

                // Calculate bounding box from center and dimensions
                int topLeftX = roiDetectionInfo.Center.X - roiMat.Width / 2;
                int topLeftY = roiDetectionInfo.Center.Y - roiMat.Height / 2;
                Rect box = new Rect(topLeftX, topLeftY, roiMat.Width, roiMat.Height);

                Cv2.Rectangle(frame, box, Scalar.Blue, 2);

                // Draw state name
                Cv2.PutText(frame, roiDetectionInfo.StateName,
                    new OpenCvSharp.Point(box.X, box.Y - 35),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.Blue, 2);

                // Draw ROI name
                Cv2.PutText(frame, roiDetectionInfo.RoiName,
                    new OpenCvSharp.Point(box.X, box.Y - 20),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.Blue, 2);

                // Draw minVal
                string minValText = $"{roiDetectionInfo.MinVal:F3}";
                Cv2.PutText(frame, minValText,
                    new OpenCvSharp.Point(box.X, box.Y - 5),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.Blue, 2);

                break; // Temporarily just draw first ROI. Remove to draw multiple
            }
        }

        private void DrawProfiler(Mat frame)
        {
            double total = _msCapture + _msDetect + _msDraw;
            double fps = total > 0 ? 1000.0 / total : 0;
            Cv2.PutText(frame, $"FPS:{fps:F0} Cap:{_msCapture:F1}ms Det:{_msDetect:F1}ms Draw:{_msDraw:F1}ms",
                new OpenCvSharp.Point(10, 60), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 2);

            int y = 80;
            foreach (var (key, ms) in _stateDetector.RoiTimings)
            {
                Cv2.PutText(frame, $"  {key}: {ms:F1}ms",
                    new OpenCvSharp.Point(10, y), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 2);
                y += 20;
            }
        }

        private void DrawState(Mat frame)
        {
            string currentState = _stateDetector.CurrentState;
            List<string> nextStates = _stateDetector.NextExpectedStates;
            string nextText = nextStates.Count > 0
                ? $"Next: {string.Join(", ", nextStates)}"
                : "Next: none";

            if (string.IsNullOrEmpty(currentState))
            {
                Cv2.PutText(frame, "State: unknown", new OpenCvSharp.Point(10, 20),
                    HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 2);
            }
            else
            {
                // Calculate confidence from RoiDetectionInfos for this state
                var stateRois = _stateDetector.RoiDetectionInfos.Where(r => r.StateName == currentState).ToList();
                double confidence = stateRois.Count > 0 ? stateRois.Average(r => r.MinVal) : 0.0;

                Cv2.PutText(frame, $"State: {currentState} ({confidence:F3})", new OpenCvSharp.Point(10, 20),
                    HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 2);
            }

            Cv2.PutText(frame, nextText, new OpenCvSharp.Point(10, 40),
                HersheyFonts.HersheySimplex, 0.5, Scalar.Cyan, 2);
        }

    }
}
