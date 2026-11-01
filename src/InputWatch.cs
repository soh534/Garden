namespace Garden
{
    // Has the stream answered our inputs? The phone sends frames only when the
    // screen changes, so silence alone proves nothing -- but silence after input
    // is how a stream that died without an EOF shows itself. The watch keeps
    // every input no frame has followed yet, and the stall check reads the
    // OLDEST. Keyed on the latest input instead, a bot that keeps tapping a
    // frozen screen kept postponing the verdict: the stall check could fire only
    // after the bot gave up and went quiet, so every recovery attempt before
    // that ran blind.
    // A frame is decoded tens of ms after it arrived, and inputs sent in between
    // are NOT answered by it -- so the watch is a queue, not one "oldest" slot
    // that the frame would clear along with them.
    // All times are Stopwatch ticks.
    internal static class InputWatch
    {
        private static readonly object _lock = new();
        private static readonly Queue<long> _unanswered = new();   // send times, oldest first

        // an input went to the phone
        public static void Sent(long ticks)
        {
            lock (_lock)
            {
                _unanswered.Enqueue(ticks);
            }
        }

        // a frame that reached us at arrivalTicks answers every input sent before it
        public static void Answered(long arrivalTicks)
        {
            lock (_lock)
            {
                while (_unanswered.Count > 0 && _unanswered.Peek() < arrivalTicks)
                {
                    _unanswered.Dequeue();
                }
            }
        }

        // a fresh capture session: inputs sent to the old one are moot
        public static void Reset()
        {
            lock (_lock)
            {
                _unanswered.Clear();
            }
        }

        // how long the oldest unanswered input has waited (0 = none waiting)
        public static long WaitingTicks(long nowTicks)
        {
            lock (_lock)
            {
                return _unanswered.Count == 0 ? 0 : nowTicks - _unanswered.Peek();
            }
        }
    }
}
