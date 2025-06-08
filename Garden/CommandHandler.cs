using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using OpenCvSharp;

namespace Garden
{
    public class CommandHandler
    {
        private readonly string _imageSavePath;

        public CommandHandler(string imageSavePath)
        {
            _imageSavePath = imageSavePath;
        }

        public void Handle(string command, Mat frame)
        {
            if (command.StartsWith("save ", StringComparison.OrdinalIgnoreCase))
            {
                HandleSaveCommand(command, frame);
                return;
            }
        }

        private void HandleSaveCommand(string command, Mat frame)
        {
            var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                string filename = parts[1].Trim();
                string savePath = Path.Combine(_imageSavePath, filename);
                Directory.CreateDirectory(_imageSavePath);
                if (File.Exists(savePath))
                {
                    Console.WriteLine($"Warning: Overwriting existing file {savePath}");
                }
                Cv2.ImWrite(savePath, frame);
                Console.WriteLine($"Frame saved to {savePath}");
            }
            else
            {
                Console.WriteLine("Usage: save filename.png");
            }
        }
    }
}
