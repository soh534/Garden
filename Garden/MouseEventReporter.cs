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

        // Events
        public event EventHandler<MouseEvent>? MouseClickCallback;
        public event EventHandler<MouseEvent>? MouseMoveCallback;

        private Win32Api.LowLevelMouseProc _proc;
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
                Win32Api.UnhookWindowsHookEx(_hookID);
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
            Win32Api.GetClientRect(hWnd, out Win32Api.RECT clientRect);

            // Get client area top-left in screen coordinates
            Win32Api.POINT clientTopLeft = new Win32Api.POINT { X = clientRect.Left, Y = clientRect.Top };
            Win32Api.ClientToScreen(hWnd, ref clientTopLeft);

            // Check if click is within client area
            if (screenX >= clientTopLeft.X && screenX < clientTopLeft.X + clientRect.Right &&
                screenY >= clientTopLeft.Y && screenY < clientTopLeft.Y + clientRect.Bottom)
            {
                // Convert to window-relative coordinates
                windowX = screenX - clientTopLeft.X;
                windowY = screenY - clientTopLeft.Y;
                return true;
            }

            return false; // Click was outside the window
        }

        private IntPtr SetHook(Win32Api.LowLevelMouseProc proc)
        {
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule!)
            {
                return Win32Api.SetWindowsHookEx(Win32Api.WH_MOUSE_LL, proc,
                    Win32Api.GetModuleHandle(curModule.ModuleName!), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isReporting)
            {
                var hookStruct = Marshal.PtrToStructure<Win32Api.MSLLHOOKSTRUCT>(lParam);

                // Scale coordinates using config value
                double scale = WindowManager.Instance.GetScale();
                int scaledX = (int)(hookStruct.Pt.X / scale);
                int scaledY = (int)(hookStruct.Pt.Y / scale);

                // Convert screen coordinates to scrcpy window coordinates
                if (ConvertToWindowCoordinates(scaledX, scaledY, out int windowX, out int windowY))
                {
                    switch ((int)wParam)
                    {
                        case Win32Api.WM_LBUTTONDOWN:
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

                        case Win32Api.WM_LBUTTONUP:
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

                        case Win32Api.WM_MOUSEMOVE:
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

            return Win32Api.CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            StopReporting();
        }
    }
}