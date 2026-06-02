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

            public class ReadArea
            {
                public string name { get; set; } = "";
                public int x { get; set; }
                public int y { get; set; }
                public int width { get; set; }
                public int height { get; set; }
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
            Dictionary<string, int> OcrReadings,
            Dictionary<string, Rect> ReadAreaRects,
            Dictionary<string, (double Score, bool Detected, int CenterX, int CenterY)> LatestScores
        );

        public const double TemplateThreshold = 0.01;

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

        private volatile DetectionSnapshot _snapshot = new(null, null, new(), new(), new Dictionary<string, (double, bool, int, int)>());
        public DetectionSnapshot Snapshot => _snapshot;

        public RoiDetector(string roiDirectory, string debugDir)
        {
            string tessPrefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
                ?? throw new InvalidOperationException("TESSDATA_PREFIX environment variable is not set. Run setup.ps1.");
            string tessDataPath = Path.Combine(tessPrefix, "tessdata");
            if (!Directory.Exists(tessDataPath))
            {
                throw new InvalidOperationException($"tessdata directory not found at {tessDataPath}. Run setup.ps1 to download language data.");
            }

            _ocrReader = new OcrReader(tessDataPath, debugDir);
            _roiDirectory = roiDirectory;
            LoadRoiData();
            LoadRoiMats();
            SetupFileWatcher();
            new Thread(ScanLoop) { IsBackground = true, Name = "RoiDetector.Scan" }.Start();
        }

        public void SetFrame(Mat frame)
        {
            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = frame.Clone();
            }
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
                return found;
            }
            finally
            {
                frame.Dispose();
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

                DetectRoi(frame, roiMat, out double score,
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

                return score < TemplateThreshold;
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

                    var ocrReadings = new Dictionary<string, int>();
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
                        ocrReadings[key] = _ocrReader.ReadInt(readMat);
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
                Logger.Info("RoiDetector reloaded");
            }
        }

        private static void DetectRoi(Mat frame, Mat roiMat,
            out double minVal, out int minLocX, out int minLocY, out int centerX, out int centerY)
        {
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
                Mat? frame;
                lock (_frameLock) { frame = _latestFrame?.Clone(); }
                if (frame == null) { Thread.Sleep(100); continue; }
                try
                {
                    List<string> names;
                    lock (_roiMatsLock) { names = _savedRoiData.Keys.ToList(); }

                    foreach (var name in names)
                    {
                        Mat? matClone = null;
                        lock (_roiMatsLock)
                        {
                            if (!_roiMats.TryGetValue(name, out var mat)) { continue; }
                            matClone = mat.Clone();
                        }
                        try
                        {
                            DetectRoi(frame, matClone, out double score, out _, out _, out int centerX, out int centerY);
                            bool detected = score < TemplateThreshold;
                            var updated = new Dictionary<string, (double, bool, int, int)>(_snapshot.LatestScores) { [name] = (score, detected, centerX, centerY) };
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
