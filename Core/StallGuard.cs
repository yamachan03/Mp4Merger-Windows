using System;
using System.Threading;

namespace Mp4Merger.Core
{
    /// Cancels a long-running tool that has stopped getting anywhere.
    ///
    /// Wraps a StallWatch, the timer that checks it and the token the tool is run
    /// with, so every long run can be guarded the same way:
    ///
    ///     using var guard = new StallGuard(token);
    ///     ... guard.Advance(position) from wherever progress is read ...
    ///     try { await ProcessRunner.CaptureAsync(exe, args, guard.Token, ...); }
    ///     catch (OperationCanceledException) when (guard.Stalled) { ... }
    ///
    /// `Stalled` says this guard fired, as opposed to the user pressing cancel.
    /// It is set before the cancel, and checked in preference to the user's
    /// token, so a cancel arriving in the same moment cannot throw away the
    /// position the run stopped at.
    public sealed class StallGuard : IDisposable
    {
        /// How long a run may report nothing new before it counts as hung.
        /// ffmpeg reports its position about twice a second even when it is
        /// crawling, so this is silence, not slowness. Measured for comparison:
        /// the longest gap between position reports in a 2GB copy merge, moov
        /// rewrite included, was 1.27 seconds (2026-11-22).
        public static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

        /// How often the watch is checked. The watch counts these checks rather
        /// than clock time, so sleeping the machine cannot look like a stall.
        public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);

        private readonly CancellationToken _userToken;
        private readonly CancellationTokenSource _source;
        private readonly StallWatch _watch;
        private readonly Timer _timer;
        private readonly TimeSpan _allowance;
        private volatile bool _stalled;

        public StallGuard(CancellationToken userToken)
            : this(userToken, (int)(Limit / CheckEvery), CheckEvery)
        {
        }

        /// For checking the behaviour without waiting minutes: the allowance and
        /// the interval are the two things a test needs to shorten.
        public StallGuard(CancellationToken userToken, int checksAllowed, TimeSpan checkEvery)
        {
            _userToken = userToken;
            _source = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            _watch = new StallWatch(checksAllowed);
            _allowance = checkEvery * Math.Max(checksAllowed, 1);
            _timer = new Timer(_ => Check(), null, checkEvery, checkEvery);
        }

        /// How long this guard actually allows. Messages must quote this rather
        /// than the shipped default: a run given a longer allowance would
        /// otherwise tell the user the wrong number.
        public TimeSpan Allowance => _allowance;

        /// Run the tool with this instead of the user's token.
        public CancellationToken Token => _source.Token;

        /// True when this guard cancelled the run because it stopped moving.
        public bool Stalled => _stalled;

        /// The furthest position the run reported, in seconds, or null if it
        /// never reported one.
        public double? Position => _watch.Position;

        /// Call this with whatever position the run just reported. A null, a
        /// repeat of the same position and a position going backwards all count
        /// as no progress.
        public void Advance(double? positionSeconds) => _watch.Advance(positionSeconds);

        private void Check()
        {
            // This runs on a timer thread, where nothing is waiting to catch
            // anything it throws: an exception here takes the whole app down.
            // Dispose does not wait for a callback already running, so this can
            // land after the source is gone; Cancel then throws, and Cancel also
            // wraps anything its own registrations throw. None of that is worth
            // dying for, so nothing is allowed out.
            try
            {
                // The user getting there first is their decision, not a stall.
                if (_userToken.IsCancellationRequested) return;
                if (!_watch.CheckSaysStalled()) return;

                _stalled = true;
                _source.Cancel();
            }
            catch
            {
                // Already disposed, or a registration threw: either way the run
                // is over and there is nobody left to tell.
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _source.Dispose();
        }
    }
}
