using System;
using System.Collections.Generic;
using System.Linq;
using CelesteMusicPlayer;
using Xunit;

namespace TagEditTests;

/// <summary>
/// 标签面板「列…」旧格式配置兼容回归。
/// 背景（2026-10-14 用户实测）：导入测试文件夹后标签面板列表有行、能选中，但一个字都不显示。
/// 根因：旧格式配置是个 JSON 数组，里面只可能躺着 6 个可选列的 Key；默认列（标题/艺术家/…）
/// 当年根本不参与勾选、恒可见。新版兼容解析第一版写成「数组里没有的全隐藏」，
/// 用户盘上存着旧版空数组 [] 时 20 个真实列全被藏掉。
/// 这几个用例就是把那条口径钉死：默认列永远不能因为旧配置被藏。
/// </summary>
public class ColumnLayoutLegacyTests
{
    /// <summary>与 TagPanelControl.xaml 的列定义一一对应（14 默认 + 6 可选；Filler 不参与显隐）。</summary>
    private static readonly string[] AllKeys =
    {
        "Index", "Title", "Artist", "Album", "Year", "Track", "Genre", "Duration",
        "FileName", "Format", "BitDepth", "SampleRate", "Bitrate", "FileSize",
        "AlbumArtist", "Composer", "Disc", "TrackCount", "Bpm", "Comment",
    };

    /// <summary>老口径的 6 个可选列（XAML 里默认 Collapsed 的那几个）。</summary>
    private static readonly string[] OptionalKeys =
    {
        "AlbumArtist", "Composer", "Disc", "TrackCount", "Bpm", "Comment",
    };

    private static readonly string[] DefaultKeys = AllKeys.Except(OptionalKeys).ToArray();

    [Fact]
    public void EmptyArray_HidesOnlyOptionalColumns_DefaultsStayVisible()
    {
        // 用户盘上真实存在的事故配置：旧版把 6 个可选列全取消后存下的空数组
        HashSet<string> hidden = TagColumnLayout.InterpretLegacy("[]", AllKeys, OptionalKeys);

        Assert.Equal(OptionalKeys.OrderBy(k => k, StringComparer.Ordinal), hidden.OrderBy(k => k, StringComparer.Ordinal));
        foreach (string key in DefaultKeys)
        {
            Assert.DoesNotContain(key, hidden);
        }
    }

    [Fact]
    public void ListedOptionalColumns_StayVisible_UnlistedOnesHidden()
    {
        HashSet<string> hidden = TagColumnLayout.InterpretLegacy(
            "[\"Bpm\",\"Comment\"]", AllKeys, OptionalKeys);

        Assert.Equal(
            new[] { "AlbumArtist", "Composer", "Disc", "TrackCount" }.OrderBy(k => k, StringComparer.Ordinal),
            hidden.OrderBy(k => k, StringComparer.Ordinal));
        Assert.DoesNotContain("Bpm", hidden);
        Assert.DoesNotContain("Comment", hidden);
    }

    [Fact]
    public void AllOptionalListed_NothingHidden()
    {
        string json = "[\"AlbumArtist\",\"Composer\",\"Disc\",\"TrackCount\",\"Bpm\",\"Comment\"]";
        HashSet<string> hidden = TagColumnLayout.InterpretLegacy(json, AllKeys, OptionalKeys);

        Assert.Empty(hidden);
    }

    [Fact]
    public void UnknownAndDefaultKeys_InArray_DoNotChangeOutcome()
    {
        // 数组里混进不认识/默认列的键：一律当没写，结果仍只有 6 个可选列隐藏
        HashSet<string> hidden = TagColumnLayout.InterpretLegacy(
            "[\"NoSuchColumn\",\"Title\",\"Artist\"]", AllKeys, OptionalKeys);

        Assert.Equal(OptionalKeys.OrderBy(k => k, StringComparer.Ordinal), hidden.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void WhitespaceAndDuplicateKeys_Tolerated()
    {
        HashSet<string> hidden = TagColumnLayout.InterpretLegacy(
            "[ \"Bpm\" , \"Bpm\" , \"Comment\" ]", AllKeys, OptionalKeys);

        Assert.Equal(
            new[] { "AlbumArtist", "Composer", "Disc", "TrackCount" }.OrderBy(k => k, StringComparer.Ordinal),
            hidden.OrderBy(k => k, StringComparer.Ordinal));
    }
}
