using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页设置页的入口薄壳（W13）。
    ///
    /// 17 个命令的读写、校验、对话框、凭据、导入导出全部在 <see cref="WebSettingsService"/>；
    /// 只能由宿主完成的那四件事在 <c>MainWindow.SettingsHost.cs</c>。
    /// 这里刻意什么都不做 —— 再往这儿加一行，就等于把业务搬回来了。
    /// </summary>
    public sealed partial class MainWindow
    {
        internal Task<object> WebSettingsAsync(JsonElement msg, object host) => Settings.HandleAsync(msg, host);
    }
}
