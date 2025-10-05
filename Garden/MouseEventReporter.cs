using System.Runtime.InteropServices;
using System.Text.Json;
using NLog;

namespace Garden
{
    public class MouseEventReporter
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        public class MouseEvent
        {
            public DateTime Timestamp { get; set; }
            public int X { get; set; } // Relative to scrcpy window client area
            public int Y { get; set; } // Relative to scrcpy window client area
            public bool IsMouseDown { get; set; } // true for down, false for up
        }

        // Win32 API declarations
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);


        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        // Events
        public event EventHandler<MouseEvent>? MouseClickCallback;
        public event EventHandler<MouseEvent>? MouseMoveCallback;

        private LowLevelMouseProc _proc;
        private IntPtr _hookID = IntPtr.Zero;
        private bool _isReporting = false;
        private readonly WindowType _windowType;

        public MouseEventReporter(WindowType windowType)
        {
            _proc = HookCallback;
            _windowType = windowType;
        }

        public void StartReporting()
        {
            if (!_isReporting)
            {
                // Check if window exists before starting
                IntPtr hWnd = WindowManager.Instance.GetWindowHandle(_windowType);
                if (hWnd == IntPtr.Zero)
                {
                    Logger.Error($"Cannot find window for type '{_windowType}'. Recording not started.");
                    return;
                }

                _hookID = SetHook(_proc);
                if (_hookID == IntPtr.Zero)
                {
                    Logger.Error("Failed to install mouse hook. Recording not started.");
                    return;
                }

                _isReporting = true;
                Logger.Info("Mouse recording started (scrcpy window only)...");
            }
            else
            {
                Logger.Info("Recording is already active.");
            }
        }

        public void StopReporting()
        {
            if (_isReporting)
            {
                UnhookWindowsHookEx(_hookID);
                _isReporting = false;
            }
        }

        private bool ConvertToWindowCoordinates(int screenX, int screenY, out int windowX, out int windowY)
        {
            windowX = 0;
            windowY = 0;

            IntPtr hWnd = WindowManager.Instance.GetWindowHandle(_windowType);
            if (hWnd == IntPtr.Zero)
                return false;

            // Get client rectangle (just the content area)
            GetClientRect(hWnd, out RECT clientRect);

            // Get client area top-left in screen coordinates
            POINT clientTopLeft = new POINT { x = clientRect.Left, y = clientRect.Top };
            ClientToScreen(hWnd, ref clientTopLeft);

            // Check if click is within client area
            if (screenX >= clientTopLeft.x && screenX < clientTopLeft.x + clientRect.Right &&
                screenY >= clientTopLeft.y && screenY < clientTopLeft.y + clientRect.Bottom)
            {
                // Convert to window-relative coordinates
                windowX = screenX - clientTopLeft.x;
                windowY = screenY - clientTopLeft.y;
                return true;
            }

            return false; // Click was outside the window
        }

        private IntPtr SetHook(LowLevelMouseProc proc)
        {
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule!)
            {
                return SetWindowsHookEx(WH_MOUSE_LL, proc,
                    GetModuleHandle(curModule.ModuleName!), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isReporting)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                // Scale coordinates using config value
                double scale = WindowManager.Instance.GetScale();
                int scaledX = (int)(hookStruct.pt.x / scale);
                int scaledY = (int)(hookStruct.pt.y / scale);

                // Convert screen coordinates to scrcpy window coordinates
                if (ConvertToWindowCoordinates(scaledX, scaledY, out int windowX, out int windowY))
                {
                    switch ((int)wParam)
                    {
                        case WM_LBUTTONDOWN:
                            var downEvent = new MouseEvent
                            {
                                Timestamp = DateTime.Now,
                                X = windowX,
                                Y = windowY,
                                IsMouseDown = true
                            };

                            // Fire MouseDown event
                            MouseClickCallback?.Invoke(this, downEvent);
                            Logger.Info($"Left button down at ({windowX}, {windowY})");
                            break;

                        case WM_LBUTTONUP:
                            var upEvent = new MouseEvent
                            {
                                Timestamp = DateTime.Now,
                                X = windowX,
                                Y = windowY,
                                IsMouseDown = false
                            };

                            // Fire MouseUp event
                            MouseClickCallback?.Invoke(this, upEvent);
                            Logger.Info($"Left button up at ({windowX}, {windowY})");
                            break;

                        case WM_MOUSEMOVE:
                            var moveEvent = new MouseEvent
                            {
                                Timestamp = DateTime.Now,
                                X = windowX,
                                Y = windowY,
                                IsMouseDown = false // Not relevant for move
                            };
                            MouseMoveCallback?.Invoke(this, moveEvent);
                            break;
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            StopReporting();
        }
    }
}