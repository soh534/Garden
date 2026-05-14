using System.Diagnostics;
using System.Net.Sockets;
using NLog;

namespace Garden
{
    internal class ScrcpyManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const string Executable = "scrcpy";
        private const int GardenServerScid = 1;
        private const int GardenServerPort = 27184;

        public record GardenServer(NetworkStream VideoStream, NetworkStream ControlStream, int PhoneWidth, int PhoneHeight);

        internal static bool IsAvailable()
        {
            return Environment
                .GetEnvironmentVariable("PATH")!
                .Split(';')
                .Any(dir => File.Exists(Path.Combine(dir, "scrcpy.exe")));
        }

        private static string? GetScrcpyDir() =>
            Environment.GetEnvironmentVariable("PATH")!
                .Split(';')
                .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "scrcpy.exe")));

        private static string GetScrcpyVersion()
        {
            using var proc = Process.Start(new ProcessStartInfo("scrcpy", "--version")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            })!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var parts = output.Split(' ');
            return parts.Length >= 2 ? parts[1].Trim() : "2.7";
        }

        private static void RunAdb(string args)
        {
            using var proc = Process.Start(new ProcessStartInfo("adb", args)
            {
                UseShellExecute = false, CreateNoWindow = true
            })!;
            proc.WaitForExit();
        }

        internal GardenServer? StartGardenServer()
        {
            string? scrcpyDir = GetScrcpyDir();
            if (scrcpyDir == null) { Logger.Error("scrcpy not found"); return null; }

            string serverPath = Path.Combine(scrcpyDir, "scrcpy-server");
            if (!File.Exists(serverPath)) { Logger.Error($"scrcpy-server not found in {scrcpyDir}"); return null; }

            string version = GetScrcpyVersion();
            Console.WriteLine($"[Garden] scrcpy version: {version}");
            Logger.Info($"Starting Garden server (scrcpy {version})");

            RunAdb($"push \"{serverPath}\" /data/local/tmp/scrcpy-server.jar");

            string scidHex = GardenServerScid.ToString("x8");
            RunAdb($"forward tcp:{GardenServerPort} localabstract:scrcpy_{scidHex}");

            string serverArgs = $"shell CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / " +
                $"com.genymobile.scrcpy.Server {version} " +
                $"scid={GardenServerScid} video=true audio=false control=true " +
                $"tunnel_forward=true send_device_meta=true send_dummy_byte=true cleanup=true";
            Process.Start(new ProcessStartInfo("adb", serverArgs)
            {
                UseShellExecute = false, CreateNoWindow = true
            });

            Thread.Sleep(500);

            var videoClient = new TcpClient("127.0.0.1", GardenServerPort) { NoDelay = true };
            var videoStream = videoClient.GetStream();

            var controlClient = new TcpClient("127.0.0.1", GardenServerPort) { NoDelay = true };
            var controlStream = controlClient.GetStream();

            int dummy = videoStream.ReadByte();
            if (dummy != 0) { Logger.Error("Garden server rejected connection"); return null; }

            byte[] nameBuf   = new byte[64]; videoStream.ReadExactly(nameBuf);
            byte[] codecBuf  = new byte[4];  videoStream.ReadExactly(codecBuf);
            byte[] widthBuf  = new byte[4];  videoStream.ReadExactly(widthBuf);
            byte[] heightBuf = new byte[4];  videoStream.ReadExactly(heightBuf);
            int phoneWidth  = (widthBuf[0]  << 24) | (widthBuf[1]  << 16) | (widthBuf[2]  << 8) | widthBuf[3];
            int phoneHeight = (heightBuf[0] << 24) | (heightBuf[1] << 16) | (heightBuf[2] << 8) | heightBuf[3];
            Logger.Info($"Phone: {phoneWidth}x{phoneHeight}");

            return new GardenServer(videoStream, controlStream, phoneWidth, phoneHeight);
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

        }
    }
}
