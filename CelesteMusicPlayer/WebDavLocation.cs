using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 一个 WebDAV 网络音乐位置（PikPak / 群晖 / 坚果云 等）。
    ///
    /// 字段与 AppSettingsStore 里的扁平字段一一对应：设置存储沿用项目既有风格（扁平属性 + DPAPI 加密密码），
    /// 这个类只负责"把一堆散字段组合成一个可用对象"，不承担持久化职责。
    ///
    /// ⚠️ 新增字段时切记同步 AppSettingsStore 的 Clone()，否则会出现
    /// "保存成功、重启后归零、且不报任何错" —— 这个项目已经踩过多次（置顶开关、标签排序面板都栽过）。
    /// </summary>
    internal sealed class WebDavLocation
    {
        public const string ProxySystem = "System";
        public const string ProxyNone = "None";
        public const string ProxyManual = "Manual";

        public string Url { get; set; } = string.Empty;

        /// <summary>用户在设置里给这个库起的名字（如 PikPak）；空=用主机名。</summary>
        public string DisplayName { get; set; } = string.Empty;

        public string User { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        /// <summary>System=跟随系统代理（默认）；None=不走代理（局域网 NAS）；Manual=手动指定。</summary>
        public string ProxyMode { get; set; } = ProxySystem;

        public string ProxyHost { get; set; } = string.Empty;

        /// <summary>默认 7890：常见代理工具（Clash 系）的默认 HTTP 端口，少填一次。</summary>
        public int ProxyPort { get; set; } = 7890;

        /// <summary>是否已经填了地址（用来判断"配没配过"）。</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

        /// <summary>代理设置的显示文字。</summary>
        public string ProxyDisplayName => ProxyMode switch
        {
            ProxyNone => "不使用代理",
            ProxyManual => string.IsNullOrWhiteSpace(ProxyHost) ? "手动（还没填地址）" : $"手动 {ProxyHost}:{ProxyPort}",
            _ => "跟随系统代理"
        };

        /// <summary>界面显示用的简短名称：优先取主机名，取不到就退回原地址。</summary>
        public string ShortName
        {
            get
            {
                if (Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host))
                {
                    return uri.Host;
                }

                return string.IsNullOrWhiteSpace(Url) ? "(未配置)" : Url;
            }
        }

        /// <summary>主界面左侧条目上显示的名字：设置里填的显示名优先，没填就用主机名。</summary>
        public string LibraryName =>
            !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName.Trim() : ShortName;

        /// <summary>按当前配置创建客户端。调用方负责 Dispose。</summary>
        public WebDavClient CreateClient()
            => new(Url, User, Password, ProxyMode, ProxyHost, ProxyPort);

        /// <summary>从设置快照构造（设置里是扁平字段）。</summary>
        public static WebDavLocation FromSettings(AppSettingsState s) => new()
        {
            Url = s.WebDavUrl,
            DisplayName = s.WebDavDisplayName,
            User = s.WebDavUser,
            Password = s.WebDavPassword,
            ProxyMode = s.WebDavProxyMode,
            ProxyHost = s.WebDavProxyHost,
            ProxyPort = s.WebDavProxyPort
        };
    }
}
