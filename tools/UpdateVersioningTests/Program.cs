using System;
using System.Collections.Generic;
using CelesteMusicPlayer;

namespace UpdateVersioningTests
{
    /// <summary>
    /// 更新检查纯逻辑的断言集。没有测试框架，跑完打印失败项并以退出码表示结果：
    /// 0 = 全过，1 = 有失败。这样在 CI 或本地都不依赖包还原。
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static void Main()
        {
            VersionParsing();
            VersionOrdering();
            AssetSelection();
            ChecksumParsing();

            if (_failures == 0)
            {
                Console.WriteLine("PASS 全部通过");
                return;
            }

            Console.WriteLine($"FAIL {_failures} 项");
            Environment.ExitCode = 1;
        }

        private static void VersionParsing()
        {
            Check("v 前缀可解析", ReleaseVersion.TryParse("v26.9.14", out ReleaseVersion? a) && a != null && a.ToString() == "26.9.14");
            Check("4 段可解析", ReleaseVersion.TryParse("26.9.13.2", out ReleaseVersion? b) && b != null && b.Revision == 2);
            Check("预发布后缀保留", ReleaseVersion.TryParse("v26.9.14-beta.2", out ReleaseVersion? c) && c != null && c.IsPrerelease && c.Prerelease == "beta.2");
            Check("build metadata 被忽略", ReleaseVersion.TryParse("26.9.14+build5", out ReleaseVersion? d) && d != null && d.ToString() == "26.9.14");
            Check("2 段不算日期版本", !ReleaseVersion.TryParse("26.9", out _));
            Check("非数字 tag 解析失败", !ReleaseVersion.TryParse("nightly", out _));
            Check("空后缀解析失败", !ReleaseVersion.TryParse("26.9.14-", out _));
            Check("空字符串解析失败", !ReleaseVersion.TryParse("   ", out _));
            Check("非法字符解析失败", !ReleaseVersion.TryParse("26.9.x", out _));
        }

        private static void VersionOrdering()
        {
            Check("新版本大于旧版本", Compare("v26.9.14", "26.9.13") > 0);
            Check("4 段修订号参与比较", Compare("26.9.13.2", "26.9.13") > 0);
            Check("相同版本相等", Compare("26.9.13", "26.9.13") == 0);
            Check("数字段按数值比而不是按字符串", Compare("26.10.1", "26.9.30") > 0);
            Check("正式版大于同号预览版", Compare("26.9.14", "26.9.14-beta.1") > 0);
            Check("同号预览版小于正式版", Compare("26.9.14-beta.1", "26.9.14") < 0);
            Check("预览版序号递增", Compare("26.9.14-beta.2", "26.9.14-beta.1") > 0);
            Check("rc 大于 beta", Compare("26.9.14-rc1", "26.9.14-beta.9") > 0);
            Check("预发布不能当成相等", Compare("26.9.14-beta.1", "26.9.14") != 0);
        }

        private static void AssetSelection()
        {
            var assets = new List<UpdateAsset>
            {
                new("CelesteMusicPlayer-Setup-26.10.20.0.exe", "https://example/fd.exe"),
                new("CelesteMusicPlayer-Setup-SC-26.10.20.0.exe", "https://example/sc.exe"),
                new("SHA256SUMS.txt", "https://example/sums"),
                new("CelesteMusicPlayer-Portable-26.10.20.0.zip", "https://example/zip")
            };

            UpdateAsset? selfContained = UpdateAssetSelector.SelectSetup(assets, UpdateVariants.SelfContained, "x64");
            Check("自包含装选择 -SC- 包", selfContained?.Name == "CelesteMusicPlayer-Setup-SC-26.10.20.0.exe");

            UpdateAsset? framework = UpdateAssetSelector.SelectSetup(assets, UpdateVariants.FrameworkDependent, "x64");
            Check("框架依赖不会拿到自包含包", framework?.Name == "CelesteMusicPlayer-Setup-26.10.20.0.exe");

            var archMixed = new List<UpdateAsset>
            {
                new("CelesteMusicPlayer-Setup-x64-26.10.20.0.exe", "https://example/x64.exe"),
                new("CelesteMusicPlayer-Setup-arm64-26.10.20.0.exe", "https://example/arm64.exe")
            };
            Check("按架构排除装不上的包", UpdateAssetSelector.SelectSetup(archMixed, UpdateVariants.FrameworkDependent, "x64")?.Name == "CelesteMusicPlayer-Setup-x64-26.10.20.0.exe");
            Check("架构都不匹配时返回空", UpdateAssetSelector.SelectSetup(archMixed, UpdateVariants.FrameworkDependent, "x86") == null);

            var wrongVariant = new List<UpdateAsset>
            {
                new("CelesteMusicPlayer-Setup-SC-26.10.20.0.exe", "https://example/sc.exe")
            };
            Check("只有自包含包时框架依赖返回空", UpdateAssetSelector.SelectSetup(wrongVariant, UpdateVariants.FrameworkDependent, "x64") == null);

            var foreign = new List<UpdateAsset>
            {
                new("Setup-26.10.20.0.exe", "https://example/legacy.exe"),
                new("OtherApp-Setup-26.10.20.0.exe", "https://example/other.exe"),
                new("CelesteMusicPlayer-Setup-26.10.20.0.exe.bak", "https://example/bak")
            };
            Check("没有应用名/不是安装器的资产不算数（去掉了任意 exe 兜底）", UpdateAssetSelector.SelectSetup(foreign, UpdateVariants.FrameworkDependent, "x64") == null);
        }

        private static void ChecksumParsing()
        {
            string hash = new string('a', 64);
            string manifest = $"# 注释行\n{hash}  CelesteMusicPlayer-Setup-26.10.20.0.exe\n{new string('b', 64)}  CelesteMusicPlayer-Setup-SC-26.10.20.0.exe\n";

            Check("命中文件名拿到哈希", ChecksumManifest.TryFind(manifest, "CelesteMusicPlayer-Setup-26.10.20.0.exe", out string? found) == ChecksumLookup.Found && found == hash);
            Check("文件名大小写不敏感", ChecksumManifest.TryFind(manifest, "celestemusicplayer-setup-26.10.20.0.exe", out _) == ChecksumLookup.Found);
            Check("清单里没有这个文件", ChecksumManifest.TryFind(manifest, "Nope.exe", out _) == ChecksumLookup.NotFound);
            Check("哈希长度不对判为坏行", ChecksumManifest.TryFind($"abc  Nope.exe\n", "Nope.exe", out _) == ChecksumLookup.Malformed);
            Check("二进制标记 * 也能解析", ChecksumManifest.TryFind($"{hash} *Nope.exe\n", "Nope.exe", out string? starred) == ChecksumLookup.Found && starred == hash);
            Check("大写哈希规范成小写", ChecksumManifest.TryFind($"{hash.ToUpperInvariant()}  Nope.exe\n", "Nope.exe", out string? upper) == ChecksumLookup.Found && upper == hash);
            Check("空清单判为没找到", ChecksumManifest.TryFind(null, "Nope.exe", out _) == ChecksumLookup.NotFound);
        }

        private static int Compare(string left, string right)
        {
            ReleaseVersion.TryParse(left, out ReleaseVersion? a);
            ReleaseVersion.TryParse(right, out ReleaseVersion? b);
            return a!.CompareTo(b);
        }

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                Console.WriteLine("  ok   " + name);
                return;
            }

            _failures++;
            Console.WriteLine("  FAIL " + name);
        }
    }
}
