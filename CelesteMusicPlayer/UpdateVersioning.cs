using System;
using System.Collections.Generic;
using System.Globalization;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 日期式发布版本号：3 段或 4 段数字，可选预发布后缀（v26.9.14-beta.2）。
    /// 刻意做成零依赖的纯逻辑（不碰网络、不写日志），方便单独测试与复用。
    /// </summary>
    public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
    {
        private ReleaseVersion(int major, int minor, int build, int revision, string prerelease)
        {
            Major = major;
            Minor = minor;
            Build = build;
            Revision = revision;
            Prerelease = prerelease;
        }

        public int Major { get; }

        public int Minor { get; }

        public int Build { get; }

        public int Revision { get; }

        /// <summary>预发布标识（如 "beta.2"）；正式版为空字符串。</summary>
        public string Prerelease { get; }

        public bool IsPrerelease => Prerelease.Length > 0;

        /// <summary>
        /// 解析 "v26.9.14" / "26.9.14.2" / "v26.9.14-beta.2"。
        /// 只接受 3～4 段数字：2 段（26.9）或非日期式 tag（nightly）都算解析失败，
        /// 宁可报错也不拿 0 去比出一个假的「有新版本」。
        /// </summary>
        public static bool TryParse(string? raw, out ReleaseVersion? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string text = raw.Trim();
            if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
            {
                text = text.Substring(1);
            }

            // 版本号里的 build metadata（+sha）不参与比较，直接丢掉。
            int plus = text.IndexOf('+');
            if (plus >= 0)
            {
                text = text.Substring(0, plus);
            }

            string prerelease = string.Empty;
            int dash = text.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = text.Substring(dash + 1).Trim();
                text = text.Substring(0, dash).Trim();
                if (prerelease.Length == 0 || !IsValidPrerelease(prerelease))
                {
                    return false;
                }
            }

            string[] segments = text.Split('.');
            if (segments.Length < 3 || segments.Length > 4)
            {
                return false;
            }

            int[] numbers = new int[4];
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment.Length == 0 || segment.Length > 9)
                {
                    return false;
                }

                for (int c = 0; c < segment.Length; c++)
                {
                    if (segment[c] < '0' || segment[c] > '9')
                    {
                        return false;
                    }
                }

                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return false;
                }
            }

            version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], numbers[3], prerelease);
            return true;
        }

        private static bool IsValidPrerelease(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '.' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 比较优先级：数字段逐段比，完全相同再看预发布。
        /// 同一版本号下，正式版 &gt; 预览版（不能把 -beta 截掉就当相等）。
        /// </summary>
        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            int result = Major.CompareTo(other.Major);
            if (result != 0)
            {
                return result;
            }

            result = Minor.CompareTo(other.Minor);
            if (result != 0)
            {
                return result;
            }

            result = Build.CompareTo(other.Build);
            if (result != 0)
            {
                return result;
            }

            result = Revision.CompareTo(other.Revision);
            if (result != 0)
            {
                return result;
            }

            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        private static int ComparePrerelease(string left, string right)
        {
            bool leftStable = left.Length == 0;
            bool rightStable = right.Length == 0;
            if (leftStable && rightStable)
            {
                return 0;
            }

            if (leftStable)
            {
                return 1;
            }

            if (rightStable)
            {
                return -1;
            }

            string[] a = left.Split('.');
            string[] b = right.Split('.');
            int shared = Math.Min(a.Length, b.Length);
            for (int i = 0; i < shared; i++)
            {
                bool aNumeric = IsNumeric(a[i], out int aValue);
                bool bNumeric = IsNumeric(b[i], out int bValue);
                int result;
                if (aNumeric && bNumeric)
                {
                    result = aValue.CompareTo(bValue);
                }
                else if (aNumeric)
                {
                    result = -1; // 数字标识优先级低于字母标识
                }
                else if (bNumeric)
                {
                    result = 1;
                }
                else
                {
                    result = string.CompareOrdinal(a[i], b[i]);
                }

                if (result != 0)
                {
                    return result;
                }
            }

            return a.Length.CompareTo(b.Length);
        }

        private static bool IsNumeric(string value, out int number)
        {
            number = 0;
            if (value.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                {
                    return false;
                }
            }

            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

        public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Major, Minor, Build, Revision, Prerelease);

        public override string ToString()
        {
            string text = Revision > 0
                ? $"{Major}.{Minor}.{Build}.{Revision}"
                : $"{Major}.{Minor}.{Build}";
            return IsPrerelease ? text + "-" + Prerelease : text;
        }
    }

    /// <summary>安装包变体标识：sc=自包含，fd=框架依赖。</summary>
    public static class UpdateVariants
    {
        public const string SelfContained = "sc";
        public const string FrameworkDependent = "fd";
    }

    /// <summary>Release 里的一个资产文件。</summary>
    public sealed class UpdateAsset
    {
        public UpdateAsset(string name, string url)
        {
            Name = name ?? string.Empty;
            Url = url ?? string.Empty;
        }

        public string Name { get; }

        public string Url { get; }
    }

    /// <summary>
    /// 从 Release 资产里挑安装包。规则写死、逐条可解释，不做「随便找个 .exe 兜底」——
    /// 曾经那个兜底会让自包含用户下到框架依赖包，或者干脆下到别的东西。
    /// </summary>
    public static class UpdateAssetSelector
    {
        /// <summary>应用名，资产名必须以它开头。</summary>
        public const string AppStem = "CelesteMusicPlayer";

        /// <summary>安装包固定中间段。</summary>
        private const string SetupToken = "Setup";

        /// <summary>
        /// 按 <paramref name="variant"/>（"sc"/"fd"）与架构挑选安装包；没有符合的返回 null。
        /// 名称形态：CelesteMusicPlayer-Setup[-SC][-x64]-&lt;版本&gt;.exe
        /// </summary>
        public static UpdateAsset? SelectSetup(IEnumerable<UpdateAsset>? assets, string variant, string architecture)
        {
            if (assets == null)
            {
                return null;
            }

            UpdateAsset? best = null;
            int bestScore = 0;
            foreach (UpdateAsset asset in assets)
            {
                if (!TryMatchSetup(asset.Name, variant, architecture, out int score))
                {
                    continue;
                }

                if (best == null || score > bestScore)
                {
                    best = asset;
                    bestScore = score;
                }
            }

            return best;
        }

        private static bool TryMatchSetup(string name, string variant, string architecture, out int score)
        {
            score = 0;
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string stem = name.Trim().Substring(0, name.Trim().Length - 4);
            string[] tokens = stem.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                return false;
            }

            if (!tokens[0].Equals(AppStem, StringComparison.OrdinalIgnoreCase)
                || !tokens[1].Equals(SetupToken, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool selfContained = false;
            bool archSpecific = false;
            bool archMatched = false;
            for (int i = 2; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token.Equals("SC", StringComparison.OrdinalIgnoreCase))
                {
                    selfContained = true;
                    continue;
                }

                if (TryNormalizeArch(token, out string tokenArch))
                {
                    archSpecific = true;
                    if (tokenArch == NormalizeArch(architecture))
                    {
                        archMatched = true;
                    }

                    continue;
                }

                // 便携版压缩包、7z 之类不是安装器，出现在名字里直接排除。
                if (token.Equals("Portable", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("7z", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("Zip", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (archSpecific && !archMatched)
            {
                return false;
            }

            bool wantSelfContained = string.Equals(variant, UpdateVariants.SelfContained, StringComparison.OrdinalIgnoreCase);
            if (selfContained != wantSelfContained)
            {
                return false;
            }

            // 带明确架构的优先于通用的（前者是更精确的匹配）。
            score = archSpecific ? 2 : 1;
            return true;
        }

        private static bool TryNormalizeArch(string token, out string architecture)
        {
            switch (token.ToLowerInvariant())
            {
                case "x64":
                case "amd64":
                    architecture = "x64";
                    return true;
                case "x86":
                case "win32":
                    architecture = "x86";
                    return true;
                case "arm64":
                case "aarch64":
                    architecture = "arm64";
                    return true;
                default:
                    architecture = string.Empty;
                    return false;
            }
        }

        /// <summary>把运行时枚举/字符串统一成 x64 / x86 / arm64。</summary>
        public static string NormalizeArch(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "x64";
            }

            string value = raw.Trim();
            if (TryNormalizeArch(value, out string architecture))
            {
                return architecture;
            }

            return value.Equals("Arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : value.ToLowerInvariant();
        }
    }

    /// <summary>安装包哈希清单（SHA256SUMS.txt）的查询结果。</summary>
    public enum ChecksumLookup
    {
        /// <summary>清单里没有这个文件名。</summary>
        NotFound,

        /// <summary>找到了，哈希格式合法。</summary>
        Found,

        /// <summary>有这一行，但哈希不是 64 位十六进制。</summary>
        Malformed
    }

    /// <summary>解析 SHA256SUMS.txt（GNU 格式：&lt;hash&gt;␠␠&lt;name&gt;，也接受 &lt;hash&gt;␠*&lt;name&gt;）。</summary>
    public static class ChecksumManifest
    {
        public static ChecksumLookup TryFind(string? manifest, string fileName, out string? hash)
        {
            hash = null;
            if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(fileName))
            {
                return ChecksumLookup.NotFound;
            }

            foreach (string rawLine in manifest.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int separator = IndexOfWhitespace(line);
                if (separator <= 0)
                {
                    continue;
                }

                string candidate = line.Substring(0, separator);
                string name = line.Substring(separator).Trim();
                if (name.Length > 0 && name[0] == '*')
                {
                    name = name.Substring(1).Trim();
                }

                if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsHex64(candidate))
                {
                    return ChecksumLookup.Malformed;
                }

                hash = candidate.ToLowerInvariant();
                return ChecksumLookup.Found;
            }

            return ChecksumLookup.NotFound;
        }

        private static int IndexOfWhitespace(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ' || line[i] == '\t')
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsHex64(string value)
        {
            if (value.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
