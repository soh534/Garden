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
        private readonly ConcurrentQueue<MouseEvent> _actionQueue;
        private MouseEvent? _lastDownEvent = null;

        public ActionPlayer(string actionDirectory, ConcurrentQueue<MouseEvent> actionQueue)
        {
            _actionDirectory = actionDirectory;
            _actionQueue = actionQueue;
        }

        private List<MouseEvent>? LoadAction(string actionName)
        {
            string filePath = Path.Combine(_actionDirectory, actionName);

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
                    Logger.Info($"No events found in {actionName}");
                    return null;
                }

                Logger.Info($"Loaded {events.Count} events from {actionName}");
                return events;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading action file: {ex.Message}");
                return null;
            }
        }

        public void QueueReplay(string actionName)
        {
            var events = LoadAction(actionName);
            if (events == null) return;

            Logger.Info($"Queueing {events.Count} events for replay");
            foreach (var evt in events)
            {
                _actionQueue.Enqueue(evt);
            }
        }

        public void QueueReplayWithOffset(string actionName, int roiCenterX, int roiCenterY)
        {
            var events = LoadAction(actionName);
            if (events == null || events.Count == 0) return;

            var firstEvent = events[0];
            int offsetX = roiCenterX - firstEvent.X;
            int offsetY = roiCenterY - firstEvent.Y;

            Logger.Info($"Queueing {events.Count} events based on ROI center ({roiCenterX}, {roiCenterY}), offset ({offsetX}, {offsetY})");

            // Apply offset to all events and enqueue
            foreach (var evt in events)
            {
                var modifiedEvent = new MouseEvent
                {
                    Timestamp = evt.Timestamp,
                    X = evt.X + offsetX,
                    Y = evt.Y + offsetY,
                    IsMouseDown = evt.IsMouseDown
                };
                _actionQueue.Enqueue(modifiedEvent);
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