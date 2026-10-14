using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CelesteMusicPlayer;
using Xunit;

namespace TagEditTests;

/// <summary>
/// 标签编辑 round-trip 测试。
/// 其中 4 个「红套」用例（多值艺术家/多值流派/封面 MIME/无改动不写盘）
/// 在当前旧后端的预期行为就是失败——它们固定的是 Kid3/mp3tag 语义，
/// 后端重写后全部转绿。
/// </summary>
public class TagEditRoundTripTests
{
    // ---------- 红套：旧后端会丢数据/乱写/空转 ----------

    [Fact]
    public async Task MultiArtistSurvivesReadSaveRead()
    {
        foreach (string ext in Fixtures.EditableFormats)
        {
            string file = await Fixtures.FileNameForAsync(ext, 1);
            string path = await Fixtures.CopyForTestAsync(file);

            TagEditModel before = TagEditorService.ReadTagRaw(path);
            Assert.Equal("谷冈音符; 星野铃", before.Artist);

            before.MarkAllDirty(); // 强制走写盘路径，验证多值写出后不丢
            Assert.True(TagEditorService.SaveTag(path, before), $"{ext}: SaveTag 返回 false");

            TagEditModel after = TagEditorService.ReadTagRaw(path);
            Assert.Equal("谷冈音符; 星野铃", after.Artist);
        }
    }

    [Fact]
    public async Task MultiGenreSurvivesReadSaveRead()
    {
        foreach (string ext in Fixtures.EditableFormats)
        {
            string file = await Fixtures.FileNameForAsync(ext, 1);
            string path = await Fixtures.CopyForTestAsync(file);

            TagEditModel before = TagEditorService.ReadTagRaw(path);
            Assert.Equal("摇滚; 电子", before.Genre);

            before.MarkAllDirty(); // 强制走写盘路径，验证多值写出后不丢
            Assert.True(TagEditorService.SaveTag(path, before), $"{ext}: SaveTag 返回 false");

            TagEditModel after = TagEditorService.ReadTagRaw(path);
            Assert.Equal("摇滚; 电子", after.Genre);
        }
    }

    [Fact]
    public async Task CoverPngBytesGetPngMimeType()
    {
        // fixtures 的封面是生成器内嵌的手搓 PNG；写盘后 MIME 必须还是 image/png
        string file = await Fixtures.FileNameForAsync(".mp3", 4);
        string path = await Fixtures.CopyForTestAsync(file);
        byte[] png = TagFixtures.Generator.CoverRedPngBytes;

        Assert.True(TagEditorService.SetCoverBytes(path, png), "SetCoverBytes 返回 false");

        using TagLib.File f = TagLib.File.Create(path);
        var pic = f.Tag.Pictures?.FirstOrDefault();
        Assert.NotNull(pic);
        Assert.Equal("image/png", pic!.MimeType);
    }

    [Fact]
    public async Task UnchangedSaveDoesNotTouchFile()
    {
        string file = await Fixtures.FileNameForAsync(".mp3", 1);
        string path = await Fixtures.CopyForTestAsync(file);

        TagEditModel model = TagEditorService.ReadTagRaw(path);
        DateTime mtimeBefore = File.GetLastWriteTimeUtc(path);
        long lengthBefore = new FileInfo(path).Length;

        await Task.Delay(150); // 避开文件系统时间戳粒度，确保「写了」一定看得出来

        Assert.True(TagEditorService.SaveTag(path, model), "SaveTag 返回 false");

        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(path));
        Assert.Equal(lengthBefore, new FileInfo(path).Length);
    }

    // ---------- 哨兵：这些现在就应该是绿的，重写后不许回归 ----------

    [Fact]
    public async Task TtaSaveRoundTrips()
    {
        // 2026-10-14 起 TTA 标签可写了（TtaTagAccess：音频数据原样搬 + 尾部拼新 APEv2，
        // 临时文件 + File.Replace 原子替换）。这个用例早年断言「保存必须返回 false」
        // （TagLib 没有 TTA 容器解析器的时代），TTA 支持落地后口径反转：
        // 保存必须成功、字段必须 round-trip、文件不许被写坏（长度只应随标签变化）。
        string file = await Fixtures.FileNameForAsync(".tta", 1);
        string path = await Fixtures.CopyForTestAsync(file);

        var model = new TagEditModel { Title = "能改动的标题" };
        model.MarkDirty(TagField.Title); // 新后端契约：只写标记过的字段
        bool ok = TagEditorService.SaveTag(path, model);

        Assert.True(ok, "TTA SaveTag 应成功（TtaTagAccess 落地后）");
        Assert.Equal("能改动的标题", TagEditorService.ReadTagRaw(path).Title);
    }

    [Fact]
    public void TtaSaveToMissingFile_FailsCleanlyWithoutThrow()
    {
        // 哨兵：路径不存在时保存必须返回 false（原因进日志），不能抛异常
        var model = new TagEditModel { Title = "无人接收的标题" };
        model.MarkDirty(TagField.Title);
        bool ok = TagEditorService.SaveTag(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "celeste-no-such-file.tta"), model);

        Assert.False(ok);
    }

    [Fact]
    public async Task UnknownId3FrameSurvivesSave()
    {
        // 先注入一个 TagLib 不认识的帧（TXYZ），再走一遍「读→存」，
        // 未知帧必须还在（TagLib 会把不识别的 T* 帧当通用文本帧原样保留）
        string file = await Fixtures.FileNameForAsync(".mp3", 2);
        string path = await Fixtures.CopyForTestAsync(file);

        using (TagLib.File f = TagLib.File.Create(path))
        {
            if (f.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                id3.AddFrame(new TagLib.Id3v2.UnknownFrame(
                    new TagLib.ByteVector("TXYZ"), new TagLib.ByteVector(new byte[] { 1, 2, 3, 4 })));
            }
            f.Save();
        }

        Assert.True(HasFrame(path, "TXYZ"), "注入后应有未知帧");

        TagEditModel model = TagEditorService.ReadTagRaw(path);
        model.Title = "改标题不动未知帧";
        model.MarkDirty(TagField.Title);
        Assert.True(TagEditorService.SaveTag(path, model));

        Assert.True(HasFrame(path, "TXYZ"), "保存后未知帧丢了");
    }

    [Fact]
    public async Task EmptyModelSaveKeepsFileReadable()
    {
        string file = await Fixtures.FileNameForAsync(".mp3", 5);
        string path = await Fixtures.CopyForTestAsync(file);

        TagEditModel model = TagEditorService.ReadTagRaw(path);
        model.MarkAllDirty();
        Assert.True(TagEditorService.SaveTag(path, model));

        TagEditModel after = TagEditorService.ReadTagRaw(path);
        Assert.Equal(string.Empty, after.Title);
        Assert.Equal(string.Empty, after.Artist);
    }

    [Fact]
    public async Task LongUnicodeSurvivesRoundTrip()
    {
        string file = await Fixtures.FileNameForAsync(".mp3", 6);
        string path = await Fixtures.CopyForTestAsync(file);

        TagEditModel before = TagEditorService.ReadTagRaw(path);
        string title = before.Title;
        Assert.Contains("🎵", title);
        Assert.Contains("عبد", before.Artist);

        before.MarkAllDirty();
        Assert.True(TagEditorService.SaveTag(path, before));

        TagEditModel after = TagEditorService.ReadTagRaw(path);
        Assert.Equal(title, after.Title);
        Assert.Equal(before.Artist, after.Artist);
    }

    [Fact]
    public async Task LyricsSurviveRoundTrip()
    {
        foreach (string ext in Fixtures.EditableFormats)
        {
            string file = await Fixtures.FileNameForAsync(ext, 7);
            string path = await Fixtures.CopyForTestAsync(file);

            TagEditModel before = TagEditorService.ReadTagRaw(path);
            Assert.Contains("日本語一行目", before.Lyrics);

            before.MarkAllDirty();
            Assert.True(TagEditorService.SaveTag(path, before), $"{ext}: SaveTag 返回 false");

            TagEditModel after = TagEditorService.ReadTagRaw(path);
            Assert.Equal(before.Lyrics, after.Lyrics);
        }
    }

    [Fact]
    public async Task MultiCommentFramesSurviveSave()
    {
        // P9 的 mp3 有两个不同描述符的 COMM 帧，保存后两个都要在
        string file = await Fixtures.FileNameForAsync(".mp3", 9);
        string path = await Fixtures.CopyForTestAsync(file);

        Assert.True(CountCommentFrames(path) >= 2, "fixture 应含 ≥2 个评论帧");

        TagEditModel model = TagEditorService.ReadTagRaw(path);
        model.Comment = "评论被改";
        model.MarkDirty(TagField.Comment);
        Assert.True(TagEditorService.SaveTag(path, model));

        Assert.True(CountCommentFrames(path) >= 2, "保存后评论帧少了");
    }

    // ---------- 新后端契约（Kid3 模型）锁定 ----------

    [Fact]
    public void SplitJoinUsesSemicolonSeparator()
    {
        string[] parts = TagEditModel.Split("甲; 乙;丙 ");
        Assert.Equal(new[] { "甲", "乙", "丙" }, parts);
        Assert.Equal("甲; 乙; 丙", TagEditModel.Join(parts));
        Assert.Empty(TagEditModel.Split("  "));
        Assert.Empty(TagEditModel.Split(null));
        Assert.Equal(string.Empty, TagEditModel.Join(null));
    }

    [Fact]
    public async Task SettingThreePerformersWritesThreeValues()
    {
        string file = await Fixtures.FileNameForAsync(".mp3", 5);
        string path = await Fixtures.CopyForTestAsync(file);

        var model = new TagEditModel
        {
            Title = "三值艺术家",
            Performers = new[] { "甲", "乙", "丙" },
        };
        model.MarkDirty(TagField.Title);
        model.MarkDirty(TagField.Performers);
        Assert.True(TagEditorService.SaveTag(path, model));

        TagEditModel after = TagEditorService.ReadTagRaw(path);
        Assert.Equal(new[] { "甲", "乙", "丙" }, after.Performers);
        Assert.Equal("甲; 乙; 丙", after.Artist);
    }

    [Fact]
    public async Task OnlyDirtyFieldIsWrittenOthersUntouched()
    {
        // 格内编辑核心路径：只改一个字段，多值字段必须原样保留
        string file = await Fixtures.FileNameForAsync(".mp3", 1);
        string path = await Fixtures.CopyForTestAsync(file);

        TagEditModel model = TagEditorService.ReadTagRaw(path);
        model.Title = "只改标题";
        model.MarkDirty(TagField.Title);
        Assert.True(TagEditorService.SaveTag(path, model));

        TagEditModel after = TagEditorService.ReadTagRaw(path);
        Assert.Equal("只改标题", after.Title);
        Assert.Equal(new[] { "谷冈音符", "星野铃" }, after.Performers);
        Assert.Equal(new[] { "摇滚", "电子" }, after.Genres);
    }

    [Fact]
    public async Task ClearingPerformersRemovesValues()
    {
        string file = await Fixtures.FileNameForAsync(".flac", 1);
        string path = await Fixtures.CopyForTestAsync(file);

        TagEditModel model = TagEditorService.ReadTagRaw(path);
        model.Performers = Array.Empty<string>();
        model.MarkDirty(TagField.Performers);
        Assert.True(TagEditorService.SaveTag(path, model));

        TagEditModel after = TagEditorService.ReadTagRaw(path);
        Assert.Empty(after.Performers);
        Assert.Equal(string.Empty, after.Artist);
    }

    [Fact]
    public async Task DiscTrackTotalsSurviveWrite()
    {
        // P8：碟片/曲目总数（2/3、5/10）写出后不能退化成 0
        string file = await Fixtures.FileNameForAsync(".mp3", 8);
        string path = await Fixtures.CopyForTestAsync(file);

        TagEditModel model = TagEditorService.ReadTagRaw(path);
        Assert.Equal(2u, model.Disc);
        Assert.Equal(3u, model.DiscCount);
        Assert.Equal(5u, model.Track);
        Assert.Equal(10u, model.TrackCount);

        model.MarkAllDirty();
        Assert.True(TagEditorService.SaveTag(path, model));

        TagEditModel after = TagEditorService.ReadTagRaw(path);
        Assert.Equal(2u, after.Disc);
        Assert.Equal(3u, after.DiscCount);
        Assert.Equal(5u, after.Track);
        Assert.Equal(10u, after.TrackCount);
    }

    private static bool HasFrame(string path, string frameId)
    {
        using TagLib.File f = TagLib.File.Create(path);
        if (f.GetTag(TagLib.TagTypes.Id3v2) is not TagLib.Id3v2.Tag id3)
        {
            return false;
        }
        return id3.GetFrames().Any(fr => fr.FrameId.ToString() == frameId);
    }

    private static int CountCommentFrames(string path)
    {
        using TagLib.File f = TagLib.File.Create(path);
        if (f.GetTag(TagLib.TagTypes.Id3v2) is not TagLib.Id3v2.Tag id3)
        {
            return 0;
        }
        return id3.GetFrames<TagLib.Id3v2.CommentsFrame>().Count();
    }
}
