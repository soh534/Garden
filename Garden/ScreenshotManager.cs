using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using OpenCvSharp;
using System.Drawing;
using System.Drawing.Imaging;

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
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }

            return OpenCvSharp.Extensions.BitmapConverter.ToMat(bmp);
        }

        internal void ProcessFrames(CancellationToken token)
        {
            IntPtr hWnd = GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                Console.WriteLine("Window not found!");
                return;
            }

            while (!token.IsCancellationRequested)
            {
                // Frame processing logic would go here
                try
                {
                    using Mat frame = CaptureWindow(hWnd);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error capturing frame: {e.Message}");
                }
            }

            return;
        }
    }
}
