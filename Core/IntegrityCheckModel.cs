using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Mp4Merger.Core
{
    /// Drives the check screen: turns dropped folders into a work list, runs the
    /// checker over it, and keeps the aggregate progress the UI shows.
    public sealed class IntegrityCheckModel : INotifyPropertyChanged
    {
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;
        /// Captured where the model is built, so notifications and collection
        /// changes always reach the UI thread.
        private readonly SynchronizationContext _ui;

        public IntegrityCheckModel(string ffmpegPath, string ffprobePath)
        {
            _ffmpegPath = ffmpegPath;
            _ffprobePath = ffprobePath;
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        public string Language { get; set; } = "ja";

        private List<string> _droppedPaths = new();
        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _checkCts;

        // Footage checked per target, weighted by pixel count so a 4K file
        // contributes proportionally more to the bar than a 1080p one.
        private readonly Dictionary<Guid, double> _processedCost = new();
        private readonly object _costGate = new();

        public ObservableCollection<CheckResult> Results { get; } = new();
        public ObservableCollection<string> RunningFiles { get; } = new();

        private List<CheckTarget> _targets = new();
        public IReadOnlyList<CheckTarget> Targets => _targets;

        private string _sourceLabel = "";
        public string SourceLabel { get => _sourceLabel; private set => Set(ref _sourceLabel, value); }

        private bool _isScanning;
        public bool IsScanning { get => _isScanning; private set { Set(ref _isScanning, value); Notify(nameof(CanStart)); } }

        private bool _isChecking;
        public bool IsChecking
        {
            get => _isChecking;
            private set { Set(ref _isChecking, value); Notify(nameof(CanStart)); Notify(nameof(IsIdle)); }
        }

        public bool IsIdle => !IsChecking;

        private double _progress;
        public double Progress { get => _progress; private set => Set(ref _progress, value); }

        /// Seconds left, or null while there is not enough data to say.
        private TimeSpan? _remaining;
        public TimeSpan? Remaining { get => _remaining; private set { Set(ref _remaining, value); Notify(nameof(RemainingText)); } }

        private string? _errorMessage;
        public string? ErrorMessage { get => _errorMessage; private set { Set(ref _errorMessage, value); Notify(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        /// Whether the current selection contains at least one folder. Only then
        /// does the subfolder option mean anything.
        private bool _hasFolderInput;
        public bool HasFolderInput { get => _hasFolderInput; private set => Set(ref _hasFolderInput, value); }

        private CheckLevel _level = CheckLevel.Full;
        public CheckLevel Level
        {
            get => _level;
            set
            {
                if (_level == value) return;
                _level = value;
                Notify();
                Notify(nameof(LevelDetail));
                Notify(nameof(EstimatedText));
            }
        }

        private bool _includeSubfolders = true;
        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set
            {
                if (_includeSubfolders == value) return;
                _includeSubfolders = value;
                Notify();
                Rescan();
            }
        }

        public bool HasTargets => _targets.Count > 0;
        public bool CanStart => HasTargets && !IsChecking && !IsScanning;

        /// Something was selected, the scan finished, and it held no video files.
        public bool FoundNothing => SourceLabel.Length > 0 && _targets.Count == 0 && !IsScanning;

        public double TotalDuration => _targets.Sum(t => t.Duration);

        public int OkCount => Results.Count(r => r.Status == CheckStatus.Ok);
        public int WarningCount => Results.Count(r => r.Status == CheckStatus.Warning);
        public int BrokenCount => Results.Count(r => r.Status == CheckStatus.Broken);

        /// Damaged and suspect files together — what "show in Explorer" acts on.
        public List<string> ProblemPaths => Results.Where(r => r.IsProblem).Select(r => r.Path).ToList();

        public string LevelDetail => Localizer.Get(Level.DetailKey(), Language);

        public string EstimatedText => HasTargets
            ? Localizer.GetDynamic("EstimatedTime", Language,
                FormatDuration(VideoIntegrityChecker.Estimate(_targets, Level)))
            : "";

        public string SummaryText => HasTargets
            ? Localizer.GetDynamic("FilesAndTotal", Language,
                _targets.Count.ToString(), FormatDuration(TimeSpan.FromSeconds(TotalDuration)))
            : "";

        public string RemainingText => Remaining is { } left
            ? Localizer.GetDynamic("RemainingTime", Language, FormatDuration(left))
            : (IsChecking ? Localizer.Get("CalculatingRemaining", Language) : "");

        // MARK: - Input

        public void Load(IEnumerable<string> paths)
        {
            _droppedPaths = paths.ToList();
            HasFolderInput = _droppedPaths.Any(System.IO.Directory.Exists);
            SourceLabel = Describe(_droppedPaths);
            Rescan();
        }

        public void Rescan()
        {
            if (_droppedPaths.Count == 0) return;

            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            Results.Clear();
            lock (_costGate) _processedCost.Clear();
            Progress = 0;
            Remaining = null;
            ErrorMessage = null;
            NotifyCounts();

            var paths = _droppedPaths.ToList();
            bool recursive = IncludeSubfolders;
            IsScanning = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    var checker = new VideoIntegrityChecker(_ffmpegPath, _ffprobePath);
                    var files = VideoIntegrityChecker.CollectVideos(paths, recursive);
                    var inspected = await checker.InspectAsync(files, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;

                    Post(() =>
                    {
                        _targets = inspected;
                        IsScanning = false;
                        NotifyTargets();
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Post(() =>
                    {
                        IsScanning = false;
                        ErrorMessage = ex.Message;
                        NotifyTargets();
                    });
                }
            });
        }

        public void Clear()
        {
            _scanCts?.Cancel();
            _checkCts?.Cancel();
            _droppedPaths = new List<string>();
            HasFolderInput = false;
            _targets = new List<CheckTarget>();
            Results.Clear();
            RunningFiles.Clear();
            lock (_costGate) _processedCost.Clear();
            SourceLabel = "";
            Progress = 0;
            Remaining = null;
            ErrorMessage = null;
            IsScanning = false;
            IsChecking = false;
            NotifyTargets();
            NotifyCounts();
        }

        // MARK: - Running

        public async Task StartAsync()
        {
            if (_targets.Count == 0 || IsChecking) return;

            Results.Clear();
            RunningFiles.Clear();
            lock (_costGate) _processedCost.Clear();
            Progress = 0;
            Remaining = null;
            ErrorMessage = null;
            NotifyCounts();
            IsChecking = true;

            _checkCts = new CancellationTokenSource();
            var token = _checkCts.Token;

            var queue = _targets.ToList();
            var level = Level;
            var startedAt = DateTime.UtcNow;
            double totalCost = queue.Sum(t => t.EstimatedCost);
            var checker = new VideoIntegrityChecker(_ffmpegPath, _ffprobePath);

            int next = -1;
            async Task Worker()
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= queue.Count || token.IsCancellationRequested) return;
                    var target = queue[index];

                    Post(() => RunningFiles.Add(System.IO.Path.GetFileName(target.Path)));
                    try
                    {
                        var result = await checker.CheckAsync(target, level,
                            seconds => UpdateProgress(target, seconds, totalCost, startedAt),
                            token).ConfigureAwait(false);

                        result.DisplayDetail =
                            $"{FormatDuration(TimeSpan.FromSeconds(result.Duration))}  ·  " +
                            $"{Localizer.Get("CheckedIn", Language)} {FormatDuration(result.Elapsed)}";

                        Post(() =>
                        {
                            Results.Add(result);
                            NotifyCounts();
                        });
                        CompleteFile(target, totalCost, startedAt);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Post(() => ErrorMessage = ex.Message);
                    }
                    finally
                    {
                        Post(() => RunningFiles.Remove(System.IO.Path.GetFileName(target.Path)));
                    }
                }
            }

            int workers = Math.Max(Math.Min(VideoIntegrityChecker.Concurrency, queue.Count), 1);
            try
            {
                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker())).ConfigureAwait(true);
            }
            finally
            {
                bool cancelled = token.IsCancellationRequested;
                // Posted, so it runs after every result a worker already queued:
                // the UI thread drains these in order.
                Post(() =>
                {
                    var sorted = Results.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
                    Results.Clear();
                    foreach (var result in sorted) Results.Add(result);

                    RunningFiles.Clear();
                    if (!cancelled) Progress = 1.0;
                    Remaining = null;
                    IsChecking = false;
                    NotifyCounts();
                });
                _checkCts?.Dispose();
                _checkCts = null;
            }
        }

        public void Cancel() => _checkCts?.Cancel();

        // MARK: - Progress bookkeeping

        private void UpdateProgress(CheckTarget target, double seconds, double totalCost, DateTime startedAt)
        {
            double capped = target.Duration > 0 ? Math.Min(seconds, target.Duration) : seconds;
            lock (_costGate) _processedCost[target.Id] = capped * target.PixelScale;
            RecomputeProgress(totalCost, startedAt);
        }

        private void CompleteFile(CheckTarget target, double totalCost, DateTime startedAt)
        {
            lock (_costGate) _processedCost[target.Id] = target.EstimatedCost;
            RecomputeProgress(totalCost, startedAt);
        }

        private void RecomputeProgress(double totalCost, DateTime startedAt)
        {
            if (totalCost <= 0) return;

            double done;
            lock (_costGate) done = _processedCost.Values.Sum();

            double fraction = Math.Min(done / totalCost, 1.0);

            // Rate-based estimate, after a few seconds of real data.
            TimeSpan? remaining = null;
            double elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
            if (elapsed > 5 && done > 0)
            {
                double rate = done / elapsed;
                if (rate > 0.0001)
                {
                    remaining = TimeSpan.FromSeconds(Math.Max((totalCost - done) / rate, 0));
                }
            }

            Post(() =>
            {
                Progress = fraction;
                Remaining = remaining;
            });
        }

        // MARK: - Helpers

        private string Describe(List<string> paths)
        {
            if (paths.Count == 1) return System.IO.Path.GetFileName(paths[0].TrimEnd('\\', '/'));
            return Localizer.GetDynamic("ItemsSelected", Language, paths.Count.ToString());
        }

        public static string FormatDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero || span.TotalSeconds > TimeSpan.FromDays(30).TotalSeconds) return "--:--";
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        private void Post(Action action)
        {
            if (SynchronizationContext.Current == _ui) action();
            else _ui.Post(_ => action(), null);
        }

        private void NotifyTargets()
        {
            Notify(nameof(HasTargets));
            Notify(nameof(CanStart));
            Notify(nameof(FoundNothing));
            Notify(nameof(TotalDuration));
            Notify(nameof(SummaryText));
            Notify(nameof(EstimatedText));
        }

        private void NotifyCounts()
        {
            Notify(nameof(OkCount));
            Notify(nameof(WarningCount));
            Notify(nameof(BrokenCount));
            Notify(nameof(ProblemPaths));
            Notify(nameof(HasResults));
            Notify(nameof(HasProblems));
        }

        public bool HasResults => Results.Count > 0;
        public bool HasProblems => Results.Any(r => r.IsProblem);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? name = null) =>
            Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Notify(name);
        }
    }
}
