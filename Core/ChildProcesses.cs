using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    public sealed class ProcessResult
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
    }

    public static class ProcessRunner
    {
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
            string? workingDirectory = null)
        {
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

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) { outDone.TrySetResult(true); return; }
                stdout.AppendLine(e.Data);
                onStdOutLine?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) { errDone.TrySetResult(true); return; }
                // Damage repeats every frame; keep the log bounded.
                if (stderr.Length < stdErrLimit) stderr.AppendLine(e.Data);
                onStdErrLine?.Invoke(e.Data);
            };

            ChildProcesses.Start(process);
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // ffmpeg reads stdin for interactive keys; closing it stops it
                // consuming the parent's console input.
                try { process.StandardInput.Close(); } catch { }

                using (cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                }))
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                // Let the redirected readers drain before reading the buffers.
                await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(2000)).ConfigureAwait(false);
            }
            finally
            {
                ChildProcesses.Forget(process);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StdOut = stdout.ToString(),
                StdErr = stderr.ToString()
            };
        }
    }
}
