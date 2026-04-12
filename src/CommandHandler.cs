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
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;

            string subject = parts[0].ToLowerInvariant();
            string verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

            switch (subject)
            {
                case "quit":
                    Logger.Info("Quit command received.");
                    return false;

                case "help":
                    ShowHelp();
                    return true;

                case "image":
                    if (verb == "save")
                    {
                        HandleSaveCommand(command, frame);
                    }
                    return true;

                case "action":
                    switch (verb)
                    {
                        case "record":
                            bool isPath = parts.Length > 2 && parts[2].ToLowerInvariant() == "path";
                            _mouseRecorder.StartRecording(isPath);
                            break;
                        case "reset":
                            _mouseRecorder.ResetRecording();
                            break;
                        case "save":
                            HandleMouseRecordSaveCommand(command);
                            break;
                        case "replay":
                            HandleRunActionCommand(command);
                            break;
                    }
                    return true;

                case "roi":
                    switch (verb)
                    {
                        case "record":
                            HandleRecordRoiCommand(command);
                            break;
                        case "stop":
                            _roiRecorder.StopRecording();
                            break;
                        case "remove":
                            HandleRemoveRoiCommand(command);
                            break;
                        case "list":
                            _roiRecorder.ListStates();
                            break;
                    }
                    return true;

                case "bot":
                    switch (verb)
                    {
                        case "start":
                            _frameManager.EnableBot();
                            Console.WriteLine("Bot started.");
                            break;
                        case "stop":
                            _frameManager.DisableBot();
                            Console.WriteLine("Bot stopped.");
                            break;
                    }
                    return true;

                default:
                    Logger.Info($"Unknown command: {command}");
                    return true;
            }
        }

        public static void ShowHelp()
        {
            Console.WriteLine("\nAvailable commands:");
            Console.WriteLine("  image save <filename.png>   - Save screenshot");
            Console.WriteLine("  action record               - Start recording mouse clicks (linear)");
            Console.WriteLine("  action record path          - Start recording mouse path");
            Console.WriteLine("  action reset                - Clear recorded events (stay recording)");
            Console.WriteLine("  action save <name>          - Save recorded clicks & end recording");
            Console.WriteLine("  action replay <filename>    - Replay recorded clicks");
            Console.WriteLine("  roi record <state_name>     - Start recording ROIs for a state");
            Console.WriteLine("  roi stop                    - Stop ROI recording");
            Console.WriteLine("  roi list                    - List all ROI states");
            Console.WriteLine("  roi remove <state_name>     - Remove ROI state and its files");
            Console.WriteLine("  bot start                   - Start the bot automation");
            Console.WriteLine("  bot stop                    - Stop the bot automation");
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
                if (!filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { filename += ".png"; }
                string savePath = Path.Combine(_imageSavePath, filename);
                Directory.CreateDirectory(_imageSavePath);
                if (File.Exists(savePath))
                {
                    Logger.Info($"Warning: Overwriting existing file {savePath}");
                }
                Cv2.ImWrite(savePath, frame);
                Console.WriteLine($"Saved: {savePath}");
            }
            else
            {
                Logger.Info("Usage: image save filename.png");
            }
        }

        private void HandleMouseRecordSaveCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string actionName = parts[2].Trim();
                _mouseRecorder.SaveRecording(actionName);
            }
            else
            {
                Logger.Info("Usage: action save <name>");
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
                Logger.Info("Usage: action replay filename");
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
                Logger.Info("Usage: roi record <state_name>");
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
                Logger.Info("Usage: roi remove <state_name>");
            }
        }
    }
}
