using System.Collections.Concurrent;

namespace Garden.Bots
{
    /// <summary>
    /// Base class for all bots. Override HandleState to implement your bot logic.
    /// Default implementation does nothing.
    /// </summary>
    public class BotBase
    {
        protected ConcurrentQueue<ActionPlayer.MouseEvent>? ActionQueue { get; private set; }
        protected StateDetector Detector { get; private set; }
        protected ActionPlayer ActionPlayer { get; private set; }

        /// <summary>
        /// Initialize the bot with action queue, detector, and action player
        /// </summary>
        public void Initialize(ConcurrentQueue<ActionPlayer.MouseEvent> actionQueue, StateDetector detector, ActionPlayer actionPlayer)
        {
            ActionQueue = actionQueue;
            Detector = detector;
            ActionPlayer = actionPlayer;
        }

        /// <summary>
        /// Called every frame when a state is detected.
        /// Override this method to implement your bot logic.
        /// </summary>
        /// <param name="stateName">The detected state name</param>
        /// <param name="detectedRois">The ROIs detected for this state</param>
        public virtual void HandleState(string stateName)
        {
            // Default: do nothing
        }

        /// <summary>
        /// Click on the center of a detected ROI by enqueuing click actions
        /// </summary>
        protected void Click(string roiName)
        {
            if (ActionQueue == null) return;

            // Strip extension for comparison
            string nameWithoutExt = roiName.Replace(".png", "");
            var roi = Detector.RoiDetectionInfos.FirstOrDefault(r => r.RoiName.Replace(".png", "") == nameWithoutExt);
            if (roi.Center.X > 0)
            {
                int centerX = roi.Center.X;
                int centerY = roi.Center.Y;

                // Enqueue mouse down
                ActionQueue.Enqueue(new ActionPlayer.MouseEvent
                {
                    Timestamp = DateTime.Now,
                    X = centerX,
                    Y = centerY,
                    IsMouseDown = true
                });

                // Enqueue mouse up (50ms later)
                ActionQueue.Enqueue(new ActionPlayer.MouseEvent
                {
                    Timestamp = DateTime.Now.AddMilliseconds(50),
                    X = centerX,
                    Y = centerY,
                    IsMouseDown = false
                });
            }
        }

        /// <summary>
        /// Swipe/drag from center of first ROI to center of second ROI
        /// </summary>
        protected void SwipeToRoiFromRoi(string fromRoiName, string toRoiName, int durationMs = 200)
        {
            if (ActionQueue == null) return;

            // Strip extensions for comparison
            string fromNameWithoutExt = fromRoiName.Replace(".png", "");
            string toNameWithoutExt = toRoiName.Replace(".png", "");
            var fromRoi = Detector.RoiDetectionInfos.FirstOrDefault(r => r.RoiName.Replace(".png", "") == fromNameWithoutExt);
            var toRoi = Detector.RoiDetectionInfos.FirstOrDefault(r => r.RoiName.Replace(".png", "") == toNameWithoutExt);

            if (fromRoi.Center.X > 0 && toRoi.Center.X > 0)
            {
                int startX = fromRoi.Center.X;
                int startY = fromRoi.Center.Y;
                int endX = toRoi.Center.X;
                int endY = toRoi.Center.Y;

                // Enqueue mouse down at start ROI center
                ActionQueue.Enqueue(new ActionPlayer.MouseEvent
                {
                    Timestamp = DateTime.Now,
                    X = startX,
                    Y = startY,
                    IsMouseDown = true
                });

                // Enqueue mouse up at end ROI center
                ActionQueue.Enqueue(new ActionPlayer.MouseEvent
                {
                    Timestamp = DateTime.Now.AddMilliseconds(durationMs),
                    X = endX,
                    Y = endY,
                    IsMouseDown = false
                });
            }
        }

        /// <summary>
        /// Swipe/drag from center of ROI to specified coordinates
        /// </summary>
        protected void SwipeFromRoi(string fromRoiName, int toX, int toY, int durationMs = 500)
        {
            if (ActionQueue == null) return;

            // Strip extension for comparison
            string fromNameWithoutExt = fromRoiName.Replace(".png", "");
            var fromRoi = Detector.RoiDetectionInfos.FirstOrDefault(r => r.RoiName.Replace(".png", "") == fromNameWithoutExt);

            if (fromRoi.Center.X > 0)
            {
                int startX = fromRoi.Center.X;
                int startY = fromRoi.Center.Y;

                // Enqueue mouse down at ROI center
                ActionQueue.Enqueue(new ActionPlayer.MouseEvent
                {
                    Timestamp = DateTime.Now,
                    X = startX,
                    Y = startY,
                    IsMouseDown = true
                });

                // Enqueue mouse up at specified coordinates
                ActionQueue.Enqueue(new ActionPlayer.MouseEvent
                {
                    Timestamp = DateTime.Now.AddMilliseconds(durationMs),
                    X = toX,
                    Y = toY,
                    IsMouseDown = false
                });
            }
        }

        /// <summary>
        /// Queue a saved action file for replay (adds .json extension if not present)
        /// </summary>
        protected void RunAction(string filename)
        {
            if (ActionQueue == null || ActionPlayer == null) return;

            if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                filename += ".json";
            }

            ActionPlayer.QueueReplay(filename, ActionQueue);
        }
    }
}
