using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 输出模式解析与应用需要宿主的两件事。服务本身只做「字符串 ↔ 引擎枚举、记账、下发」。
    /// </summary>
    internal interface IOutputModeHost
    {
        /// <summary>把模式变化记进音频链路事件（终端/极客页那条流水）。</summary>
        void NoteChainMode(string? settingsMode);

        /// <summary>把模式下发给引擎（引擎没建时什么都不做）。</summary>
        void SetEngineOutputMode(HiFiOutputBackend.OutputMode mode);
    }

    /// <summary>
    /// 输出模式（共享 / WASAPI 独占 / ASIO）。
    ///
    /// W13：从 MainWindow.Settings.cs 的 ApplyEngineOutputMode 搬出来。
    /// 这里只负责三件事：设置里的字符串怎么映射到引擎枚举、切换要不要记账、把结果下发给引擎。
    /// 「改设置 + 刷新界面 + 重载设备列表」仍由宿主做（那些是 UI 与设置存储的事）。
    ///
    /// 口径：**认不出的字符串一律按共享处理**，不抛异常 —— 设置文件被手改坏时也要能出声。
    /// </summary>
    internal sealed class OutputModeService(IOutputModeHost host)
    {
        /// <summary>设置里的字符串 → 引擎枚举。空白/未知值 = 共享。</summary>
        internal static HiFiOutputBackend.OutputMode Parse(string? settingsMode)
        {
            return string.Equals(settingsMode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase)
                ? HiFiOutputBackend.OutputMode.WasapiExclusive
                : string.Equals(settingsMode, "Asio", StringComparison.OrdinalIgnoreCase)
                    ? HiFiOutputBackend.OutputMode.Asio
                    : HiFiOutputBackend.OutputMode.WasapiShared;
        }

        /// <summary>把当前设置里的输出模式应用到引擎。启动恢复、设置变更、起播前都会调。</summary>
        internal void Apply(AppSettingsState settings)
        {
            HiFiOutputBackend.OutputMode mode = Parse(settings.OutputMode);

            // B4：输出模式切换 = 链路事件（启动恢复首次调用只记值，不算"切换"）
            host.NoteChainMode(settings.OutputMode);

            host.SetEngineOutputMode(mode);
        }
    }
}
