using NLog;
using System.Collections.Concurrent;

namespace Garden.Bots
{
    public abstract class BotBase
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        protected ConcurrentQueue<ActionPlayer.MouseEvent>? ActionQueue { get; private set; }
        protected ActionPlayer ActionPlayer { get; private set; }
        protected StateDetector StateDetector { get; private set; }

        public void Initialize(ConcurrentQueue<ActionPlayer.MouseEvent> actionQueue, StateDetector detector, ActionPlayer actionPlayer)
        {
            ActionQueue = actionQueue;
            ActionPlayer = actionPlayer;
            StateDetector = detector;
        }

        protected void QueueAction(string actionName)
        {
            if (ActionQueue == null || ActionPlayer == null) return;

            if (!actionName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                actionName += ".json";
            }

            ActionPlayer.QueueReplay(actionName);
        }

        protected void QueueAction(string roiName, string actionName)
        {
            if (ActionQueue == null || ActionPlayer == null) return;

            if (!actionName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                actionName += ".json";
            }

            var roiInfo = StateDetector.RoiDetectionInfos.FirstOrDefault(r => r.RoiName == roiName);
            if (roiInfo.RoiName == null)
            {
                Logger.Error($"ROI '{roiName}' not found in detection results");
                return;
            }

            ActionPlayer.QueueReplayWithOffset(actionName, roiInfo.Center.X, roiInfo.Center.Y);
        }
        public abstract void HandleState(string stateName);
    }
}
