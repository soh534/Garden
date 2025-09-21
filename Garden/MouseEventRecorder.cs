using System.Runtime.InteropServices;
using System.Text.Json;

namespace Garden
{
    public class MouseEventRecorder
    {
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
        private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpmsg);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXVIRTUALSCREEN = 78; // Width of virtual screen
        private const int SM_CYVIRTUALSCREEN = 79; // Height of virtual screen
        private const int SM_XVIRTUALSCREEN = 76;  // Left of virtual screen
        private const int SM_YVIRTUALSCREEN = 77;  // Top of virtual screen


        private readonly List<MouseEvent> _recordedEvents;
        private readonly string _saveDirectory;
        private readonly string _windowTitle;
        private readonly double _scale;
        private LowLevelMouseProc _proc;
        private IntPtr _hookID = IntPtr.Zero;
        private bool _isRecording = false;

        public MouseEventRecorder(string saveDirectory, double scale = 1.0, string windowTitle = "Garden")
        {
            _recordedEvents = new List<MouseEvent>();
            _saveDirectory = saveDirectory;
            _windowTitle = windowTitle;
            _scale = scale;
            _proc = HookCallback;
        }

        public void StartRecording()
        {
            if (!_isRecording)
            {
                _recordedEvents.Clear();

                // Check if window exists before starting
                IntPtr hWnd = GetScrcpyWindowHandle();
                if (hWnd == IntPtr.Zero)
                {
                    Console.WriteLine("ERROR: Cannot find scrcpy window with title 'Garden'. Recording not started.");
                    return;
                }

                _hookID = SetHook(_proc);
                if (_hookID == IntPtr.Zero)
                {
                    Console.WriteLine("ERROR: Failed to install mouse hook. Recording not started.");
                    return;
                }

                _isRecording = true;
                Console.WriteLine("Mouse recording started (scrcpy window only)...");
            }
            else
            {
                Console.WriteLine("Recording is already active.");
            }
        }

        public void StopRecording()
        {
            if (_isRecording)
            {
                UnhookWindowsHookEx(_hookID);
                _isRecording = false;
                Console.WriteLine($"Mouse recording stopped. Recorded {_recordedEvents.Count} events.");
            }
        }

        public void ResetRecording()
        {
            if (_isRecording)
            {
                int previousCount = _recordedEvents.Count;
                _recordedEvents.Clear();
                Console.WriteLine($"Recording buffer cleared. Removed {previousCount} events. Recording continues...");
            }
            else
            {
                Console.WriteLine("No active recording to reset. Use 'start recording' first.");
            }
        }

        public void SaveRecording(string filename)
        {
            // Auto-end recording if still active
            if (_isRecording)
            {
                StopRecording();
            }

            if (_recordedEvents.Count == 0)
            {
                Console.WriteLine("No mouse events to save.");
                return;
            }

            Directory.CreateDirectory(_saveDirectory);
            string filePath = Path.Combine(_saveDirectory, filename);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string jsonString = JsonSerializer.Serialize(_recordedEvents, options);
            File.WriteAllText(filePath, jsonString);
            Console.WriteLine($"Mouse events saved to {filePath}");
        }

        private IntPtr GetScrcpyWindowHandle()
        {
            return FindWindow(null, _windowTitle);
        }


        private bool ConvertToWindowCoordinates(int screenX, int screenY, out int windowX, out int windowY)
        {
            windowX = 0;
            windowY = 0;

            IntPtr hWnd = GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
                return false;

            // Get client rectangle (just the content area)
            GetClientRect(hWnd, out RECT clientRect);

            // Get client area top-left in screen coordinates
            POINT clientTopLeft = new POINT { x = clientRect.Left, y = clientRect.Top };
            ClientToScreen(hWnd, ref clientTopLeft);

            Console.WriteLine($"DEBUG RECORD: Client rect: ({clientRect.Left}, {clientRect.Top}) to ({clientRect.Right}, {clientRect.Bottom})");
            Console.WriteLine($"DEBUG RECORD: Client top-left in screen coords: ({clientTopLeft.x}, {clientTopLeft.y})");

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
            if (nCode >= 0 && _isRecording)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                // Only process and debug on actual clicks, not mouse movement
                switch ((int)wParam)
                {
                    case WM_LBUTTONDOWN:
                    case WM_LBUTTONUP:
                        Console.WriteLine($"DEBUG RECORD: Raw hook coords: ({hookStruct.pt.x}, {hookStruct.pt.y})");

                        // Scale coordinates using config value
                        int scaledX = (int)(hookStruct.pt.x / _scale);
                        int scaledY = (int)(hookStruct.pt.y / _scale);

                        Console.WriteLine($"DEBUG RECORD: After scaling (/{_scale:F2}): ({scaledX}, {scaledY})");

                        // Convert screen coordinates to scrcpy window coordinates
                        if (ConvertToWindowCoordinates(scaledX, scaledY, out int windowX, out int windowY))
                        {
                            Console.WriteLine($"DEBUG RECORD: Final window coords saved: ({windowX}, {windowY})");
                            if ((int)wParam == WM_LBUTTONDOWN)
                            {
                                var downEvent = new MouseEvent
                                {
                                    Timestamp = DateTime.Now,
                                    X = windowX,
                                    Y = windowY,
                                    IsMouseDown = true
                                };
                                _recordedEvents.Add(downEvent);
                                Console.WriteLine($"Left button down at ({windowX}, {windowY})");
                            }
                            else // WM_LBUTTONUP
                            {
                                var upEvent = new MouseEvent
                                {
                                    Timestamp = DateTime.Now,
                                    X = windowX,
                                    Y = windowY,
                                    IsMouseDown = false
                                };
                                _recordedEvents.Add(upEvent);
                                Console.WriteLine($"Left button up at ({windowX}, {windowY})");
                            }
                        }
                        break;
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            StopRecording();
        }
    }
}