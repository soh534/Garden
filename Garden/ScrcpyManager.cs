using System.Diagnostics;
using static Garden.Runner;
using System.Text.Json;

namespace Garden
{
    internal class ScrcpyManager
    {
        readonly string _executablePath;
        public class Thirdparty
        {
            public string name { get; set; }
            public string path { get; set; }
        }

        public ScrcpyManager(string thirdPartyPath)
        {
            string jsonString = File.ReadAllText(thirdPartyPath);
            List<Thirdparty>? thirdPartySdks = JsonSerializer.Deserialize<List<Thirdparty>>(jsonString);
            Debug.Assert(thirdPartySdks != null);

            Thirdparty? scrcpy = thirdPartySdks.Find(sdk => sdk.name.Equals("scrcpy"));
            Debug.Assert(scrcpy != null);
            _executablePath = scrcpy.path;
        }

        internal Process? Start()
        {
            ProcessStartInfo startInfo = new();
            startInfo.FileName = _executablePath;
            startInfo.Arguments = "--no-mouse-hover --window-title=Garden"; // Without this, seed is planted without hovering.
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
                        Debug.WriteLine("Process exited before window is created.");
                        return null;
                    }
                    Thread.Sleep(100);
                    exeProcess.Refresh();
                }

                return exeProcess;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Failed somehow: {e.Message}");
            }

            return null;
        }
    }
}
