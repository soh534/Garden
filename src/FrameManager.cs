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
        private readonly RoiDetector _roiDetector;
        private readonly WindowPositionManager _windowPosManager;

        private readonly string _imageSavePath;

        private const int TARGET_FRAME_TIME_MS = 33;

        // Shared frame between render and detection threads
        private Mat? _sharedFrame;
        private readonly object _frameLock = new();

        // Profiling (_msCapture/_msDraw written by render thread)
        private readonly Stopwatch _sw = new();
        private double _msCapture, _msDraw;

        // Bot control
        private bool _isBotEnabled = false;
        public bool IsBotEnabled => _isBotEnabled;
        public void EnableBot()  { _isBotEnabled = true;  _bot.Enable(); }
        public void DisableBot() { _isBotEnabled = false; _bot.Disable(); }

        public FrameManager(string imageSavePath, LuaBot bot, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer, RoiRecorder roiRecorder, RoiDetector roiDetector, WindowPositionManager windowPosManager)
        {
            _imageSavePath = imageSavePath;
            _bot = bot;
            _mouseRecorder = mouseRecorder;
            _actionPlayer = actionPlayer;
            _roiRecorder = roiRecorder;
            _roiDetector = roiDetector;
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

        internal void ProcessFrames(CancellationTokenSource cts, Process proc, ConcurrentQueue<string> commandQueue, ConcurrentQueue<ActionPlayer.MouseEvent> actionQueue)
        {
            var token = cts.Token;

            IntPtr hWnd = WindowManager.Instance.GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                Logger.Error("Window not found!");
                return;
            }

            Cv2.NamedWindow("Captured Frame", WindowFlags.AutoSize);
            using (Mat firstFrame = CaptureWindow(hWnd))
            {
                Cv2.ImShow("Captured Frame", firstFrame);
                Cv2.WaitKey(1);
            }
            _windowPosManager.PositionAndAdvance(Win32Api.FindWindow(null, "Captured Frame"));

            var commandHandler = new CommandHandler(_imageSavePath, _mouseRecorder, _actionPlayer, actionQueue, _roiRecorder, this);

            Task botTask = Task.Run(() => _bot.Run(token));
            Task controlTask = Task.Run(() => ControlLoop(token));

            while (!token.IsCancellationRequested && !proc.HasExited)
            {
                var frameStartTime = DateTime.Now;

                try
                {
                    _sw.Restart();
                    using Mat frame = CaptureWindow(hWnd);
                    _roiRecorder.SetCurrentFrame(frame);
                    _roiDetector.SetFrame(frame);
                    _msCapture = _sw.Elapsed.TotalMilliseconds;

                    lock (_frameLock)
                    {
                        _sharedFrame?.Dispose();
                        _sharedFrame = frame.Clone();
                    }

                    if (!_roiRecorder.IsWaitingForInput && commandQueue.TryDequeue(out var command))
                    {
                        bool shouldContinue = commandHandler.Handle(command, frame);
                        if (!shouldContinue)
                        {
                            cts.Cancel();
                            break;
                        }
                    }

                    var snapshot = _roiDetector.Snapshot;
                    _sw.Restart();
                    DrawCreatingRoi(frame, _roiRecorder);
                    if (!_roiRecorder.IsRecording)
                    {
                        DrawAction(frame);
                        DrawDetectedRois(frame, snapshot);
                        DrawReadAreas(frame, snapshot);
                        DrawBotStatus(frame, snapshot);
                        DrawProfiler(frame, snapshot);
                    }
                    Cv2.ImShow("Captured Frame", frame);
                    Cv2.WaitKey(1);
                    _msDraw = _sw.Elapsed.TotalMilliseconds;
                }
                catch (Exception e)
                {
                    Logger.Error($"Error in render loop: {e.Message}");
                    throw;
                }

                var frameElapsed = (DateTime.Now - frameStartTime).TotalMilliseconds;
                int sleepTime = Math.Max(0, TARGET_FRAME_TIME_MS - (int)frameElapsed);
                Thread.Sleep(sleepTime);
            }

            Task.WaitAll(botTask, controlTask);
        }

        private void ControlLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                _actionPlayer.StepAction(DateTime.Now);
                Thread.Sleep(10);
            }
        }

        private void DrawAction(Mat frame)
        {
            var cursorPos = _actionPlayer.CurrentCursorPosition;
            if (cursorPos.HasValue)
            {
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

        private void DrawReadAreas(Mat frame, RoiDetector.DetectionSnapshot snapshot)
        {
            foreach (var (key, rect) in snapshot.ReadAreaRects)
            {
                Cv2.Rectangle(frame, rect, Scalar.Cyan, 2);
                Cv2.PutText(frame, key, new OpenCvSharp.Point(rect.X, rect.Y - 5),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.Cyan, 1);
            }
        }

        private void DrawDetectedRois(Mat frame, RoiDetector.DetectionSnapshot snapshot)
        {
            if (snapshot.WaitingRoiResult == null) { return; }
            var roiInfo = snapshot.WaitingRoiResult.Value;
            Mat? roiMat = _roiDetector.GetRoiMat(roiInfo.RoiName);
            if (roiMat == null) { return; }

            int topLeftX = roiInfo.Center.X - roiMat.Width / 2;
            int topLeftY = roiInfo.Center.Y - roiMat.Height / 2;
            Rect box = new Rect(topLeftX, topLeftY, roiMat.Width, roiMat.Height);
            Cv2.Rectangle(frame, box, Scalar.Blue, 2);
            Cv2.PutText(frame, roiInfo.RoiName,
                new OpenCvSharp.Point(box.X, box.Y - 20),
                HersheyFonts.HersheySimplex, 0.4, Scalar.Blue, 2);
            Cv2.PutText(frame, $"{roiInfo.Score:F3}",
                new OpenCvSharp.Point(box.X, box.Y - 5),
                HersheyFonts.HersheySimplex, 0.4, Scalar.Blue, 2);
        }

        private void DrawProfiler(Mat frame, RoiDetector.DetectionSnapshot snapshot)
        {
            double total = _msCapture + _msDraw;
            double fps = total > 0 ? 1000.0 / total : 0;
            Cv2.PutText(frame, $"FPS:{fps:F0} Cap:{_msCapture:F1}ms Draw:{_msDraw:F1}ms",
                new OpenCvSharp.Point(10, 40), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 2);

            int y = 60;
            foreach (var (key, val) in snapshot.OcrReadings)
            {
                Cv2.PutText(frame, $"  ocr {key}: {val}",
                    new OpenCvSharp.Point(10, y), HersheyFonts.HersheySimplex, 0.5, Scalar.Cyan, 2);
                y += 20;
            }
        }

        private void DrawBotStatus(Mat frame, RoiDetector.DetectionSnapshot snapshot)
        {
            string waitTarget = snapshot.WaitingForRoi ?? "none";
            bool found = snapshot.WaitingRoiResult.HasValue;
            Scalar color = found ? Scalar.LimeGreen : Scalar.Yellow;
            string status = _isBotEnabled ? "ON" : "OFF";
            Cv2.PutText(frame, $"Bot:{status} Waiting:{waitTarget} [{(found ? "FOUND" : "searching")}]",
                new OpenCvSharp.Point(10, 20), HersheyFonts.HersheySimplex, 0.5, color, 2);
        }
    }
}
