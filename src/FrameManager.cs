using System.Runtime.InteropServices;
using System.Net.Sockets;

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
        private readonly NetworkStream _videoStream;
        private readonly int _phoneWidth;
        private readonly int _phoneHeight;
        private Mat? _latestVideoFrame;
        private readonly object _videoFrameLock = new();

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

        public FrameManager(string imageSavePath, LuaBot bot, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer, RoiRecorder roiRecorder, RoiDetector roiDetector, WindowPositionManager windowPosManager, ScrcpyManager.GardenServer gardenServer)
        {
            _imageSavePath    = imageSavePath;
            _bot              = bot;
            _mouseRecorder    = mouseRecorder;
            _actionPlayer     = actionPlayer;
            _roiRecorder      = roiRecorder;
            _roiDetector      = roiDetector;
            _windowPosManager = windowPosManager;
            _videoStream      = gardenServer.VideoStream;
            _phoneWidth       = gardenServer.PhoneWidth;
            _phoneHeight      = gardenServer.PhoneHeight;
        }

        private System.Net.Sockets.TcpListener? _ffmpegOutputListener;
        private System.Net.Sockets.TcpClient?   _ffmpegOutputClient;

        private Process StartVideoDecoder(int width, int height)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            _ffmpegOutputListener = listener;

            var proc = new Process();
            proc.StartInfo.FileName               = "ffmpeg";
            proc.StartInfo.Arguments              = $"-loglevel error -probesize 32 -analyzeduration 0 -f h264 -i pipe:0 -f rawvideo -pix_fmt bgr24 tcp://127.0.0.1:{port}";
            proc.StartInfo.RedirectStandardInput  = true;
            proc.StartInfo.RedirectStandardError  = true;
            proc.StartInfo.UseShellExecute        = false;
            proc.StartInfo.CreateNoWindow         = true;
            proc.Start();
            Task.Run(() => {
                string? line;
                while ((line = proc.StandardError.ReadLine()) != null)
                    Console.WriteLine($"[ffmpeg] {line}");
            });

            return proc;
        }

        private void VideoStreamLoop(Process ffmpeg, CancellationToken token)
        {
            var header = new byte[12];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    _videoStream.ReadExactly(header);
                    int packetSize = (header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11];
                    var data = new byte[packetSize];
                    _videoStream.ReadExactly(data);
                    ffmpeg.StandardInput.BaseStream.Write(data, 0, data.Length);
                    ffmpeg.StandardInput.BaseStream.Flush();
                }
            }
            catch (Exception e) { if (!token.IsCancellationRequested) { Logger.Error($"VideoStreamLoop error: {e.Message}"); } }
        }

        private void VideoDecodeLoop(Process ffmpeg, CancellationToken token)
        {
            int frameSize = _phoneWidth * _phoneHeight * 3;
            var buf = new byte[frameSize];
            var stream = _ffmpegOutputClient!.GetStream();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    stream.ReadExactly(buf);
                    var mat = new Mat(_phoneHeight, _phoneWidth, MatType.CV_8UC3);
                    Marshal.Copy(buf, 0, mat.Data, frameSize);
                    lock (_videoFrameLock)
                    {
                        _latestVideoFrame?.Dispose();
                        _latestVideoFrame = mat;
                    }
                }
            }
            catch (Exception e) { if (!token.IsCancellationRequested) { Console.WriteLine($"[Garden] VideoDecodeLoop error: {e.Message}"); } }
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
            Win32Api.GetClientRect(hWnd, out Win32Api.RECT scrcpyRect);
            int displayW = scrcpyRect.Right  > 0 ? scrcpyRect.Right  : _phoneWidth  / 2;
            int displayH = scrcpyRect.Bottom > 0 ? scrcpyRect.Bottom : _phoneHeight / 2;

            var ffmpeg = StartVideoDecoder(_phoneWidth, _phoneHeight);
            Task.Run(() => VideoStreamLoop(ffmpeg, token));
            Console.WriteLine("[Garden] waiting for ffmpeg TCP output connection...");
            _ffmpegOutputClient = _ffmpegOutputListener!.AcceptTcpClient();
            _ffmpegOutputListener.Stop();
            Console.WriteLine("[Garden] ffmpeg TCP output connected");
            Task.Run(() => VideoDecodeLoop(ffmpeg, token));

            Mat? firstVideoFrame = null;
            while (firstVideoFrame == null)
            {
                lock (_videoFrameLock) { firstVideoFrame = _latestVideoFrame?.Clone(); }
                if (firstVideoFrame == null) Thread.Sleep(10);
            }

            Cv2.NamedWindow("Captured Frame", WindowFlags.AutoSize);
            using var displayFirst = new Mat();
            Cv2.Resize(firstVideoFrame, displayFirst, new OpenCvSharp.Size(displayW, displayH));
            firstVideoFrame.Dispose();
            Cv2.ImShow("Captured Frame", displayFirst);
            Cv2.WaitKey(1);
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
                    Mat? phoneFrame = null;
                    lock (_videoFrameLock) { phoneFrame = _latestVideoFrame?.Clone(); }
                    if (phoneFrame == null) { Thread.Sleep(1); continue; }

                    _roiRecorder.SetCurrentFrame(phoneFrame);
                    _roiDetector.SetFrame(phoneFrame);
                    _msCapture = _sw.Elapsed.TotalMilliseconds;

                    lock (_frameLock)
                    {
                        _sharedFrame?.Dispose();
                        _sharedFrame = phoneFrame.Clone();
                    }

                    if (commandQueue.TryDequeue(out var command))
                    {
                        if (_roiRecorder.IsPrompting)
                        {
                            _roiRecorder.FeedInput(command);
                        }
                        else
                        {
                            bool shouldContinue = commandHandler.Handle(command, phoneFrame);
                            if (!shouldContinue) { cts.Cancel(); break; }
                        }
                    }

                    var snapshot = _roiDetector.Snapshot;
                    _sw.Restart();

                    using var displayFrame = new Mat();
                    Cv2.Resize(phoneFrame, displayFrame, new OpenCvSharp.Size(displayW, displayH));
                    phoneFrame.Dispose();

                    DrawCreatingRoi(displayFrame, _roiRecorder);
                    if (!_roiRecorder.IsRecording)
                    {
                        DrawAction(displayFrame);
                        DrawDetectedRois(displayFrame, snapshot);
                        DrawReadAreas(displayFrame, snapshot);
                        DrawBotStatus(displayFrame, snapshot);
                        DrawRoiScores(displayFrame, snapshot);
                        DrawProfiler(displayFrame, snapshot);
                    }
                    Cv2.ImShow("Captured Frame", displayFrame);
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
            if (!ffmpeg.HasExited) { ffmpeg.Kill(); }
            ffmpeg.Dispose();
            _ffmpegOutputClient?.Dispose();
            lock (_videoFrameLock) { _latestVideoFrame?.Dispose(); }
            Cv2.DestroyAllWindows();
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
                var (x, y) = InputManager.PhoneToDisplay(cursorPos.Value.X, cursorPos.Value.Y, frame.Width, frame.Height);
                Cv2.Circle(frame, new OpenCvSharp.Point(x, y), 10, Scalar.Red, 2);
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
                var (rx, ry) = InputManager.PhoneToDisplay(rect.X, rect.Y, frame.Width, frame.Height);
                var (rw, rh) = InputManager.PhoneToDisplay(rect.Width, rect.Height, frame.Width, frame.Height);
                var dispRect = new Rect(rx, ry, rw, rh);
                Cv2.Rectangle(frame, dispRect, Scalar.Cyan, 2);
                Cv2.PutText(frame, key, new OpenCvSharp.Point(dispRect.X, dispRect.Y - 5),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.Cyan, 1);
            }
        }

        private void DrawDetectedRois(Mat frame, RoiDetector.DetectionSnapshot snapshot)
        {
            if (snapshot.WaitingRoiResult == null) { return; }
            var roiInfo = snapshot.WaitingRoiResult.Value;
            Mat? roiMat = _roiDetector.GetRoiMat(roiInfo.RoiName);
            if (roiMat == null) { return; }

            var (cx, cy)       = InputManager.PhoneToDisplay(roiInfo.Center.X, roiInfo.Center.Y, frame.Width, frame.Height);
            var (dispW, dispH) = InputManager.PhoneToDisplay(roiMat.Width, roiMat.Height, frame.Width, frame.Height);
            Rect box = new Rect(cx - dispW / 2, cy - dispH / 2, dispW, dispH);
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

        private void DrawRoiScores(Mat frame, RoiDetector.DetectionSnapshot snapshot)
        {
            int x = 10;
            int y = 60;
            foreach (var (name, score) in snapshot.LatestScores)
            {
                bool detected = score < RoiDetector.TemplateThreshold;
                Scalar color = detected ? Scalar.LimeGreen : Scalar.Black;
                Cv2.PutText(frame, $"{name} {score:F4}", new OpenCvSharp.Point(x, y),
                    HersheyFonts.HersheySimplex, 0.5, color, 2);
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
