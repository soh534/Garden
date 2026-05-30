using NLog;
using NLua;

namespace Garden.Bots
{
    public class LuaBot : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly Lua _lua;
        private readonly RoiDetector _roiDetector;
        private readonly ActionPlayer _actionPlayer;

        public LuaBot(string scriptPath, RoiDetector roiDetector, ActionPlayer actionPlayer)
        {
            _scriptPath = scriptPath;
            _roiDetector = roiDetector;
            _actionPlayer = actionPlayer;

            _lua = new Lua();
            _lua.State.Encoding = System.Text.Encoding.UTF8;

            _lua["queueWait"]     = (Action<int>)(ms => _actionPlayer.QueueWait(ms));
            _lua["waitMs"]        = (Action<int>)(ms => Thread.Sleep(ms));
            _lua["getOcrInt"]     = (Func<string, int>)(key => GetOcrInt(key));
            _lua["queueAction"]   = (Action<string>)(actionName => QueueAction(actionName, null));
            _lua["queueActionAt"] = (Action<string, string>)((actionName, roiName) => QueueAction(actionName, roiName));
            _lua["getRoiScore"]   = (Func<string, double>)(roiName => GetRoiScore(roiName));
            _lua["roiVisible"]    = (Func<string, bool>)(name => RoiVisible(name));

            _lua.DoFile(scriptPath);

            _watcher = new FileSystemWatcher(Path.GetDirectoryName(scriptPath)!, Path.GetFileName(scriptPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnScriptFileChanged;
            _watcher.Created += OnScriptFileChanged;
            _watcher.Renamed += (_, _) => OnScriptFileChanged(null, null!);
        }

        private readonly string _scriptPath;
        private volatile bool _enabled = false;
        private volatile bool _reloadPending = false;
        private readonly FileSystemWatcher _watcher;
        private DateTime _lastWatcherEvent = DateTime.MinValue;

        public void Enable()   => _enabled = true;
        public void Disable()  => _enabled = false;

        public void Dispose()
        {
            _watcher.Dispose();
            _lua.Dispose();
        }

        private void OnScriptFileChanged(object? sender, FileSystemEventArgs e)
        {
            if ((DateTime.UtcNow - _lastWatcherEvent).TotalMilliseconds < 500) { return; }
            _lastWatcherEvent = DateTime.UtcNow;
            _reloadPending = true;
        }

        private void ReloadScript()
        {
            try
            {
                _lua.DoFile(_scriptPath);
                _reloadPending = false;
                Console.WriteLine("[LuaBot] gardenbot.lua reloaded.");
            }
            catch (Exception ex) { Logger.Error($"Reload failed: {ex.Message}"); }
        }

        private bool RoiVisible(string name)
        {
            bool found = _roiDetector.TryFindRoi(name, out RoiDetector.DetectedRoiInfo roiInfo);
            if (found) { _roiDetector.ProcessReadAreas(name, roiInfo); }
            return found;
        }

        private void QueueAction(string actionName, string? roiName)
        {
            if (!_enabled) { return; }
            if (roiName == null)
            {
                _actionPlayer.QueueReplay(actionName);
                return;
            }

            _roiDetector.TryFindRoi(roiName, out RoiDetector.DetectedRoiInfo roiInfo);
            if (roiInfo.RoiName == null)
            {
                Logger.Error($"ROI '{roiName}' not found in detection results");
                return;
            }

            _actionPlayer.QueueReplayWithOffset(actionName, roiInfo.ClickPoint.X, roiInfo.ClickPoint.Y);
        }

        private int GetOcrInt(string key)
        {
            var snapshot = _roiDetector.Snapshot;
            if (snapshot.OcrReadings.TryGetValue(key, out int value))
            {
                return value;
            }
            Logger.Warn($"getOcrInt: key '{key}' not found in OCR readings");
            return -1;
        }

        private double GetRoiScore(string roiName)
        {
            _roiDetector.TryFindRoi(roiName, out RoiDetector.DetectedRoiInfo roiInfo);
            if (roiInfo.RoiName == null)
            {
                Logger.Warn($"getRoiScore: ROI '{roiName}' not found");
                return double.MaxValue;
            }
            return roiInfo.Score;
        }

        public void Run(CancellationToken token)
        {
            var main = _lua["main"] as LuaFunction;
            if (main == null)
            {
                Logger.Error("gardenbot.lua does not define a main() function");
                return;
            }

            while (!token.IsCancellationRequested)
            {
                if (_reloadPending)
                {
                    ReloadScript();
                    main = _lua["main"] as LuaFunction;
                    if (main == null) { Logger.Error("No main() after reload"); Thread.Sleep(500); continue; }
                }
                try { main!.Call(); }
                catch (Exception ex) { Logger.Error($"Bot error: {ex.Message}"); }
                Thread.Sleep(100);
            }
        }

    }
}
