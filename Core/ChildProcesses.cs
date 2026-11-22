using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mp4Merger.Core
{
    /// ffmpeg runs as a separate process, so quitting the app does not stop it:
    /// an abandoned re-encode would keep writing gigabytes into the temp folder.
    /// Long-running tools are launched through here so they can be stopped on quit.
    public static class ChildProcesses
    {
        private static readonly object Gate = new();
        private static readonly HashSet<Process> Running = new();

        /// `process.Start()`, remembering the process until it exits.
        public static void Start(Process process)
        {
            lock (Gate) { Running.Add(process); }
            try
            {
                process.Start();
            }
            catch
            {
                lock (Gate) { Running.Remove(process); }
                throw;
            }
        }

        public static void Forget(Process process)
        {
            lock (Gate) { Running.Remove(process); }
        }

        public static void TerminateAll()
        {
            Process[] processes;
            lock (Gate) { processes = new Process[Running.Count]; Running.CopyTo(processes); }

            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { /* already gone */ }
            }
            lock (Gate) { Running.Clear(); }
        }
    }

    /// A tool that did not finish in the time allowed for it. Used for the short
    /// calls (probing, verifying, testing an encoder), where a plain wall-clock
    /// limit is simpler than watching for progress: they have none to report.
    public sealed class ToolTimedOutException : Exception
    {
        public string Tool { get; }
        public TimeSpan Limit { get; }

        public ToolTimedOutException(string tool, TimeSpan limit)
            : base($"{tool} did not finish within {limit.TotalSeconds:F0} seconds.")
        {
            Tool = tool;
            Limit = limit;
        }
    }

    public sealed class ProcessResult
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
    }

    public static class ProcessRunner
    {
        /// How long to wait for a killed process to actually go away.
        private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(30);


        /// Runs a tool to completion without blocking the caller's thread.
        /// Arguments are passed as a list, so paths never need quoting and a
        /// file name can never be read as an option.
        public static async Task<ProcessResult> CaptureAsync(
            string executable,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken,
            Action<string>? onStdErrLine = null,
            Action<string>? onStdOutLine = null,
            int stdErrLimit = 256_000,
            string? workingDirectory = null,
            TimeSpan? timeout = null)
        {
            // A tool with nothing to report cannot be watched for progress, so
            // the short calls get a plain limit instead. Without one, a frozen
            // ffprobe hangs the app exactly as a frozen ffmpeg used to.
            using var limit = timeout is { } span
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            limit?.CancelAfter(timeout!.Value);
            var token = limit?.Token ?? cancellationToken;

            using var process = new Process();
            process.StartInfo.FileName = executable;
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!string.IsNullOrEmpty(workingDirectory)) process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = true;
            process.EnableRaisingEvents = true;

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var outDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // These handlers run on a reader thread, where nothing is waiting to
            // catch anything they throw: an exception here takes the whole app
            // down. A progress callback marshalling to the UI thread does throw
            // once the window has gone away, which is reachable by quitting
            // mid-run, so the line handlers are never allowed to escape.
            void Deliver(Action<string>? handler, string line)
            {
                try { handler?.Invoke(line); } catch { }
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) { outDone.TrySetResult(true); return; }
                stdout.AppendLine(e.Data);
                Deliver(onStdOutLine, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) { errDone.TrySetResult(true); return; }
                // Damage repeats every frame; keep the log bounded.
                if (stderr.Length < stdErrLimit) stderr.AppendLine(e.Data);
                Deliver(onStdErrLine, e.Data);
            };

            ChildProcesses.Start(process);
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // ffmpeg reads stdin for interactive keys; closing it stops it
                // consuming the parent's console input.
                try { process.StandardInput.Close(); } catch { }

                var exit = process.WaitForExitAsync(CancellationToken.None);
                using (token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                }))
                {
                    if (token.CanBeCanceled)
                    {
                        var cancelled = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        using (token.Register(() => cancelled.TrySetResult(true)))
                        {
                            await Task.WhenAny(exit, cancelled.Task).ConfigureAwait(false);
                        }

                        // Killed, but a process wedged inside a driver call may not
                        // die. Waiting on it without a limit would hang the app just
                        // as the frozen run it was killed for did.
                        if (!exit.IsCompleted)
                        {
                            await Task.WhenAny(exit, Task.Delay(KillGrace)).ConfigureAwait(false);
                            if (!exit.IsCompleted)
                            {
                                Debug.WriteLine(
                                    $"{Path.GetFileName(executable)} did not die within " +
                                    $"{KillGrace.TotalSeconds:F0}s of being killed; giving up on it.");
                            }
                        }
                    }
                    else
                    {
                        await exit.ConfigureAwait(false);
                    }
                }

                // Let the redirected readers drain before reading the buffers.
                await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(2000)).ConfigureAwait(false);
            }
            finally
            {
                ChildProcesses.Forget(process);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (limit?.IsCancellationRequested == true)
            {
                throw new ToolTimedOutException(Path.GetFileName(executable), timeout!.Value);
            }

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StdOut = stdout.ToString(),
                StdErr = stderr.ToString()
            };
        }
    }
}
