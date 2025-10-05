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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

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
            return FindWindow(null, _windowTitle);
        }

        public IntPtr GetWindowHandle(WindowType windowType)
        {
            string windowTitle = windowType switch
            {
                WindowType.Garden => _windowTitle,
                WindowType.CapturedFrame => "Captured Frame",
                _ => _windowTitle
            };
            return FindWindow(null, windowTitle);
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
            const int SM_XVIRTUALSCREEN = 76;
            const int SM_YVIRTUALSCREEN = 77;

            screenX = 0;
            screenY = 0;

            IntPtr hWnd = GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            // Get client area top-left in screen coordinates (same as MouseEventRecorder)
            GetClientRect(hWnd, out RECT clientRect);
            POINT clientTopLeft = new POINT { x = clientRect.Left, y = clientRect.Top };
            ClientToScreen(hWnd, ref clientTopLeft);

            int absoluteX = (int)(clientTopLeft.x) + windowX;
            int absoluteY = (int)(clientTopLeft.y) + windowY;

            // Get virtual screen offset to map coordinates to (0,0) origin
            int virtualScreenLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int virtualScreenTop = GetSystemMetrics(SM_YVIRTUALSCREEN);

            int virtualAdjustedX = absoluteX - virtualScreenLeft;
            int virtualAdjustedY = absoluteY - virtualScreenTop;

            screenX = (int)(virtualAdjustedX);
            screenY = (int)(virtualAdjustedY);

            return true;
        }
    }
}