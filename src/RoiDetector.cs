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
            public int frameWidth { get; set; }
            public int frameHeight { get; set; }
            public string roiType { get; set; } = "template";
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
            Dictionary<string, Rect> ReadAreaRects
        );

        private const double TemplateThreshold = 0.003;
        private const double ContourDetectionThreshold = 0.7;

        private readonly string _roiDirectory;
        private readonly OcrReader _ocrReader;

        private Dictionary<string, RoiData> _savedRoiData = new();
        private Dictionary<string, Mat> _roiMats = new();
        private Dictionary<string, (Point[] contour, double area)> _contourRefs = new();
        private FileSystemWatcher _fileWatcher;
        private readonly object _roiMatsLock = new();

        private Mat? _latestFrame;
        private Mat? _latestUpscaledFrame; // upscaled to canonical recording size when frame is smaller
        private Size _canonicalSize;       // the recording resolution derived from ROI metadata
        private readonly object _frameLock = new();

        private volatile DetectionSnapshot _snapshot = new(null, null, new(), new());
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
        }

        public void SetFrame(Mat frame)
        {
            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = frame.Clone();

                _latestUpscaledFrame?.Dispose();
                _latestUpscaledFrame = null;
                if (_canonicalSize.Width > 0 && frame.Width < _canonicalSize.Width)
                {
                    _latestUpscaledFrame = new Mat();
                    Cv2.Resize(frame, _latestUpscaledFrame, _canonicalSize);
                }
            }
        }

        public bool TryFindRoi(string name, out DetectedRoiInfo info)
        {
            info = default;
            Mat? frame;
            Mat? upscaledFrame;
            lock (_frameLock)
            {
                frame = _latestFrame?.Clone();
                upscaledFrame = _latestUpscaledFrame?.Clone();
            }
            if (frame == null) { return false; }

            try
            {
                bool found = TryFindRoiInFrame(frame, upscaledFrame, name, out info);
                _snapshot = new DetectionSnapshot(name, found ? info : null, _snapshot.OcrReadings, _snapshot.ReadAreaRects);
                return found;
            }
            finally
            {
                frame.Dispose();
                upscaledFrame?.Dispose();
            }
        }

        public string? FindBestRoi(IEnumerable<string> names)
        {
            Mat? frame;
            Mat? upscaledFrame;
            lock (_frameLock)
            {
                frame = _latestFrame?.Clone();
                upscaledFrame = _latestUpscaledFrame?.Clone();
            }
            if (frame == null) { return null; }

            try
            {
                string? bestName = null;
                double bestScore = double.MaxValue;

                foreach (string name in names)
                {
                    if (TryFindRoiInFrame(frame, upscaledFrame, name, out DetectedRoiInfo info) && info.Score < bestScore)
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
                upscaledFrame?.Dispose();
            }
        }

        private bool TryFindRoiInFrame(Mat frame, Mat? upscaledFrame, string name, out DetectedRoiInfo info)
        {
            info = default;
            lock (_roiMatsLock)
            {
                if (!_savedRoiData.TryGetValue(name, out RoiData? roiData)) { return false; }
                if (!_roiMats.TryGetValue(name, out Mat? roiMat)) { return false; }

                if (roiData.frameWidth == 0)
                {
                    throw new InvalidOperationException($"ROI '{name}' has no frame dimensions recorded. Re-record it.");
                }

                // outputScale converts detection-space coords back to actual frame space.
                // When the frame is smaller than the recording resolution, detect on the
                // pre-upscaled frame at detectionScale=1.0 so the original threshold applies.
                // The upscaled frame is already cloned by the caller — no extra lock needed.
                double outputScale = (double)frame.Width / roiData.frameWidth;
                Mat detectionFrame = (upscaledFrame != null && outputScale < 1.0) ? upscaledFrame : frame;
                double detectionScale = (upscaledFrame != null && outputScale < 1.0) ? 1.0 : outputScale;

                double score;
                int centerX, centerY, clickX, clickY;

                if (roiData.roiType == "contour" && _contourRefs.TryGetValue(name, out var contourRef))
                {
                    DetectContourRoi(detectionFrame, contourRef.contour, contourRef.area, detectionScale,
                        out double combinedScore, out centerX, out centerY);
                    score = combinedScore < ContourDetectionThreshold ? 0.0 : combinedScore;
                    centerX = (int)(centerX * outputScale);
                    centerY = (int)(centerY * outputScale);
                    clickX = centerX;
                    clickY = centerY;
                }
                else
                {
                    DetectRoi(detectionFrame, roiMat, detectionScale, out score,
                        out int minLocX, out int minLocY, out centerX, out centerY);
                    minLocX = (int)(minLocX * outputScale);
                    minLocY = (int)(minLocY * outputScale);
                    centerX = (int)(centerX * outputScale);
                    centerY = (int)(centerY * outputScale);
                    clickX = roiData.clickOffsetX.HasValue
                        ? minLocX + (int)(roiData.clickOffsetX.Value * outputScale)
                        : centerX;
                    clickY = roiData.clickOffsetY.HasValue
                        ? minLocY + (int)(roiData.clickOffsetY.Value * outputScale)
                        : centerY;
                }

                info = new DetectedRoiInfo
                {
                    RoiName = name,
                    Center = new Point(centerX, centerY),
                    ClickPoint = new Point(clickX, clickY),
                    Score = score
                };

                return roiData.roiType == "contour" ? score == 0.0 : score < TemplateThreshold;
            }
        }

        private Size ComputeCanonicalSize()
        {
            if (_savedRoiData.Count == 0) { return default; }
            // Use the most common (frameWidth, frameHeight) pair across all ROIs.
            return _savedRoiData.Values
                .GroupBy(r => (r.frameWidth, r.frameHeight))
                .OrderByDescending(g => g.Count())
                .Select(g => new Size(g.Key.frameWidth, g.Key.frameHeight))
                .First();
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

                    double scale = (double)frame.Width / roiData.frameWidth;
                    int scaledW = (int)(roiMat.Width * scale);
                    int scaledH = (int)(roiMat.Height * scale);
                    int minLocX = roiInfo.Center.X - scaledW / 2;
                    int minLocY = roiInfo.Center.Y - scaledH / 2;

                    var ocrReadings = new Dictionary<string, int>();
                    var readAreaRects = new Dictionary<string, Rect>();

                    foreach (var readArea in roiData.readAreas)
                    {
                        int areaX = minLocX + (int)(readArea.x * scale);
                        int areaY = minLocY + (int)(readArea.y * scale);
                        int areaW = (int)(readArea.width * scale);
                        int areaH = (int)(readArea.height * scale);

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

                    _snapshot = new DetectionSnapshot(_snapshot.WaitingForRoi, _snapshot.WaitingRoiResult, ocrReadings, readAreaRects);
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
            Logger.Info("roi_metadata.json changed, reloading...");
            Thread.Sleep(100);
            Reload();
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
                _canonicalSize = ComputeCanonicalSize();
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

            foreach (var (roiName, roiData) in _savedRoiData)
            {
                string roiPath = Path.Combine(_roiDirectory, $"{roiName}.png");
                if (File.Exists(roiPath))
                {
                    Mat roiMat = Cv2.ImRead(roiPath, ImreadModes.Color);
                    _roiMats[roiName] = roiMat;

                    if (roiData.roiType == "contour")
                    {
                        Point[]? refContour = ExtractReferenceContour(roiMat);
                        if (refContour != null)
                        {
                            _contourRefs[roiName] = (refContour, Cv2.ContourArea(refContour));
                            Logger.Info($"Loaded contour ROI: {roiName} (area={Cv2.ContourArea(refContour):F1})");
                        }
                        else
                        {
                            Logger.Warn($"Could not extract contour from ROI: {roiName}");
                        }
                    }
                    else
                    {
                        Logger.Info($"Loaded ROI: {roiName}");
                    }
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
                _contourRefs.Clear();
                LoadRoiData();
                LoadRoiMats();
                Logger.Info("RoiDetector reloaded");
            }
        }

        private static void DetectRoi(Mat frame, Mat roiMat, double scale,
            out double minVal, out int minLocX, out int minLocY, out int centerX, out int centerY)
        {
            int scaledW = (int)(roiMat.Width * scale);
            int scaledH = (int)(roiMat.Height * scale);
            bool needResize = Math.Abs(scale - 1.0) >= 0.001;
            Mat scaledTemplate = needResize ? new Mat() : roiMat;
            if (needResize) { Cv2.Resize(roiMat, scaledTemplate, new Size(scaledW, scaledH)); }
            Mat result = new Mat();
            Cv2.MatchTemplate(frame, scaledTemplate, result, TemplateMatchModes.SqDiffNormed);
            Cv2.MinMaxLoc(result, out minVal, out _, out Point minLoc, out _);
            result.Dispose();
            if (needResize) { scaledTemplate.Dispose(); }
            minLocX = minLoc.X;
            minLocY = minLoc.Y;
            centerX = minLoc.X + scaledW / 2;
            centerY = minLoc.Y + scaledH / 2;
        }

        private static void DetectContourRoi(Mat frame, Point[] refContour, double refArea, double scale,
            out double combinedScore, out int centerX, out int centerY)
        {
            double expectedArea = refArea * scale * scale;
            double minArea = expectedArea / 3.0;
            double maxArea = expectedArea * 3.0;

            using Mat gray = new Mat();
            using Mat binary = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, binary, 200, 255, ThresholdTypes.Binary);
            Cv2.FindContours(binary, out Point[][] contours, out _,
                RetrievalModes.List, ContourApproximationModes.ApproxSimple);

            combinedScore = double.MaxValue;
            centerX = 0;
            centerY = 0;

            foreach (Point[] c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area < minArea || area > maxArea) { continue; }
                double shapeScore = Cv2.MatchShapes(refContour, c, ShapeMatchModes.I1, 0);
                double areaRatio = Math.Abs(area / expectedArea - 1.0);
                double darkFrac = ComputeInnerDarkFrac(gray, c);
                double score = 2 * shapeScore + areaRatio - 0.5 * darkFrac;
                if (score < combinedScore)
                {
                    combinedScore = score;
                    Moments m = Cv2.Moments(c);
                    if (m.M00 != 0)
                    {
                        centerX = (int)(m.M10 / m.M00);
                        centerY = (int)(m.M01 / m.M00);
                    }
                }
            }
        }

        private static Point[]? ExtractReferenceContour(Mat roiMat)
        {
            using Mat gray = new Mat();
            using Mat binary = new Mat();
            Cv2.CvtColor(roiMat, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, binary, 200, 255, ThresholdTypes.Binary);
            Cv2.FindContours(binary, out Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0) { return null; }
            return contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        }

        private static double ComputeInnerDarkFrac(Mat gray, Point[] contour, byte darkThresh = 120)
        {
            Rect bbox = Cv2.BoundingRect(contour);
            int x = Math.Max(0, bbox.X);
            int y = Math.Max(0, bbox.Y);
            int x2 = Math.Min(gray.Width, bbox.X + bbox.Width);
            int y2 = Math.Min(gray.Height, bbox.Y + bbox.Height);
            int w = x2 - x;
            int h = y2 - y;
            if (w <= 0 || h <= 0) { return 0.0; }

            using Mat mask = Mat.Zeros(gray.Size(), MatType.CV_8UC1);
            Cv2.DrawContours(mask, new[] { contour }, -1, Scalar.White, -1);
            Rect croppedRect = new Rect(x, y, w, h);
            using Mat bboxGray = new Mat(gray, croppedRect);
            using Mat bboxMask = new Mat(mask, croppedRect);
            using Mat gapMask = new Mat();
            Cv2.BitwiseNot(bboxMask, gapMask);
            using Mat darkBinary = new Mat();
            Cv2.Threshold(bboxGray, darkBinary, darkThresh, 255, ThresholdTypes.BinaryInv);
            using Mat darkGap = new Mat();
            Cv2.BitwiseAnd(darkBinary, gapMask, darkGap);
            double totalGap = Cv2.CountNonZero(gapMask);
            if (totalGap == 0) { return 0.0; }
            return Cv2.CountNonZero(darkGap) / totalGap;
        }

        public void Dispose()
        {
            _fileWatcher.Dispose();
            _ocrReader.Dispose();
            lock (_frameLock) { _latestFrame?.Dispose(); _latestUpscaledFrame?.Dispose(); }
            lock (_roiMatsLock)
            {
                foreach (var mat in _roiMats.Values) { mat.Dispose(); }
                _roiMats.Clear();
            }
        }
    }
}
