using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    public static class FormatConvertService
    {
        private static readonly string[] SupportedFormats = { "wav", "mp3", "flac", "ogg" };

        public static bool IsSupportedFormat(string format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return false;
            }

            return SupportedFormats.Contains(format.TrimStart('.').ToLowerInvariant());
        }

        public static string? TryFindFfmpeg()
        {
            string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

            string besideExe = Path.Combine(AppContext.BaseDirectory, exeName);
            if (File.Exists(besideExe))
            {
                return besideExe;
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv))
            {
                return null;
            }

            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        public static async Task<(bool Success, string Message)> ConvertAsync(
            string inputPath,
            string outputPath,
            string format,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                return (false, "Input file not found.");
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return (false, "Output path is required.");
            }

            string ext = format.TrimStart('.').ToLowerInvariant();
            if (!IsSupportedFormat(ext))
            {
                return (false, $"Unsupported format: {format}");
            }

            if (!outputPath.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            {
                outputPath = Path.ChangeExtension(outputPath, "." + ext);
            }

            string? ffmpeg = TryFindFfmpeg();
            if (ffmpeg == null)
            {
                return (false, "ffmpeg not found in PATH or beside the application.");
            }

            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -hide_banner -loglevel error -i \"{inputPath}\" \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using Process process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start ffmpeg.");

                var stderr = new StringBuilder();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        stderr.AppendLine(e.Data);
                    }
                };
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    return (true, outputPath);
                }

                string message = stderr.Length > 0 ? stderr.ToString().Trim() : $"ffmpeg exited with code {process.ExitCode}.";
                return (false, message);
            }
            catch (OperationCanceledException)
            {
                return (false, "Conversion cancelled.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
