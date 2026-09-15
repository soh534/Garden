using NLog;
using OpenCvSharp;
using System.Text.Json;

namespace Garden
{
    public class RoiDetector : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public class RoiData
        {
            public int x { get; set; }
            public int y { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public int? clickOffsetX { get; set; }
            public int? clickOffsetY { get; set; }
            public List<ReadArea> readAreas { get; set; } = new();
            public bool fixedLocation { get; set; } = false;
            // per-ROI override for animated/decorated icons whose present-score
            // wobbles above the global threshold (gardenlisticon: present =
            // 0.0007-0.0103, absent >= 0.20). null = global TemplateThreshold.
            public double? threshold { get; set; }

            public class ReadArea
            {
                public string name { get; set; } = "";
                public int x { get; set; }
                public int y { get; set; }
                public int width { get; set; }
                public int height { get; set; }
                public string? lang { get; set; }   // OCR language for this area; null = engine default
            }
        }

        public struct DetectedRoiInfo
        {
            public string RoiName;
            public Point Center;
            public Point ClickPoint;
            public double Score;
        }

        public record DetectionSnapshot(
            string? WaitingForRoi,
            DetectedRoiInfo? WaitingRoiResult,
            Dictionary<string, string> OcrReadings,
            Dictionary<string, Rect> ReadAreaRects,
            Dictionary<string, RoiScanResult> LatestScores
        );

        public record RoiScanResult(
            double Score, bool Detected,
            int CenterX, int CenterY,
            int ClickX, int ClickY,
            Dictionary<string, string> Readings);

        public const double TemplateThreshold = 0.005;

        private readonly string _roiDirectory;
        private readonly OcrReader _ocrReader;
        private readonly CancellationTokenSource _cts = new();

        private Dictionary<string, RoiData> _savedRoiData = new();
        private Dictionary<string, Mat> _roiMats = new();
        private FileSystemWatcher _fileWatcher = null!;
        private readonly object _roiMatsLock = new();
        private DateTime _lastWatcherEvent = DateTime.MinValue;

        private Mat? _latestFrame;
        private readonly object _frameLock = new();

        private volatile DetectionSnapshot _snapshot = new(null, null, new(), new(), new Dictionary<string, RoiScanResult>());
        public DetectionSnapshot Snapshot => _snapshot;

        private volatile bool _scanEnabled = false;
        public bool ScanEnabled => _scanEnabled;
        public void SetScanEnabled(bool on)
        {
            _scanEnabled = on;
            if (!on)
            {
                // Blank the overlay scores so stale values aren't shown while the scan is off.
                _snapshot = new DetectionSnapshot(_snapshot.WaitingForRoi, _snapshot.WaitingRoiResult, _snapshot.OcrReadings, _snapshot.ReadAreaRects, new Dictionary<string, RoiScanResult>());
            }
        }

        public RoiDetector(string roiDirectory, string debugDir, string ocrLang)
        {
            string tessPrefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
                ?? throw new InvalidOperationException("TESSDATA_PREFIX environment variable is not set. Run setup.ps1.");
            string tessDataPath = Path.Combine(tessPrefix, "tessdata");
            if (!Directory.Exists(tessDataPath))
            {
                throw new InvalidOperationException($"tessdata directory not found at {tessDataPath}. Run setup.ps1 to download language data.");
            }

            _ocrReader = new OcrReader(tessDataPath, debugDir, ocrLang);
            _roiDirectory = roiDirectory;
            LoadRoiData();
            LoadRoiMats();
            SetupFileWatcher();
            new Thread(ScanLoop) { IsBackground = true, Name = "RoiDetector.Scan" }.Start();
        }

        // signaled once per REAL frame delivered by the render loop (which is
        // itself gated on decoded-frame arrival): the scan loop BLOCKS on this
        // instead of polling -- identical pixels yield identical scores, so a
        // static screen must cost the scanner nothing
        private readonly AutoResetEvent _frameArrived = new(false);

        public void SetFrame(Mat frame)
        {
            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = frame.Clone();
            }
            _frameArrived.Set();
        }

        public bool TryFindRoi(string name, out DetectedRoiInfo info)
        {
            info = default;
            Mat? frame;
            lock (_frameLock)
            {
                frame = _latestFrame?.Clone();
            }
            if (frame == null) { return false; }

            try
            {
                bool found = TryFindRoiInFrame(frame, name, out info);
                _snapshot = new DetectionSnapshot(name, found ? info : null, _snapshot.OcrReadings, _snapshot.ReadAreaRects, _snapshot.LatestScores);
                LogDetection(name, found, info);
                return found;
            }
            finally
            {
                frame.Dispose();
            }
        }

        // --- Detection flight recorder ------------------------------------
        // Every TryFindRoi result is appended to roi_detections.log so the
        // tail always shows the bot's recent perception. Consecutive repeats
        // of the same (roi, outcome) collapse into a "repeated xN" line so
        // poll loops don't flush real history. Rotated into roi_detections.old
        // -- total disk is bounded, logging never stops.
        //
        // Sized for ~5 weeks at the observed ~20KB/h. The old 64KB cap held only
        // ~6h, which repeatedly rotated away the ONSET of a failure before anyone
        // could read it: on 08-17 the start of the gardenlisticon outage was
        // already gone by the time we looked, and a chronic login-OCR flake was
        // invisible in the window entirely. 16MB of text against the video ring's
        // 480MB is the right balance -- the text log IS the decision trail.
        private readonly object _detLogLock = new();
        private string? _detLogLastKey;
        private int _detLogRepeat;
        private bool _detLogRotateFailed;      // warn once per failure streak (guarded by _detLogLock)
        private const long DetLogMaxBytes = 8 * 1024 * 1024;

        private string DetLogDir
        {
            get
            {
                string? dir = Environment.GetEnvironmentVariable("GARDEN_STATE_DIR");
                if (!string.IsNullOrEmpty(dir)) { return dir; }
                return Path.GetDirectoryName(_roiDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
            }
        }

        private void LogDetection(string name, bool found, DetectedRoiInfo info)
        {
            lock (_detLogLock)
            {
                string key = name + "|" + found;
                if (key == _detLogLastKey)
                {
                    _detLogRepeat++;
                    return;
                }
                if (_detLogRepeat > 1)
                {
                    AppendDetLog($"                     ... repeated x{_detLogRepeat}");
                }
                _detLogLastKey = key;
                _detLogRepeat = 1;
                string score = info.RoiName == null ? "n/a" : info.Score.ToString("F4");
                AppendDetLog($"{DateTime.Now:MM-dd HH:mm:ss}  {(found ? "HIT " : "miss")}  {name}  score={score}");
            }
        }

        // Mirror a console message (script log(), bot errors, reloads) into the
        // flight recorder so decisions and detections interleave in one
        // remotely-readable stream. Flushes any pending repeat-collapse first.
        public void LogEvent(string msg)
        {
            lock (_detLogLock)
            {
                if (_detLogRepeat > 1)
                {
                    AppendDetLog($"                     ... repeated x{_detLogRepeat}");
                }
                _detLogLastKey = null;
                _detLogRepeat = 0;
                AppendDetLog($"{DateTime.Now:MM-dd HH:mm:ss}  [bot] {msg}");
            }
        }

        private void AppendDetLog(string line)
        {
            try
            {
                string path = Path.Combine(DetLogDir, "roi_detections.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > DetLogMaxBytes)
                {
                    // Rotation gets its OWN try: a failed move must never stop the
                    // append. Sharing one try turned a TRANSIENT lock into PERMANENT
                    // silent death -- the file stays over the cap, so every later
                    // append retries the same doomed move and never recovers.
                    // Observed 08-17 21:07: a leftover `tail -f` had followed the log
                    // through an earlier rotation and pinned .old, so File.Move could
                    // not overwrite it. The recorder went dark for 28 minutes while
                    // the bot completed four account visits, none of them logged.
                    // Plenty of things hold these files briefly -- OneDrive sync, AV,
                    // an editor, a human tailing the log -- so this path must degrade
                    // to "the log grows past its cap", never to "logging stops".
                    try
                    {
                        File.Move(path, Path.Combine(DetLogDir, "roi_detections.old"), true);
                        _detLogRotateFailed = false;
                    }
                    catch (Exception ex)
                    {
                        if (!_detLogRotateFailed)
                        {
                            _detLogRotateFailed = true;
                            Logger.Warn($"flight recorder rotation blocked ({ex.Message}) -- still appending; the log will exceed its {DetLogMaxBytes / (1024 * 1024)}MB cap until the lock clears");
                        }
                    }
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // the recorder must never break detection
            }
        }

        public string? FindBestRoi(IEnumerable<string> names)
        {
            Mat? frame;
            lock (_frameLock)
            {
                frame = _latestFrame?.Clone();
            }
            if (frame == null) { return null; }

            try
            {
                string? bestName = null;
                double bestScore = double.MaxValue;

                foreach (string name in names)
                {
                    if (TryFindRoiInFrame(frame, name, out DetectedRoiInfo info) && info.Score < bestScore)
                    {
                        bestScore = info.Score;
                        bestName = name;
                    }
                }

                return bestName;
            }
            finally
            {
                frame.Dispose();
            }
        }

        private bool TryFindRoiInFrame(Mat frame, string name, out DetectedRoiInfo info)
        {
            info = default;
            lock (_roiMatsLock)
            {
                if (!_savedRoiData.TryGetValue(name, out RoiData? roiData)) { return false; }
                if (!_roiMats.TryGetValue(name, out Mat? roiMat)) { return false; }

                DetectRoi(frame, roiMat, roiData, out double score,
                    out int minLocX, out int minLocY, out int centerX, out int centerY);
                int clickX = roiData.clickOffsetX.HasValue ? minLocX + roiData.clickOffsetX.Value : centerX;
                int clickY = roiData.clickOffsetY.HasValue ? minLocY + roiData.clickOffsetY.Value : centerY;

                info = new DetectedRoiInfo
                {
                    RoiName = name,
                    Center = new Point(centerX, centerY),
                    ClickPoint = new Point(clickX, clickY),
                    Score = score
                };

                return score < (roiData.threshold ?? TemplateThreshold);
            }
        }

        public void ProcessReadAreas(string roiName, DetectedRoiInfo roiInfo)
        {
            Mat? frame;
            lock (_frameLock)
            {
                frame = _latestFrame?.Clone();
            }
            if (frame == null) { return; }

            try
            {
                lock (_roiMatsLock)
                {
                    if (!_savedRoiData.TryGetValue(roiName, out var roiData)) { return; }
                    if (roiData.readAreas.Count == 0) { return; }
                    if (!_roiMats.TryGetValue(roiName, out var roiMat)) { return; }

                    int minLocX = roiInfo.Center.X - roiMat.Width / 2;
                    int minLocY = roiInfo.Center.Y - roiMat.Height / 2;

                    var ocrReadings = new Dictionary<string, string>();
                    var readAreaRects = new Dictionary<string, Rect>();

                    foreach (var readArea in roiData.readAreas)
                    {
                        int areaX = minLocX + readArea.x;
                        int areaY = minLocY + readArea.y;
                        int areaW = readArea.width;
                        int areaH = readArea.height;

                        areaX = Math.Max(0, Math.Min(areaX, frame.Width - 1));
                        areaY = Math.Max(0, Math.Min(areaY, frame.Height - 1));
                        areaW = Math.Min(areaW, frame.Width - areaX);
                        areaH = Math.Min(areaH, frame.Height - areaY);

                        if (areaW <= 0 || areaH <= 0) { continue; }

                        string key = $"{roiName}/{readArea.name}";
                        var readRect = new Rect(areaX, areaY, areaW, areaH);
                        readAreaRects[key] = readRect;
                        using Mat readMat = new Mat(frame, readRect);
                        ocrReadings[key] = _ocrReader.Read(readMat, key, readArea.lang);
                    }

                    _snapshot = new DetectionSnapshot(_snapshot.WaitingForRoi, _snapshot.WaitingRoiResult, ocrReadings, readAreaRects, _snapshot.LatestScores);
                }
            }
            finally
            {
                frame.Dispose();
            }
        }

        public Mat? GetRoiMat(string name)
        {
            lock (_roiMatsLock)
            {
                return _roiMats.TryGetValue(name, out var mat) ? mat : null;
            }
        }

        public IEnumerable<string> GetAllRoiNames()
        {
            lock (_roiMatsLock)
            {
                return _savedRoiData.Keys.ToList();
            }
        }

        // console `ocr read <png|dir> [lang]`: run the exact live read pipeline on
        // saved crops (cut from the video ring) so a read-area language is verified
        // against KNOWN digits before it goes live -- never "should work"
        public void OcrRead(string path, string? lang)
        {
            string[] files = Directory.Exists(path)
                ? Directory.GetFiles(path, "*.png").OrderBy(f => f).ToArray()
                : new[] { path };
            foreach (string f in files)
            {
                using Mat m = Cv2.ImRead(f);
                if (m.Empty()) { Console.WriteLine($"  {Path.GetFileName(f)}: cannot read image"); continue; }
                string text = _ocrReader.Read(m, "", lang);
                Console.WriteLine($"  {Path.GetFileName(f),-28} -> '{text}'");
            }
        }

        private void SetupFileWatcher()
        {
            Directory.CreateDirectory(_roiDirectory);
            _fileWatcher = new FileSystemWatcher(_roiDirectory, "roi_metadata.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _fileWatcher.Changed += OnRoiDataFileChanged;
            _fileWatcher.Created += OnRoiDataFileChanged;
            _fileWatcher.EnableRaisingEvents = true;
            Logger.Info("File watcher setup for roi_metadata.json");
        }

        private void OnRoiDataFileChanged(object sender, FileSystemEventArgs e)
        {
            if ((DateTime.UtcNow - _lastWatcherEvent).TotalMilliseconds < 500) { return; }
            _lastWatcherEvent = DateTime.UtcNow;
            Console.WriteLine("[RoiDetector] roi_metadata.json changed, reloading...");
            Thread.Sleep(100);
            Reload();
            Console.WriteLine("[RoiDetector] Reload complete.");
        }

        private void LoadRoiData()
        {
            string roiDataPath = Path.Combine(_roiDirectory, "roi_metadata.json");
            if (!File.Exists(roiDataPath))
            {
                Logger.Error($"ROI metadata file not found: {roiDataPath}");
                return;
            }

            try
            {
                string jsonString = File.ReadAllText(roiDataPath);
                _savedRoiData = JsonSerializer.Deserialize<Dictionary<string, RoiData>>(jsonString) ?? new();
                Logger.Info($"Loaded {_savedRoiData.Count} ROIs");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading ROI metadata: {ex.Message}");
                throw;
            }
        }

        private void LoadRoiMats()
        {
            if (_savedRoiData == null) { return; }

            List<string> missingRois = new();

            foreach (string roiName in _savedRoiData.Keys)
            {
                string roiPath = Path.Combine(_roiDirectory, $"{roiName}.png");
                if (File.Exists(roiPath))
                {
                    Mat roiMat = Cv2.ImRead(roiPath, ImreadModes.Color);
                    _roiMats[roiName] = roiMat;
                    Logger.Info($"Loaded ROI: {roiName}");
                }
                else
                {
                    Logger.Warn($"ROI image not found: {roiPath}");
                    missingRois.Add(roiName);
                }
            }

            foreach (string name in missingRois)
            {
                _savedRoiData.Remove(name);
                Logger.Info($"Removed missing ROI from metadata: {name}");
            }
        }

        public void Reload()
        {
            lock (_roiMatsLock)
            {
                foreach (var mat in _roiMats.Values) { mat.Dispose(); }
                _roiMats.Clear();
                LoadRoiData();
                LoadRoiMats();

                // Drop overlay entries for ROIs no longer in metadata (e.g. after `roi remove`)
                var pruned = _snapshot.LatestScores
                    .Where(kv => _savedRoiData.ContainsKey(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                _snapshot = new DetectionSnapshot(_snapshot.WaitingForRoi, _snapshot.WaitingRoiResult, _snapshot.OcrReadings, _snapshot.ReadAreaRects, pruned);

                Logger.Info("RoiDetector reloaded");
            }
        }

        private static void DetectRoi(Mat frame, Mat roiMat, RoiData roiData,
            out double minVal, out int minLocX, out int minLocY, out int centerX, out int centerY)
        {
            if (roiData.fixedLocation)
            {
                minLocX = roiData.x;
                minLocY = roiData.y;
                centerX = roiData.x + roiMat.Width / 2;
                centerY = roiData.y + roiMat.Height / 2;
                if (roiData.x < 0 || roiData.y < 0 ||
                    roiData.x + roiMat.Width > frame.Width || roiData.y + roiMat.Height > frame.Height)
                {
                    minVal = double.MaxValue;   // recorded region falls off this frame
                    return;
                }
                using Mat region = new Mat(frame, new Rect(roiData.x, roiData.y, roiMat.Width, roiMat.Height));
                using Mat fixedResult = new Mat();
                Cv2.MatchTemplate(region, roiMat, fixedResult, TemplateMatchModes.SqDiffNormed);
                minVal = fixedResult.At<float>(0, 0);
                return;
            }

            using Mat result = new Mat();
            Cv2.MatchTemplate(frame, roiMat, result, TemplateMatchModes.SqDiffNormed);
            Cv2.MinMaxLoc(result, out minVal, out _, out Point minLoc, out _);
            minLocX = minLoc.X;
            minLocY = minLoc.Y;
            centerX = minLoc.X + roiMat.Width / 2;
            centerY = minLoc.Y + roiMat.Height / 2;
        }

        private void ScanLoop()
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            while (!_cts.IsCancellationRequested)
            {
                if (!_scanEnabled) { Thread.Sleep(100); continue; }
                // block until a new frame actually arrives (timeout only so the
                // loop can notice scan-off / shutdown); a signal that fired while
                // we were mid-sweep stays latched, so the newest frame is never
                // missed -- and a static screen costs zero sweeps
                if (!_frameArrived.WaitOne(500)) { continue; }
                Mat? frame;
                lock (_frameLock) { frame = _latestFrame?.Clone(); }
                if (frame == null) { continue; }
                try
                {
                    List<string> names;
                    lock (_roiMatsLock) { names = _savedRoiData.Keys.ToList(); }

                    foreach (var name in names)
                    {
                        if (!_scanEnabled) { break; }
                        Mat? matClone = null;
                        RoiData? roiData = null;
                        lock (_roiMatsLock)
                        {
                            if (!_roiMats.TryGetValue(name, out var mat)) { continue; }
                            if (!_savedRoiData.TryGetValue(name, out roiData)) { continue; }
                            matClone = mat.Clone();
                        }
                        try
                        {
                            DetectRoi(frame, matClone, roiData, out double score, out int minLocX, out int minLocY, out int centerX, out int centerY);
                            bool detected = score < (roiData.threshold ?? TemplateThreshold);
                            int clickX = roiData.clickOffsetX.HasValue ? minLocX + roiData.clickOffsetX.Value : centerX;
                            int clickY = roiData.clickOffsetY.HasValue ? minLocY + roiData.clickOffsetY.Value : centerY;
                            var readings = new Dictionary<string, string>();
                            if (detected && roiData.readAreas.Count > 0)
                            {
                                foreach (var ra in roiData.readAreas)
                                {
                                    int ax = Math.Max(0, Math.Min(minLocX + ra.x, frame.Width - 1));
                                    int ay = Math.Max(0, Math.Min(minLocY + ra.y, frame.Height - 1));
                                    int aw = Math.Min(ra.width, frame.Width - ax);
                                    int ah = Math.Min(ra.height, frame.Height - ay);
                                    if (aw <= 0 || ah <= 0) { continue; }
                                    using Mat readMat = new Mat(frame, new Rect(ax, ay, aw, ah));
                                    readings[ra.name] = _ocrReader.Read(readMat, "", ra.lang);
                                }
                            }
                            var result = new RoiScanResult(score, detected, centerX, centerY, clickX, clickY, readings);
                            if (!_scanEnabled) { break; }
                            var updated = new Dictionary<string, RoiScanResult>(_snapshot.LatestScores) { [name] = result };
                            _snapshot = new DetectionSnapshot(_snapshot.WaitingForRoi, _snapshot.WaitingRoiResult, _snapshot.OcrReadings, _snapshot.ReadAreaRects, updated);
                        }
                        finally { matClone?.Dispose(); }
                    }
                }
                finally { frame.Dispose(); }
                Thread.Sleep(200);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _frameArrived.Set();     // release a scan loop blocked on the wait
            _fileWatcher.Dispose();
            _ocrReader.Dispose();
            lock (_frameLock) { _latestFrame?.Dispose(); }
            lock (_roiMatsLock)
            {
                foreach (var mat in _roiMats.Values) { mat.Dispose(); }
                _roiMats.Clear();
            }
        }
    }
}
