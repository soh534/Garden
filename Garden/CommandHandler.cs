using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using OpenCvSharp;
using NLog;
using System.Collections.Concurrent;

namespace Garden
{
    internal class CommandHandler
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _imageSavePath;
        private readonly MouseEventRecorder _mouseRecorder;
        private readonly ActionPlayer _actionPlayer;
        private readonly ConcurrentQueue<ActionPlayer.MouseEvent> _actionQueue;
        private readonly RoiRecorder _roiRecorder;
        private readonly FrameManager _frameManager;

        public CommandHandler(string imageSavePath, MouseEventRecorder mouseRecorder, ActionPlayer actionPlayer, ConcurrentQueue<ActionPlayer.MouseEvent> actionQueue, RoiRecorder roiRecorder, FrameManager frameManager)
        {
            _imageSavePath = imageSavePath;
            _mouseRecorder = mouseRecorder;
            _actionPlayer = actionPlayer;
            _actionQueue = actionQueue;
            _roiRecorder = roiRecorder;
            _frameManager = frameManager;
        }

        public bool Handle(string command, Mat frame)
        {
            if (command.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("Quit command received.");
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

            if (command.StartsWith("record roi ", StringComparison.OrdinalIgnoreCase))
            {
                HandleRecordRoiCommand(command);
                return true; // Continue processing
            }

            if (command.Equals("stop roi", StringComparison.OrdinalIgnoreCase))
            {
                _roiRecorder.StopRecording();
                return true; // Continue processing
            }

            if (command.StartsWith("remove roi ", StringComparison.OrdinalIgnoreCase))
            {
                HandleRemoveRoiCommand(command);
                return true; // Continue processing
            }

            if (command.Equals("list roi", StringComparison.OrdinalIgnoreCase))
            {
                _roiRecorder.ListStates();
                return true; // Continue processing
            }

            if (command.Equals("start bot", StringComparison.OrdinalIgnoreCase))
            {
                _frameManager.EnableBot();
                Console.WriteLine("Bot started.");
                return true; // Continue processing
            }

            if (command.Equals("stop bot", StringComparison.OrdinalIgnoreCase))
            {
                _frameManager.DisableBot();
                Console.WriteLine("Bot stopped.");
                return true; // Continue processing
            }

            if (command.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                ShowHelp();
                return true; // Continue processing
            }

            Logger.Info($"Unknown command: {command}");
            return true; // Continue processing
        }

        public static void ShowHelp()
        {
            Console.WriteLine("\nAvailable commands:");
            Console.WriteLine("  save image <filename.png>   - Save screenshot");
            Console.WriteLine("  record action               - Start recording mouse clicks");
            Console.WriteLine("  reset action                - Clear recorded events (stay recording)");
            Console.WriteLine("  save action <filename>      - Save recorded clicks & end recording");
            Console.WriteLine("  replay action <filename>    - Replay recorded clicks");
            Console.WriteLine("  record roi <state_name>     - Start recording ROIs for a state");
            Console.WriteLine("  stop roi                    - Stop ROI recording");
            Console.WriteLine("  list roi                    - List all ROI states");
            Console.WriteLine("  remove roi <state_name>     - Remove ROI state and its files");
            Console.WriteLine("  start bot                   - Start the bot automation");
            Console.WriteLine("  stop bot                    - Stop the bot automation");
            Console.WriteLine("  help                        - Show this help message");
            Console.WriteLine("  quit                        - Exit application");
            Console.WriteLine();
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
                    Logger.Info($"Warning: Overwriting existing file {savePath}");
                }
                Cv2.ImWrite(savePath, frame);
                Logger.Info($"Frame saved to {savePath}");
            }
            else
            {
                Logger.Info("Usage: save image filename.png");
            }
        }

        private void HandleMouseRecordSaveCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string filename = parts[2].Trim();
                _mouseRecorder.SaveRecording(filename);
            }
            else
            {
                Logger.Info("Usage: save action filename.json");
            }
        }

        private void HandleRunActionCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string filename = parts[2].Trim();
                _actionPlayer.QueueReplay(filename);
            }
            else
            {
                Logger.Info("Usage: replay action filename");
            }
        }

        private void HandleRecordRoiCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string stateName = parts[2].Trim();
                _roiRecorder.StartRecording(stateName);
            }
            else
            {
                Logger.Info("Usage: record roi <state_name>");
            }
        }

        private void HandleRemoveRoiCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string stateName = parts[2].Trim();
                _roiRecorder.RemoveState(stateName);
            }
            else
            {
                Logger.Info("Usage: remove roi <state_name>");
            }
        }
    }
}
