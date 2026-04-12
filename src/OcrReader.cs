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

        public OcrReader(string tessDataPath, string debugDir)
        {
            _engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
            _engine.SetVariable("tessedit_char_whitelist", "0123456789");
            _debugDir = debugDir;
        }

        public int ReadInt(Mat mat, string debugKey = "")
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
                using var pix = Pix.LoadFromMemory(pngBytes);
                using var page = _engine.Process(pix, PageSegMode.SingleWord);
                string text = page.GetText().Trim();
                if (int.TryParse(text, out int value))
                {
                    return value;
                }
                Logger.Warn($"OCR could not parse int from: '{text}'");
                return -1;
            }
            catch (Exception ex)
            {
                Logger.Error($"OCR error: {ex.Message}");
                return -1;
            }
        }

        public void Dispose()
        {
            _engine.Dispose();
        }
    }
}
