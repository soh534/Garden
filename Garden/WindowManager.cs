using System.Runtime.InteropServices;

namespace Garden
{
    public enum WindowType
    {
        Garden,
        CapturedFrame
    }

    public sealed class WindowManager
    {
        private static WindowManager? _instance;
        private static readonly object _lock = new object();

        private string _windowTitle = "Garden";
        private double _scale = 1.0;

        private WindowManager() { }

        public static WindowManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new WindowManager();
                        }
                    }
                }
                return _instance;
            }
        }

        public void SetWindowTitle(string windowTitle)
        {
            _windowTitle = windowTitle;
        }

        public void SetScale(double scale)
        {
            _scale = scale;
        }

        public IntPtr GetScrcpyWindowHandle()
        {
            return Win32Api.FindWindow(null, _windowTitle);
        }

        public IntPtr GetWindowHandle(WindowType windowType)
        {
            string windowTitle = windowType switch
            {
                WindowType.Garden => _windowTitle,
                WindowType.CapturedFrame => "Captured Frame",
                _ => _windowTitle
            };
            return Win32Api.FindWindow(null, windowTitle);
        }

        public string GetWindowTitle()
        {
            return _windowTitle;
        }

        public double GetScale()
        {
            return _scale;
        }

        public bool ConvertToScreenCoordinates(int windowX, int windowY, out int screenX, out int screenY)
        {
            screenX = 0;
            screenY = 0;

            IntPtr hWnd = GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            // Get client area top-left in screen coordinates (same as MouseEventReporter)
            Win32Api.GetClientRect(hWnd, out Win32Api.RECT clientRect);
            Win32Api.POINT clientTopLeft = new Win32Api.POINT { X = clientRect.Left, Y = clientRect.Top };
            Win32Api.ClientToScreen(hWnd, ref clientTopLeft);

            int absoluteX = (int)(clientTopLeft.X) + windowX;
            int absoluteY = (int)(clientTopLeft.Y) + windowY;

            // Get virtual screen offset to map coordinates to (0,0) origin
            int virtualScreenLeft = Win32Api.GetSystemMetrics(Win32Api.SM_XVIRTUALSCREEN);
            int virtualScreenTop = Win32Api.GetSystemMetrics(Win32Api.SM_YVIRTUALSCREEN);

            int virtualAdjustedX = absoluteX - virtualScreenLeft;
            int virtualAdjustedY = absoluteY - virtualScreenTop;

            screenX = (int)(virtualAdjustedX);
            screenY = (int)(virtualAdjustedY);

            return true;
        }
    }
}