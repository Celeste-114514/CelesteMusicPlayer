using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>
/// 标签面板「列…」配置的纯逻辑（不碰任何 UI 类型，方便单测：测试工程不引 WinUI）。
/// 配置格式有两代：
/// - 旧格式（2026-10-14 之前）：一个 JSON 数组 = 当年勾出来的可选列。
///   数组里只可能出现 6 个可选列的 Key；默认列（标题/艺术家/…）当年不参与勾选、恒可见。
/// - 新格式：对象 { "order": [全部列Key], "hidden": [隐藏的Key] }，支持全部列的显隐 + 前后顺序。
/// </summary>
public static class TagColumnLayout
{
    /// <summary>老口径的 6 个可选列（XAML 里默认 Collapsed 的那几个）。</summary>
    public static readonly IReadOnlyList<string> DefaultHiddenColumns = new[]
    {
        "AlbumArtist", "Composer", "Disc", "TrackCount", "Bpm", "Comment",
    };

    /// <summary>
    /// 旧格式配置（JSON 数组）→ 应该隐藏的列集合。
    /// 口径（2026-10-14 用户实测「导入后列表有行不显示字」后定稿）：
    /// 数组里有的可选列 = 可见；数组里没有的可选列 = 隐藏；**默认列恒可见**。
    /// ⚠ 第一版兼容逻辑写的是「数组里没有的全隐藏」——用户盘上存着旧版空数组 []
    /// 时 20 个真实列全被藏掉：表格有行、能选中，但一个字都不显示。
    /// </summary>
    public static HashSet<string> InterpretLegacy(
        string json, IReadOnlyCollection<string> allColumnKeys, IReadOnlyCollection<string>? defaultHiddenKeys = null)
    {
        IReadOnlyCollection<string> optional = defaultHiddenKeys ?? DefaultHiddenColumns;

        HashSet<string> visible = new(StringComparer.Ordinal);
        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    string? key = element.GetString();
                    if (!string.IsNullOrEmpty(key) && allColumnKeys.Contains(key))
                    {
                        visible.Add(key);
                    }
                }
            }
        }

        return new HashSet<string>(
            allColumnKeys.Where(k => !visible.Contains(k) && optional.Contains(k)),
            StringComparer.Ordinal);
    }
}
