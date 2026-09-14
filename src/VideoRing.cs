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
        private const long MinSegmentBytes = 24 * 1024 * 1024;   // ~30-45s per segment on a busy screen
        private const int MaxSegments = 185;                      // ~5GB at ~27MB/segment: ~3h of ACTIVE work, far more calendar time (idle barely rotates)
        private const int MaxSavedDays = 30;                      // revisit a bug for a month
        private const int MaxSavedDirs = 100;                     // storm backstop: a recovery loop preserves every ~16 min

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
            // Retention is derived from the DIRECTORY, not from this process's
            // memory. The queue used to start empty on every launch, so the ring
            // could only delete segments IT had created -- each restart orphaned
            // its predecessor's MaxSegments files permanently. Measured 09-14:
            // 339 files / 8.8GB from ~17 runs since 07-10, the per-day counts
            // landing on exactly 20 (= the cap) run after run.
            // Ordered by write time, not name: segment names carry no year and
            // would misorder across a year boundary.
            try
            {
                foreach (string f in new DirectoryInfo(_dir).GetFiles("*.h264")
                                                            .OrderBy(fi => fi.LastWriteTimeUtc)
                                                            .Select(fi => fi.FullName))
                {
                    _segments.Enqueue(f);
                }
                int adopted = _segments.Count;
                int pruned = PruneSegments();
                if (adopted > 0) { Logger.Info($"video ring: adopted {adopted} segments from disk, pruned {pruned}"); }
            }
            catch (Exception ex) { Logger.Warn($"video ring: could not adopt existing segments: {ex.Message}"); }
        }

        // delete oldest segments beyond the cap; returns how many went
        private int PruneSegments()
        {
            int n = 0;
            while (_segments.Count > MaxSegments)
            {
                string old = _segments.Dequeue();
                try { File.Delete(old); n++; } catch { }
            }
            return n;
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
            PruneSegments();
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
                    // bound the archive by AGE so old bugs stay visitable, with a
                    // count backstop so a failure storm cannot fill the disk.
                    // By write time, not name (names carry no year).
                    var dirs = new DirectoryInfo(savedRoot).GetDirectories()
                                                           .OrderBy(di => di.LastWriteTimeUtc)
                                                           .ToList();
                    var cutoff = DateTime.UtcNow.AddDays(-MaxSavedDays);
                    while (dirs.Count > 0 && (dirs[0].LastWriteTimeUtc < cutoff || dirs.Count > MaxSavedDirs))
                    {
                        try { dirs[0].Delete(true); } catch { }
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
