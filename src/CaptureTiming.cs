using System.Diagnostics;

namespace Garden
{
    // How far behind the phone the engine's view runs, and where that time goes.
    //
    // The scrcpy packet header carries each frame's capture time (PTS, microseconds
    // on the phone's encoder clock). arrival - pts = clock offset + pipeline latency,
    // so its lowest value in an encoder session is the best case the pipeline has
    // shown, and
    //     lag(t) = (t - pts) - lowest
    // is how far behind that best case a frame is at time t: when the packet reader
    // gets it, when ffmpeg hands it back decoded, and when the detector gets it.
    // The packet reader's own time splits three ways: waiting on the phone, writing
    // the ring, and writing into ffmpeg (which blocks while the decode side is
    // behind). A lag that grows while the reader waits on the phone is upstream
    // (phone encoder, adb); one that grows while the reader is blocked writing into
    // ffmpeg is downstream back-pressure.
    //
    // One line per second of stream activity goes to a rolling local log; a lag
    // episode (> 0.5 s) opens and closes with a line in the flight recorder, so it
    // interleaves with the bot's decisions. Decoded frames are paired with their
    // packets in order: one frame per media packet, none per config packet.
    internal sealed class CaptureTiming
    {
        public readonly record struct FrameStamp(long PtsUs, long ArrivalTicks, long DecodedTicks);

        private const long LogMaxBytes = 8 * 1024 * 1024;
        private const double EpisodeOpenMs = 500;
        private const double EpisodeCloseMs = 200;

        private readonly string _path;
        private readonly string _oldPath;
        private readonly Action<string> _recorder;
        private readonly object _lock = new();
        private bool _rotateFailed;

        private long _baseUs = long.MaxValue;     // lowest (arrival - pts) this encoder session
        private long _lastPtsUs = -1;
        private readonly Queue<(long PtsUs, long ArrivalTicks)> _undecoded = new();
        private long _lastPacketTicks;

        // the current one-second bucket (0 = none open)
        private long _bucketTicks;
        private DateTime _bucketWall;
        private int _packets, _media, _keyFrames, _frames, _views;
        private long _bytes;
        private double _lagMin, _lagSum, _lagMax;
        private double _waitMs, _ringMs, _ffmpegMs, _ffmpegMaxMs;
        private double _frameLagMax, _decodeWaitMs, _viewLagMax;

        private bool _episode;
        private double _episodePeak;
        private DateTime _episodeStart;

        public CaptureTiming(string dir, Action<string> recorder)
        {
            _path = Path.Combine(dir, "capture_timing.log");
            _oldPath = Path.Combine(dir, "capture_timing.old");
            _recorder = recorder;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        private static long Us(long ticks) => (long)(ticks * (1_000_000.0 / Stopwatch.Frequency));
        private double LagMs(long ticks, long ptsUs) => (Us(ticks) - ptsUs - _baseUs) / 1000.0;

        // a new capture session: new server, new PTS origin, nothing in flight
        public void NewSession(int gen)
        {
            lock (_lock)
            {
                _baseUs = long.MaxValue;
                _lastPtsUs = -1;
                _undecoded.Clear();
                Append($"{DateTime.Now:MM-dd HH:mm:ss.fff}  --- capture session {gen}");
            }
        }

        // a packet fully read off the phone's socket, before it is forwarded;
        // waitTicks = how long the reader sat waiting for its bytes
        public void OnPacket(long ptsUs, bool isConfig, bool isKeyFrame, int size, long arrivalTicks, long waitTicks)
        {
            lock (_lock)
            {
                Roll(arrivalTicks);
                _packets++;
                _bytes += size;
                _waitMs += Ms(Math.Min(waitTicks, arrivalTicks - _bucketTicks));   // only this second's share of an idle wait
                _lastPacketTicks = arrivalTicks;
                if (isConfig)
                {
                    // a (re)configured encoder: its PTS origin may differ
                    _baseUs = long.MaxValue;
                    _lastPtsUs = -1;
                    return;
                }
                if (ptsUs < _lastPtsUs)
                {
                    _baseUs = long.MaxValue;
                }
                _lastPtsUs = ptsUs;
                _media++;
                if (isKeyFrame)
                {
                    _keyFrames++;
                }
                long offset = Us(arrivalTicks) - ptsUs;
                if (offset < _baseUs)
                {
                    _baseUs = offset;
                }
                double lag = (offset - _baseUs) / 1000.0;
                _lagMin = Math.Min(_lagMin, lag);
                _lagSum += lag;
                _lagMax = Math.Max(_lagMax, lag);
                _undecoded.Enqueue((ptsUs, arrivalTicks));
            }
        }

        // the same packet after the ring write and the ffmpeg stdin write + flush
        public void OnForwarded(long ringTicks, long ffmpegTicks)
        {
            lock (_lock)
            {
                _ringMs += Ms(ringTicks);
                double ff = Ms(ffmpegTicks);
                _ffmpegMs += ff;
                _ffmpegMaxMs = Math.Max(_ffmpegMaxMs, ff);
            }
        }

        // a decoded frame out of ffmpeg; waitTicks = how long the decode reader
        // sat waiting for it. Null when no packet is waiting to pair with it.
        public FrameStamp? OnFrame(long decodedTicks, long waitTicks)
        {
            lock (_lock)
            {
                Roll(decodedTicks);
                _frames++;
                _decodeWaitMs += Ms(Math.Min(waitTicks, decodedTicks - _bucketTicks));
                if (!_undecoded.TryDequeue(out var p))
                {
                    return null;
                }
                if (_baseUs != long.MaxValue)
                {
                    _frameLagMax = Math.Max(_frameLagMax, LagMs(decodedTicks, p.PtsUs));
                }
                return new FrameStamp(p.PtsUs, p.ArrivalTicks, decodedTicks);
            }
        }

        // the frame the detector now reads
        public void OnView(FrameStamp? stamp, long nowTicks)
        {
            lock (_lock)
            {
                Roll(nowTicks);
                _views++;
                if (stamp is FrameStamp s && _baseUs != long.MaxValue)
                {
                    _viewLagMax = Math.Max(_viewLagMax, LagMs(nowTicks, s.PtsUs));
                }
            }
        }

        // render-loop heartbeat: closes a bucket the stream left open, and a lag
        // episode the stream went quiet on
        public void Tick(long nowTicks)
        {
            lock (_lock)
            {
                Roll(nowTicks);
                if (_episode && nowTicks - _lastPacketTicks > 2 * Stopwatch.Frequency)
                {
                    _episode = false;
                    _recorder($"(engine) stream lag episode ended with the stream going idle (peak {_episodePeak / 1000:F1}s over {(DateTime.Now - _episodeStart).TotalSeconds:F0}s)");
                }
            }
        }

        private void Roll(long nowTicks)
        {
            if (_bucketTicks != 0 && nowTicks - _bucketTicks < Stopwatch.Frequency)
            {
                return;
            }
            if (_bucketTicks != 0)
            {
                Flush();
            }
            _bucketTicks = nowTicks;
            _bucketWall = DateTime.Now;
            _packets = _media = _keyFrames = _frames = _views = 0;
            _bytes = 0;
            _lagMin = double.MaxValue;
            _lagSum = _lagMax = 0;
            _waitMs = _ringMs = _ffmpegMs = _ffmpegMaxMs = 0;
            _frameLagMax = _decodeWaitMs = _viewLagMax = 0;
        }

        private void Flush()
        {
            if (_packets == 0 && _frames == 0)
            {
                return;
            }
            string lag = _media > 0 ? $"{_lagMin:F0}/{_lagSum / _media:F0}/{_lagMax:F0}" : "-";
            Append($"{_bucketWall:MM-dd HH:mm:ss.fff}  pk {_packets} ({_keyFrames} key) {_bytes / 1024}KB  lag {lag}"
                 + $"  wait {_waitMs:F0} ring {_ringMs:F0} ffmpeg {_ffmpegMs:F0} (max {_ffmpegMaxMs:F0})"
                 + $"  | frames {_frames} lag {_frameLagMax:F0} wait {_decodeWaitMs:F0}"
                 + $"  | view {_views} lag {_viewLagMax:F0}  | queued {_undecoded.Count}");

            if (_media == 0)
            {
                return;
            }
            if (!_episode && _lagMax > EpisodeOpenMs)
            {
                _episode = true;
                _episodePeak = _lagMax;
                _episodeStart = _bucketWall;
                _recorder($"(engine) stream lag {_lagMax / 1000:F1}s -- this second the packet reader waited on the phone {_waitMs:F0}ms, wrote the ring {_ringMs:F0}ms, was blocked writing into ffmpeg {_ffmpegMs:F0}ms");
            }
            else if (_episode)
            {
                _episodePeak = Math.Max(_episodePeak, _lagMax);
                if (_lagMax < EpisodeCloseMs)
                {
                    _episode = false;
                    _recorder($"(engine) stream lag cleared (peak {_episodePeak / 1000:F1}s over {(_bucketWall - _episodeStart).TotalSeconds:F0}s)");
                }
            }
        }

        private void Append(string line)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > LogMaxBytes)
                {
                    // own try, as in the flight recorder: a blocked rotation must
                    // degrade to an oversized log, never to a silent one
                    try
                    {
                        File.Move(_path, _oldPath, true);
                        _rotateFailed = false;
                    }
                    catch (Exception ex)
                    {
                        if (!_rotateFailed)
                        {
                            _rotateFailed = true;
                            Console.WriteLine($"[Garden] capture timing log rotation blocked ({ex.Message}) -- still appending");
                        }
                    }
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // timing is forensics: it must never hurt the stream
            }
        }
    }
}
