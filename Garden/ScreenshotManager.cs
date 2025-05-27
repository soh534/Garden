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

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private readonly string _windowTitle;

        public ScreenshotManager(string windowTitle = "Garden")
        {
            _windowTitle = windowTitle;
        }

        public IntPtr GetScrcpyWindowHandle()
        {
            return FindWindow(null, _windowTitle);
        }

        public Mat CaptureWindow(IntPtr hWnd)
        {
            GetWindowRect(hWnd, out RECT rect);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool success = PrintWindow(hWnd, hdc, 0);
                g.ReleaseHdc(hdc);

                if (!success)
                {
                    throw new InvalidOperationException("PrintWindow failed.");
                }
            }

            return OpenCvSharp.Extensions.BitmapConverter.ToMat(bmp);
        }

        internal void ProcessFrames(CancellationToken token, Process proc)
        {
            IntPtr hWnd = GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                Console.WriteLine("Window not found!");
                return;
            }

            Cv2.NamedWindow("Captured Frame", WindowFlags.AutoSize);

            while (!token.IsCancellationRequested && !proc.HasExited)
            {
                // Frame processing logic would go here
                try
                {
                    using Mat frame = CaptureWindow(hWnd);

                    Cv2.ImShow("Captured Frame", frame);

                    int key = Cv2.WaitKey(1);

                    //InputManager.Move(500, 500);
                    //InputManager.Click();
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
