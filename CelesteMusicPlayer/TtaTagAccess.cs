using System;
using System.IO;
using IOFile = System.IO.File;
using TagLib;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// TTA（True Audio）标签读写 —— 2026-10-14 用户选定的「方案 1」。
    ///
    /// 背景：底层库 TagLib 没有 TTA 容器解析器，<see cref="File.Create(string)"/> 对 .tta
    /// 直接抛 UnsupportedFormatException，所以标签面板以前一个 TTA 文件都读不出来
    /// （实测 10 个样本全失败）。但 TTA 的标签本身就是两种标准格式：
    ///  · ID3v2（可能有）贴在文件最前面，以 "ID3" 开头，长度在头 4 字节的 syncsafe 里；
    ///  · APEv2（可能有）贴在文件最后面，尾 32 字节是 footer（"APETAGEX" 开头）。
    /// 所以这里自己按偏移把标签字节切出来，交给 TagLib 现成的 Id3v2.Tag / Ape.Tag 解析，
    /// 不自己写帧解析（这就是"方案 1：直接用底层库的读取器"）。
    ///
    /// 写入口径：**APEv2 是唯一真相**。写入 = 重写文件（音频数据原样搬，尾部换成新 APE
    /// 标签）；如果文件头部原本有 ID3v2，会把它的值并进模型后整段剥掉，避免两套标签
    /// 互相遮挡（读的时候也是 APE 优先、ID3v2 只补空缺）。写盘走临时文件 + 原子替换，
    /// 中途失败不动原文件。
    /// </summary>
    internal static class TtaTagAccess
    {
        /// <summary>是不是 .tta 文件（大小写不敏感）。</summary>
        public static bool IsTtaPath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".tta", StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        // 读
        // =====================================================================

        /// <summary>
        /// 读 TTA 的标签。任何异常都吞掉返回空模型（调用方按"读不到"处理，
        /// 但绝不让一个格式问题把整个导入搞炸）。读不到任何标签时返回的模型
        /// 所有字段为空——上层显示"-"即可，不算失败。
        /// </summary>
        public static TagEditModel Read(string path)
        {
            TagEditModel model = new();
            try
            {
                using FileStream stream = IOFile.OpenRead(path);

                // 1) 头部 ID3v2（先读，值作底；APEv2 会覆盖它）
                TagLib.Id3v2.Tag? id3 = ReadHeadId3v2(stream);
                if (id3 != null)
                {
                    ApplyToModel(model, id3);
                    model.Bpm = string.IsNullOrEmpty(model.Bpm) ? ReadId3v2Bpm(id3) : model.Bpm;
                }

                // 2) 尾部 APEv2（TTA 的事实标准，优先）
                TagLib.Ape.Tag? ape = ReadTailApe(stream);
                if (ape != null)
                {
                    ApplyToModel(model, ape);
                    if (ape.BeatsPerMinute > 0)
                    {
                        model.Bpm = ape.BeatsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TtaTagAccess.Read", caught);
            }

            return model;
        }

        /// <summary>把 TagLib 的标准标签属性映射进编辑模型（ID3v2 和 APEv2 都是 TagLib.Tag，同一套写法）。</summary>
        private static void ApplyToModel(TagEditModel model, TagLib.Tag tag)
        {
            model.Title = tag.Title ?? string.Empty;
            model.Performers = Clean(tag.Performers);
            model.Album = tag.Album ?? string.Empty;
            model.AlbumArtists = Clean(tag.AlbumArtists);
            model.Year = tag.Year;
            model.Track = tag.Track;
            model.TrackCount = tag.TrackCount;
            model.Disc = tag.Disc;
            model.DiscCount = tag.DiscCount;
            model.Genres = Clean(tag.Genres);
            model.Composers = Clean(tag.Composers);
            model.Comment = tag.Comment ?? string.Empty;
            model.Lyrics = tag.Lyrics ?? string.Empty;
        }

        private static string[] Clean(string[]? values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<string>();
            }

            System.Collections.Generic.List<string> list = new();
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    list.Add(value.Trim());
                }
            }

            return list.ToArray();
        }

        /// <summary>读头部 ID3v2：流当前位置无所谓（内部自己 seek 回 0）。没有 / 不是就返回 null。</summary>
        private static TagLib.Id3v2.Tag? ReadHeadId3v2(FileStream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            byte[] head = new byte[10];
            if (stream.Read(head, 0, 10) != 10)
            {
                return null;
            }

            if (head[0] != 'I' || head[1] != 'D' || head[2] != '3')
            {
                return null;
            }

            // syncsafe 长度：4 字节，每字节只用低 7 位
            int size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
            bool hasFooter = (head[5] & 0x10) != 0;
            int total = 10 + size + (hasFooter ? 10 : 0);
            if (total <= 10 || total > stream.Length)
            {
                return null;
            }

            byte[] bytes = new byte[total];
            stream.Seek(0, SeekOrigin.Begin);
            if (stream.Read(bytes, 0, total) != total)
            {
                return null;
            }

            return new TagLib.Id3v2.Tag(new ByteVector(bytes));
        }

        /// <summary>读尾部 APEv2：返回 null = 没有 APE 标签。</summary>
        private static TagLib.Ape.Tag? ReadTailApe(FileStream stream)
        {
            if (stream.Length < 32)
            {
                return null;
            }

            stream.Seek(stream.Length - 32, SeekOrigin.Begin);
            byte[] footer = new byte[32];
            if (stream.Read(footer, 0, 32) != 32)
            {
                return null;
            }

            if (System.Text.Encoding.ASCII.GetString(footer, 0, 8) != "APETAGEX")
            {
                return null;
            }

            int tagSize = BitConverter.ToInt32(footer, 12);
            int flags = BitConverter.ToInt32(footer, 20);

            // flags 位 31 = 标签带 32 字节头（头在项前面，尾的 tagSize 不含头）
            bool hasHeader = (flags & unchecked((int)0x80000000)) != 0;
            int total = tagSize + (hasHeader ? 32 : 0);
            if (total <= 32 || total > stream.Length)
            {
                return null;
            }

            byte[] bytes = new byte[total];
            stream.Seek(stream.Length - total, SeekOrigin.Begin);
            if (stream.Read(bytes, 0, total) != total)
            {
                return null;
            }

            return new TagLib.Ape.Tag(new ByteVector(bytes));
        }

        /// <summary>ID3v2 的 BPM 在 TBPM 帧里（TagLib 没有直接属性，按帧读）。</summary>
        private static string ReadId3v2Bpm(TagLib.Id3v2.Tag tag)
        {
            try
            {
                foreach (TagLib.Id3v2.Frame frame in tag.GetFrames("TBPM"))
                {
                    if (frame is TagLib.Id3v2.TextInformationFrame text && text.Text.Length > 0)
                    {
                        return string.Join("; ", text.Text);
                    }
                }
            }
            catch
            {
                // 读不到 BPM 不影响别的字段
            }

            return string.Empty;
        }

        // =====================================================================
        // 写
        // =====================================================================

        /// <summary>
        /// 把模型整体写进 TTA（保存按钮走这条）。整份覆盖：模型里空的字段 = 清空该标签。
        /// 临时文件 + 原子替换，失败返回 false 且原文件不动。
        /// </summary>
        public static bool Write(string path, TagEditModel model)
        {
            return WriteTag(path, tag =>
            {
                tag.Title = model.Title ?? string.Empty;
                tag.Performers = model.Performers ?? Array.Empty<string>();
                tag.Album = model.Album ?? string.Empty;
                tag.AlbumArtists = model.AlbumArtists ?? Array.Empty<string>();
                tag.Year = model.Year;
                tag.Track = model.Track;
                tag.TrackCount = model.TrackCount;
                tag.Disc = model.Disc;
                tag.DiscCount = model.DiscCount;
                tag.Genres = model.Genres ?? Array.Empty<string>();
                tag.Composers = model.Composers ?? Array.Empty<string>();
                tag.Comment = model.Comment ?? string.Empty;
                tag.Lyrics = model.Lyrics ?? string.Empty;
                WriteBpm(tag, model.Bpm ?? string.Empty);
            });
        }

        /// <summary>写 BPM：APEv2 有现成属性（0 = 删掉这一项，不写空壳）。</summary>
        private static void WriteBpm(TagLib.Ape.Tag tag, string bpm)
        {
            if (uint.TryParse(bpm, out uint value))
            {
                tag.BeatsPerMinute = value;
            }
            else
            {
                tag.BeatsPerMinute = 0;
            }
        }

        /// <summary>
        /// 通用写入口：读现有 APE 标签（没有就新建）→ 交给 mutate 改 → 重写文件。
        /// 封面读写也走这条（只动 Pictures，文字标签原样保留）。
        /// </summary>
        private static bool WriteTag(string path, Action<TagLib.Ape.Tag> mutate)
        {
            string temp = path + ".tta-tmp";
            try
            {
                // ① 读现有标签 + 算音频数据范围。⚠ 这段必须短开短关：File.Replace 要求
                //    替换时原文件没有任何流占着（2026-10-14 实测：外层 using 没关就
                //    Replace，直接 IOException "used by another process"——占用它的就是自己）。
                TagLib.Ape.Tag tag;
                int payloadStart;
                int payloadEnd;
                using (FileStream stream = IOFile.OpenRead(path))
                {
                    tag = ReadTailApe(stream) ?? new TagLib.Ape.Tag();
                    payloadStart = HeadId3v2Length(stream);
                    payloadEnd = (int)(stream.Length - TailApeLength(stream));
                }

                if (payloadEnd <= payloadStart)
                {
                    global::CelesteMusicPlayer.StartupLog.Write(
                        "TtaTagAccess: 音频数据范围异常（start=" + payloadStart + " end=" + payloadEnd + "），放弃写入 " + path);
                    return false;
                }

                mutate(tag);
                ByteVector rendered = tag.Render();

                // ② 音频数据原样搬进临时文件，尾部拼上新标签
                using (FileStream input = IOFile.OpenRead(path))
                using (FileStream output = new FileStream(temp, FileMode.Create, FileAccess.Write))
                {
                    input.Seek(payloadStart, SeekOrigin.Begin);
                    byte[] buffer = new byte[81920];
                    long remaining = payloadEnd - payloadStart;
                    while (remaining > 0)
                    {
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0)
                        {
                            break;
                        }

                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }

                    output.Write(rendered.Data, 0, rendered.Count);
                }

                // ③ 原子替换：此刻原文件上没有流，要么是旧的要么是新的，不会半截
                IOFile.Replace(temp, path, null);
                return true;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TtaTagAccess.WriteTag", caught);
                try
                {
                    if (IOFile.Exists(temp))
                    {
                        IOFile.Delete(temp);
                    }
                }
                catch
                {
                    // 清理失败不掩盖原始错误
                }

                return false;
            }
        }

        /// <summary>头部 ID3v2 标签占多少字节（没有 = 0）。</summary>
        private static int HeadId3v2Length(FileStream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            byte[] head = new byte[10];
            if (stream.Read(head, 0, 10) != 10)
            {
                return 0;
            }

            if (head[0] != 'I' || head[1] != 'D' || head[2] != '3')
            {
                return 0;
            }

            int size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
            bool hasFooter = (head[5] & 0x10) != 0;
            return 10 + size + (hasFooter ? 10 : 0);
        }

        /// <summary>尾部 APE 标签占多少字节（没有 = 0）。</summary>
        private static int TailApeLength(FileStream stream)
        {
            if (stream.Length < 32)
            {
                return 0;
            }

            stream.Seek(stream.Length - 32, SeekOrigin.Begin);
            byte[] footer = new byte[32];
            if (stream.Read(footer, 0, 32) != 32)
            {
                return 0;
            }

            if (System.Text.Encoding.ASCII.GetString(footer, 0, 8) != "APETAGEX")
            {
                return 0;
            }

            int tagSize = BitConverter.ToInt32(footer, 12);
            int flags = BitConverter.ToInt32(footer, 20);
            bool hasHeader = (flags & unchecked((int)0x80000000)) != 0;
            return tagSize + (hasHeader ? 32 : 0);
        }

        // =====================================================================
        // 封面（右键菜单那套接口对 TTA 的对应实现）
        // =====================================================================

        /// <summary>读第一张内嵌封面；没有返回 null。</summary>
        public static byte[]? ReadCoverBytes(string path)
        {
            try
            {
                using FileStream stream = IOFile.OpenRead(path);
                TagLib.Ape.Tag? ape = ReadTailApe(stream);
                if (ape?.Pictures is { Length: > 0 } pictures)
                {
                    return pictures[0].Data?.Data;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TtaTagAccess.ReadCoverBytes", caught);
            }

            return null;
        }

        /// <summary>替换封面（整份换掉，不是追加）。</summary>
        public static bool SetCoverBytes(string path, byte[] bytes)
        {
            return WriteTag(path, tag =>
            {
                TagLib.Picture picture = new TagLib.Picture
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = GuessMime(bytes),
                    Data = new ByteVector(bytes),
                };
                tag.Pictures = new TagLib.IPicture[] { picture };
            });
        }

        /// <summary>清掉内嵌封面（文字标签不动）。</summary>
        public static bool ClearCover(string path)
        {
            return WriteTag(path, tag => { tag.Pictures = Array.Empty<TagLib.IPicture>(); });
        }

        /// <summary>按文件头几个字节猜 MIME（APE 封面项要带 MIME）。</summary>
        private static string GuessMime(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
            {
                return "image/png";
            }

            return "application/octet-stream";
        }
    }
}
