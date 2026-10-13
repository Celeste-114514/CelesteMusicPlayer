using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace TagEditTests;

/// <summary>fixtures 生成器自身的一致性哨兵（防止样本库烂了导致后面的测试假绿/假红）。</summary>
public class FixtureSanityTests
{
    [Fact]
    public async Task ManifestHas100FilesAcross10Formats()
    {
        var manifest = await Fixtures.GetManifestAsync();
        Assert.Equal(100, manifest.FileCount);
        Assert.Equal(100, manifest.Files.Count);
        Assert.Equal(10, manifest.Files.Select(f => f.Format).Distinct().Count());
        Assert.Equal(10, manifest.Files.Select(f => f.Profile).Distinct().Count());
    }

    [Fact]
    public async Task EveryFixtureFileExistsOnDisk()
    {
        var manifest = await Fixtures.GetManifestAsync();
        string dir = await Fixtures.GetSourceDirAsync();
        foreach (var f in manifest.Files)
        {
            string path = Path.Combine(dir, f.File);
            Assert.True(File.Exists(path), $"缺文件：{f.File}");
            Assert.True(new FileInfo(path).Length < 512 * 1024, $"文件太大（应保持微型）：{f.File}");
        }
    }

    [Fact]
    public async Task MultiValueProfileHasTwoValuesOnEveryEditableFormat()
    {
        var manifest = await Fixtures.GetManifestAsync();
        foreach (var f in manifest.Files.Where(f => f.Profile == 1 && f.Format != ".tta"))
        {
            Assert.Equal("2", f.Verified.GetValueOrDefault("performers_count"));
            Assert.Equal("谷冈音符; 星野铃", f.Verified.GetValueOrDefault("performers"));
            Assert.Equal("2", f.Verified.GetValueOrDefault("genres_count"));
            Assert.Equal("摇滚; 电子", f.Verified.GetValueOrDefault("genres"));
        }
    }

    [Fact]
    public async Task TtaFixturesAreMarkedUnsupported()
    {
        // TagLib 打不开 TTA：这 10 个文件是「已知不支持」的文档化样本，
        // 后端对它们必须干净失败（见 TtaSaveFailsCleanlyWithoutThrow）
        var manifest = await Fixtures.GetManifestAsync();
        var ttas = manifest.Files.Where(f => f.Format == ".tta").ToList();
        Assert.Equal(10, ttas.Count);
        Assert.All(ttas, f => Assert.Contains(f.Notes, n => n.Contains("TagLib 打开/保存失败")));
    }

    [Fact]
    public async Task CoverProfileHasTwoPicturesOnEveryEditableFormat()
    {
        var manifest = await Fixtures.GetManifestAsync();
        foreach (var f in manifest.Files.Where(f => f.Profile == 4 && f.Format != ".tta"))
        {
            Assert.Equal("2", f.Verified.GetValueOrDefault("pictures"));
        }
    }
}
