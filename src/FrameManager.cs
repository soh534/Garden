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
        private NetworkStream _videoStream;                // per-session: swapped by RebuildSession
        private readonly VideoRing _videoRing;
        private readonly int _phoneWidth;
        private readonly int _phoneHeight;
        private Mat? _latestVideoFrame;
        private readonly object _videoFrameLock = new();

        // --- capture-session self-heal ---------------------------------------
        // The phone-side scrcpy server dies after long uptimes (~35h observed:
        // encoder gives out, video socket EOFs, the process lingers half-dead).
        // The pump threads flag the death the INSTANT they see it (the catch
        // block IS the event); the render loop then tears down and rebuilds the
        // whole session: sweep the dead server, re-handshake, fresh ffmpeg.
        // A frame-age check backstops silent stalls: input sent but no frame
        // followed for 30s. Five manual rescues (07-08 .. 07-23) preceded this.
        private readonly ScrcpyManager _scrcpy;
        private ScrcpyManager.GardenServer _server;
        private Process? _ffmpeg;
        private int _sessionGen;
        private volatile bool _streamDead;
        private long _lastFrameTicks;
        private DateTime _lastRebuildTry = DateTime.MinValue;

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
        public void EvalLua(string code) => _bot.Eval(code);
        public void AbortLua() => _bot.AbortEval();
        public void SetScanEnabled(bool on) => _roiDetector.SetScanEnabled(on);
        public bool ScanEnabled => _roiDetector.ScanEnabled;

        public FrameManager(string imageSavePath, LuaBot bot, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer, RoiRecorder roiRecorder, RoiDetector roiDetector, WindowPositionManager windowPosManager, ScrcpyManager scrcpyManager, ScrcpyManager.GardenServer gardenServer)
        {
            _imageSavePath    = imageSavePath;
            _bot              = bot;
            _mouseRecorder    = mouseRecorder;
            _actionPlayer     = actionPlayer;
            _roiRecorder      = roiRecorder;
            _roiDetector      = roiDetector;
            _windowPosManager = windowPosManager;
            _scrcpy           = scrcpyManager;
            _server           = gardenServer;
            _videoStream      = gardenServer.VideoStream;
            _phoneWidth       = gardenServer.PhoneWidth;
            _phoneHeight      = gardenServer.PhoneHeight;
            _videoRing        = new VideoRing(imageSavePath);
            bot.PreserveVideo = tag => _videoRing.Preserve(tag);
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
            proc.StartInfo.Arguments              = $"-loglevel warning -fflags +discardcorrupt -probesize 32 -analyzeduration 0 -threads 1 -f h264 -i pipe:0 -f rawvideo -pix_fmt bgr24 tcp://127.0.0.1:{port}";
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

        private void VideoStreamLoop(Process ffmpeg, int gen, CancellationToken token)
        {
            var header = new byte[12];
            var stream = _videoStream;                     // THIS session's stream: a rebuild swaps the field, not our capture
            try
            {
                while (!token.IsCancellationRequested)
                {
                    stream.ReadExactly(header);
                    // scrcpy packet header: top bits of the 8-byte PTS field
                    // flag config (SPS/PPS) and keyframe packets
                    bool isConfig   = (header[0] & 0x80) != 0;
                    bool isKeyFrame = (header[0] & 0x40) != 0;
                    int packetSize = (header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11];
                    var data = new byte[packetSize];
                    stream.ReadExactly(data);
                    _videoRing.Write(data, isConfig, isKeyFrame);
                    ffmpeg.StandardInput.BaseStream.Write(data, 0, data.Length);
                    ffmpeg.StandardInput.BaseStream.Flush();
                }
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested && gen == _sessionGen)
                {
                    Logger.Error($"VideoStreamLoop error: {e.Message}");
                    _roiDetector.LogEvent($"(engine) VIDEO STREAM DIED: {e.Message} -- rebuilding session");
                    _streamDead = true;
                }
            }
        }

        private void VideoDecodeLoop(Process ffmpeg, int gen, CancellationToken token)
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
                    Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                }
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested && gen == _sessionGen)
                {
                    Console.WriteLine($"[Garden] VideoDecodeLoop error: {e.Message}");
                    _roiDetector.LogEvent($"(engine) VIDEO DECODE DIED: {e.Message} -- rebuilding session");
                    _streamDead = true;
                }
            }
        }

        // start (or restart) the decode pipeline for the CURRENT _videoStream
        private bool StartSession(CancellationToken token)
        {
            int gen = ++_sessionGen;
            var ffmpeg = StartVideoDecoder(_phoneWidth, _phoneHeight);
            _ffmpeg = ffmpeg;
            Task.Run(() => VideoStreamLoop(ffmpeg, gen, token));
            for (int i = 0; i < 100 && !_ffmpegOutputListener!.Pending(); i++) { Thread.Sleep(100); }   // ffmpeg connects back within ~10s or the session is dead
            if (!_ffmpegOutputListener!.Pending())
            {
                Logger.Error("ffmpeg never connected its output socket");
                return false;
            }
            _ffmpegOutputClient = _ffmpegOutputListener.AcceptTcpClient();
            _ffmpegOutputListener.Stop();
            Console.WriteLine("[Garden] ffmpeg TCP output connected");
            Task.Run(() => VideoDecodeLoop(ffmpeg, gen, token));
            return true;
        }

        // full capture-session teardown + rebuild; paced to one attempt per 30s
        private void RebuildSession(CancellationToken token)
        {
            if ((DateTime.UtcNow - _lastRebuildTry).TotalSeconds < 30) { return; }
            _lastRebuildTry = DateTime.UtcNow;
            _sessionGen++;   // retire the old pumps NOW so their teardown EOFs don't log as new deaths
            _roiDetector.LogEvent("(engine) rebuilding capture session...");
            try { _server.Dispose(); } catch { }
            try { if (_ffmpeg != null && !_ffmpeg.HasExited) { _ffmpeg.Kill(); } _ffmpeg?.Dispose(); } catch { }
            try { _ffmpegOutputClient?.Dispose(); } catch { }
            var server = _scrcpy.StartGardenServer();      // sweeps the dead phone-side server + retried handshake
            if (server == null)
            {
                _roiDetector.LogEvent("(engine) capture session rebuild FAILED (handshake) -- retrying in 30s");
                return;
            }
            _server = server;
            _videoStream = server.VideoStream;
            InputManager.Initialize(server.ControlStream, server.PhoneWidth, server.PhoneHeight);
            if (!StartSession(token))
            {
                _roiDetector.LogEvent("(engine) capture session rebuild FAILED (ffmpeg) -- retrying in 30s");
                return;
            }
            _streamDead = false;
            Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
            _roiDetector.LogEvent("(engine) capture session REBUILT -- stream restored");
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

            Console.WriteLine("[Garden] starting capture session...");
            if (!StartSession(token))
            {
                Logger.Error("initial capture session failed");
                return;
            }

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
                    if (_streamDead) { RebuildSession(token); }
                    else
                    {
                        // silent-stall backstop: we injected input but no frame followed --
                        // a live screen always changes under a tap/swipe, so this means the
                        // stream died without an EOF. Static idle screens can't false-trigger
                        // (no input, no expectation).
                        long lf = Interlocked.Read(ref _lastFrameTicks), li = Interlocked.Read(ref InputManager.LastInjectTicks);
                        if (lf > 0 && li > lf
                            && DateTime.UtcNow.Ticks - li > TimeSpan.FromSeconds(15).Ticks
                            && DateTime.UtcNow.Ticks - lf > TimeSpan.FromSeconds(30).Ticks)
                        {
                            _roiDetector.LogEvent("(engine) VIDEO STALLED (input sent, no frame followed) -- rebuilding session");
                            _streamDead = true;
                        }
                    }
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
            try { if (_ffmpeg != null && !_ffmpeg.HasExited) { _ffmpeg.Kill(); } _ffmpeg?.Dispose(); } catch { }
            _ffmpegOutputClient?.Dispose();
            lock (_videoFrameLock) { _latestVideoFrame?.Dispose(); }
            Cv2.DestroyAllWindows();
        }

        private void ControlLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // the action pump must survive anything: if it dies, IsIdle
                // freezes and the Lua thread hangs forever in WaitForActions
                try { _actionPlayer.StepAction(DateTime.Now); }
                catch (Exception e) { Logger.Error($"StepAction error: {e.Message}"); }
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

        private static readonly Scalar[] DetectedColors =
        {
            Scalar.LimeGreen,
            Scalar.Orange,
            Scalar.Magenta,
            Scalar.Cyan,
            Scalar.Yellow,
        };

        private void DrawDetectedRois(Mat frame, RoiDetector.DetectionSnapshot snapshot)
        {
            int colorIdx = 0;
            foreach (var kv in snapshot.LatestScores)
            {
                string name = kv.Key;
                RoiDetector.RoiScanResult r = kv.Value;
                if (!r.Detected) { continue; }
                Scalar color = DetectedColors[colorIdx++ % DetectedColors.Length];
                Mat? roiMat = _roiDetector.GetRoiMat(name);
                if (roiMat == null) { continue; }
                var (dispCx, dispCy) = InputManager.PhoneToDisplay(r.CenterX, r.CenterY, frame.Width, frame.Height);
                var (dispW, dispH)   = InputManager.PhoneToDisplay(roiMat.Width, roiMat.Height, frame.Width, frame.Height);
                Rect box = new Rect(dispCx - dispW / 2, dispCy - dispH / 2, dispW, dispH);
                Cv2.Rectangle(frame, box, color, 2);
                Cv2.PutText(frame, $"{name} {r.Score:F4}",
                    new OpenCvSharp.Point(box.X, box.Y - 5),
                    HersheyFonts.HersheySimplex, 0.4, color, 1);
                var (clx, cly) = InputManager.PhoneToDisplay(r.ClickX, r.ClickY, frame.Width, frame.Height);
                Cv2.Circle(frame, new OpenCvSharp.Point(clx, cly), 6, color, 2);
                int ty = box.Y + box.Height + 15;
                foreach (var (raName, val) in r.Readings)
                {
                    Cv2.PutText(frame, $"{raName}: {val}",
                        new OpenCvSharp.Point(box.X, ty),
                        HersheyFonts.HersheySimplex, 0.4, color, 1);
                    ty += 15;
                }
            }
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
            int colorIdx = 0;
            foreach (var kv in snapshot.LatestScores)
            {
                Scalar color = kv.Value.Detected ? DetectedColors[colorIdx++ % DetectedColors.Length] : Scalar.Black;
                Cv2.PutText(frame, $"{kv.Key} {kv.Value.Score:F4}", new OpenCvSharp.Point(x, y),
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
