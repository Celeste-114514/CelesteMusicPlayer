using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CelesteMusicPlayer;
using Xunit;

namespace TagEditTests;

public class WebTagWriteTests
{
    private static bool Apply(TagEditModel model, Dictionary<string, string> fields) =>
        (bool)typeof(TagEditModel).Assembly.GetType("CelesteMusicPlayer.WebTagEditRules", true)!
            .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { model, fields })!;

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".m4a")]
    public async Task WebEditActuallyWritesOnlyRequestedField(string extension)
    {
        string path = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(extension, 1));
        var before = TagEditorService.ReadTagRaw(path);
        string originalArtist = before.Artist, originalAlbum = before.Album;
        Assert.True(Apply(before, new() { ["title"] = "网页标签写盘验证" }));
        Assert.Single(before.Dirty);
        var saved = TagEditorService.TrySaveTag(path, before);
        Assert.True(saved.Ok, saved.Error);
        Assert.False(saved.Skipped);
        var after = TagEditorService.ReadTagRaw(path);
        Assert.Equal("网页标签写盘验证", after.Title);
        Assert.Equal(originalArtist, after.Artist);
        Assert.Equal(originalAlbum, after.Album);
    }

    [Fact]
    public async Task EmptyNumericInputClearsYearOnDisk()
    {
        string path = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".flac", 1));
        var model = TagEditorService.ReadTagRaw(path);
        model.Year = 2026; model.MarkDirty(TagField.Year); Assert.True(TagEditorService.SaveTag(path, model));
        model = TagEditorService.ReadTagRaw(path);
        Assert.True(Apply(model, new() { ["year"] = "" }));
        Assert.True(TagEditorService.SaveTag(path, model));
        Assert.Equal(0u, TagEditorService.ReadTagRaw(path).Year);
    }

    [Fact]
    public async Task SameWebValueSkipsDiskWrite()
    {
        string path = await Fixtures.CopyForTestAsync(await Fixtures.FileNameForAsync(".mp3", 1));
        var model = TagEditorService.ReadTagRaw(path); DateTime time = File.GetLastWriteTimeUtc(path);
        Assert.False(Apply(model, new() { ["title"] = model.Title }));
        Assert.True(TagEditorService.TrySaveTag(path, model).Skipped);
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void InvalidNumericValueRejectsWrite()
    {
        var error = Assert.Throws<TargetInvocationException>(() => Apply(new(), new() { ["year"] = "not-a-year" }));
        Assert.IsType<ArgumentException>(error.InnerException);
    }
}
