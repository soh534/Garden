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
        }

        private readonly string _actionDirectory;
        private readonly ConcurrentQueue<MouseEvent> _actionQueue;

        // real time information of executing action
        private bool _isInterpolating = false;
        private int _downX, _downY;
        private int _upX, _upY;
        private DateTime _downTime;
        private DateTime _upTime;
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

        private OpenCvSharp.Point? CalculateCurrentCursorPosition(DateTime currentTime)
        {
            if (!_isInterpolating)
                return null;

            // Calculate interpolation progress (0.0 to 1.0)
            double totalDuration = (_upTime - _downTime).TotalMilliseconds;
            double elapsed = (currentTime - _downTime).TotalMilliseconds;
            double progress = Math.Clamp(elapsed / totalDuration, 0.0, 1.0);

            // Interpolate position
            int currentX = (int)(_downX + (_upX - _downX) * progress);
            int currentY = (int)(_downY + (_upY - _downY) * progress);

            return new OpenCvSharp.Point(currentX, currentY);
        }

        public void StepAction(DateTime time)
        {
            // Process actions - check timing before dequeuing
            if (_actionQueue.TryPeek(out var nextAction))
            {
                // Check if enough time has elapsed based on timestamps
                var timeSinceLastAction = DateTime.Now - _lastActionTime;
                var requiredDelay = nextAction.Timestamp - _lastActionTimestamp;
                if (timeSinceLastAction >= requiredDelay)
                {
                    _actionQueue.TryDequeue(out var action);
                    Debug.Assert(action != null);
                    _lastActionTime = DateTime.Now;
                    _lastActionTimestamp = action.Timestamp;

                    if (action.IsMouseDown)
                    {
                        // Mouse down - peek at next action to get up position
                        if (_actionQueue.TryPeek(out var upAction))
                        {
                            _downX = action.X;
                            _downY = action.Y;
                            _downTime = DateTime.Now;
                            _upX = upAction.X;
                            _upY = upAction.Y;
                            _upTime = DateTime.Now + (upAction.Timestamp - action.Timestamp);
                            _isInterpolating = true;
                        }
                    }
                    else
                    {
                        // Mouse up - stop interpolating
                        _isInterpolating = false;
                    }

                    // Convert window-relative coordinates back to screen coordinates
                    if (WindowManager.Instance.ConvertToScreenCoordinates(action.X, action.Y, out int screenX, out int screenY))
                    {
                        // Move to position first
                        InputManager.Move(screenX, screenY);
                        Thread.Sleep(10); // Small delay for movement

                        // Perform mouse action based on IsMouseDown
                        InputManager.MouseEvent(action.IsMouseDown);
                    }
                }
            }

            // Move mouse to interpolated position if replaying
            _currentCursorPosition = CalculateCurrentCursorPosition(time);
            if (_currentCursorPosition.HasValue)
            {
                if (WindowManager.Instance.ConvertToScreenCoordinates(_currentCursorPosition.Value.X, _currentCursorPosition.Value.Y, out int screenX, out int screenY))
                {
                    InputManager.Move(screenX, screenY);
                }
            }
        }
    }
}