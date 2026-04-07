using Garden;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

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
            public bool IsMouseMove { get; set; } = false;
        }

        private readonly string _actionDirectory;
        private readonly ConcurrentQueue<MouseEvent> _actionQueue;

        // real time information of executing action
        private int _latestX, _latestY;
        private int _nextX, _nextY;
        private DateTime _latestTime;
        private DateTime _nextTime;
        private DateTime _lastActionTime = DateTime.MinValue;
        private DateTime _lastActionTimestamp = DateTime.MinValue;

        // Current cursor position for visualization
        private OpenCvSharp.Point? _currentCursorPosition = null;
        public OpenCvSharp.Point? CurrentCursorPosition => _currentCursorPosition;

        public ActionPlayer(string actionDirectory, ConcurrentQueue<MouseEvent> actionQueue)
        {
            _actionDirectory = actionDirectory;
            _actionQueue = actionQueue;
        }

        private List<MouseEvent>? LoadAction(string actionName)
        {
            string actionFileName = $"{actionName}.json";
            string filePath = Path.Combine(_actionDirectory, actionFileName);

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
                throw;
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
                    IsMouseDown = evt.IsMouseDown,
                    IsMouseMove = evt.IsMouseMove
                };
                _actionQueue.Enqueue(modifiedEvent);
            }
        }

        private OpenCvSharp.Point? CalculateCurrentCursorPosition(DateTime currentTime)
        {
            // Calculate interpolation progress (0.0 to 1.0)
            double totalDuration = (_nextTime - _latestTime).TotalMilliseconds;
            double elapsed = (currentTime - _latestTime).TotalMilliseconds;
            double progress = Math.Clamp(elapsed / totalDuration, 0.0, 1.0);

            // Interpolate position
            int currentX = (int)(_latestX + (_nextX - _latestX) * progress);
            int currentY = (int)(_latestY + (_nextY - _latestY) * progress);

            return new OpenCvSharp.Point(currentX, currentY);
        }

        public void StepAction(DateTime time)
        {
            // Queue is empty - clear visualization and reset timing
            if (!_actionQueue.TryPeek(out var nextAction))
            {
                _currentCursorPosition = null;
                _lastActionTime = DateTime.MinValue;
                _lastActionTimestamp = DateTime.MinValue;
                return;
            }

            // 1. Pop and record down/up information
            MouseEvent? actionToExecute = null;
            var timeSinceLastAction = time - _lastActionTime;
            var requiredDelay = nextAction.Timestamp - _lastActionTimestamp;

            if (timeSinceLastAction >= requiredDelay)
            {
                _actionQueue.TryDequeue(out var action);
                Debug.Assert(action != null);
                _lastActionTime = time;
                _lastActionTimestamp = action.Timestamp;
                actionToExecute = action;

                if (action.IsMouseDown || action.IsMouseMove)
                {
                    // Record latest/next information for interpolation
                    if (_actionQueue.TryPeek(out var upcomingAction))
                    {
                        _latestX = action.X;
                        _latestY = action.Y;
                        _latestTime = time;
                        _nextX = upcomingAction.X;
                        _nextY = upcomingAction.Y;
                        _nextTime = time + (upcomingAction.Timestamp - action.Timestamp);
                    }
                }
            }

            // 2. Interpolate movement
            _currentCursorPosition = CalculateCurrentCursorPosition(time);
            if (_currentCursorPosition.HasValue)
            {
                if (WindowManager.Instance.ConvertToScreenCoordinates(_currentCursorPosition.Value.X, _currentCursorPosition.Value.Y, out int screenX, out int screenY))
                {
                    InputManager.Move(screenX, screenY);
                }
            }

            // 3. Click if it's time
            if (actionToExecute != null && !actionToExecute.IsMouseMove)
            {
                Thread.Sleep(10); // Small delay for movement
                InputManager.MouseEvent(actionToExecute.IsMouseDown);
            }

            // Clear queue after having emptied
            if (_actionQueue.IsEmpty)
            {
                _currentCursorPosition = null;
                _lastActionTime = DateTime.MinValue;
                _lastActionTimestamp = DateTime.MinValue;
                return;
            }
        }
    }
}