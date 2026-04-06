using NLog;
using NLua;
using System.Collections.Concurrent;

namespace Garden.Bots
{
    public class LuaBot
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly Lua _lua;
        private readonly StateDetector _stateDetector;
        private readonly ActionPlayer _actionPlayer;

        public LuaBot(string scriptPath, StateDetector stateDetector, ActionPlayer actionPlayer)
        {
            _stateDetector = stateDetector;
            _actionPlayer = actionPlayer;

            _lua = new Lua();
            _lua.State.Encoding = System.Text.Encoding.UTF8;

            _lua["getState"] = (Func<string>)(() => _stateDetector.CurrentState);
            _lua["queueAction"] = (Action<string>)(actionName => QueueAction(actionName, null));
            _lua["queueActionAt"] = (Action<string, string>)((actionName, roiName) => QueueAction(actionName, roiName));
            _lua["getRoiScore"] = (Func<string, double>)(roiName => GetRoiScore(roiName));

            _lua.DoFile(scriptPath);
        }

        private void QueueAction(string actionName, string? roiName)
        {
            if (roiName == null)
            {
                _actionPlayer.QueueReplay(actionName);
                return;
            }

            var roiInfo = _stateDetector.RoiDetectionInfos.FirstOrDefault(r => r.RoiName == roiName);
            if (roiInfo.RoiName == null)
            {
                Logger.Error($"ROI '{roiName}' not found in detection results");
                return;
            }

            _actionPlayer.QueueReplayWithOffset(actionName, roiInfo.Center.X, roiInfo.Center.Y);
        }

        private double GetRoiScore(string roiName)
        {
            var snapshot = _stateDetector.Snapshot;
            var roiInfo = snapshot.RoiDetectionInfos.FirstOrDefault(r => r.RoiName == roiName);
            if (roiInfo.RoiName == null)
            {
                Logger.Warn($"getRoiScore: ROI '{roiName}' not found");
                return double.MaxValue;
            }
            return roiInfo.MinVal;
        }

        public void QueueStateResponse()
        {
            var onFrame = _lua["onFrame"] as LuaFunction;
            if (onFrame == null)
            {
                Logger.Error("gardenbot.lua does not define an onFrame() function");
                return;
            }

            onFrame.Call();
        }
    }
}
