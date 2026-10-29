namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>W13：MainWindow 不再是 Window，队列改由原生消息泵提供（行为等价）。</summary>
        internal Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue =>
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    }
}
