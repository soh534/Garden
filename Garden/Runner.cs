using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using NLog;

using Thirdparty = Garden.ConfigManager.Config.Thirdparty;

namespace Garden
{
    class Runner
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        const string ConfigPath = "E:\\Code\\Garden\\Garden\\config.json";

        public void Run()
        {
            ConfigManager configManager = new(ConfigPath);

            Thirdparty? thirdparty = configManager.GetThirdPartySdk("scrcpy");
            if (thirdparty == null)
            {
                Console.WriteLine("scrcpy SDK not found in config.");
                return;
            }
            // Initialize window manager with the window title and scale
            WindowManager.Instance.SetWindowTitle("Garden");
            WindowManager.Instance.SetScale(configManager.Scale);

            ScrcpyManager scrcpyManager = new(thirdparty.path);
            Process? proc = scrcpyManager.Start();
            Debug.Assert(proc != null);

            Console.WriteLine();
            Console.WriteLine("==========================================");
            Console.WriteLine("     Garden - Ready for Commands");
            Console.WriteLine("==========================================");
            Console.WriteLine("Available commands:");
            Console.WriteLine("  save image <filename.png>   - Save screenshot");
            Console.WriteLine("  record action               - Start recording mouse clicks");
            Console.WriteLine("  reset action                - Clear recorded events (stay recording)");
            Console.WriteLine("  save action <filename>      - Save recorded clicks & end recording");
            Console.WriteLine("  replay action <filename>    - Replay recorded clicks");
            Console.WriteLine("  record roi <state_name>     - Start recording ROIs for a state");
            Console.WriteLine("  stop roi                    - Stop ROI recording");
            Console.WriteLine("  quit                        - Exit application");
            Console.WriteLine("==========================================");
            Console.WriteLine();
            Console.Write("Enter command: ");

            var cts = new CancellationTokenSource();

            // Start a command listener
            ConcurrentQueue<string> commandQueue = new ConcurrentQueue<string>();
            ConcurrentQueue<ActionPlayer.MouseEvent> actionQueue = new ConcurrentQueue<ActionPlayer.MouseEvent>();
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var command = Console.ReadLine();
                    if (command != null)
                    {
                        commandQueue.Enqueue(command);
                    }
                }
            });

            ScreenshotManager ssManager = new(configManager.ImageSavePath, configManager.ActionSavePath);
            MouseEventRecorder mouseRecorder = new(configManager.ActionSavePath);
            ActionPlayer actionPlayer = new(configManager.ActionSavePath);
            RoiRecorder roiRecorder = new(configManager.RoiSavePath, commandQueue);
            StateDetector stateDetector = new(configManager.RoiSavePath);
            var processingTask = Task.Run(() => ssManager.ProcessFrames(cts.Token, proc, commandQueue, actionQueue, mouseRecorder, actionPlayer, roiRecorder, stateDetector), cts.Token);

            // Wait for processing to finish (cancellation will be triggered by "quit" command
            processingTask.Wait();

            // Send close message, this doesn't ensure process is killed.
            proc.Refresh();
            if (!proc.CloseMainWindow())
            {
                // Could not send close message, fallback to kill.
                proc.Kill();
            }
            else
            {
                if (!proc.WaitForExit(5000))
                {
                    // If didn't close after 5 seconds, fallback to kill.
                    proc.Kill();
                }
            }

            cts.Cancel();
            processingTask.Wait();
            mouseRecorder.Dispose();
            roiRecorder.Dispose();
            return;
        }
    }
}
