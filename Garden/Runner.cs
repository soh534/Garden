using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Garden
{
    class Runner
    {
        const string ConfigPath = "E:\\Code\\Garden\\Garden\\config.json";

        public void Run()
        {
            ScrcpyManager scrcpyManager = new(ConfigPath);
            Process? proc = scrcpyManager.Start();
            Debug.Assert(proc != null);

            ScreenshotManager ssManager = new();

            var cts = new CancellationTokenSource();
            var processingTask = Task.Run(() => ssManager.ProcessFrames(cts.Token, proc), cts.Token);

            // Wait for enter key to be pressed marking exit.
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();

            proc.Refresh();
            // Send close message, this doesn't ensure process is killed.
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
