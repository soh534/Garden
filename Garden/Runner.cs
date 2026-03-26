using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using NLog;
using Garden.Bots;

using Thirdparty = Garden.ConfigManager.Config.Thirdparty;

namespace Garden
{
    class Runner
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public void Run()
        {
            string dataRoot = Environment.GetEnvironmentVariable("GARDEN_DATA")
                ?? throw new InvalidOperationException("GARDEN_DATA environment variable is not set.");

            string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            ConfigManager configManager = new(configPath);

            string imageSavePath  = Path.Combine(dataRoot, "images");
            string actionSavePath = Path.Combine(dataRoot, "actions");
            string roiSavePath    = Path.Combine(dataRoot, "rois");
            string fsmPath        = Path.Combine(dataRoot, "fsm.json");

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

            // Position scrcpy window
            // Create window position manager
            WindowPositionManager windowPosManager = new(configManager.WindowPositions);
            windowPosManager.PositionAndAdvance(proc.MainWindowHandle);

            Console.WriteLine();
            Console.WriteLine("==========================================");
            Console.WriteLine("     Garden - Ready for Commands");
            Console.WriteLine("==========================================");
            CommandHandler.ShowHelp();
            Console.WriteLine("==========================================");
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

            MouseEventRecorder mouseRecorder = new(actionSavePath);
            ActionPlayer actionPlayer = new(actionSavePath, actionQueue);
            RoiRecorder roiRecorder = new(roiSavePath, commandQueue);
            Fsm fsm = new(fsmPath);
            StateDetector stateDetector = new(fsm, roiSavePath);

            // Initialize bot with action queue, detector, and action player
            // Bot for Garden game
            BotBase bot = new GardenBot();
            bot.Initialize(actionQueue, stateDetector, actionPlayer);
            FrameManager ssManager = new(imageSavePath, bot, mouseRecorder, actionPlayer, roiRecorder, stateDetector, windowPosManager);


            var processingTask = Task.Run(() => ssManager.ProcessFrames(cts.Token, proc, commandQueue, actionQueue), cts.Token);

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
