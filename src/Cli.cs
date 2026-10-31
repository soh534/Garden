using OpenCvSharp;

namespace Garden
{
    // Offline harnesses: run the live pipelines over saved frames/crops and exit -- no
    // phone, no capture, no console. They write their debug ring and recorder lines to a
    // temp folder, so a running engine's ring and flight recorder are never touched.
    //   Garden.exe --ocr-read <png|dir> [lang]     OCR each crop (OcrReader.Read)
    //   Garden.exe --find-all <roi> <png|dir>      every match of an ROI on each frame,
    //                                              with its read areas (FindAllRoi)
    internal static class Cli
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "garden_cli");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string[] Pngs(string path) => Directory.Exists(path)
            ? Directory.GetFiles(path, "*.png").OrderBy(f => f).ToArray()
            : new[] { path };

        public static void OcrRead(string path, string? lang)
        {
            Environment.CurrentDirectory = AppContext.BaseDirectory;   // Tesseract native DLL lookup
            string? prefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
            if (string.IsNullOrEmpty(prefix))
            {
                Console.WriteLine("TESSDATA_PREFIX is not set");
                return;
            }
            using var reader = new OcrReader(Path.Combine(prefix, "tessdata"), TempDir(), "jpn");
            foreach (string f in Pngs(path))
            {
                using Mat m = Cv2.ImRead(f);
                if (m.Empty())
                {
                    Console.WriteLine($"{Path.GetFileName(f)}\t<unreadable>");
                    continue;
                }
                Console.WriteLine($"{Path.GetFileName(f)}\t{reader.Read(m, "", lang)}");
            }
        }

        public static void FindAll(string roiName, string path)
        {
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            string? data = Environment.GetEnvironmentVariable("GARDEN_DATA");
            if (string.IsNullOrEmpty(data))
            {
                Console.WriteLine("GARDEN_DATA is not set");
                return;
            }
            string tmp = TempDir();
            Environment.SetEnvironmentVariable("GARDEN_STATE_DIR", tmp);   // recorder lines go to temp
            using var detector = new RoiDetector(Path.Combine(data, "rois"), tmp, "jpn");
            foreach (string f in Pngs(path))
            {
                using Mat frame = Cv2.ImRead(f);
                if (frame.Empty())
                {
                    Console.WriteLine($"{Path.GetFileName(f)}\t<unreadable>");
                    continue;
                }
                detector.SetFrame(frame);
                var matches = detector.FindAllRoi(roiName, 32);
                var parts = matches.Select(m => $"y={m.Y} x={m.X} s={m.Score:F4} click=({m.ClickX},{m.ClickY}) "
                    + string.Join(" ", m.Readings.Select(kv => $"{kv.Key}=[{kv.Value}]")));
                Console.WriteLine($"{Path.GetFileName(f)}\t{matches.Count}\t{string.Join(" | ", parts)}");
            }
        }
    }
}
