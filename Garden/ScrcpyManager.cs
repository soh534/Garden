using System.Diagnostics;
using NLog;

namespace Garden
{
    internal class ScrcpyManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        // Ensure scrcpy.exe > Properties > Compatibility > Change high DPI settings > Override high DPI scaling behavior > Scaling performed by: System
        private const string Executable = "scrcpy";

        internal static bool IsAvailable()
        {
            return Environment
                .GetEnvironmentVariable("PATH")!
                .Split(';')
                .Any(dir => File.Exists(Path.Combine(dir, "scrcpy.exe")));
        }

        internal Process? Start()
        {
            ProcessStartInfo startInfo = new();
            startInfo.FileName = Executable;
            startInfo.Arguments = "--no-mouse-hover --stay-awake --power-off-on-close --window-title=Garden"; // Without this, seed is planted without hovering.
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = false;
            try
            {
                Process? exeProcess = Process.Start(startInfo);

                if (exeProcess == null)
                {
                    return null;
                }

                // Block until Garden window is created.
                while (string.IsNullOrEmpty(exeProcess.MainWindowTitle))
                {
                    // Check if the process exits before window is created and get out of loop.
                    if (exeProcess.HasExited)
                    {
                        Logger.Info("Process exited before window is created.");
                        return null;
                    }
                    Thread.Sleep(100);
                    exeProcess.Refresh();
                }

                return exeProcess;
            }
            catch (Exception e)
            {
                Logger.Error($"Failed somehow: {e.Message}");
                throw;
            }

            return null;
        }
    }
}
