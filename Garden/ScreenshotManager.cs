using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using OpenCvSharp;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Collections.Concurrent;
using NLog;

namespace Garden
{
    internal class ScreenshotManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        // You take a picture via  
        // 1. open_a_terminal_here.bat  
        // 2. adb exec-out screencap -p > file.png  
        // 3. this saves file.png which is current phone screen under same path as open_a_terminal_here.bat

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        private readonly string _imageSavePath;
        private readonly string _actionSavePath;
        private readonly string _windowTitle;

        // For visual feedback with interpolation
        private bool _isInterpolating = false;
        private int _downX, _downY;
        private int _upX, _upY;
        private DateTime _downTime;
        private DateTime _upTime;

        // For replay timing
        private DateTime? _lastActionTime = null;
        private DateTime? _lastActionTimestamp = null;
        private const int TARGET_FRAME_TIME_MS = 33;

        public ScreenshotManager(string imageSavePath, string actionSavePath, string windowTitle = "Garden")
        {
            _imageSavePath = imageSavePath;
            _actionSavePath = actionSavePath;
            _windowTitle = windowTitle;
        }

        public Mat CaptureWindow(IntPtr hWnd)
        {
            // Get bmp of full window (including borders, screen-space)
            GetWindowRect(hWnd, out RECT windowRect);
            int windowWidth = windowRect.Right - windowRect.Left;
            int windowHeight = windowRect.Bottom - windowRect.Top;

            using var bmp = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool success = PrintWindow(hWnd, hdc, 0);
                g.ReleaseHdc(hdc);

                if (!success)
                {
                    bmp.Dispose();
                    throw new InvalidOperationException("PrintWindow failed.");
                }
            }

            // Get client rect in client-space
            GetClientRect(hWnd, out RECT clientRect);

            // Get client top left in screen coordinates
            POINT clientTopLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
            ClientToScreen(hWnd, ref clientTopLeft);

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

        internal void ProcessFrames(CancellationToken token, Process proc, ConcurrentQueue<string> commandQueue, ConcurrentQueue<ActionPlayer.MouseEvent> actionQueue, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer)
        {
            IntPtr hWnd = WindowManager.Instance.GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                Logger.Error("Window not found!");
                return;
            }

            Cv2.NamedWindow("Captured Frame", WindowFlags.AutoSize);

            var commandHandler = new CommandHandler(_imageSavePath, mouseRecorder, actionPlayer, actionQueue);

            while (!token.IsCancellationRequested && !proc.HasExited)
            {
                var frameStartTime = DateTime.Now;

                try
                {
                    using Mat frame = CaptureWindow(hWnd);

                    // Process actions - check timing before dequeuing
                    if (actionQueue.TryPeek(out var nextAction))
                    {
                        bool shouldExecute = false;

                        if (_lastActionTime == null)
                        {
                            // First action, execute immediately
                            shouldExecute = true;
                        }
                        else
                        {
                            // Check if enough time has elapsed based on timestamps
                            var timeSinceLastAction = DateTime.Now - _lastActionTime.Value;
                            var requiredDelay = nextAction.Timestamp - _lastActionTimestamp.Value;

                            if (timeSinceLastAction >= requiredDelay)
                            {
                                shouldExecute = true;
                            }
                        }

                        if (shouldExecute)
                        {
                            actionQueue.TryDequeue(out var action);
                            _lastActionTime = DateTime.Now;
                            _lastActionTimestamp = action.Timestamp;

                            if (action.IsMouseDown)
                            {
                                // Mouse down - peek at next action to get up position
                                if (actionQueue.TryPeek(out var upAction))
                                {
                                    _downX = action.X;
                                    _downY = action.Y;
                                    _downTime = DateTime.Now;
                                    _upX = upAction.X;
                                    _upY = upAction.Y;
                                    _upTime = DateTime.Now + (upAction.Timestamp - action.Timestamp);
                                    _isInterpolating = true;
                                }
                            }
                            else
                            {
                                // Mouse up - stop interpolating
                                _isInterpolating = false;
                            }

                            actionPlayer.ExecuteAction(action);
                        }
                    }
                    else if (_lastActionTime != null)
                    {
                        // Queue is empty, reset timing
                        _lastActionTime = null;
                        _lastActionTimestamp = null;
                    }

                    // Process terminal commands
                    if (commandQueue.TryDequeue(out var command))
                    {
                        bool shouldContinue = commandHandler.Handle(command, frame);
                        if (!shouldContinue)
                        {
                            return; // Exit ProcessFrames if quit command was received
                        }
                    }

                    // Draw and render at the end
                    DrawAction(frame);
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
            if (_isInterpolating)
            {
                DateTime now = DateTime.Now;

                // Calculate interpolation progress (0.0 to 1.0)
                double totalDuration = (_upTime - _downTime).TotalMilliseconds;
                double elapsed = (now - _downTime).TotalMilliseconds;
                double progress = Math.Clamp(elapsed / totalDuration, 0.0, 1.0);

                // Interpolate position
                int currentX = (int)(_downX + (_upX - _downX) * progress);
                int currentY = (int)(_downY + (_upY - _downY) * progress);

                // Draw circle at interpolated position
                Cv2.Circle(frame, new OpenCvSharp.Point(currentX, currentY), 10, Scalar.Red, 2);
            }
        }
    }
}
