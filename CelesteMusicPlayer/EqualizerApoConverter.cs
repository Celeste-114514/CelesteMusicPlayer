using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// Equalizer APO 配置（config.txt）与程序内曲线 EQ（<see cref="EqCurveState"/>）的互转。
    ///
    /// APO 行格式：
    ///   Preamp: -6.5 dB
    ///   Filter: ON PK Fc 1000 Hz Gain 3 dB Q 1
    ///   Filter: ON LP Fc 20000 Hz Q 0.707
    ///
    /// 支持的滤波类型：PK(峰值) / LS(低架) / HS(高架) / LP(低通) / HP(高通) / NO(切除)。
    /// 其余类型（如 AP 全通）本程序没有对应实现，解析时跳过并计入 skipped。
    /// 低通 / 高通 / 切除在 APO 里不带 Gain，导出时也相应省略。
    /// </summary>
    public static class EqualizerApoConverter
    {
        /// <summary>解析 APO 配置文本。返回是否成功解析出至少一个有效滤波段（或 preamp）。</summary>
        public static bool TryImport(string? text, out EqCurveState? curve, out int imported, out int skipped, out string error)
        {
            curve = null;
            imported = 0;
            skipped = 0;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "文件内容为空。";
                return false;
            }

            var result = new EqCurveState { Enabled = true, PresetId = "custom", PresetName = "APO 导入" };
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool sawAny = false;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue; // 不认识的行（Include / Device / Channel 等）直接跳过
                }

                string key = line.Substring(0, colon).Trim();
                string body = line.Substring(colon + 1).Trim();

                if (string.Equals(key, "Preamp", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseLastNumber(body, out double preampDb))
                    {
                        result.PreampDb = Math.Clamp(preampDb, -24, 24);
                        sawAny = true;
                    }
                    else
                    {
                        skipped++;
                    }

                    continue;
                }

                if (!string.Equals(key, "Filter", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EqBand? band = ParseFilterLine(body, ref skipped);
                if (band == null)
                {
                    continue;
                }

                result.Bands.Add(band);
                imported++;
                sawAny = true;
            }

            if (!sawAny || imported == 0)
            {
                error = "没有识别到任何滤波段（Filter 行）。确认这是 Equalizer APO 的 config.txt？";
                return false;
            }

            result.Normalize();
            curve = result;
            return true;
        }

        /// <summary>解析一行 Filter 的正文（"ON PK Fc 1000 Hz Gain 3 dB Q 1"）。无法识别时返回 null。</summary>
        private static EqBand? ParseFilterLine(string body, ref int skipped)
        {
            string[] tokens = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            bool enabled = true;
            EqFilterType type = EqFilterType.Peaking;
            bool typeFound = false;
            double freq = double.NaN, gain = 0.0, q = double.NaN;

            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                string up = t.ToUpperInvariant();

                if (up == "ON") { enabled = true; continue; }
                if (up == "OFF") { enabled = false; continue; }

                // 关键字 + 紧随其后的数值
                if (string.Equals(up, "FC", StringComparison.Ordinal) && i + 1 < tokens.Length)
                {
                    if (TryParseNumber(tokens[i + 1], out freq)) i++;
                    continue;
                }

                if (string.Equals(up, "GAIN", StringComparison.Ordinal) && i + 1 < tokens.Length)
                {
                    if (TryParseNumber(tokens[i + 1], out gain)) i++;
                    continue;
                }

                if (string.Equals(up, "Q", StringComparison.Ordinal) && i + 1 < tokens.Length)
                {
                    if (TryParseNumber(tokens[i + 1], out q)) i++;
                    continue;
                }

                if (!typeFound && TryMapType(up, out EqFilterType mapped))
                {
                    type = mapped;
                    typeFound = true;
                    continue;
                }
            }

            if (!typeFound)
            {
                skipped++; // 未支持的类型（AP 全通等）
                return null;
            }

            if (double.IsNaN(freq))
            {
                skipped++;
                return null;
            }

            return new EqBand
            {
                Enabled = enabled,
                FilterType = type,
                FrequencyHz = Math.Clamp(freq, 20, 20000),
                GainDb = Math.Clamp(gain, -24, 24),
                Q = double.IsNaN(q) ? 1.0 : Math.Clamp(q, 0.1, 24)
            };
        }

        private static bool TryMapType(string upperToken, out EqFilterType type)
        {
            switch (upperToken)
            {
                case "PK": type = EqFilterType.Peaking; return true;
                case "LS": type = EqFilterType.LowShelf; return true;
                case "HS": type = EqFilterType.HighShelf; return true;
                case "LP": type = EqFilterType.LowPass; return true;
                case "HP": type = EqFilterType.HighPass; return true;
                case "NO": type = EqFilterType.Notch; return true;
                default: type = EqFilterType.Peaking; return false;
            }
        }

        private static string TypeCode(EqFilterType type) => type switch
        {
            EqFilterType.Peaking => "PK",
            EqFilterType.LowShelf => "LS",
            EqFilterType.HighShelf => "HS",
            EqFilterType.LowPass => "LP",
            EqFilterType.HighPass => "HP",
            EqFilterType.Notch => "NO",
            _ => "PK"
        };

        /// <summary>把曲线导出为 Equalizer APO 的 config.txt 文本。</summary>
        public static string Export(EqCurveState? curve)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Equalizer APO 配置 —— 由 CelesteMusicPlayer 导出");
            if (curve == null)
            {
                sb.AppendLine("Preamp: 0 dB");
                return sb.ToString();
            }

            sb.Append("Preamp: ").Append(FormatDb(curve.PreampDb)).AppendLine(" dB");

            foreach (EqBand b in curve.Bands)
            {
                if (b == null) continue;
                string onOff = b.Enabled ? "ON" : "OFF";
                string code = TypeCode(b.FilterType);
                sb.Append("Filter: ").Append(onOff).Append(' ').Append(code)
                  .Append(" Fc ").Append(FormatFreq(b.FrequencyHz)).Append(" Hz");

                // 低通 / 高通 / 切除在 APO 里没有 Gain 参数
                if (b.FilterType is EqFilterType.Peaking or EqFilterType.LowShelf or EqFilterType.HighShelf)
                {
                    sb.Append(" Gain ").Append(FormatDb(b.GainDb)).Append(" dB");
                }

                sb.Append(" Q ").Append(FormatQ(b.Q));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string FormatDb(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static string FormatQ(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string FormatFreq(double v)
        {
            // 频率多为整数，保留一位小数即可（APO 也接受小数）
            return Math.Abs(v % 1) < 0.05
                ? v.ToString("0", CultureInfo.InvariantCulture)
                : v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static bool TryParseNumber(string token, out double value)
        {
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>取一行里最后一个可解析的数值（兼容 "Preamp: 1 2 -6.5 dB" 这类带声道选择器的写法）。</summary>
        private static bool TryParseLastNumber(string body, out double value)
        {
            value = 0;
            bool found = false;
            foreach (string tok in body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseNumber(tok, out double v))
                {
                    value = v;
                    found = true;
                }
            }

            return found;
        }
    }
}
