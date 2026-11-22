using System;

namespace Mp4Merger.Core
{
    /// Watches how long an ffmpeg run has gone without getting any further.
    ///
    /// ffmpeg can stop making progress while still holding its process open: on
    /// this machine a long NVENC conversion froze with one core spinning, the GPU
    /// encoder idle and the output file no longer growing (2026-11-08, cause still
    /// unknown). Nothing timed it out, so the app waited for the user to press
    /// cancel and the log said nothing. Whatever the cause, this turns that into
    /// an error that names where it stopped.
    ///
    /// The clock is passed in rather than read here, so the behaviour can be
    /// checked without waiting for real minutes to pass.
    public sealed class StallWatch
    {
        private readonly TimeSpan _limit;
        private double _position;
        private DateTime _lastProgress;

        /// `limit` is how long a run may report nothing new before it counts as
        /// stalled. It is generous on purpose: a slow encode still reports its
        /// position about twice a second, so silence this long is not slowness.
        public StallWatch(TimeSpan limit, DateTime startedAt)
        {
            _limit = limit;
            _lastProgress = startedAt;
            _position = -1;
        }

        /// The furthest position reported so far, in seconds, or null if ffmpeg
        /// has not reported one yet.
        public double? Position => _position < 0 ? null : _position;

        /// Takes a position ffmpeg reported. Only a position further on counts as
        /// progress: ffmpeg repeats the same line while it is stuck, and a seek
        /// backwards is not the run moving forward either.
        public void Advance(double? positionSeconds, DateTime now)
        {
            if (positionSeconds is not { } position) return;
            if (position <= _position) return;

            _position = position;
            _lastProgress = now;
        }

        /// How long it has been since anything moved.
        public TimeSpan IdleFor(DateTime now)
        {
            var idle = now - _lastProgress;
            return idle < TimeSpan.Zero ? TimeSpan.Zero : idle;
        }

        /// True once nothing has moved for longer than the limit.
        public bool HasStalled(DateTime now) => IdleFor(now) > _limit;
    }
}
