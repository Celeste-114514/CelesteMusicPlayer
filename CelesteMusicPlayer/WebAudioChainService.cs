using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页端「音频链路」面板的后端投影。
    ///
    /// 分工与原生面板一致：这里只负责抓快照并序列化，**不做任何口径判断**——
    /// 节点状态、格式、诊断、DAC 图谱全部来自 <see cref="AudioSignalPath.Capture"/>，
    /// 网页端只负责渲染。铁律不变：拿不到的值是 null/空，前端显示"—"或整块收起，绝不编数。
    ///
    /// 抓取走调用方（UI 线程）：Capture 会读引擎属性与各 Store 的内存缓存，
    /// 原生面板的 200ms 节拍就是在 UI 线程跑的，照做即可。
    ///
    /// W13：从 MainWindow.WebAudioChain.cs 搬出来。取快照用一支委托注入——
    /// 引擎和 DSP 全局旁路开关的所有权还在 MainWindow。
    /// </summary>
    internal sealed class WebAudioChainService(Func<AudioSignalPath> capture)
    {
        internal Task<object> ChainAsync()
        {
            var path = capture();
            return Task.FromResult<object>(new
            {
                summaryLine = path.SummaryLine,
                bitPerfect = path.BitPerfect,
                verdictReason = path.VerdictReason,
                bitPerfectConfirmed = path.BitPerfectConfirmed,
                stageTitle = path.StageTitle,
                stageNote = path.StageNote,
                layerCount = path.LayerCount,
                sourceValue = path.SourceValue,
                sourceNote = path.SourceNote,
                processValue = path.ProcessValue,
                processNote = path.ProcessNote,
                dspHeadroomValue = path.DspHeadroomValue,
                dspHeadroomNote = path.DspHeadroomNote,
                dacDeviceName = path.DacDeviceName,
                stabilityTitle = path.StabilityTitle,
                stabilityNote = path.StabilityNote,
                fields = path.Fields.Select(ChainField).ToArray(),
                dacFields = path.DacFields.Select(ChainField).ToArray(),
                stabilityFields = path.StabilityFields.Select(ChainField).ToArray(),
                techTags = path.TechTags.Select(tag => new { text = tag.Text, tone = tag.Tone.ToString().ToLowerInvariant() }).ToArray(),
                diagnostics = path.Diagnostics.Select(item => new { category = item.Category, title = item.Title, reason = item.Reason, advice = item.Advice }).ToArray(),
                nodes = path.Nodes.Select(node => new
                {
                    id = node.Id, title = node.Title, state = node.State.ToString().ToLowerInvariant(),
                    stateText = ChainStateText(node.State),
                    detail = DescribeChainFormats(node.InFormat, node.OutFormat),
                    note = node.Note,
                    latencyMs = node.LatencyMs
                }).ToArray(),
                levels = new
                {
                    channels = path.LevelChannels,
                    peak = path.PeakLevels.Take(path.LevelChannels).ToArray(),
                    rms = path.RmsLevels.Take(path.LevelChannels).ToArray()
                },
                events = AudioSignalPath.RecentEvents(20).Select(item => new
                {
                    // RecordEvent 写的是 DateTime.Now（本地时间），这里不再做时区换算。
                    time = item.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    kind = item.Kind, text = item.Text
                }).ToArray()
            });
        }

        /// <summary>
        /// 极客预设顶部那排技术读数（OUT / FMT / FLAG / BUF）。
        ///
        /// 原则照方案第 5 节：**只显示后端确认的值，不从文件格式猜输出状态**。
        /// 所以这里不做任何推断，只把链路快照里已有的字段拣出来；拿不到就是 null，
        /// 前端显示"—"，而不是拿源文件规格冒充输出规格。
        /// </summary>
        internal object Readout()
        {
            AudioSignalPath path = capture();
            string? Field(string label)
            {
                foreach (ChainField field in path.Fields)
                {
                    if (string.Equals(field.Label, label, StringComparison.Ordinal)) return field.Value;
                }
                return null;
            }

            return new
            {
                mode = Field("输出模式"),
                internalFormat = Field("内部处理格式"),
                deviceFormat = Field("实际输出"),
                bufferMs = path.OutputBufferMs > 0 ? path.OutputBufferMs : (int?)null,
                bitPerfect = path.BitPerfect,
                confirmed = path.BitPerfectConfirmed,
                reason = path.VerdictReason
            };
        }

        private static object ChainField(ChainField field) => new { label = field.Label, value = field.Value, note = field.Note };

        /// <summary>节点状态文案，与原生面板 AudioChainPanelControl 完全一致（Unknown 返回空串 = 不显示）。</summary>
        private static string ChainStateText(SignalNodeState state) => state switch
        {
            SignalNodeState.Active => "处理中",
            SignalNodeState.Bypassed => "已旁路",
            SignalNodeState.Standby => "待命",
            SignalNodeState.Off => "未启用",
            _ => string.Empty
        };

        /// <summary>进/出格式一行。两端都拿不到返回空串；这一环节不改格式时只写一次。</summary>
        private static string DescribeChainFormats(AudioFormat? inFormat, AudioFormat? outFormat)
        {
            if (inFormat == null && outFormat == null) return string.Empty;
            if (inFormat == null) return "→ " + outFormat!.Value.DescribeShort();
            if (outFormat == null) return inFormat.Value.DescribeShort();

            AudioFormat a = inFormat.Value, b = outFormat.Value;
            return a.Rate == b.Rate && a.Bits == b.Bits && a.Channels == b.Channels
                ? a.DescribeShort()
                : a.DescribeShort() + " → " + b.DescribeShort();
        }
    }
}
