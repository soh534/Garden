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
                    _frameManager.DisableBot();
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
                            string? recName = isPath
                                ? (parts.Length > 3 ? string.Join(' ', parts.Skip(3)).Trim() : null)
                                : (parts.Length > 2 ? string.Join(' ', parts.Skip(2)).Trim() : null);
                            if (recName == null) { Console.WriteLine("Usage: action record <name>  |  action record path <name>"); break; }
                            _mouseRecorder.StartRecording(isPath, recName);
                            break;
                        case "reset":
                            _mouseRecorder.ResetRecording();
                            break;
                        case "stop":
                            _mouseRecorder.StopRecording();
                            break;
                        case "replay":
                            HandleRunActionCommand(command);
                            break;
                        case "list":
                            _actionPlayer.ListActions();
                            break;
                        case "remove":
                            HandleRemoveActionCommand(command);
                            break;
                    }
                    return true;

                case "roi":
                    switch (verb)
                    {
                        case "record":
                            bool isFixed = parts.Length > 2 && parts[2].ToLowerInvariant() == "fixed";
                            string roiName = isFixed
                                ? (parts.Length > 3 ? string.Join(' ', parts.Skip(3)).Trim() : "")
                                : (parts.Length > 2 ? string.Join(' ', parts.Skip(2)).Trim() : "");
                            if (string.IsNullOrEmpty(roiName)) { Console.WriteLine("Usage: roi record <name>  |  roi record fixed <name>"); break; }
                            _roiRecorder.StartRecording(roiName, isFixed);
                            break;
                        case "stop":
                            _roiRecorder.StopRecording();
                            break;
                        case "remove":
                            HandleRemoveRoiCommand(command);
                            break;
                        case "list":
                            _roiRecorder.ListRois();
                            break;
                        case "rename":
                            HandleRenameRoiCommand(command);
                            break;
                        case "fixed":
                            if (parts.Length < 3) { Console.WriteLine("Usage: roi fixed <name>"); break; }
                            _roiRecorder.ToggleFixed(parts[2]);
                            break;
                        case "clickpoint":
                            if (parts.Length < 3) { Console.WriteLine("Usage: roi clickpoint <name>"); break; }
                            _roiRecorder.EditClickPoint(parts[2]);
                            break;
                        case "box":
                            if (parts.Length < 3) { Console.WriteLine("Usage: roi box <name>"); break; }
                            _roiRecorder.EditBox(parts[2]);
                            break;
                        case "readarea":
                            if (parts.Length < 4) { Console.WriteLine("Usage: roi readarea <name> add | roi readarea <name> remove <area>"); break; }
                            if (parts[3].ToLowerInvariant() == "add") { _roiRecorder.AddReadArea(parts[2]); }
                            else if (parts[3].ToLowerInvariant() == "remove" && parts.Length >= 5) { _roiRecorder.RemoveReadArea(parts[2], parts[4]); }
                            else { Console.WriteLine("Usage: roi readarea <name> add | roi readarea <name> remove <area>"); }
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

                case "lua":
                    int sp = command.IndexOf(' ');
                    if (sp >= 0 && sp + 1 < command.Length) { _frameManager.EvalLua(command.Substring(sp + 1)); }
                    else { Console.WriteLine("Usage: lua <code>"); }
                    return true;

                case "abort":
                    _frameManager.AbortLua();
                    Console.WriteLine("Aborting lua eval...");
                    return true;

                case "scan":
                    if (verb == "on")       { _frameManager.SetScanEnabled(true);  Console.WriteLine("Scan ON."); }
                    else if (verb == "off") { _frameManager.SetScanEnabled(false); Console.WriteLine("Scan OFF."); }
                    else { Console.WriteLine($"Scan is {(_frameManager.ScanEnabled ? "ON" : "OFF")}. Usage: scan on | scan off"); }
                    return true;

                default:
                    Console.WriteLine($"Unknown command: '{command}'. Type 'help' for available commands.");
                    return true;
            }
        }

        public static void ShowHelp()
        {
            Console.WriteLine("\nAvailable commands:");
            Console.WriteLine("  image save <filename.png>   - Save screenshot");
            Console.WriteLine("  action record <name>        - Start recording mouse clicks (linear)");
            Console.WriteLine("  action record path <name>   - Start recording mouse path");
            Console.WriteLine("  action reset                - Clear recorded events (stay recording)");
            Console.WriteLine("  action stop                 - Stop recording and save");
            Console.WriteLine("  action replay <filename>    - Replay recorded clicks");
            Console.WriteLine("  action list                 - List all actions");
            Console.WriteLine("  action remove <name>        - Remove an action");
            Console.WriteLine("  roi record <name>           - Record an ROI and save it as <name>");
            Console.WriteLine("  roi record fixed <name>     - Record a fixed-location ROI (checked in place, no search)");
            Console.WriteLine("  lua <code>                  - Run Lua against the live bot state (debug)");
            Console.WriteLine("  abort                       - Stop a running 'lua' eval");
            Console.WriteLine("  scan on | scan off          - Toggle the background detection scan / overlay");
            Console.WriteLine("  roi stop                    - Cancel ROI recording");
            Console.WriteLine("  roi list                    - List all ROIs");
            Console.WriteLine("  roi remove <roi_name>       - Remove an ROI and its image");
            Console.WriteLine("  roi rename <old> <new>      - Rename an ROI (image + metadata)");
            Console.WriteLine("  roi fixed <name>            - Toggle an ROI's fixedLocation flag");
            Console.WriteLine("  roi clickpoint <name>       - Re-set the clickpoint (then click the target)");
            Console.WriteLine("  roi box <name>              - Re-draw the box (then drag); re-crops, keeps clickpoint/read areas");
            Console.WriteLine("  roi readarea <name> add     - Add a read area (then drag + name it)");
            Console.WriteLine("  roi readarea <name> remove <area> - Remove a read area");
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

        private void HandleRemoveActionCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                _actionPlayer.RemoveAction(parts[2].Trim());
            }
            else
            {
                Logger.Info("Usage: action remove <name>");
            }
        }

        private void HandleRenameRoiCommand(string command)
        {
            var parts = command.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4)
            {
                _roiRecorder.RenameRoi(parts[2].Trim(), parts[3].Trim());
            }
            else
            {
                Logger.Info("Usage: roi rename <old_name> <new_name>");
            }
        }

        private void HandleRemoveRoiCommand(string command)
        {
            var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                string roiName = parts[2].Trim();
                _roiRecorder.RemoveRoi(roiName);
            }
            else
            {
                Logger.Info("Usage: roi remove <roi_name>");
            }
        }
    }
}
