// ============================================================================
// CoverVariant / CoverSizing —— 封面档位真值与"只缩不放"几何（W6 第五小步）
//
// 2026-10-29 从 CoverArtService.cs 原样搬出。原因：CoverArtService 整个文件
// 引用了 WinRT 图像管线（BitmapDecoder/Encoder、BitmapImage），无法链接进
// 跨平台测试宿主；而"有哪些档位""源图要缩成多大"这两件事其实一点也不涉及
// 解码器。搬到这里后，界面侧和缩略图生成侧共用同一份真值，测试宿主也能钉住它。
//
// ⚠️ 数值与判定式必须和搬出前逐字等价，否则磁盘上已生成的缓存会和运行时
//    期望的档位对不上（Convention：改档位同时把 CoverCacheLayout.CacheVersion +1）。
// ============================================================================

using System;

namespace CelesteMusicPlayer
{
    /// <summary>封面档位（长边像素）。数值即该档的长边目标尺寸。</summary>
    internal enum CoverVariant
    {
        /// <summary>
        /// 列表小图 / 播放栏缩略图 / 迷你播放器封面。
        /// 演进：96（初版）→ 160（2026-10-19）→ 240（2026-11-01）。
        /// 2026-11-01：三档整体上调，并且**取档改成按物理像素算**（见 CoverCacheService.ForWebSize
        /// 与 WebShellWindow 的封面请求、MainWindow.CoverDecodeTargetPx）：
        /// 160 CSS px 的封面在 150% 缩放下要 240 物理像素、200% 要 320 —— 给 160 只能放大变糊。
        /// ⚠ 改档位必须同时把 CoverCacheLayout.CacheVersion +1，否则磁盘上的旧小图会一直命中。
        /// </summary>
        Thumb = 240,

        /// <summary>常规尺寸（歌单墙、专辑卡）。覆盖 480 物理像素以内的展示。</summary>
        Album = 480,

        /// <summary>详情页、播放页大图。1024 长边，2 倍屏下 512 CSS px 仍清晰。</summary>
        Large = 1024,
    }

    /// <summary>封面缩放几何。纯算术，不碰任何图像库。</summary>
    internal static class CoverSizing
    {
        /// <summary>
        /// 算出源图缩到<paramref name="longEdge"/>长边后的目标尺寸。
        /// 只缩不放：源图长边本来就小于目标档就原样返回（放大只会更糊）。
        /// </summary>
        public static (int Width, int Height) ComputeTargetSize(int sourceWidth, int sourceHeight, int longEdge)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return (0, 0);
            }

            double scale = Math.Min(1.0, (double)longEdge / Math.Max(sourceWidth, sourceHeight));
            int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            return (width, height);
        }

        /// <summary>该档位的长边像素。</summary>
        public static int LongEdge(CoverVariant variant) => (int)variant;
    }
}
