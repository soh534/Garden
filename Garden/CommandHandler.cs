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
        private readonly MouseEventRecorder _mouseRecorder;
        private readonly ActionPlayer _actionPlayer;

        public CommandHandler(string imageSavePath, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer)
        {
            _imageSavePath = imageSavePath;
            _mouseRecorder = mouseRecorder;
            _actionPlayer = actionPlayer;
        }

        public bool Handle(string command, Mat frame)
        {
            if (command.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Quit command received.");
                return false; // Signal to stop processing
            }

            if (command.StartsWith("save image ", StringComparison.OrdinalIgnoreCase))
            {
                HandleSaveCommand(command, frame);
                return true; // Continue processing
            }

            if (command.Equals("record action", StringComparison.OrdinalIgnoreCase))
            {
                _mouseRecorder.StartRecording();
                return true; // Continue processing
            }

            if (command.Equals("reset action", StringComparison.OrdinalIgnoreCase))
            {
                _mouseRecorder.ResetRecording();
                return true; // Continue processing
            }


            if (command.StartsWith("save action ", StringComparison.OrdinalIgnoreCase))
            {
                HandleMouseRecordSaveCommand(command);
                return true; // Continue processing
            }

            if (command.StartsWith("replay action ", StringComparison.OrdinalIgnoreCase))
            {
                HandleRunActionCommand(command);
                return true; // Continue processing
            }

            Console.WriteLine($"Unknown command: {command}");
            return true; // Continue processing
        }

        private void HandleSaveCommand(string command, Mat frame)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string filename = parts[2].Trim();
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
                Console.WriteLine("Usage: save image filename.png");
            }
        }

        private void HandleMouseRecordSaveCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string filename = parts[2].Trim();
                if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    filename += ".json";
                }
                _mouseRecorder.SaveRecording(filename);
            }
            else
            {
                Console.WriteLine("Usage: save action filename.json");
            }
        }

        private void HandleRunActionCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string filename = parts[2].Trim();
                if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    filename += ".json";
                }
                _actionPlayer.ReplayAction(filename);
            }
            else
            {
                Console.WriteLine("Usage: replay action filename");
            }
        }
    }
}
