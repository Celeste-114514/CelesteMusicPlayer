// 用例工程只链接模型与格式两个文件，不链接真实设置存储。
// NetworkIdentity.Reference 需要一个配置目录来拼 .ref 路径，这里给个假目录就够 ——
// 用例只做字符串往返，不会真的写文件。
namespace CelesteMusicPlayer;

internal static class AppSettingsStore
{
    internal static string GetConfigDirectory() => @"C:\fake-config";
}
