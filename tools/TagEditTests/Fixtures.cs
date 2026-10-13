using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TagEditTests;

/// <summary>
/// fixtures 源目录：整个测试进程只生成一次（复用 %TEMP% 下固定目录，二次跑测试不必再等 25 秒）。
/// 会改动文件的测试一律先 <see cref="CopyForTest"/> 到独立临时文件再动手，源目录保持只读。
/// </summary>
public static class Fixtures
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<string>? _sourceDirTask;

    public static async Task<string> GetSourceDirAsync()
    {
        if (_sourceDirTask != null)
        {
            return await _sourceDirTask;
        }
        await Gate.WaitAsync();
        try
        {
            if (_sourceDirTask == null)
            {
                _sourceDirTask = Task.Run(GenerateOnceAsync);
            }
            return await _sourceDirTask;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> GenerateOnceAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "celeste-tagfixtures-src");
        string manifest = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest))
        {
            var options = new TagFixtures.Generator.Options { OutDir = dir, Force = true };
            await TagFixtures.Generator.GenerateAsync(options);
        }
        return dir;
    }

    /// <summary>读 manifest（生成器产物的地面真值）。</summary>
    public static async Task<FixtureManifest> GetManifestAsync()
    {
        string dir = await GetSourceDirAsync();
        string json = await File.ReadAllTextAsync(Path.Combine(dir, "manifest.json"));
        return JsonSerializer.Deserialize<FixtureManifest>(json)!;
    }

    /// <summary>把源目录里的某个样本拷成独立临时文件（测试可随便改）。</summary>
    public static async Task<string> CopyForTestAsync(string fileName)
    {
        string srcDir = await GetSourceDirAsync();
        string src = Path.Combine(srcDir, fileName);
        string dst = Path.Combine(Path.GetTempPath(), "tagfixture-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
        File.Copy(src, dst, overwrite: true);
        return dst;
    }

    /// <summary>按格式+档位取文件名（如 (".mp3", 1) → f001_mp3_p01_多值基本盘.mp3）。</summary>
    public static async Task<string> FileNameForAsync(string ext, int profile)
    {
        var manifest = await GetManifestAsync();
        var entry = manifest.Files.First(f =>
            f.Format.Equals(ext, StringComparison.OrdinalIgnoreCase) && f.Profile == profile);
        return entry.File;
    }

    /// <summary>除 TTA（TagLib 不支持，无法写标签）外全部格式。</summary>
    public static readonly string[] EditableFormats =
    {
        ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".opus", ".wma", ".wv", ".mp2",
    };
}

// manifest.json 的结构镜像（生成器 C# 属性名 → JSON PascalCase，见 Generator 的序列化设置）
public sealed class FixtureManifest
{
    public string GeneratedUtc { get; set; } = "";
    public string Ffmpeg { get; set; } = "";
    public int FileCount { get; set; }
    public List<FixtureFile> Files { get; set; } = new();
}

public sealed class FixtureFile
{
    public string File { get; set; } = "";
    public int Index { get; set; }
    public string Format { get; set; } = "";
    public string FormatName { get; set; } = "";
    public int Profile { get; set; }
    public string ProfileName { get; set; } = "";
    public Dictionary<string, string> Expected { get; set; } = new();
    public Dictionary<string, string> Verified { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}
