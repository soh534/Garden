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

                if (!string.IsNullOrEmpty(debugKey))
                {
                    string path = Path.Combine(_debugDir, $"ocr_{debugKey.Replace("/", "_")}.png");
                    thresholded.SaveImage(path);
                }

                byte[] pngBytes = thresholded.ToBytes(".png");
                lock (_engineLock)
                {
                    using var pix = Pix.LoadFromMemory(pngBytes);
                    using var page = _engine.Process(pix, PageSegMode.SingleWord);
                    return NormalizeDigits(page.GetText().Trim());
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

        public void Dispose()
        {
            _engine.Dispose();
        }
    }
}
