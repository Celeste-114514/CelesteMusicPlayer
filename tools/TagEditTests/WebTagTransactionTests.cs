using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CelesteMusicPlayer;
using Xunit;

namespace TagEditTests;

/// <summary>
/// 批量写事务（WebTagFileService.WriteTransaction / WriteAsync）的语义钉子：
/// 全成、预校验拦下（一个字节都不写）、**写一半失败整组回滚**、没变化跳过。
/// 事务从 MainWindow 搬出（2026-10-30）就是为了这套语义能脱离界面被直接测。
/// </summary>
public class WebTagTransactionTests
{
    private static Dictionary<string, string> Fields(params (string K, string V)[] pairs)
        => pairs.ToDictionary(p => p.K, p => p.V);

    private static List<(string Id, string? Path, Dictionary<string, string> Fields)> Jobs(
        params (string Id, string Path, Dictionary<string, string> Fields)[] items)
        => items.Select(i => (i.Id, (string?)i.Path, i.Fields)).ToList();

    [Fact]
    public async Task GroupWriteAppliesToEveryFile()
    {
        string mp3 = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".mp3", 1));
        string flac = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".flac", 1));

        var results = WebTagFileService.WriteTransaction(Jobs(
            ("a", mp3, Fields(("title", "事务甲"), ("artist", "事务艺术家"))),
            ("b", flac, Fields(("title", "事务乙")))));

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal("事务甲", TagEditorService.ReadTagRaw(mp3).Title);
        Assert.Equal("事务艺术家", TagEditorService.ReadTagRaw(mp3).Artist);
        Assert.Equal("事务乙", TagEditorService.ReadTagRaw(flac).Title);
    }

    [Fact]
    public async Task ValidationFailureBeforeAnyWriteLeavesEverythingUntouched()
    {
        string mp3 = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".mp3", 2));
        var before = TagEditorService.ReadTagRaw(mp3);
        DateTime mtime = File.GetLastWriteTimeUtc(mp3);

        // 第二个作业路径不存在 → 预校验阶段整组拒绝；第一个文件必须一个字节没动
        var results = WebTagFileService.WriteTransaction(Jobs(
            ("a", mp3, Fields(("title", "不该被写进去"))),
            ("b", Path.Combine(Path.GetTempPath(), "tagtx-不存在-" + Guid.NewGuid().ToString("N") + ".mp3"),
                 Fields(("title", "x")))));

        Assert.All(results, r => Assert.False(r.Ok));
        Assert.All(results, r => Assert.Contains("文件不存在", r.Error));
        var after = TagEditorService.ReadTagRaw(mp3);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(mp3));
    }

    [Fact]
    public async Task FailedMidTransactionRollsBackAlreadyWrittenFiles()
    {
        string mp3 = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".mp3", 3));
        string flac = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".flac", 3));
        string mp3Before = TagEditorService.ReadTagRaw(mp3).Title;

        // 只读属性：读正常（预校验过得了），写必炸（TrySaveTag 报错）→ 走"写一半失败"的回滚分支
        File.SetAttributes(flac, File.GetAttributes(flac) | FileAttributes.ReadOnly);
        try
        {
            var results = WebTagFileService.WriteTransaction(Jobs(
                ("a", mp3, Fields(("title", "写完必须被还原"))),
                ("b", flac, Fields(("title", "这个永远写不进去")))));

            // 整组失败：每个作业都拿到同一条错误说明；只读的 flac 连回滚都写不进去，
            // 事务会如实报"部分标签还原失败"——所以只断言"还原"字样，真标准在下面的回值比对。
            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.False(r.Ok));
            Assert.All(results, r => Assert.Contains("还原", r.Error));

            // 第一个文件被回滚回原值
            Assert.Equal(mp3Before, TagEditorService.ReadTagRaw(mp3).Title);
        }
        finally
        {
            File.SetAttributes(flac, File.GetAttributes(flac) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public async Task UnchangedValuesSkipDiskWrite()
    {
        string mp3 = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".mp3", 4));
        var model = TagEditorService.ReadTagRaw(mp3);
        string sameTitle = model.Title;
        DateTime mtime = File.GetLastWriteTimeUtc(mp3);

        var results = WebTagFileService.WriteTransaction(Jobs(("a", mp3, Fields(("title", sameTitle)))));

        var result = Assert.Single(results);
        Assert.True(result.Ok);
        Assert.True(result.Skipped);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(mp3));
    }

    [Fact]
    public async Task WriteAsyncSerializesBehindTheGate()
    {
        // 两个并发批次写同一个文件：闸门保证互斥，最终值是后到批次的结果，且两批都报成功
        string flac = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".flac", 5));
        var first = WebTagFileService.WriteAsync(Jobs(("a", flac, Fields(("title", "批次一")))));
        var second = WebTagFileService.WriteAsync(Jobs(("b", flac, Fields(("title", "批次二")))));
        var results = await Task.WhenAll(first, second);

        Assert.All(results.SelectMany(r => r), r => Assert.True(r.Ok, r.Error));
        Assert.Equal("批次二", TagEditorService.ReadTagRaw(flac).Title);
    }
}
