using System.Runtime.InteropServices;
using System.Text.Json;
using NLog;
using System.Collections.Concurrent;

namespace Garden
{
    public class ActionPlayer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Reuse MouseEvent structure from MouseEventRecorder
        public class MouseEvent
        {
            public DateTime Timestamp { get; set; }
            public int X { get; set; } // Window-relative coordinates
            public int Y { get; set; } // Window-relative coordinates
            public bool IsMouseDown { get; set; } // true for down, false for up
        }

        private readonly string _actionDirectory;
        private MouseEvent? _lastDownEvent = null;

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
            Win32Api.SetForegroundWindow(hWnd);
            while (Win32Api.GetForegroundWindow() != hWnd)
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
                if (WindowManager.Instance.ConvertToScreenCoordinates(mouseEvent.X, mouseEvent.Y, out int screenX, out int screenY))
                {
                    // Move to position first
                    InputManager.Move(screenX, screenY);
                    Thread.Sleep(10); // Small delay for movement

                    // Perform mouse action based on IsMouseDown
                    Logger.Info($"Mouse {(mouseEvent.IsMouseDown ? "down" : "up")} at ({mouseEvent.X}, {mouseEvent.Y}) -> screen ({screenX}, {screenY})");
                    InputManager.MouseEvent(mouseEvent.IsMouseDown);
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
            if (WindowManager.Instance.ConvertToScreenCoordinates(mouseEvent.X, mouseEvent.Y, out int screenX, out int screenY))
            {
                // Move to position first
                InputManager.Move(screenX, screenY);
                Thread.Sleep(10); // Small delay for movement

                // Perform mouse action based on IsMouseDown
                InputManager.MouseEvent(mouseEvent.IsMouseDown);
            }
        }

    }
}