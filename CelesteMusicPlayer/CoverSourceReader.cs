// ============================================================================
// CoverSourceReader —— 从音频文件取"封面原始字节"（W6 第五小步）
//
// 2026-10-29 从 MainWindow.Misc.cs 的 TryLoadInnerCover / TryLoadOuterCover /
// TryMeasureImageSize 原样搬出。搬出前这三段住在主窗口的 partial class 里，
// 想复用就得把整个 WinUI 主窗口拖进来 —— 而它们其实只依赖 TagLib# 与文件 IO，
// 在 Linux/macOS 上一样能跑（TagLib# 是纯托管实现）。
//
// 对应 ECHO 架构里的"native 扫描器只提字节"那一层：这里只负责把内嵌图或
// 同目录图**原字节**抠出来，不解码、不缩放、不落盘。解码缩放是 CoverArtService
// 的事（那半边才绑 Windows）。
//
// 取图规则（与搬出前逐字等价）：
//   · 内嵌优先：标签里有几张图时**取像素最大的那张**，同尺寸才优先 FrontCover；
//     源文件里压根只有 300×300 的，这条规则救不了（那是源的问题）。
//   · 外置兜底：folder.jpg/png → cover.jpg/png → 同名 .jpg/.png。
//   · 两者之间按设置 UseInnerCoverFirst 决定先后，缺哪边就用另一边。
//
// 测尺寸只看文件头（JPEG/PNG/GIF/BMP/WebP），不解码整图 —— 曲库滚动时这一步
// 会被调上千次，整图解码会直接卡死列表。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>一张候选内嵌图的元信息（把 TagLib 的 IPicture 拆成纯数据，方便自测）。</summary>
    internal readonly struct CoverPictureCandidate
    {
        public CoverPictureCandidate(byte[]? data, bool isFrontCover)
        {
            Data = data;
            IsFrontCover = isFrontCover;
        }

        public byte[]? Data { get; }

        public bool IsFrontCover { get; }
    }

    internal static class CoverSourceReader
    {
        // =====================================================================
        // 纯逻辑：挑图 / 定优先级（自检可直接测，不碰文件）
        // =====================================================================

        /// <summary>
        /// 从候选内嵌图里挑"像素最大的那张"的下标；同尺寸才优先 FrontCover。
        /// 一个都量不出尺寸时按原实现退回第 0 张（而不是返回 -1 让调用方改行为）。
        /// 返回 -1 只表示候选列表为空。
        /// </summary>
        public static int PickBestPictureIndex(IReadOnlyList<CoverPictureCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return -1;
            }

            int bestIndex = -1;
            int bestPx = -1;
            bool bestIsFront = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                byte[]? raw = candidates[i].Data;
                if (raw == null || raw.Length == 0)
                {
                    continue;
                }

                bool isFront = candidates[i].IsFrontCover;
                int px = TryMeasureImageSize(raw, out int w, out int h) ? Math.Max(w, h) : 0;

                if (bestIndex < 0 || px > bestPx || (px == bestPx && isFront && !bestIsFront))
                {
                    bestIndex = i;
                    bestPx = px;
                    bestIsFront = isFront;
                }
            }

            // 原实现：best ?? pictures[0] —— 全部候选都没有有效字节时仍然取第 0 张
            return bestIndex >= 0 ? bestIndex : 0;
        }

        /// <summary>
        /// 内嵌图与外置图之间选一个：preferInner 为真则内嵌优先，为假则外置优先；
        /// 优先的那边没有就用另一边。两边都没有返回 null。
        /// </summary>
        public static byte[]? PickSource(byte[]? inner, byte[]? outer, bool preferInner)
        {
            byte[]? first = preferInner ? inner : outer;
            byte[]? second = preferInner ? outer : inner;
            if (first is { Length: > 0 })
            {
                return first;
            }

            return second is { Length: > 0 } ? second : null;
        }

        // =====================================================================
        // 读文件：内嵌 / 外置 / 组合
        // =====================================================================

        /// <summary>
        /// 提取封面源字节（与 MainWindow.ExtractCoverBytes 搬出前的取图规则一致）。
        /// 可在后台线程调用。取不到返回 null。
        /// </summary>
        public static byte[]? Read(string audioPath, bool preferInnerCover)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            byte[]? inner = ReadInner(audioPath);
            byte[]? outer = ReadOuter(audioPath);
            return PickSource(inner, outer, preferInnerCover);
        }

        /// <summary>只读标签里内嵌的那张（取像素最大的）。没有返回 null。</summary>
        public static byte[]? ReadInner(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            try
            {
                using TagLib.File tagFile = TagLib.File.Create(audioPath);
                TagLib.IPicture[]? pictures = tagFile.Tag.Pictures;
                if (pictures == null || pictures.Length == 0)
                {
                    return null;
                }

                // 2026-10-16 K2「歌曲面板封面还是糊」第二轮：
                // 原来死取 FrontCover —— 但不少文件的内嵌图里 FrontCover 恰恰是 200×200 的小缩略图
                // （真正的 1000×1000 大图挂在别的 PictureType 上），取到小的那张放到 320px 的框里必然糊。
                // 改成**按像素尺寸取最大的一张**，同尺寸时才优先 FrontCover。
                // ⚠️ 只解决"取错了图"；源文件里压根只有 300×300 的情况无解，那要靠在线高清图源。
                var candidates = new CoverPictureCandidate[pictures.Length];
                for (int i = 0; i < pictures.Length; i++)
                {
                    TagLib.IPicture picture = pictures[i];
                    byte[]? raw = picture.Data?.Data;
                    candidates[i] = new CoverPictureCandidate(raw, picture.Type == TagLib.PictureType.FrontCover);
                }

                int index = PickBestPictureIndex(candidates);
                if (index < 0)
                {
                    return null;
                }

                byte[]? data = candidates[index].Data;
                return data is { Length: > 0 } ? data : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"提取封面失败 [{audioPath}]: {ex.Message}");
                return null;
            }
        }

        /// <summary>只读同目录的 folder / cover / 同名图片。没有返回 null。</summary>
        public static byte[]? ReadOuter(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            try
            {
                string dir = Path.GetDirectoryName(audioPath) ?? string.Empty;
                string name = Path.GetFileNameWithoutExtension(audioPath);
                string[] candidates =
                {
                    Path.Combine(dir, "folder.jpg"),
                    Path.Combine(dir, "folder.png"),
                    Path.Combine(dir, "cover.jpg"),
                    Path.Combine(dir, "cover.png"),
                    Path.Combine(dir, name + ".jpg"),
                    Path.Combine(dir, name + ".png")
                };

                foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        return System.IO.File.ReadAllBytes(candidate);
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("CoverSourceReader.ReadOuter", caught);
            }

            return null;
        }

        // =====================================================================
        // 文件头测尺寸（逐字搬自 MainWindow.Misc.cs）
        // =====================================================================

        /// <summary>
        /// 从图片字节头解析宽高（JPEG/PNG/BMP/GIF/WebP），不解码整图，用于「按显示尺寸解码」。
        /// 解析不了返回 false，调用方按全分辨率解码处理（行为与从前一致）。
        /// </summary>
        public static bool TryMeasureImageSize(byte[]? data, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (data == null || data.Length < 16)
            {
                return false;
            }

            try
            {
                // PNG：\x89PNG\r\n\x1a\n + IHDR（宽高在 16..24，大端）
                if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                    && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A
                    && data.Length >= 24)
                {
                    width = (int)(((uint)data[16] << 24) | ((uint)data[17] << 16) | ((uint)data[18] << 8) | data[19]);
                    height = (int)(((uint)data[20] << 24) | ((uint)data[21] << 16) | ((uint)data[22] << 8) | data[23]);
                    return width > 0 && height > 0;
                }

                // GIF："GIF8..." 逻辑屏幕宽高在 6..10（小端）
                if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data.Length >= 10)
                {
                    width = data[6] | (data[7] << 8);
                    height = data[8] | (data[9] << 8);
                    return width > 0 && height > 0;
                }

                // BMP："BM"，DIB 头宽高在 18..26（有符号，向下位图可能为负，取绝对值）
                if (data[0] == 0x42 && data[1] == 0x4D && data.Length >= 26)
                {
                    width = Math.Abs(data[18] | (data[19] << 8) | (data[20] << 16) | (data[21] << 24));
                    height = Math.Abs(data[22] | (data[23] << 8) | (data[24] << 16) | (data[25] << 24));
                    return width > 0 && height > 0;
                }

                // WebP：RIFF????WEBP
                if (data.Length >= 30
                    && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                    && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
                {
                    string chunk = System.Text.Encoding.ASCII.GetString(data, 12, 4);
                    if (chunk == "VP8 ")
                    {
                        width = (data[26] | (data[27] << 8)) & 0x3FFF;
                        height = (data[28] | (data[29] << 8)) & 0x3FFF;
                        return width > 0 && height > 0;
                    }
                    if (chunk == "VP8L" && data.Length >= 25)
                    {
                        int bits = data[21] | (data[22] << 8) | (data[23] << 16) | (data[24] << 24);
                        width = (bits & 0x3FFF) + 1;
                        height = ((bits >> 14) & 0x3FFF) + 1;
                        return width > 0 && height > 0;
                    }
                    if (chunk == "VP8X" && data.Length >= 30)
                    {
                        width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
                        height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
                        return width > 0 && height > 0;
                    }
                    return false;
                }

                // JPEG：逐段扫到 SOF0~SOF15（排除 DHT/DAC/RST 等非尺寸段）
                if (data[0] == 0xFF && data[1] == 0xD8)
                {
                    int i = 2;
                    while (i + 9 < data.Length)
                    {
                        if (data[i] != 0xFF)
                        {
                            i++;
                            continue;
                        }
                        byte marker = data[i + 1];
                        bool isSof = marker >= 0xC0 && marker <= 0xCF
                            && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                        if (isSof)
                        {
                            height = (data[i + 5] << 8) | data[i + 6];
                            width = (data[i + 7] << 8) | data[i + 8];
                            return width > 0 && height > 0;
                        }
                        if (marker == 0xFF)
                        {
                            i++;
                            continue;
                        }
                        if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7))
                        {
                            i += 2;
                            continue;
                        }
                        int seg = (data[i + 2] << 8) | data[i + 3];
                        i += 2 + seg;
                    }
                }
            }
            catch
            {
                // 解析失败按未知尺寸处理
            }

            return false;
        }
    }
}
