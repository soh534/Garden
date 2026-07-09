using NLog;

namespace Garden
{
    // Always-on video forensics: tees the raw H.264 packets from the scrcpy
    // video socket into time-named rolling segments, bounded on disk. The
    // scrcpy packet header flags config (SPS/PPS) and keyframe packets, so
    // segments rotate on keyframes and get the cached config prepended --
    // every segment plays standalone (ffplay/VLC), and preserved evidence is
    // losslessly remuxed to .mp4. Forensics must never hurt the pipeline: any
    // IO failure disables the ring, not the stream.
    internal class VideoRing
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private const long MinSegmentBytes = 8 * 1024 * 1024;
        private const int MaxSegments = 12;
        private const int MaxSaved = 5;

        private readonly string _dir;
        private readonly object _lock = new();
        private byte[]? _configPacket;
        private FileStream? _segment;
        private readonly Queue<string> _segments = new();
        private bool _broken;

        public VideoRing(string baseDir)
        {
            _dir = Path.Combine(baseDir, "video_ring");
            Directory.CreateDirectory(_dir);
        }

        // called from VideoStreamLoop for every packet; must never throw
        public void Write(byte[] data, bool isConfig, bool isKeyFrame)
        {
            if (_broken) { return; }
            lock (_lock)
            {
                try
                {
                    if (isConfig)
                    {
                        _configPacket = data;   // re-emitted at each segment head
                        return;
                    }
                    if (_segment == null || (isKeyFrame && _segment.Length >= MinSegmentBytes))
                    {
                        Rotate();
                    }
                    _segment!.Write(data, 0, data.Length);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"video ring disabled: {ex.Message}");
                    _broken = true;
                }
            }
        }

        private void Rotate()
        {
            _segment?.Dispose();
            string path = Path.Combine(_dir, $"video_{DateTime.Now:MMdd_HHmmss}.h264");
            _segment = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            if (_configPacket != null) { _segment.Write(_configPacket, 0, _configPacket.Length); }
            _segments.Enqueue(path);
            while (_segments.Count > MaxSegments)
            {
                string old = _segments.Dequeue();
                try { File.Delete(old); } catch { }
            }
        }

        // copy the newest segments aside so the ring can't overwrite evidence;
        // remux happens outside the lock so the packet pump never waits on ffmpeg
        public string Preserve(string tag)
        {
            var copied = new List<string>();
            string dest;
            lock (_lock)
            {
                try
                {
                    _segment?.Flush();
                    string savedRoot = Path.Combine(_dir, "saved");
                    Directory.CreateDirectory(savedRoot);
                    dest = Path.Combine(savedRoot, $"{DateTime.Now:MMdd_HHmmss}_{tag}");
                    Directory.CreateDirectory(dest);
                    foreach (string f in _segments.TakeLast(2))
                    {
                        string raw = Path.Combine(dest, Path.GetFileName(f));
                        File.Copy(f, raw, true);
                        copied.Add(raw);
                    }
                    // bound the archive: drop oldest saved dirs beyond MaxSaved
                    var dirs = Directory.GetDirectories(savedRoot).OrderBy(d => d).ToList();
                    while (dirs.Count > MaxSaved)
                    {
                        try { Directory.Delete(dirs[0], true); } catch { }
                        dirs.RemoveAt(0);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"video preserve failed: {ex.Message}");
                    return "";
                }
            }
            foreach (string raw in copied) { RemuxToMp4(raw); }
            return dest;
        }

        // lossless remux .h264 -> .mp4 (double-click playable); the raw stream
        // is kept only if the remux fails
        private static void RemuxToMp4(string rawPath)
        {
            try
            {
                string mp4 = Path.ChangeExtension(rawPath, ".mp4");
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "ffmpeg", $"-y -loglevel error -i \"{rawPath}\" -c copy \"{mp4}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true
                })!;
                if (proc.WaitForExit(15000) && proc.ExitCode == 0 && File.Exists(mp4))
                {
                    File.Delete(rawPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"video remux failed: {ex.Message}");
            }
        }
    }
}
