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

        // Key = $"{state.Key}/{roi.name}"
        private Dictionary<string, Mat> _roiMats = new();
        private FileSystemWatcher _fileWatcher;
        private readonly object _roiMatsLock = new object();

        public RoiDetectionInfo[] RoiDetectionInfos { get; private set; } = Array.Empty<RoiDetectionInfo>();
        public string CurrentState { get; private set; } = string.Empty;
        public List<string> NextExpectedStates { get; set; } = new();

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
            string roiDataPath = Path.Combine(_roiDirectory, "roi_metadata.json");
            if (!File.Exists(roiDataPath))
            {
                Logger.Error("ROI metadata file not found, file watcher not started");
                return;
            }

            _fileWatcher = new FileSystemWatcher(_roiDirectory, "roi_metadata.json");
            _fileWatcher.NotifyFilter = NotifyFilters.LastWrite;
            _fileWatcher.Changed += OnRoiDataFileChanged;
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
                        Logger.Info($"Loaded ROI: {key}");
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
                if (_savedRoiData == null)
                {
                    return;
                }

                CurrentState = string.Empty;

                // Allocate array if size changed
                if (RoiDetectionInfos.Length != _roiMats.Count)
                {
                    RoiDetectionInfos = new RoiDetectionInfo[_roiMats.Count];
                }

                foreach (string expectedState in NextExpectedStates)
                {
                    if (DetectSingleState(frame, expectedState))
                    {
                        return;
                    }
                }

                if (CurrentState != string.Empty && DetectSingleState(frame, CurrentState))
                {
                    return;
                }

                // Detect all ROIs
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
                    DetectRoi(frame, roiMat, scale, out double minVal, out int centerX, out int centerY);

                    RoiDetectionInfos[index++] = new RoiDetectionInfo
                    {
                        StateName = stateName,
                        RoiName = roiName,
                        Center = new Point(centerX, centerY),
                        MinVal = minVal
                    };
                }

                // Find the best matching state
                string bestStateName = string.Empty;
                double bestAvgMinVal = double.MaxValue;
                RoiDetectionInfo[] bestStateRois = Array.Empty<RoiDetectionInfo>();

                foreach (var state in _savedRoiData)
                {
                    string stateName = state.Key;
                    List<RoiRecorder.RoiData> expectedRois = state.Value;

                    // Find all detected ROIs for this state
                    var stateDetectedRois = RoiDetectionInfos.Where(r => r.StateName == stateName).ToList();

                    if (stateDetectedRois.Count == expectedRois.Count)
                    {
                        // All ROIs found for this state
                        double avgMinVal = stateDetectedRois.Average(r => r.MinVal);

                        // Check if this state is better (most ROIs, then lowest minVal)
                        if (avgMinVal < bestAvgMinVal)
                        {
                            bestStateName = stateName;
                            bestAvgMinVal = avgMinVal;
                            bestStateRois = stateDetectedRois.ToArray();
                        }
                    }
                }

                if (bestAvgMinVal < 0.001)
                {
                    CurrentState = bestStateName;
                    NextExpectedStates = GetNextExpectedStates(CurrentState);
                }

                // Sort by minVal so best match is first
                Array.Sort(RoiDetectionInfos, (a, b) => a.MinVal.CompareTo(b.MinVal));
            }
        }

        private bool DetectSingleState(Mat frame, string state)
        {
            List<RoiData> nextExpectedStateRois = _savedRoiData[state];
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
                DetectRoi(frame, roiMat, scale, out double minVal, out int centerX, out int centerY);

                RoiDetectionInfos[index++] = new RoiDetectionInfo
                {
                    StateName = state,
                    RoiName = roiData.name,
                    Center = new Point(centerX, centerY),
                    MinVal = minVal
                };
            }

            // Find all detected ROIs for this state
            var nextExpectedStateDetectedRois = RoiDetectionInfos[0..index].ToList();

            if (nextExpectedStateDetectedRois.Count == nextExpectedStateRois.Count)
            {
                // All ROIs found for this state
                double avgMinVal = nextExpectedStateDetectedRois.Average(r => r.MinVal);

                // Check if this state is better (most ROIs, then lowest minVal)
                if (avgMinVal < 0.001)
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
