using NLog;
using NLua;
using System.Text.Json;

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
            _lua["waitMs"]        = (Action<int>)(ms => WaitMs(ms));
            _lua["getOcrInt"]     = (Func<string, int>)(key => GetOcrInt(key));
            _lua["queueAction"]   = (Action<string>)(actionName => QueueAction(actionName, null));
            _lua["queueActionAt"] = (Action<string, string>)((actionName, roiName) => QueueAction(actionName, roiName));
            _lua["getRoiScore"]   = (Func<string, double>)(roiName => GetRoiScore(roiName));
            _lua["roiVisible"]    = (Func<string, bool>)(name => RoiVisible(name));
            _lua["log"]           = (Action<string>)(msg => Console.WriteLine($"[bot] {msg}"));
            _lua["stateSave"]     = (Action<LuaTable>)(t => StateSave(t));
            _lua["stateLoad"]     = (Func<object?>)(StateLoad);

            string stdlibPath = Path.Combine(AppContext.BaseDirectory, "stdlib.lua");
            if (File.Exists(stdlibPath))
            {
                _lua.DoFile(stdlibPath);
            }
            else
            {
                Logger.Warn($"stdlib.lua not found at {stdlibPath}; stdlib combinators unavailable");
            }

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
        private volatile string? _pendingEval = null;
        private volatile bool _evaluating = false;
        private volatile bool _abortEval = false;
        private readonly FileSystemWatcher _watcher;
        private DateTime _lastWatcherEvent = DateTime.MinValue;
        private CancellationToken _token;

        private class BotStoppedException : Exception { }
        private class EvalAbortedException : Exception { }

        private static bool HasInChain<T>(Exception ex) where T : Exception
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is T) { return true; }
            }
            return false;
        }

        private void CheckAbort()
        {
            _token.ThrowIfCancellationRequested();
            if (_evaluating)
            {
                if (_abortEval) { throw new EvalAbortedException(); }
                return;
            }
            if (!_enabled) { throw new BotStoppedException(); }
        }

        private void WaitMs(int ms)
        {
            int elapsed = 0;
            while (elapsed < ms)
            {
                CheckAbort();
                int chunk = Math.Min(50, ms - elapsed);
                Thread.Sleep(chunk);
                elapsed += chunk;
            }
            CheckAbort();
        }

        public void Enable()   => _enabled = true;
        public void Disable()  => _enabled = false;
        public void Eval(string code) => _pendingEval = code;
        public void AbortEval() => _abortEval = true;

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

        private void RunEval(string code)
        {
            _evaluating = true;
            _abortEval = false;
            try
            {
                var results = _lua.DoString(code);
                if (results != null && results.Length > 0)
                {
                    foreach (var r in results) { Console.WriteLine($"[lua] => {r ?? "nil"}"); }
                }
                else
                {
                    Console.WriteLine("[lua] ok");
                }
            }
            catch (Exception ex)
            {
                if (HasInChain<EvalAbortedException>(ex)) { Console.WriteLine("[lua] aborted"); }
                else { Console.WriteLine($"[lua] error: {ex.Message}"); }
            }
            finally { _evaluating = false; _abortEval = false; }
        }

        // State lives next to the script unless GARDEN_STATE_DIR points elsewhere
        // (e.g. a synced folder shared between machines). Data (scripts, ROIs,
        // actions) is durable and versioned; state is mutable runtime memory.
        private string StatePath
        {
            get
            {
                string? dir = Environment.GetEnvironmentVariable("GARDEN_STATE_DIR");
                if (string.IsNullOrEmpty(dir)) { return Path.Combine(Path.GetDirectoryName(_scriptPath)!, "state.json"); }
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "state.json");
            }
        }

        // Persist a Lua table as JSON, atomically (temp file + rename) so a kill
        // mid-write can never leave a corrupt state file.
        private void StateSave(LuaTable table)
        {
            try
            {
                string json = JsonSerializer.Serialize(LuaToObject(table), new JsonSerializerOptions { WriteIndented = true });
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, StatePath, true);
            }
            catch (Exception ex) { Logger.Error($"stateSave failed: {ex.Message}"); }
        }

        private object? StateLoad()
        {
            try
            {
                if (!File.Exists(StatePath)) { return null; }
                using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                return JsonToLua(doc.RootElement);
            }
            catch (Exception ex)
            {
                Logger.Error($"stateLoad failed: {ex.Message}");
                return null;
            }
        }

        private static object? LuaToObject(object? value)
        {
            if (value is LuaTable t)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var key in t.Keys)
                {
                    dict[key.ToString()!] = LuaToObject(t[key]);
                }
                return dict;
            }
            return value;
        }

        // JSON object keys that parse as integers come back as numeric Lua keys,
        // so tables keyed by account number round-trip correctly.
        private object? JsonToLua(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    LuaTable t = NewTable();
                    foreach (var prop in el.EnumerateObject())
                    {
                        object key = long.TryParse(prop.Name, out long n) ? n : prop.Name;
                        t[key] = JsonToLua(prop.Value);
                    }
                    return t;
                }
                case JsonValueKind.Array:
                {
                    LuaTable t = NewTable();
                    long i = 1;
                    foreach (var item in el.EnumerateArray())
                    {
                        t[i++] = JsonToLua(item);
                    }
                    return t;
                }
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number: return el.TryGetInt64(out long l) ? l : el.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }

        private LuaTable NewTable() => (LuaTable)_lua.DoString("return {}")[0];

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
            CheckAbort();
            bool found = _roiDetector.TryFindRoi(name, out RoiDetector.DetectedRoiInfo roiInfo);
            if (found) { _roiDetector.ProcessReadAreas(name, roiInfo); }
            return found;
        }

        private void QueueAction(string actionName, string? roiName)
        {
            if (!_enabled && !_evaluating) { return; }
            if (roiName == null)
            {
                _actionPlayer.QueueReplay(actionName);
            }
            else
            {
                _roiDetector.TryFindRoi(roiName, out RoiDetector.DetectedRoiInfo roiInfo);
                if (roiInfo.RoiName == null)
                {
                    Logger.Error($"ROI '{roiName}' not found in detection results");
                    return;
                }

                _actionPlayer.QueueReplayWithOffset(actionName, roiInfo.ClickPoint.X, roiInfo.ClickPoint.Y);
            }

            WaitForActions();
        }

        private void WaitForActions()
        {
            while (!_actionPlayer.IsIdle)
            {
                CheckAbort();
                Thread.Sleep(20);
            }
            CheckAbort();
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
            _token = token;
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
                var evalCode = Interlocked.Exchange(ref _pendingEval, null);
                if (evalCode != null) { RunEval(evalCode); }
                if (!_enabled) { Thread.Sleep(100); continue; }
                try { main!.Call(); }
                catch (Exception ex)
                {
                    if (HasInChain<OperationCanceledException>(ex)) { break; }
                    if (HasInChain<BotStoppedException>(ex)) { /* bot stop: loop idles */ }
                    else { Logger.Error($"Bot error: {ex.Message}"); }
                }
                Thread.Sleep(100);
            }
        }

    }
}
