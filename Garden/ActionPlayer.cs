using System.Runtime.InteropServices;
using System.Text.Json;
using NLog;
using System.Collections.Concurrent;

namespace Garden
{
    public class ActionPlayer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        public class ActionEventArgs : EventArgs
        {
            public int WindowX { get; set; }
            public int WindowY { get; set; }
            public bool IsMouseDown { get; set; }
        }

        // Events for visual feedback
        public event EventHandler<ActionEventArgs>? ActionPerformed;
        // Reuse MouseEvent structure from MouseEventRecorder
        public class MouseEvent
        {
            public DateTime Timestamp { get; set; }
            public int X { get; set; } // Window-relative coordinates
            public int Y { get; set; } // Window-relative coordinates
            public bool IsMouseDown { get; set; } // true for down, false for up
        }

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

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private const int SM_XVIRTUALSCREEN = 76;  // Left of virtual screen
        private const int SM_YVIRTUALSCREEN = 77;  // Top of virtual screen

        private readonly string _actionDirectory;
        public ActionPlayer(string actionDirectory)
        {
            _actionDirectory = actionDirectory;
        }

        public List<MouseEvent>? LoadAction(string filename)
        {
            string filePath = Path.Combine(_actionDirectory, filename);

            if (!File.Exists(filePath))
            {
                Logger.Error($"Action file not found: {filePath}");
                return null;
            }

            try
            {
                string jsonString = File.ReadAllText(filePath);
                var events = JsonSerializer.Deserialize<List<MouseEvent>>(jsonString);

                if (events == null || events.Count == 0)
                {
                    Logger.Info($"No events found in {filename}");
                    return null;
                }

                Logger.Info($"Loaded {events.Count} events from {filename}");
                return events;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading action file: {ex.Message}");
                return null;
            }
        }

        public void ReplayAction(string filename)
        {
            var events = LoadAction(filename);
            if (events == null) return;

            Logger.Info($"Replaying {events.Count} mouse events...");

            // Get scrcpy window for coordinate conversion
            IntPtr hWnd = WindowManager.Instance.GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                Logger.Error("Cannot find scrcpy window for replay.");
                return;
            }

            // Bring scrcpy window to foreground and wait for it to be active
            SetForegroundWindow(hWnd);
            while (GetForegroundWindow() != hWnd)
            {
                Thread.Sleep(100);
            }

            DateTime? lastEventTime = null;

            foreach (var mouseEvent in events)
            {
                // Calculate delay based on timestamp difference
                if (lastEventTime.HasValue)
                {
                    var timeDelta = mouseEvent.Timestamp - lastEventTime.Value;
                    int delayMs = (int)timeDelta.TotalMilliseconds;
                    if (delayMs > 0 && delayMs < 5000) // Cap at 5 seconds max delay
                    {
                        Thread.Sleep(delayMs);
                    }
                }
                lastEventTime = mouseEvent.Timestamp;

                // Convert window-relative coordinates back to screen coordinates
                if (ConvertToScreenCoordinates(mouseEvent.X, mouseEvent.Y, out int screenX, out int screenY))
                {
                    // Move to position first
                    InputManager.Move(screenX, screenY);
                    Thread.Sleep(10); // Small delay for movement

                    // Perform mouse action based on IsMouseDown
                    Logger.Info($"Mouse {(mouseEvent.IsMouseDown ? "down" : "up")} at ({mouseEvent.X}, {mouseEvent.Y}) -> screen ({screenX}, {screenY})");
                    InputManager.MouseEvent(mouseEvent.IsMouseDown);

                    // Fire event for visual feedback
                    ActionPerformed?.Invoke(this, new ActionEventArgs
                    {
                        WindowX = mouseEvent.X,
                        WindowY = mouseEvent.Y,
                        IsMouseDown = mouseEvent.IsMouseDown
                    });
                }
            }

            Logger.Info("Replay completed.");
        }

        public void QueueReplay(string filename, ConcurrentQueue<MouseEvent> actionQueue)
        {
            var events = LoadAction(filename);
            if (events == null) return;

            Logger.Info($"Queueing {events.Count} events for replay");
            foreach (var evt in events)
            {
                actionQueue.Enqueue(evt);
            }
        }

        public void ExecuteAction(MouseEvent mouseEvent)
        {
            // Convert window-relative coordinates back to screen coordinates
            if (ConvertToScreenCoordinates(mouseEvent.X, mouseEvent.Y, out int screenX, out int screenY))
            {
                // Move to position first
                InputManager.Move(screenX, screenY);
                Thread.Sleep(10); // Small delay for movement

                // Perform mouse action based on IsMouseDown
                InputManager.MouseEvent(mouseEvent.IsMouseDown);

                // Fire event for visual feedback
                ActionPerformed?.Invoke(this, new ActionEventArgs
                {
                    WindowX = mouseEvent.X,
                    WindowY = mouseEvent.Y,
                    IsMouseDown = mouseEvent.IsMouseDown
                });
            }
        }

        private bool ConvertToScreenCoordinates(int windowX, int windowY, out int screenX, out int screenY)
        {
            screenX = 0;
            screenY = 0;

            IntPtr hWnd = WindowManager.Instance.GetScrcpyWindowHandle();
            if (hWnd == IntPtr.Zero)
            {
                Logger.Info("Could not find scrcpy window for replay");
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