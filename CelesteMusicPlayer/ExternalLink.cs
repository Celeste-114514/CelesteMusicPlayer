using System;
using System.Diagnostics;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页里点到的外部链接统一走这里：只放行 https 的官方站点，其余一律拒绝。
    /// 网页没有直接开浏览器的能力（WebView2 的新窗口请求被宿主接管），
    /// 所以「打开下载页」「GitHub ↗」这类入口必须由宿主代劳，顺带把协议拦一遍：
    /// 不拦的话，一个被篡改的介绍文本里的 file:// 或 javascript: 就能从应用里被「打开」。
    /// </summary>
    public static class ExternalLink
    {
        /// <summary>允许打开的站点（含其子域名）。目前只放 GitHub。</summary>
        private const string AllowedRoot = "github.com";

        /// <summary>校验并规范化链接；不合法时抛出带中文说明的异常。</summary>
        public static Uri Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException("链接为空。");
            }

            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? uri) || uri == null)
            {
                throw new InvalidOperationException("链接格式不正确。");
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("只允许打开 HTTPS 页面。");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException("链接里不能带账号信息。");
            }

            string host = uri.Host;
            bool allowed = host.Equals(AllowedRoot, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + AllowedRoot, StringComparison.OrdinalIgnoreCase);
            if (!allowed)
            {
                throw new InvalidOperationException($"只允许打开 {AllowedRoot} 上的页面。");
            }

            return uri;
        }

        /// <summary>校验并交给系统默认浏览器打开。失败抛异常，由调用方转成网页能看懂的提示。</summary>
        public static Uri Open(string? raw)
        {
            Uri uri = Normalize(raw);
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return uri;
        }
    }
}
