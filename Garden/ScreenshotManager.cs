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

namespace Garden
{
    internal class ScreenshotManager
    {
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

        public ScreenshotManager(string imageSavePath, string actionSavePath, string windowTitle = "Garden")
        {
            _imageSavePath = imageSavePath;
            _actionSavePath = actionSavePath;
            _windowTitle = windowTitle;
        }

        public IntPtr GetScrcpyWindowHandle()
        {
            return FindWindow(null, _windowTitle);
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

        internal void ProcessFrames(CancellationToken token, Process proc, ConcurrentQueue<string> commandQueue, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer)
        {
            IntPtr hWnd = GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                Console.WriteLine("Window not found!");
                return;
            }

            Cv2.NamedWindow("Captured Frame", WindowFlags.AutoSize);

            var commandHandler = new CommandHandler(_imageSavePath, mouseRecorder, actionPlayer);
            while (!token.IsCancellationRequested && !proc.HasExited)
            {
                try
                {
                    using Mat frame = CaptureWindow(hWnd);
                    Cv2.ImShow("Captured Frame", frame);
                    int key = Cv2.WaitKey(1);

                    // Check for commands passed through terminal.
                    while (commandQueue.TryDequeue(out var command))
                    {
                        bool shouldContinue = commandHandler.Handle(command, frame);
                        if (!shouldContinue)
                        {
                            return; // Exit ProcessFrames if quit command was received
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error capturing frame: {e.Message}");
                }

                Thread.Sleep(33);
            }

            return;
        }
    }
}
