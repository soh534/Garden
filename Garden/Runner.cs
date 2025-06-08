using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;

using Thirdparty = Garden.ConfigManager.Config.Thirdparty;

namespace Garden
{
    class Runner
    {
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
            ScrcpyManager scrcpyManager = new(thirdparty.path);
            Process? proc = scrcpyManager.Start();
            Debug.Assert(proc != null);

            var cts = new CancellationTokenSource();

            // Start a command listener
            ConcurrentQueue<string> commandQueue = new ConcurrentQueue<string>();
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var command = Console.ReadLine();
                    if (command != null)
                    {
                        if (command.Equals("quit", StringComparison.OrdinalIgnoreCase))
                        {
                            cts.Cancel();
                            break;
                        }
                        commandQueue.Enqueue(command);
                    }
                }
            });

            ScreenshotManager ssManager = new(configManager.ImageSavePath);
            var processingTask = Task.Run(() => ssManager.ProcessFrames(cts.Token, proc, commandQueue), cts.Token);

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
            return;
        }
    }
}
