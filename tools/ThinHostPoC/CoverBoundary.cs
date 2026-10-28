using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace CelesteMusicPlayer.ThinHost;

/// <summary>
/// W6 第五步：封面纯逻辑边界自检。
/// 只碰自检专属临时目录（Path.GetTempPath 下的随机子目录），不碰用户真实缓存；
/// 素材用仓库里的"封面双图"测试音频（构建时复制到 Fixtures/），只读。
/// 覆盖面：缓存目录规则、SHA256 键、分桶路径、档位文件名、只缩不放几何、
/// 文件头测尺寸、多图取最大、内外封面优先级、sidecar 顺序、真实容器提取。
/// </summary>
internal static class CoverBoundary
{
    internal sealed record Result(bool Passed, int Checks, IReadOnlyList<string> Details);

    internal static Result Run()
    {
        var details = new List<string>();
        int checks = 0;
        string tempRoot = Path.Combine(Path.GetTempPath(), "CelestePocCoverBoundary-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            RunLayoutChecks(details, ref checks, tempRoot);
            RunSizingChecks(details, ref checks);
            RunMeasureChecks(details, ref checks);
            RunPickChecks(details, ref checks);
            RunSourceChecks(details, ref checks, tempRoot);
            RunThumbnailChecks(details, ref checks, tempRoot);
            RunFixtureChecks(details, ref checks);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* 尽力清理 */ }
        }

        bool passed = details.All(d => !d.Contains("= FAIL", StringComparison.Ordinal));
        return new Result(passed, checks, details);
    }

    // ---------------------------------------------------------------- 缓存布局

    private static void RunLayoutChecks(List<string> details, ref int checks, string tempRoot)
    {
        string expectedRoot = Path.Combine(tempRoot, "CelesteMusicPlayer", "CoverCache", "v2");
        Check(details, ref checks, "layout.root-explicit",
            CoverCacheLayout.GetRoot(tempRoot, 2) == expectedRoot,
            CoverCacheLayout.GetRoot(tempRoot, 2));

        string defaultRoot = CoverCacheLayout.GetRoot();
        Check(details, ref checks, "layout.root-default",
            CoverCacheLayout.CacheVersion == 2
            && defaultRoot.EndsWith(Path.Combine("CelesteMusicPlayer", "CoverCache", "v" + CoverCacheLayout.CacheVersion), StringComparison.Ordinal),
            defaultRoot);

        // SHA256("abc") 的标准答案，钉住"哈希算法与小写十六进制"不能再被顺手改掉
        const string expectedAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        string keyAbc = CoverCacheLayout.GetKey(System.Text.Encoding.UTF8.GetBytes("abc"));
        Check(details, ref checks, "layout.key-sha256", keyAbc == expectedAbc, keyAbc);

        string keyAbc2 = CoverCacheLayout.GetKey(System.Text.Encoding.UTF8.GetBytes("abc"));
        string keyAbd = CoverCacheLayout.GetKey(System.Text.Encoding.UTF8.GetBytes("abd"));
        Check(details, ref checks, "layout.key-dedup", keyAbc == keyAbc2, "同一张图两次算键必须一样");
        Check(details, ref checks, "layout.key-changes", keyAbc != keyAbd, "换图必须换键，旧缓存自动失效");

        string entry = CoverCacheLayout.GetEntryDirectory(expectedRoot, keyAbc);
        Check(details, ref checks, "layout.bucket",
            entry == Path.Combine(expectedRoot, keyAbc.Substring(0, 2), keyAbc),
            entry);

        string thumb = CoverCacheLayout.GetVariantFile(expectedRoot, keyAbc, CoverVariant.Thumb);
        string album = CoverCacheLayout.GetVariantFile(expectedRoot, keyAbc, CoverVariant.Album);
        string large = CoverCacheLayout.GetVariantFile(expectedRoot, keyAbc, CoverVariant.Large);
        Check(details, ref checks, "layout.variant-names",
            Path.GetFileName(thumb) == "Thumb.jpg" && Path.GetFileName(album) == "Album.jpg" && Path.GetFileName(large) == "Large.jpg",
            $"{Path.GetFileName(thumb)}/{Path.GetFileName(album)}/{Path.GetFileName(large)}");
        Check(details, ref checks, "layout.variant-same-entry",
            Path.GetDirectoryName(thumb) == entry && Path.GetDirectoryName(album) == entry && Path.GetDirectoryName(large) == entry,
            "三档必须落在同一个键目录里，清理时才能整目录删");
        Check(details, ref checks, "layout.temp-same-dir",
            CoverCacheLayout.GetTempFile(album) == album + ".tmp" && Path.GetDirectoryName(CoverCacheLayout.GetTempFile(album)) == entry,
            "先写 .tmp 再改名必须同目录（跨盘改名不是原子操作）");
        Check(details, ref checks, "layout.inflight-keys",
            CoverCacheLayout.GetInFlightKey(keyAbc, CoverVariant.Thumb) != CoverCacheLayout.GetInFlightKey(keyAbc, CoverVariant.Album)
            && CoverCacheLayout.GetInFlightKey(keyAbc, CoverVariant.Thumb).Contains(keyAbc, StringComparison.Ordinal),
            "并发去重键必须带档位，否则同一张图的三档会互相吃掉");
        Check(details, ref checks, "layout.entry-throws-on-bad-key",
            Throws(() => CoverCacheLayout.GetEntryDirectory(expectedRoot, "a")),
            "键短于 2 位必须报错，不能悄悄产出奇怪路径");
    }

    // ---------------------------------------------------------------- 只缩不放

    private static void RunSizingChecks(List<string> details, ref int checks)
    {
        Check(details, ref checks, "sizing.square",
            CoverSizing.ComputeTargetSize(1200, 1200, 320) == (320, 320),
            "1200² → 320 档");

        Check(details, ref checks, "sizing.wide",
            CoverSizing.ComputeTargetSize(1600, 900, 320) == (320, 180),
            "1600×900 → 320 档必须按长边缩、比例不变");

        Check(details, ref checks, "sizing.tall",
            CoverSizing.ComputeTargetSize(900, 1600, 320) == (180, 320),
            "竖图按高边缩");

        Check(details, ref checks, "sizing.no-upscale",
            CoverSizing.ComputeTargetSize(200, 200, 768) == (200, 200),
            "比档位小的源图原样输出（放大只会更糊）");

        Check(details, ref checks, "sizing.thumb-value",
            CoverSizing.LongEdge(CoverVariant.Thumb) == 160
            && CoverSizing.LongEdge(CoverVariant.Album) == 320
            && CoverSizing.LongEdge(CoverVariant.Large) == 768,
            "三档长边 = 160/320/768");

        Check(details, ref checks, "sizing.bad-source",
            CoverSizing.ComputeTargetSize(0, 100, 320) == (0, 0),
            "零尺寸源图返回 0，由调用方判失败（原实现 return false）");
    }

    // ---------------------------------------------------------------- 文件头测尺寸

    private static void RunMeasureChecks(List<string> details, ref int checks)
    {
        Check(details, ref checks, "measure.png",
            CoverSourceReader.TryMeasureImageSize(FakePng(1000, 500), out int pw, out int ph) && pw == 1000 && ph == 500,
            "PNG IHDR 大端");

        Check(details, ref checks, "measure.gif",
            CoverSourceReader.TryMeasureImageSize(FakeGif(320, 240), out int gw, out int gh) && gw == 320 && gh == 240,
            "GIF 逻辑屏幕小端");

        Check(details, ref checks, "measure.bmp",
            CoverSourceReader.TryMeasureImageSize(FakeBmp(640, 480), out int bw, out int bh) && bw == 640 && bh == 480,
            "BMP DIB 头");

        Check(details, ref checks, "measure.webp-vp8x",
            CoverSourceReader.TryMeasureImageSize(FakeWebpVp8x(800, 600), out int ww, out int wh) && ww == 800 && wh == 600,
            "WebP VP8X 24 位宽高 +1");

        Check(details, ref checks, "measure.jpeg",
            CoverSourceReader.TryMeasureImageSize(FakeJpeg(1280, 720), out int jw, out int jh) && jw == 1280 && jh == 720,
            "JPEG 逐段扫到 SOF0");

        Check(details, ref checks, "measure.garbage",
            !CoverSourceReader.TryMeasureImageSize(new byte[64], out _, out _),
            "全零字节必须判为测不出，不能瞎猜尺寸");

        Check(details, ref checks, "measure.too-short",
            !CoverSourceReader.TryMeasureImageSize(new byte[8], out _, out _),
            "不足 16 字节直接放弃（原行为）");
    }

    // ---------------------------------------------------------------- 挑图 / 优先级

    private static void RunPickChecks(List<string> details, ref int checks)
    {
        // 关键回归：FrontCover 是小图、另一张才是大图时，必须取大图
        var candidates = new[]
        {
            new CoverPictureCandidate(FakePng(200, 200), true),
            new CoverPictureCandidate(FakePng(1000, 1000), false),
        };
        Check(details, ref checks, "pick.largest-wins", CoverSourceReader.PickBestPictureIndex(candidates) == 1,
            "200² FrontCover 与 1000² 普通图并存 → 取 1000²");

        var tie = new[]
        {
            new CoverPictureCandidate(FakePng(600, 600), false),
            new CoverPictureCandidate(FakePng(600, 600), true),
        };
        Check(details, ref checks, "pick.tie-prefers-front", CoverSourceReader.PickBestPictureIndex(tie) == 1,
            "同尺寸时优先 FrontCover");

        var tieFrontFirst = new[]
        {
            new CoverPictureCandidate(FakePng(600, 600), true),
            new CoverPictureCandidate(FakePng(600, 600), false),
        };
        Check(details, ref checks, "pick.tie-keeps-first", CoverSourceReader.PickBestPictureIndex(tieFrontFirst) == 0,
            "同尺寸且第一张已是 FrontCover → 不换");

        var emptyData = new[]
        {
            new CoverPictureCandidate(Array.Empty<byte>(), false),
            new CoverPictureCandidate(null, true),
        };
        Check(details, ref checks, "pick.all-empty-falls-back",
            CoverSourceReader.PickBestPictureIndex(emptyData) == 0,
            "全都取不出字节时退回第 0 张（原实现 best ?? pictures[0]）");

        Check(details, ref checks, "pick.none", CoverSourceReader.PickBestPictureIndex(Array.Empty<CoverPictureCandidate>()) == -1);

        byte[] inner = { 1, 2, 3 };
        byte[] outer = { 9, 8, 7 };
        Check(details, ref checks, "source.inner-first", CoverSourceReader.PickSource(inner, outer, true)?.SequenceEqual(inner) == true);
        Check(details, ref checks, "source.outer-first", CoverSourceReader.PickSource(inner, outer, false)?.SequenceEqual(outer) == true);
        Check(details, ref checks, "source.fallback-inner-when-outer-missing",
            CoverSourceReader.PickSource(inner, null, false)?.SequenceEqual(inner) == true,
            "外置优先但外置没有 → 用内嵌");
        Check(details, ref checks, "source.fallback-outer-when-inner-missing",
            CoverSourceReader.PickSource(null, outer, true)?.SequenceEqual(outer) == true,
            "内嵌优先但内嵌没有 → 用外置");
        Check(details, ref checks, "source.both-missing", CoverSourceReader.PickSource(null, null, true) == null);
    }

    // ---------------------------------------------------------------- 真实文件读写

    private static void RunSourceChecks(List<string> details, ref int checks, string tempRoot)
    {
        string dir = Path.Combine(tempRoot, "sidecar");
        Directory.CreateDirectory(dir);
        string audioPath = Path.Combine(dir, "track.mp3");
        File.WriteAllBytes(audioPath, new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00, 0x00 }); // 假音频，TagLib 打不开

        byte[] folderJpg = { 0xA1 };
        byte[] coverPng = { 0xA2 };
        byte[] sameNameJpg = { 0xA3 };
        File.WriteAllBytes(Path.Combine(dir, "cover.png"), coverPng);
        File.WriteAllBytes(Path.Combine(dir, "track.jpg"), sameNameJpg);
        File.WriteAllBytes(Path.Combine(dir, "folder.jpg"), folderJpg);

        Check(details, ref checks, "sidecar.folder-first",
            CoverSourceReader.ReadOuter(audioPath)?.SequenceEqual(folderJpg) == true,
            "folder.jpg 排第一（与搬出前顺序一致）");

        File.Delete(Path.Combine(dir, "folder.jpg"));
        Check(details, ref checks, "sidecar.cover-second",
            CoverSourceReader.ReadOuter(audioPath)?.SequenceEqual(coverPng) == true,
            "没有 folder.jpg 就用 cover.png");

        File.Delete(Path.Combine(dir, "cover.png"));
        Check(details, ref checks, "sidecar.same-name-last",
            CoverSourceReader.ReadOuter(audioPath)?.SequenceEqual(sameNameJpg) == true,
            "都没有才用同名 track.jpg");

        Check(details, ref checks, "read.invalid-audio-uses-sidecar",
            CoverSourceReader.Read(audioPath, preferInnerCover: true)?.SequenceEqual(sameNameJpg) == true,
            "内嵌读不出来（不是真音频）→ Read 自动落到 sidecar");

        File.Delete(Path.Combine(dir, "track.jpg"));
        Check(details, ref checks, "sidecar.none", CoverSourceReader.ReadOuter(audioPath) == null);
        Check(details, ref checks, "read.nothing-anywhere", CoverSourceReader.Read(audioPath, preferInnerCover: true) == null);
        Check(details, ref checks, "read.missing-file", CoverSourceReader.Read(Path.Combine(dir, "nope.flac"), preferInnerCover: false) == null,
            "文件不存在 → null 不抛");
        Check(details, ref checks, "read.blank-path", CoverSourceReader.Read("   ", preferInnerCover: true) == null);
    }

    // ---------------------------------------------------------------- 缩略图生成（ImageSharp）

    /// <summary>
    /// 第六步的核心：确认"解码 → 按档缩放 → 编码 jpg"这半边离开 WinRT 也能跑，
    /// 且输出尺寸与磁盘上已有缓存（CoverSizing 算出来的档位）完全对齐。
    /// </summary>
    private static void RunThumbnailChecks(List<string> details, ref int checks, string tempRoot)
    {
        string dir = Path.Combine(tempRoot, "thumbs");
        Directory.CreateDirectory(dir);
        byte[] source = MakeGradientPng(1200, 900);

        Check(details, ref checks, "thumb.source-4x3",
            CoverSourceReader.TryMeasureImageSize(source, out int sw, out int sh) && sw == 1200 && sh == 900,
            $"{sw}x{sh} 的合成源图");

        CheckVariantFile(details, ref checks, dir, source, CoverVariant.Thumb, 160, 120);
        CheckVariantFile(details, ref checks, dir, source, CoverVariant.Album, 320, 240);
        CheckVariantFile(details, ref checks, dir, source, CoverVariant.Large, 768, 576);

        // 只缩不放：小图进大档必须原样尺寸，不能被放大
        byte[] small = MakeGradientPng(200, 200);
        string smallPath = Path.Combine(dir, "small_large.jpg");
        bool smallOk = CoverThumbnailer.TryWriteVariant(small, smallPath, CoverVariant.Large, out string? smallErr);
        Check(details, ref checks, "thumb.no-upscale",
            smallOk && MeasureOnDisk(smallPath) == (200, 200),
            smallOk ? "200² 进 768 档保持 200²" : smallErr);

        // 坏输入：不能抛，必须给出原因（调用方据此回退占位图）
        string badPath = Path.Combine(dir, "bad.jpg");
        bool badOk = CoverThumbnailer.TryWriteVariant(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, badPath, CoverVariant.Album, out string? badErr);
        Check(details, ref checks, "thumb.bad-source",
            !badOk && !string.IsNullOrWhiteSpace(badErr) && !File.Exists(badPath),
            badErr);

        Check(details, ref checks, "thumb.empty-source",
            !CoverThumbnailer.TryWriteVariant(Array.Empty<byte>(), Path.Combine(dir, "empty.jpg"), CoverVariant.Album, out _),
            "空字节直接判失败，不进解码器");

        // 同一路径重复写（缓存被删后重建的路径）：不能因为文件已存在就抛
        string reusePath = Path.Combine(dir, "reuse.jpg");
        bool first = CoverThumbnailer.TryWriteVariant(source, reusePath, CoverVariant.Thumb, out _);
        bool second = CoverThumbnailer.TryWriteVariant(source, reusePath, CoverVariant.Thumb, out _);
        Check(details, ref checks, "thumb.overwrite-ok",
            first && second && MeasureOnDisk(reusePath) == (160, 120),
            "重复生成到同一路径必须覆盖成功");

        // 与布局拼起来：写进 {root}\{键前2位}\{键}\Album.jpg 后路径可复算（缓存命中就靠这个）
        string root = CoverCacheLayout.GetRoot(tempRoot, CoverCacheLayout.CacheVersion);
        string key = CoverCacheLayout.GetKey(source);
        string entryDir = CoverCacheLayout.GetEntryDirectory(root, key);
        Directory.CreateDirectory(entryDir);
        string albumPath = CoverCacheLayout.GetVariantFile(root, key, CoverVariant.Album);
        bool placed = CoverThumbnailer.TryWriteVariant(source, albumPath, CoverVariant.Album, out _);
        Check(details, ref checks, "thumb.layout-path",
            placed && File.Exists(CoverCacheLayout.GetVariantFile(root, key, CoverVariant.Album)),
            "按布局算出的路径写盘后，同一套规则必须能再找到它");

        Check(details, ref checks, "thumb.engine", CoverThumbnailer.EngineName == "SkiaSharp",
            "引擎标识（将来换库要同步改日志与这条断言）");
    }

    private static void CheckVariantFile(List<string> details, ref int checks, string dir, byte[] source, CoverVariant variant, int expectW, int expectH)
    {
        string path = Path.Combine(dir, variant + ".jpg");
        string name = "thumb." + variant.ToString().ToLowerInvariant();

        bool ok = CoverThumbnailer.TryWriteVariant(source, path, variant, out string? error);
        if (!ok)
        {
            Check(details, ref checks, name, false, error);
            return;
        }

        (int width, int height) = MeasureOnDisk(path);
        long size = new FileInfo(path).Length;
        bool isJpeg = IsJpegFile(path);
        Check(details, ref checks, name,
            width == expectW && height == expectH && isJpeg && size > 0,
            $"{width}x{height}（期望 {expectW}x{expectH}），{size} 字节，JPEG={isJpeg}");
    }

    private static (int Width, int Height) MeasureOnDisk(string path)
    {
        try
        {
            using SKCodec? codec = SKCodec.Create(path);
            return codec == null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool IsJpegFile(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            return fs.ReadByte() == 0xFF && fs.ReadByte() == 0xD8;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>造一张带对角渐变的合成图（保证缩放后仍能看出方向与比例，纯色会掩盖问题）。</summary>
    private static byte[] MakeGradientPng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = new SKColor[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new SKColor(
                    (byte)(x * 255 / width),
                    (byte)(y * 255 / height),
                    (byte)((x + y) * 255 / (width + height)),
                    255);
            }
        }

        bitmap.Pixels = pixels;

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ---------------------------------------------------------------- 真实容器（fixture）

    private static void RunFixtureChecks(List<string> details, ref int checks)
    {
        string fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        string[] fixtures =
        {
            "cover-dual.mp3",
            "cover-dual.flac",
            "cover-dual.ogg",
        };

        int seen = 0;
        foreach (string name in fixtures)
        {
            string path = Path.Combine(fixtureDir, name);
            if (!File.Exists(path))
            {
                Check(details, ref checks, "fixture." + name + ".exists", false, "素材没复制到输出目录");
                continue;
            }

            seen++;
            byte[]? data = CoverSourceReader.ReadInner(path);
            int longEdge = 0;
            bool measured = data != null
                && CoverSourceReader.TryMeasureImageSize(data, out int w, out int h)
                && (longEdge = Math.Max(w, h)) > 0;

            Check(details, ref checks, "fixture." + name + ".inner-bytes",
                data is { Length: > 0 } && measured,
                data == null ? "取不到内嵌图" : $"{data.Length} 字节，长边 {longEdge}px");

            // 独立核对：直接问 TagLib 一共有几张图，确认"双图"素材没有被搬坏
            int pictureCount = CountPictures(path);
            Check(details, ref checks, "fixture." + name + ".has-two-pictures",
                pictureCount >= 2,
                "TagLib 读到 " + pictureCount + " 张图（素材本应 ≥2）");
        }

        Check(details, ref checks, "fixture.count", seen == fixtures.Length, $"实际读到 {seen}/{fixtures.Length} 份素材");
    }

    private static int CountPictures(string path)
    {
        try
        {
            using TagLib.File tagFile = TagLib.File.Create(path);
            return tagFile.Tag.Pictures?.Length ?? 0;
        }
        catch
        {
            return -1;
        }
    }

    // ---------------------------------------------------------------- 假图片字节

    private static byte[] FakePng(int width, int height)
    {
        var b = new byte[33];
        b[0] = 0x89; b[1] = 0x50; b[2] = 0x4E; b[3] = 0x47;
        b[4] = 0x0D; b[5] = 0x0A; b[6] = 0x1A; b[7] = 0x0A;
        b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16); b[18] = (byte)(width >> 8); b[19] = (byte)width;
        b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16); b[22] = (byte)(height >> 8); b[23] = (byte)height;
        return b;
    }

    private static byte[] FakeGif(int width, int height)
    {
        var b = new byte[32];
        b[0] = 0x47; b[1] = 0x49; b[2] = 0x46; b[3] = 0x38;
        b[4] = 0x39; b[5] = 0x61;
        b[6] = (byte)width; b[7] = (byte)(width >> 8);
        b[8] = (byte)height; b[9] = (byte)(height >> 8);
        return b;
    }

    private static byte[] FakeBmp(int width, int height)
    {
        var b = new byte[32];
        b[0] = 0x42; b[1] = 0x4D;
        b[18] = (byte)width; b[19] = (byte)(width >> 8); b[20] = (byte)(width >> 16); b[21] = (byte)(width >> 24);
        b[22] = (byte)height; b[23] = (byte)(height >> 8); b[24] = (byte)(height >> 16); b[25] = (byte)(height >> 24);
        return b;
    }

    private static byte[] FakeWebpVp8x(int width, int height)
    {
        var b = new byte[32];
        b[0] = 0x52; b[1] = 0x49; b[2] = 0x46; b[3] = 0x46; // RIFF
        b[8] = 0x57; b[9] = 0x45; b[10] = 0x42; b[11] = 0x50; // WEBP
        System.Text.Encoding.ASCII.GetBytes("VP8X").CopyTo(b, 12);
        int w = width - 1, h = height - 1; // 该字段存的是"实际尺寸 - 1"
        b[24] = (byte)w; b[25] = (byte)(w >> 8); b[26] = (byte)(w >> 16);
        b[27] = (byte)h; b[28] = (byte)(h >> 8); b[29] = (byte)(h >> 16);
        return b;
    }

    private static byte[] FakeJpeg(int width, int height)
    {
        // FF D8 | APP0(FF E0 + 长度 0x0010) | SOF0(FF C0 ...)
        // 注意原解析器按 i += 2 + seg 跳段，所以 SOF0 要落在 i = 20
        var b = new byte[40];
        b[0] = 0xFF; b[1] = 0xD8;
        b[2] = 0xFF; b[3] = 0xE0; b[4] = 0x00; b[5] = 0x10;
        b[20] = 0xFF; b[21] = 0xC0;
        b[22] = 0x00; b[23] = 0x11; // 段长
        b[24] = 0x08;               // 精度
        b[25] = (byte)(height >> 8); b[26] = (byte)height;
        b[27] = (byte)(width >> 8); b[28] = (byte)width;
        return b;
    }

    // ---------------------------------------------------------------- 工具

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void Check(List<string> details, ref int checks, string name, bool ok, string? note = null)
    {
        checks++;
        details.Add($"{name} = {(ok ? "OK" : "FAIL")}{(note == null ? "" : "（" + note + "）")}");
    }
}
