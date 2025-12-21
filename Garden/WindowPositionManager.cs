using System;
using NLog;

namespace Garden
{
    internal class WindowPositionManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private int _currentX;
        private int _y;
        private int _spacing;

        public WindowPositionManager(ConfigManager.Config.WindowPositions? windowPositions)
        {
            if (windowPositions?.topLeft == null)
            {
                _currentX = 0;
                _y = 0;
                _spacing = 0;
                return;
            }

            _currentX = windowPositions.topLeft.x;
            _y = windowPositions.topLeft.y;
            _spacing = windowPositions.spacing;
        }

        public void Position(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;

            // Position the window without advancing
            Win32Api.SetWindowPos(hWnd, IntPtr.Zero, _currentX, _y, 0, 0,
                Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);
        }

        public void PositionAndAdvance(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;

            // Position the window
            Win32Api.SetWindowPos(hWnd, IntPtr.Zero, _currentX, _y, 0, 0,
                Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);

            // Get window width and advance currentX for next window
            if (Win32Api.GetWindowRect(hWnd, out Win32Api.RECT rect))
            {
                int width = rect.Right - rect.Left;
                _currentX += width + _spacing;
            }
        }
    }
}
