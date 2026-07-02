using NLog;
using OpenCvSharp;
using Tesseract;

namespace Garden
{
    public class OcrReader : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly TesseractEngine _engine;
        private readonly string _debugDir;
        private readonly string _ringDir;
        private readonly string?[] _ringFiles = new string?[RingSize];
        private int _ringSeq;
        private const int RingSize = 64;
        private readonly object _engineLock = new();

        public OcrReader(string tessDataPath, string debugDir, string lang)
        {
            // Language configurable via config.json (default: jpn). No whitelist --
            // the engine returns raw text and callers interpret it: getOcrInt strips
            // non-digits, getOcrStr takes it as-is. Lets mixed text (digits + 年/月/日)
            // like the jouro deadline survive instead of being forced to digits.
            if (string.IsNullOrEmpty(lang)) { lang = "jpn"; }
            _engine = new TesseractEngine(tessDataPath, lang, EngineMode.Default);
            // Silence Tesseract's internal diagnostic spew (STATS/baseline prints) by
            // routing its debug output to the null device.
            _engine.SetVariable("debug_file", "NUL");
            _debugDir = debugDir;
            _ringDir = Path.Combine(debugDir, "ocr_ring");
            if (Directory.Exists(_ringDir)) { Directory.Delete(_ringDir, true); }
            Directory.CreateDirectory(_ringDir);
            Logger.Info($"OCR engine: lang={lang}");
        }

        // Returns the raw recognized text (trimmed), or "" on failure.
        public string Read(Mat mat, string debugKey = "")
        {
            try
            {
                using Mat upscaled = new Mat();
                Cv2.Resize(mat, upscaled, new Size(mat.Width * 3, mat.Height * 3), interpolation: InterpolationFlags.Cubic);
                using Mat gray = new Mat();
                Cv2.CvtColor(upscaled, gray, ColorConversionCodes.BGR2GRAY);
                using Mat thresholded = new Mat();
                Cv2.Threshold(gray, thresholded, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                byte[] pngBytes = thresholded.ToBytes(".png");
                lock (_engineLock)
                {
                    using var pix = Pix.LoadFromMemory(pngBytes);
                    using var page = _engine.Process(pix, PageSegMode.SingleWord);
                    string text = NormalizeDigits(page.GetText().Trim());
                    if (!string.IsNullOrEmpty(debugKey)) { RingSave(debugKey, gray, thresholded, text); }
                    return text;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"OCR error: {ex.Message}");
                return "";
            }
        }

        // The game renders digits as enclosed/circled glyphs (①②③.., and there
        // are ~6 circled blocks spanning 0..50 plus black/sans-serif variants).
        // Rather than enumerate them, ask Unicode for each non-ASCII char's numeric
        // value: ⑳ -> 20, ④ -> 4, fullwidth ０ -> 0, etc. Non-numeric chars
        // (年/月/日 and everything else) have value -1 and pass through untouched.
        private static string NormalizeDigits(string text)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                double v = c > 0x7F ? System.Globalization.CharUnicodeInfo.GetNumericValue(c) : -1;
                if (v >= 0 && v == Math.Floor(v)) { sb.Append(((long)v).ToString()); }
                else { sb.Append(c); }
            }
            return sb.ToString();
        }

        // Always-on OCR forensics: every keyed read drops what Tesseract saw
        // (raw gray stacked over thresholded) into a fixed-size ring, with the
        // key and result in the filename. When a read turns out wrong hours
        // later, the evidence is already on disk -- the flight-recorder
        // philosophy, in pixels. ~64 small crops, disk bounded.
        private void RingSave(string key, Mat gray, Mat thresholded, string result)
        {
            try
            {
                int slot = _ringSeq % RingSize;
                if (_ringFiles[slot] != null) { File.Delete(_ringFiles[slot]!); }
                string path = Path.Combine(_ringDir, $"{_ringSeq:D5}_{Sanitize(key)}={Sanitize(result)}.png");
                using Mat stacked = new Mat();
                Cv2.VConcat(new[] { gray, thresholded }, stacked);
                stacked.SaveImage(path);
                _ringFiles[slot] = path;
                _ringSeq++;
            }
            catch (Exception ex) { Logger.Warn($"ocr ring save failed: {ex.Message}"); }
        }

        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            string t = sb.ToString();
            if (t.Length == 0) { t = "EMPTY"; }
            return t.Length > 24 ? t.Substring(0, 24) : t;
        }

        public void Dispose()
        {
            _engine.Dispose();
        }
    }
}
