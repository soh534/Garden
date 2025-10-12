using OpenCvSharp;
using System.Text.Json;
using NLog;
using static Garden.RoiRecorder;

namespace Garden
{
    public class StateDetector
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _roiDirectory;
        private RoiMetadataFile? _metadata;
        private Dictionary<string, Mat> _templates = new();
        private const double MATCH_THRESHOLD = 0.85;
        private FileSystemWatcher? _fileWatcher;

        public StateDetector(string roiDirectory)
        {
            _roiDirectory = roiDirectory;
            LoadMetadata();
            LoadTemplates();
            SetupFileWatcher();
        }

        private void SetupFileWatcher()
        {
            string metadataPath = Path.Combine(_roiDirectory, "roi_metadata.json");
            if (!File.Exists(metadataPath))
            {
                Logger.Warn("ROI metadata file not found, file watcher not started");
                return;
            }

            _fileWatcher = new FileSystemWatcher(_roiDirectory, "roi_metadata.json");
            _fileWatcher.NotifyFilter = NotifyFilters.LastWrite;
            _fileWatcher.Changed += OnMetadataFileChanged;
            _fileWatcher.EnableRaisingEvents = true;
            Logger.Info("File watcher setup for roi_metadata.json");
        }

        private void OnMetadataFileChanged(object sender, FileSystemEventArgs e)
        {
            Logger.Info("roi_metadata.json changed, reloading...");
            Thread.Sleep(100); // Small delay to ensure file write is complete
            Reload();
        }

        private void LoadMetadata()
        {
            string metadataPath = Path.Combine(_roiDirectory, "roi_metadata.json");
            if (!File.Exists(metadataPath))
            {
                Logger.Warn($"ROI metadata file not found: {metadataPath}");
                return;
            }

            try
            {
                string jsonString = File.ReadAllText(metadataPath);
                _metadata = JsonSerializer.Deserialize<RoiMetadataFile>(jsonString);
                Logger.Info($"Loaded ROI metadata with {_metadata?.states.Count ?? 0} states");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading ROI metadata: {ex.Message}");
            }
        }

        private void LoadTemplates()
        {
            if (_metadata == null) return;

            foreach (var state in _metadata.states)
            {
                foreach (var roi in state.Value)
                {
                    // Try to load edge-detected version first
                    string edgeFilename = roi.name.Replace(".png", "_edges.png");
                    string edgePath = Path.Combine(_roiDirectory, state.Key, edgeFilename);

                    if (File.Exists(edgePath))
                    {
                        Mat template = Cv2.ImRead(edgePath, ImreadModes.Grayscale);
                        string key = $"{state.Key}/{roi.name}";
                        _templates[key] = template;
                        Logger.Info($"Loaded edge template: {key}");
                    }
                    else
                    {
                        // Fall back to original if edges not found
                        string templatePath = Path.Combine(_roiDirectory, state.Key, roi.name);
                        if (File.Exists(templatePath))
                        {
                            Mat template = Cv2.ImRead(templatePath, ImreadModes.Color);
                            string key = $"{state.Key}/{roi.name}";
                            _templates[key] = template;
                            Logger.Info($"Loaded template: {key}");
                        }
                        else
                        {
                            Logger.Warn($"Template not found: {templatePath}");
                        }
                    }
                }
            }
        }

        public List<Rect> LastDetectedBoxes { get; private set; } = new();

        public (string? stateName, double confidence) DetectState(Mat frame)
        {
            if (_metadata == null) return (null, 0.0);

            LastDetectedBoxes.Clear();
            double bestConfidence = 0.0;

            foreach (var state in _metadata.states)
            {
                // Find all ROIs in frame
                Dictionary<string, OpenCvSharp.Point> foundPositions = new();
                Dictionary<string, double> similarities = new();
                bool allRoisFound = true;

                foreach (var roi in state.Value)
                {
                    string templateKey = $"{state.Key}/{roi.name}";
                    if (!_templates.ContainsKey(templateKey))
                    {
                        allRoisFound = false;
                        break;
                    }

                    Mat template = _templates[templateKey];
                    var result = FindTemplate(frame, template);

                    if (result.HasValue)
                    {
                        foundPositions[roi.name] = result.Value.position;
                        similarities[roi.name] = result.Value.similarity;
                    }
                    else
                    {
                        allRoisFound = false;
                        break;
                    }
                }

                // Track best confidence even if state doesn't fully match
                if (similarities.Count > 0)
                {
                    double avgConfidence = similarities.Values.Average();
                    if (avgConfidence > bestConfidence)
                    {
                        bestConfidence = avgConfidence;
                    }
                }

                // If all ROIs found, check if relative positions match
                if (allRoisFound && CheckRelativePositions(state.Value, foundPositions))
                {
                    // Store bounding boxes for matched state
                    foreach (var roi in state.Value)
                    {
                        string templateKey = $"{state.Key}/{roi.name}";
                        if (_templates.ContainsKey(templateKey))
                        {
                            Mat template = _templates[templateKey];
                            var pos = foundPositions[roi.name];
                            int topLeftX = pos.X - template.Width / 2;
                            int topLeftY = pos.Y - template.Height / 2;
                            LastDetectedBoxes.Add(new Rect(topLeftX, topLeftY, template.Width, template.Height));
                        }
                    }

                    // Calculate average confidence
                    double avgConfidence = similarities.Values.Average();
                    return (state.Key, avgConfidence);
                }
            }

            return (null, bestConfidence);
        }

        private (OpenCvSharp.Point position, double similarity)? FindTemplate(Mat frame, Mat template)
        {
            try
            {
                Mat searchFrame = frame;
                bool needsDispose = false;

                // If template is grayscale (edge-detected), convert frame to edges too
                if (template.Channels() == 1)
                {
                    Mat gray = new Mat();
                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    searchFrame = new Mat();
                    Cv2.Canny(gray, searchFrame, 50, 150);
                    gray.Dispose();
                    needsDispose = true;
                }

                // Perform template matching
                Mat result = new Mat();
                Cv2.MatchTemplate(searchFrame, template, result, TemplateMatchModes.CCoeffNormed);

                // Get best match
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                result.Dispose();
                if (needsDispose) searchFrame.Dispose();

                // Return center position and similarity if match is good enough
                if (maxVal >= MATCH_THRESHOLD)
                {
                    int centerX = maxLoc.X + template.Width / 2;
                    int centerY = maxLoc.Y + template.Height / 2;
                    return (new OpenCvSharp.Point(centerX, centerY), maxVal);
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error finding template: {ex.Message}");
                return null;
            }
        }

        private bool CheckRelativePositions(List<RoiMetadata> expectedRois, Dictionary<string, OpenCvSharp.Point> foundPositions)
        {
            // Compare all pairs of ROIs
            for (int i = 0; i < expectedRois.Count; i++)
            {
                for (int j = i + 1; j < expectedRois.Count; j++)
                {
                    var roi1 = expectedRois[i];
                    var roi2 = expectedRois[j];

                    if (!foundPositions.ContainsKey(roi1.name) || !foundPositions.ContainsKey(roi2.name))
                        continue;

                    // Calculate expected centers
                    int expectedCenter1X = roi1.x + roi1.width / 2;
                    int expectedCenter1Y = roi1.y + roi1.height / 2;
                    int expectedCenter2X = roi2.x + roi2.width / 2;
                    int expectedCenter2Y = roi2.y + roi2.height / 2;

                    // Get found centers
                    var found1 = foundPositions[roi1.name];
                    var found2 = foundPositions[roi2.name];

                    // Check horizontal relationship (left/right)
                    bool expectedLeftOf = expectedCenter1X < expectedCenter2X;
                    bool foundLeftOf = found1.X < found2.X;
                    if (expectedLeftOf != foundLeftOf)
                    {
                        Logger.Debug($"Horizontal relationship mismatch: {roi1.name} vs {roi2.name}");
                        return false;
                    }

                    // Check vertical relationship (above/below)
                    bool expectedAbove = expectedCenter1Y < expectedCenter2Y;
                    bool foundAbove = found1.Y < found2.Y;
                    if (expectedAbove != foundAbove)
                    {
                        Logger.Debug($"Vertical relationship mismatch: {roi1.name} vs {roi2.name}");
                        return false;
                    }
                }
            }

            return true;
        }

        public void Reload()
        {
            // Dispose old templates
            foreach (var template in _templates.Values)
            {
                template?.Dispose();
            }
            _templates.Clear();

            // Reload metadata and templates
            LoadMetadata();
            LoadTemplates();

            Logger.Info("StateDetector reloaded");
        }

        public void Dispose()
        {
            _fileWatcher?.Dispose();
            foreach (var template in _templates.Values)
            {
                template?.Dispose();
            }
            _templates.Clear();
        }
    }
}
