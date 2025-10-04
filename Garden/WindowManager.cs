using System.Runtime.InteropServices;

namespace Garden
{
    public sealed class WindowManager
    {
        private static WindowManager? _instance;
        private static readonly object _lock = new object();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

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

        public string GetWindowTitle()
        {
            return _windowTitle;
        }

        public double GetScale()
        {
            return _scale;
        }
    }
}