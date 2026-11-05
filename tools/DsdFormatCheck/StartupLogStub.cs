namespace CelesteMusicPlayer
{
    // 自检工程不带宿主日志系统，给 DsdNativeReader 一个空实现即可。
    internal static class StartupLog
    {
        public static void Write(string message) { }
    }
}
