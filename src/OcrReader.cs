using NLog;
using OpenCvSharp;
using Tesseract;

namespace Garden
{
    public class OcrReader : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly Dictionary<string, TesseractEngine> _engines = new();
        private readonly string _tessDataPath;
        private readonly string _defaultLang;
        private readonly string _debugDir;
        private readonly string _ringDir;
        private readonly string?[] _ringFiles = new string?[RingSize];
        private int _ringSeq;
        private const int RingSize = 64;
        private readonly object _engineLock = new();

        public OcrReader(string tessDataPath, string debugDir, string lang)
        {
            // One engine per language, created on first use. The default (config.json,
            // jpn) suits mixed text like the jouro deadline (digits + 年/月/日). A read
            // area may name its own `lang`: the jpn LSTM, handed a bare 2-digit crop
            // with no context, hallucinates kana -- '22' -> 'レ_タ_4', '13' -> '】',
            // '24' -> 'レ Z|' (ocr_ring + app log, 09-15) -- so digit-only areas read
            // with eng. No whitelist: callers interpret raw text (getOcrInt strips
            // non-digits, getOcrStr takes it as-is).
            _tessDataPath = tessDataPath;
            _defaultLang = string.IsNullOrEmpty(lang) ? "jpn" : lang;
            _debugDir = debugDir;
            _ringDir = Path.Combine(debugDir, "ocr_ring");
            if (Directory.Exists(_ringDir)) { Directory.Delete(_ringDir, true); }
            Directory.CreateDirectory(_ringDir);
            GetEngine(_defaultLang);
        }

        // must be called under _engineLock (the constructor is the one exception)
        private TesseractEngine GetEngine(string lang)
        {
            if (!_engines.TryGetValue(lang, out var engine))
            {
                engine = new TesseractEngine(_tessDataPath, lang, EngineMode.Default);
                // Silence Tesseract's internal diagnostic spew (STATS/baseline prints) by
                // routing its debug output to the null device.
                engine.SetVariable("debug_file", "NUL");
                _engines[lang] = engine;
                Logger.Info($"OCR engine: lang={lang}");
            }
            return engine;
        }

        // Returns the raw recognized text (trimmed), or "" on failure.
        public string Read(Mat mat, string debugKey = "", string? lang = null)
        {
            if (string.IsNullOrEmpty(lang)) { lang = _defaultLang; }
            try
            {
                using Mat upscaled = new Mat();
                Cv2.Resize(mat, upscaled, new Size(mat.Width * 3, mat.Height * 3), interpolation: InterpolationFlags.Cubic);
                using Mat gray = new Mat();
                Cv2.CvtColor(upscaled, gray, ColorConversionCodes.BGR2GRAY);
                using Mat thresholded = new Mat();
                Cv2.Threshold(gray, thresholded, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                // Tesseract segments glyphs against the page around them. A crop cut to
                // the digit extents (the tightened read areas) has no page: '11' with no
                // margin is one tall blob -> 'i' (109111, 09-16 13:14), '22' -> 'pY.'.
                // Add the margin in the image's OWN page colour (majority of its border),
                // so polarity is untouched -- the 09-15 WHITE margin + inversion is what
                // turned a light corner wedge into a phantom leading '1'. No inversion.
                Scalar pageColour = PageColour(thresholded);
                using Mat padded = new Mat();
                Cv2.CopyMakeBorder(thresholded, padded, 16, 16, 16, 16, BorderTypes.Constant, pageColour);

                byte[] pngBytes = padded.ToBytes(".png");
                lock (_engineLock)
                {
                    TesseractEngine engine = GetEngine(lang);
                    using var pix = Pix.LoadFromMemory(pngBytes);
                    using var page = engine.Process(pix, PageSegMode.SingleWord);
                    string text = NormalizeDigits(page.GetText().Trim());
                    if (!string.IsNullOrEmpty(debugKey)) { RingSave(debugKey, lang, gray, thresholded, text); }
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

        // the binary image's page colour = whichever value dominates its border
        private static Scalar PageColour(Mat bin)
        {
            int white = 0, total = 0;
            for (int x = 0; x < bin.Cols; x++)
            {
                if (bin.At<byte>(0, x) == 255) { white++; }
                if (bin.At<byte>(bin.Rows - 1, x) == 255) { white++; }
                total += 2;
            }
            for (int y = 1; y < bin.Rows - 1; y++)
            {
                if (bin.At<byte>(y, 0) == 255) { white++; }
                if (bin.At<byte>(y, bin.Cols - 1) == 255) { white++; }
                total += 2;
            }
            return white * 2 > total ? Scalar.White : Scalar.Black;
        }

        // Always-on OCR forensics: every keyed read drops what Tesseract saw
        // (raw gray stacked over thresholded) into a fixed-size ring, with the
        // key and result in the filename. When a read turns out wrong hours
        // later, the evidence is already on disk -- the flight-recorder
        // philosophy, in pixels. ~64 small crops, disk bounded.
        private void RingSave(string key, string lang, Mat gray, Mat thresholded, string result)
        {
            try
            {
                int slot = _ringSeq % RingSize;
                if (_ringFiles[slot] != null) { File.Delete(_ringFiles[slot]!); }
                string path = Path.Combine(_ringDir, $"{_ringSeq:D5}_{Sanitize(key)}@{lang}={Sanitize(result)}.png");
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
            foreach (TesseractEngine e in _engines.Values) { e.Dispose(); }
        }
    }
}
