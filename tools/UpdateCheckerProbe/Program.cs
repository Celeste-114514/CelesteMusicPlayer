using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>探针里替代真实的启动日志：只打到控制台，不碰磁盘。</summary>
    internal static class StartupLog
    {
        public static void Write(string message) => Console.WriteLine("[log] " + message);

        public static void WriteException(string scope, Exception error) => Console.WriteLine($"[log] {scope}: {error.GetType().Name} {error.Message}");
    }

    /// <summary>
    /// 与主工程同样的 HttpClient 配置（含 User-Agent，GitHub 会挑没有 UA 的请求）。
    /// 探针额外支持 PROBE_PROXY：指到一个不通的地址就能复现"断网"，
    /// 用来验证失败不会被说成「已是最新」。
    /// </summary>
    internal static class HttpClients
    {
        public static readonly HttpClient Default = Create();

        private static HttpClient Create()
        {
            var handler = new HttpClientHandler();
            string? proxy = Environment.GetEnvironmentVariable("PROBE_PROXY");
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                handler.Proxy = new System.Net.WebProxy(proxy);
                handler.UseProxy = true;
            }

            HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(100) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
            return client;
        }
    }
}

namespace UpdateCheckerProbe
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            Console.WriteLine("当前版本（模拟）：" + CelesteMusicPlayer.UpdateChecker.CurrentVersionText());
            CelesteMusicPlayer.UpdateChecker.UpdateCheckResult result = await CelesteMusicPlayer.UpdateChecker.CheckAsync();

            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"latestTag={result.LatestTag ?? "(空)"}");
            Console.WriteLine($"checkedAt={result.CheckedAt:HH:mm:ss}");
            if (result.ErrorCategory != null)
            {
                Console.WriteLine($"errorCategory={result.ErrorCategory}");
                Console.WriteLine($"error={result.ErrorMessage}");
            }

            if (result.Update == null)
            {
                Console.WriteLine($"update=(空) prereleaseIgnored={result.PrereleaseIgnored} lastKnown={result.LastKnownUpdate?.Tag ?? "(无)"}");
                return 0;
            }

            Console.WriteLine($"update.tag={result.Update.Tag}");
            Console.WriteLine($"update.packageKind={result.Update.PackageKind} ({result.Update.PackageKindText})");
            Console.WriteLine($"update.checksum={result.Update.ChecksumState} sha256={result.Update.ExpectedSha256 ?? "(无)"}");
            Console.WriteLine($"update.setup={result.Update.SetupUrl ?? "(无)"}");
            Console.WriteLine($"update.release={result.Update.ReleaseUrl ?? "(无)"}");
            return 0;
        }
    }
}
