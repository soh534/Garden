using NLog;
using OpenCvSharp;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using static Garden.RoiRecorder;
using SavedRoiData = System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Garden.RoiRecorder.RoiData>>;

namespace Garden
{
    public class StateDetector
    {
        public struct RoiDetectionInfo
        {
            public string StateName;
            public string RoiName;
            public Point Center;
            public double MinVal;
        }

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _roiDirectory;
        private readonly Fsm _fsm;

        // Key = state, value = List of RoiData
        private SavedRoiData _savedRoiData;

        private const double ContourDetectionThreshold = 0.6;

        // Key = $"{state.Key}/{roi.name}"
        private Dictionary<string, Mat> _roiMats = new();
        private Dictionary<string, (OpenCvSharp.Point[] contour, double area)> _contourRefs = new();
        private FileSystemWatcher _fileWatcher;
        private readonly object _roiMatsLock = new object();

        public record DetectionSnapshot(
            string CurrentState,
            RoiDetectionInfo[] RoiDetectionInfos,
            List<string> NextExpectedStates,
            Dictionary<string, double> RoiTimings
        );

        private volatile DetectionSnapshot _snapshot = new(string.Empty, Array.Empty<RoiDetectionInfo>(), new(), new());
        public DetectionSnapshot Snapshot => _snapshot;

        public RoiDetectionInfo[] RoiDetectionInfos { get; private set; } = Array.Empty<RoiDetectionInfo>();
        public string CurrentState { get; private set; } = string.Empty;
        public List<string> NextExpectedStates { get; set; } = new();
        public Dictionary<string, double> RoiTimings { get; } = new();

        public Mat? GetRoiMat(string stateName, string roiName)
        {
            string roiKey = $"{stateName}/{roiName}";
            return _roiMats.ContainsKey(roiKey) ? _roiMats[roiKey] : null;
        }

        public StateDetector(Fsm fsm, string roiDirectory)
        {
            _fsm = fsm;
            _roiDirectory = roiDirectory;
            LoadRoiData();
            LoadRoiMats();
            SetupFileWatcher();
        }

        private void SetupFileWatcher()
        {
            Directory.CreateDirectory(_roiDirectory);
            _fileWatcher = new FileSystemWatcher(_roiDirectory, "roi_metadata.json");
            _fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            _fileWatcher.Changed += OnRoiDataFileChanged;
            _fileWatcher.Created += OnRoiDataFileChanged;
            _fileWatcher.EnableRaisingEvents = true;
            Logger.Info("File watcher setup for roi_metadata.json");
        }

        private void OnRoiDataFileChanged(object sender, FileSystemEventArgs e)
        {
            Logger.Info("roi_metadata.json changed, reloading...");
            Thread.Sleep(100); // Small delay to ensure file write is complete
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
                _savedRoiData = JsonSerializer.Deserialize<SavedRoiData>(jsonString);
                Logger.Info($"Loaded ROI metadata with {_savedRoiData?.Count ?? 0} states");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading ROI metadata: {ex.Message}");
                throw;
            }
        }

        private void LoadRoiMats()
        {
            if (_savedRoiData == null) return;

            // First, clean up orphaned image files (images not in JSON)
            CleanupOrphanedImages();

            List<(string stateName, RoiRecorder.RoiData roi)> missingRois = new();

            foreach (var state in _savedRoiData)
            {
                foreach (var roi in state.Value)
                {
                    string key = $"{state.Key}/{roi.name}";
                    string roiFileName = $"{roi.name}.png";
                    string roiPath = Path.Combine(_roiDirectory, state.Key, roiFileName);

                    if (File.Exists(roiPath))
                    {
                        Mat roiMat = Cv2.ImRead(roiPath, ImreadModes.Color);
                        _roiMats[key] = roiMat;

                        if (roi.roiType == "contour")
                        {
                            OpenCvSharp.Point[]? refContour = ExtractReferenceContour(roiMat);
                            if (refContour != null)
                            {
                                _contourRefs[key] = (refContour, Cv2.ContourArea(refContour));
                                Logger.Info($"Loaded contour ROI: {key} (area={Cv2.ContourArea(refContour):F1})");
                            }
                            else
                            {
                                Logger.Warn($"Could not extract contour from ROI: {key}");
                            }
                        }
                        else
                        {
                            Logger.Info($"Loaded ROI: {key}");
                        }
                    }
                    else
                    {
                        // Track missing ROI for cleanup
                        missingRois.Add((state.Key, roi));
                        Logger.Warn($"ROI not found: {key}");
                    }
                }
            }

            // Clean up missing ROIs from metadata
            if (missingRois.Count > 0)
            {
                CleanupMissingRois(missingRois);
            }
        }

        private void CleanupOrphanedImages()
        {
            if (_savedRoiData == null) return;

            // Get all ROI names from metadata
            HashSet<string> knownRois = new();
            foreach (var state in _savedRoiData)
            {
                foreach (var roi in state.Value)
                {
                    knownRois.Add($"{state.Key}/{roi.name}");
                }
            }

            // Scan all state directories for image files
            foreach (var stateDir in Directory.GetDirectories(_roiDirectory))
            {
                string stateName = Path.GetFileName(stateDir);
                foreach (var imageFile in Directory.GetFiles(stateDir, "*.png"))
                {
                    string imageFileName = Path.GetFileName(imageFile);
                    string imageName = Path.GetFileNameWithoutExtension(imageFileName);
                    string key = $"{stateName}/{imageName}";

                    if (!knownRois.Contains(key))
                    {
                        // This image is not in JSON - delete it
                        try
                        {
                            File.Delete(imageFile);
                            Logger.Info($"Deleted orphaned image: {key}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error deleting orphaned image {key}: {ex.Message}");
                            throw;
                        }
                    }
                }

                // Remove empty directories
                if (Directory.GetFiles(stateDir).Length == 0)
                {
                    try
                    {
                        Directory.Delete(stateDir);
                        Logger.Info($"Deleted empty state directory: {stateName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error deleting directory {stateName}: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        private void CleanupMissingRois(List<(string stateName, RoiRecorder.RoiData roi)> missingRois)
        {
            if (_savedRoiData == null) return;

            foreach (var (stateName, roi) in missingRois)
            {
                Logger.Info($"Removing missing ROI from metadata: {stateName}/{roi.name}");
                _savedRoiData[stateName].Remove(roi);
            }

            // Remove empty states
            var emptyStates = _savedRoiData.Where(s => s.Value.Count == 0).Select(s => s.Key).ToList();
            foreach (var stateName in emptyStates)
            {
                Logger.Info($"Removing empty state: {stateName}");
                _savedRoiData.Remove(stateName);
            }

            // Save updated metadata
            try
            {
                string roiDataPath = Path.Combine(_roiDirectory, "roi_metadata.json");
                var options = new JsonSerializerOptions { WriteIndented = true };
                string updatedJson = JsonSerializer.Serialize(_savedRoiData, options);
                File.WriteAllText(roiDataPath, updatedJson);
                Logger.Info($"Cleaned up {missingRois.Count} missing ROI(s) from metadata");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving cleaned metadata: {ex.Message}");
                throw;
            }
        }

        public void DetectState(Mat frame)
        {
            lock (_roiMatsLock)
            {
                try
                {
                    if (_savedRoiData == null)
                    {
                        return;
                    }

                    string previousState = CurrentState;
                    CurrentState = string.Empty;

                    // Allocate array if size changed
                    if (RoiDetectionInfos.Length != _roiMats.Count)
                    {
                        RoiDetectionInfos = new RoiDetectionInfo[_roiMats.Count];
                    }

                    // Early-out 1: check FSM-expected next states first — most likely transitions
                    foreach (string expectedState in NextExpectedStates)
                    {
                        if (DetectSingleState(frame, expectedState))
                        {
                            return;
                        }
                    }

                    // Early-out 2: re-check the previous state — state rarely changes frame-to-frame
                    if (previousState != string.Empty && DetectSingleState(frame, previousState))
                    {
                        return;
                    }

                    // Full scan: check all ROIs
                    int index = 0;
                    foreach (KeyValuePair<string, Mat> kvp in _roiMats)
                    {
                        string roiKey = kvp.Key;
                        Mat roiMat = kvp.Value;

                        // Parse state name and ROI name from key (format: "stateName/roiName")
                        string[] parts = roiKey.Split('/');
                        string stateName = parts[0];
                        string roiName = parts[1];

                        RoiData roiData = _savedRoiData[stateName].First(r => r.name == roiName);
                        if (roiData.frameWidth == 0)
                        {
                            throw new InvalidOperationException($"ROI '{stateName}/{roiName}' has no frame dimensions recorded. Re-record this ROI.");
                        }
                        double scale = (double)frame.Width / roiData.frameWidth;

                        double minVal;
                        int centerX, centerY;
                        if (roiData.roiType == "contour" && _contourRefs.TryGetValue(roiKey, out var contourRef))
                        {
                            DetectContourRoi(frame, contourRef.contour, contourRef.area, scale, out double combinedScore, out centerX, out centerY);
                            minVal = combinedScore < ContourDetectionThreshold ? 0.0 : 1.0;
                        }
                        else
                        {
                            DetectRoi(frame, roiMat, scale, out minVal, out centerX, out centerY);
                        }

                        RoiDetectionInfos[index++] = new RoiDetectionInfo
                        {
                            StateName = stateName,
                            RoiName = roiName,
                            Center = new Point(centerX, centerY),
                            MinVal = minVal
                        };
                    }

                    // Find the best matching state — prefer more ROIs (more specific), then lower avgMinVal
                    string bestStateName = string.Empty;
                    double bestAvgMinVal = double.MaxValue;
                    int bestRoiCount = 0;
                    RoiDetectionInfo[] bestStateRois = Array.Empty<RoiDetectionInfo>();

                    foreach (var state in _savedRoiData)
                    {
                        string stateName = state.Key;
                        List<RoiRecorder.RoiData> expectedRois = state.Value;

                        var requiredExpectedRois = expectedRois.Where(r => !r.optional).ToList();
                        var stateRequiredDetectedRois = RoiDetectionInfos
                            .Where(r => r.StateName == stateName && requiredExpectedRois.Any(req => req.name == r.RoiName)).ToList();

                        if (requiredExpectedRois.Count > 0 && stateRequiredDetectedRois.Count == requiredExpectedRois.Count)
                        {
                            double avgMinVal = stateRequiredDetectedRois.Average(r => r.MinVal);
                            if (avgMinVal < 0.003)
                            {
                                int roiCount = requiredExpectedRois.Count;

                                bool moreSpecific = roiCount > bestRoiCount;
                                bool sameSpecificityButBetter = roiCount == bestRoiCount && avgMinVal < bestAvgMinVal;

                                if (moreSpecific || sameSpecificityButBetter)
                                {
                                    bestStateName = stateName;
                                    bestAvgMinVal = avgMinVal;
                                    bestRoiCount = roiCount;
                                    bestStateRois = stateRequiredDetectedRois.ToArray();
                                }
                            }
                        }
                    }

                    if (bestStateName != string.Empty)
                    {
                        CurrentState = bestStateName;
                        NextExpectedStates = GetNextExpectedStates(CurrentState);
                    }

                    // Sort by minVal so best match is first
                    Array.Sort(RoiDetectionInfos, (a, b) => a.MinVal.CompareTo(b.MinVal));
                }
                finally
                {
                    _snapshot = new DetectionSnapshot(
                        CurrentState,
                        RoiDetectionInfos.ToArray(),
                        NextExpectedStates.ToList(),
                        new Dictionary<string, double>(RoiTimings)
                    );
                }
            }
        }

        private bool DetectSingleState(Mat frame, string state)
        {
            // FSM may reference states that haven't been recorded as ROIs yet
            if (!_savedRoiData.TryGetValue(state, out List<RoiData>? nextExpectedStateRois))
            {
                return false;
            }

            var requiredRois = nextExpectedStateRois.Where(r => !r.optional).ToList();

            var sw = Stopwatch.StartNew();
            int index = 0;
            foreach (RoiData roiData in nextExpectedStateRois)
            {
                Mat? roiMat = GetRoiMat(state, roiData.name);
                Debug.Assert(roiMat != null);
                if (roiData.frameWidth == 0)
                {
                    throw new InvalidOperationException($"ROI '{state}/{roiData.name}' has no frame dimensions recorded. Re-record this ROI.");
                }
                double scale = (double)frame.Width / roiData.frameWidth;

                double minVal;
                int centerX, centerY;
                string roiKey = $"{state}/{roiData.name}";
                sw.Restart();
                if (roiData.roiType == "contour" && _contourRefs.TryGetValue(roiKey, out var contourRef))
                {
                    DetectContourRoi(frame, contourRef.contour, contourRef.area, scale, out double combinedScore, out centerX, out centerY);
                    minVal = combinedScore < ContourDetectionThreshold ? 0.0 : 1.0;
                }
                else
                {
                    DetectRoi(frame, roiMat, scale, out minVal, out centerX, out centerY);
                }
                RoiTimings[roiKey] = sw.Elapsed.TotalMilliseconds;

                RoiDetectionInfos[index++] = new RoiDetectionInfo
                {
                    StateName = state,
                    RoiName = roiData.name,
                    Center = new Point(centerX, centerY),
                    MinVal = minVal
                };
            }

            // Check only required ROIs for state match
            var detectedRequired = RoiDetectionInfos[0..index]
                .Where(info => requiredRois.Any(r => r.name == info.RoiName)).ToList();

            if (requiredRois.Count > 0 && detectedRequired.Count == requiredRois.Count)
            {
                double avgMinVal = detectedRequired.Average(r => r.MinVal);

                if (avgMinVal < 0.003)
                {
                    CurrentState = state;
                    NextExpectedStates = GetNextExpectedStates(CurrentState);
                    return true;
                }
            }

            return false;
        }

        private List<string> GetNextExpectedStates(string state)
        {
            return _fsm.Transitions.TryGetValue(state, out var transitions)
                ? transitions.Values.ToList()
                : new List<string>();
        }

        private static OpenCvSharp.Point[]? ExtractReferenceContour(Mat roiMat)
        {
            using Mat gray = new Mat();
            using Mat binary = new Mat();
            Cv2.CvtColor(roiMat, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, binary, 200, 255, ThresholdTypes.Binary);
            Cv2.FindContours(binary, out OpenCvSharp.Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0) { return null; }
            return contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        }

        private static double ComputeInnerDarkFrac(Mat gray, OpenCvSharp.Point[] contour, byte darkThresh = 120)
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

        private void DetectContourRoi(Mat frame, OpenCvSharp.Point[] refContour, double refArea, double scale,
            out double combinedScore, out int centerX, out int centerY)
        {
            double expectedArea = refArea * scale * scale;
            double minArea = expectedArea / 3.0;
            double maxArea = expectedArea * 3.0;

            using Mat gray = new Mat();
            using Mat binary = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, binary, 200, 255, ThresholdTypes.Binary);
            Cv2.FindContours(binary, out OpenCvSharp.Point[][] contours, out _,
                RetrievalModes.List, ContourApproximationModes.ApproxSimple);

            combinedScore = double.MaxValue;
            centerX = 0;
            centerY = 0;

            foreach (OpenCvSharp.Point[] c in contours)
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

        private void DetectRoi(Mat frame, Mat roiMat, double scale, out double minVal, out int centerX, out int centerY)
        {
            int scaledW = (int)(roiMat.Width * scale);
            int scaledH = (int)(roiMat.Height * scale);

            bool needResize = Math.Abs(scale - 1.0) >= 0.001;
            Mat scaledTemplate = needResize ? new Mat() : roiMat;
            if (needResize)
            {
                Cv2.Resize(roiMat, scaledTemplate, new Size(scaledW, scaledH));
            }

            Mat result = new Mat();
            Cv2.MatchTemplate(frame, scaledTemplate, result, TemplateMatchModes.SqDiffNormed);
            Cv2.MinMaxLoc(result, out minVal, out _, out Point minLoc, out _);
            result.Dispose();
            if (needResize)
            {
                scaledTemplate.Dispose();
            }

            centerX = minLoc.X + scaledW / 2;
            centerY = minLoc.Y + scaledH / 2;
        }

        public void Reload()
        {
            lock (_roiMatsLock)
            {
                // Dispose old ROI mats
                foreach (var roiMat in _roiMats.Values)
                {
                    roiMat.Dispose();
                }
                _roiMats.Clear();
                _contourRefs.Clear();

                // Reload metadata and ROI mats
                LoadRoiData();
                LoadRoiMats();

                Logger.Info("StateDetector reloaded");
            }
        }

        public void Dispose()
        {
            _fileWatcher.Dispose();
            foreach (var roiMat in _roiMats.Values)
            {
                roiMat.Dispose();
            }
            _roiMats.Clear();
        }
    }
}
