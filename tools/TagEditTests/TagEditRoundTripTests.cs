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
    public async Task TtaSaveFailsCleanlyWithoutThrow()
    {
        // TagLib 不支持 TTA：保存必须返回 false（原因进日志），不能抛异常、不能写坏文件
        string file = await Fixtures.FileNameForAsync(".tta", 1);
        string path = await Fixtures.CopyForTestAsync(file);
        byte[] bytesBefore = File.ReadAllBytes(path);

        var model = new TagEditModel { Title = "改不动的标题" };
        bool ok = TagEditorService.SaveTag(path, model);

        Assert.False(ok);
        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
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
        Assert.True(TagEditorService.SaveTag(path, model));

        Assert.True(HasFrame(path, "TXYZ"), "保存后未知帧丢了");
    }

    [Fact]
    public async Task EmptyModelSaveKeepsFileReadable()
    {
        string file = await Fixtures.FileNameForAsync(".mp3", 5);
        string path = await Fixtures.CopyForTestAsync(file);

        TagEditModel model = TagEditorService.ReadTagRaw(path);
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
        Assert.True(TagEditorService.SaveTag(path, model));

        Assert.True(CountCommentFrames(path) >= 2, "保存后评论帧少了");
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
