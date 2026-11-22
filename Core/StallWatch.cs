using System;

namespace Mp4Merger.Core
{
    /// Watches whether an ffmpeg run is still getting anywhere.
    ///
    /// ffmpeg can stop making progress while still holding its process open: on
    /// this machine a long NVENC conversion froze with one core spinning, the GPU
    /// encoder idle and the output file no longer growing (2026-11-08, cause still
    /// unknown). Nothing timed it out, so the app waited for the user to press
    /// cancel and the log said nothing. Whatever the cause, this turns that into
    /// an error that names where it stopped.
    ///
    /// Progress is counted in checks, not in clock time. The clock is the wrong
    /// measure here: it keeps running while the machine sleeps, so a laptop that
    /// wakes up after an hour would look like an hour of silence and a perfectly
    /// healthy run would be killed and its output deleted. The watchdog's timer
    /// does not fire while the machine is asleep, so counting its checks measures
    /// time the run was actually given.
    ///
    /// Three threads touch this: the one reading ffmpeg's output calls Advance,
    /// the watchdog timer calls CheckSaysStalled, and the one waiting on the
    /// process reads Position to write the failure message. Every member takes
    /// the same lock.
    public sealed class StallWatch
    {
        private readonly object _gate = new();
        private readonly int _quietChecksAllowed;
        private double _position = -1;
        private double _positionAtLastCheck = -1;
        private int _quietChecks;

        /// `quietChecksAllowed` is how many checks in a row may find nothing new
        /// before the run counts as stalled. It is generous on purpose: a slow
        /// encode still reports its position about twice a second, so silence
        /// across that many checks is not slowness.
        public StallWatch(int quietChecksAllowed)
        {
            _quietChecksAllowed = Math.Max(quietChecksAllowed, 1);
        }

        /// The furthest position reported so far, in seconds, or null if ffmpeg
        /// has not reported one yet.
        public double? Position
        {
            get { lock (_gate) { return _position < 0 ? null : _position; } }
        }

        /// How many checks in a row have found nothing new.
        public int QuietChecks
        {
            get { lock (_gate) { return _quietChecks; } }
        }

        /// Takes a position ffmpeg reported. Only a position further on counts as
        /// progress: ffmpeg repeats the same line while it is stuck, and a seek
        /// backwards is not the run moving forward either.
        public void Advance(double? positionSeconds)
        {
            if (positionSeconds is not { } position) return;

            lock (_gate)
            {
                if (position > _position) _position = position;
            }
        }

        /// One check by the watchdog. True once nothing has moved for the allowed
        /// number of checks in a row.
        public bool CheckSaysStalled()
        {
            lock (_gate)
            {
                if (_position != _positionAtLastCheck)
                {
                    _positionAtLastCheck = _position;
                    _quietChecks = 0;
                    return false;
                }

                _quietChecks++;
                return _quietChecks >= _quietChecksAllowed;
            }
        }
    }
}
