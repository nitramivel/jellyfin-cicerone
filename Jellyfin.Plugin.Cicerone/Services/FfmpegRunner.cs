using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>What a finished ffmpeg process produced.</summary>
    /// <param name="ExitCode">The exit code.</param>
    /// <param name="Output">Everything it wrote to stdout.</param>
    /// <param name="StandardError">Everything it wrote to stderr.</param>
    public sealed record FfmpegResult(int ExitCode, byte[] Output, string StandardError)
    {
        /// <summary>Gets whether the run succeeded and produced something.</summary>
        public bool Ok => ExitCode == 0 && Output.Length > 0;
    }

    /// <summary>
    /// Runs ffmpeg at low priority and collects what it writes.
    /// </summary>
    /// <remarks>
    /// <b>The one class here that cannot be tested on the machine this was written
    /// on</b> — there is no ffmpeg on it and no Jellyfin server. Everything decided
    /// about the arguments lives in <c>Core/Audio</c>, which is pure and covered;
    /// what is left is process handling, written defensively for that reason.
    /// </remarks>
    public sealed class FfmpegRunner
    {
        private readonly ILogger<FfmpegRunner> _logger;

        /// <summary>Initialises a new instance of the <see cref="FfmpegRunner"/> class.</summary>
        /// <param name="logger">The logger.</param>
        public FfmpegRunner(ILogger<FfmpegRunner> logger)
        {
            _logger = logger;
        }

        /// <summary>Runs ffmpeg and returns its stdout.</summary>
        /// <param name="executable">Full path to ffmpeg.</param>
        /// <param name="arguments">The command line.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Exit code, output bytes and stderr.</returns>
        public async Task<FfmpegResult> RunAsync(
            string executable,
            string arguments,
            CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            _logger.LogDebug("Cicerone: {Executable} {Arguments}", executable, arguments);

            process.Start();
            TryLowerPriority(process);

            // Both pipes are drained on their own tasks for the whole life of the
            // process. A clip is a megabyte or so and would fill the stdout pipe
            // buffer several times over; left unread until exit, ffmpeg blocks
            // writing to it and waits forever while we wait for it to finish.
            using var buffer = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await stdoutTask.ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            return new FfmpegResult(process.ExitCode, buffer.ToArray(), stderr);
        }

        /// <summary>
        /// Drops the child to below-normal scheduling priority.
        /// </summary>
        /// <remarks>
        /// How Cicerone stays out of a live transcode's way. Extracting thirty
        /// seconds of audio is a small job, but a run does it several hundred times
        /// and somebody is trying to watch something. Handing the problem to the OS
        /// scheduler is both simpler and better than asking Jellyfin what is playing,
        /// which 10.11 offers no way to do from a scheduled task.
        /// </remarks>
        private void TryLowerPriority(Process process)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                // Containers with a restricted seccomp profile refuse setpriority, and
                // a process that finished before the call refuses it too. Neither is
                // worth failing a run over: the work still happens, at normal
                // priority, which is what every other plugin does anyway.
                _logger.LogDebug(ex, "Cicerone: could not lower ffmpeg priority");
            }
        }

        private void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(ex, "Cicerone: could not kill ffmpeg after cancellation");
            }
        }
    }
}
