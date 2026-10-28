using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 采样率转换（SRC）的质量档位与抖动模式常量 —— 设置里存的就是这些字符串。
    ///
    /// 为什么单独一个文件：它们原先定义在 <c>ResamplingSourceProvider</c> 里，而那个类依赖
    /// NAudio。W6 抽出设置读写边界时，光为了读两个字符串就要把整个音频栈拖进来，不合理。
    /// 这里只放常量，不引用任何界面或音频库；ResamplingSourceProvider 保留同名常量指向本文件，
    /// 所以老代码 `ResamplingSourceProvider.QualityBalanced` 照旧能编译、值也完全相同。
    /// </summary>
    internal static class SrcOptions
    {
        public const string QualityLowLatency = "lowlatency";
        public const string QualityBalanced = "balanced";
        public const string QualityTransparent = "transparent";

        public const string DitherOff = "off";
        public const string DitherTpdf = "tpdf";
        public const string DitherHighpass = "highpass";
        public const string DitherNs5 = "ns5";
    }
}
