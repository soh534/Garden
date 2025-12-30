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
        private readonly BotBase _bot;
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

        // Bot control
        private bool _isBotEnabled = false;
        public bool IsBotEnabled => _isBotEnabled;
        public void EnableBot() => _isBotEnabled = true;
        public void DisableBot() => _isBotEnabled = false;

        public FrameManager(string imageSavePath, BotBase bot, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer, RoiRecorder roiRecorder, StateDetector stateDetector, WindowPositionManager windowPosManager)
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
            // Get bmp of full window (including borders, screen-space)
            Win32Api.GetWindowRect(hWnd, out Win32Api.RECT windowRect);
            int windowWidth = windowRect.Right - windowRect.Left;
            int windowHeight = windowRect.Bottom - windowRect.Top;

            using var bmp = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool success = Win32Api.PrintWindow(hWnd, hdc, 0);
                g.ReleaseHdc(hdc);

                if (!success)
                {
                    bmp.Dispose();
                    throw new InvalidOperationException("PrintWindow failed.");
                }
            }

            // Get client rect in client-space
            Win32Api.GetClientRect(hWnd, out Win32Api.RECT clientRect);

            // Get client top left in screen coordinates
            Win32Api.POINT clientTopLeft = new Win32Api.POINT { X = clientRect.Left, Y = clientRect.Top };
            Win32Api.ClientToScreen(hWnd, ref clientTopLeft);

            // Calculate offset from window border to client
            int offsetX = clientTopLeft.X - windowRect.Left;
            int offsetY = clientTopLeft.Y - windowRect.Top;

            // Crop out client bmp from full window bmp
            using var croppedBmp = bmp.Clone(new Rectangle(offsetX, offsetY, clientRect.Right, clientRect.Bottom), bmp.PixelFormat);
            bmp.Dispose();

            Mat mat = OpenCvSharp.Extensions.BitmapConverter.ToMat(croppedBmp);
            croppedBmp.Dispose();
            return mat;
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

            var commandHandler = new CommandHandler(_imageSavePath, _mouseRecorder, _actionPlayer, actionQueue, _roiRecorder, this);

            while (!token.IsCancellationRequested && !proc.HasExited)
            {
                var frameStartTime = DateTime.Now;

                try
                {
                    using Mat frame = CaptureWindow(hWnd);
                    _windowPosManager.Position(Win32Api.FindWindow(null, "Captured Frame"));
                    _roiRecorder.SetCurrentFrame(frame);

                    string currentState = string.Empty;
                    if (!_roiRecorder.IsRecording && actionQueue.IsEmpty)
                    {
                        currentState = _stateDetector.DetectState(frame);
                        if (!string.IsNullOrEmpty(currentState) && _isBotEnabled)
                        {
                            _bot.HandleState(currentState);
                        }
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

                    // Draw and render at the end
                    DrawAction(frame);
                    DrawCreatingRoi(frame, _roiRecorder);
                    DrawDetectedRois(frame, _stateDetector, _roiRecorder);
                    DrawState(frame, currentState);
                    Cv2.ImShow("Captured Frame", frame);

                    int key = Cv2.WaitKey(1);
                }
                catch (Exception e)
                {
                    Logger.Error($"Error capturing frame: {e.Message}");
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
                    HersheyFonts.HersheySimplex, 0.4, Scalar.Blue, 1);

                // Draw ROI name
                Cv2.PutText(frame, roiDetectionInfo.RoiName,
                    new OpenCvSharp.Point(box.X, box.Y - 20),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.Blue, 1);

                // Draw minVal
                string minValText = $"{roiDetectionInfo.MinVal:F3}";
                Cv2.PutText(frame, minValText,
                    new OpenCvSharp.Point(box.X, box.Y - 5),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.Blue, 1);
            }
        }

        private void DrawState(Mat frame, string? stateName)
        {
            if (stateName == null)
            {
                Cv2.PutText(frame, "State: unknown", new OpenCvSharp.Point(10, 20),
                    HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
            }
            else
            {
                // Calculate confidence from RoiDetectionInfos for this state
                var stateRois = _stateDetector.RoiDetectionInfos.Where(r => r.StateName == stateName).ToList();
                double confidence = stateRois.Count > 0 ? stateRois.Average(r => r.MinVal) : 0.0;

                Cv2.PutText(frame, $"State: {stateName} ({confidence:F3})", new OpenCvSharp.Point(10, 20),
                    HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
            }
        }

    }
}
