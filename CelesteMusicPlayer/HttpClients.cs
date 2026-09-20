using System;
using System.Net.Http;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 进程内共享的 HttpClient（P1-6 清理）。
    /// 短命 HttpClient 每次新建会把 TCP 连接拖入 TIME_WAIT，高频调用时耗尽本地端口/套接字；
    /// Timeout 在实例创建后不可变，因此按超时档位各持有一个实例：
    /// Default 用于 API/元数据等短请求，LongRunning 用于安装包等大文件下载。
    /// 需要更短超时的调用方请用 CancellationTokenSource 按次控制，不要另建 HttpClient。
    /// </summary>
    internal static class HttpClients
    {
        public static readonly HttpClient Default = Create(TimeSpan.FromSeconds(100));

        public static readonly HttpClient LongRunning = Create(TimeSpan.FromMinutes(30));

        private static HttpClient Create(TimeSpan timeout)
        {
            HttpClient client = new()
            {
                Timeout = timeout
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
            return client;
        }
    }
}
