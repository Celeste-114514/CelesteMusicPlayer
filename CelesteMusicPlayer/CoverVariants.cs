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
        /// 2026-10-19 由 96 提到 160：播放栏小封面显示 58px，屏幕 150% 缩放就是 87 物理像素、
        /// 200% 是 116、250% 是 145 —— 96 在 200% 以上会被放大，正好是用户报的
        /// 「播放栏封面糊，歌曲面板里的却清楚」（歌曲面板走 Album 320 档，所以清楚）。
        /// 提到 160 后 250% 也仍是缩小显示。改档位同时要把 CacheVersion 升上去，
        /// 否则磁盘里旧的 96 小图会一直命中，新档位永远生效不了。
        /// </summary>
        Thumb = 160,

        /// <summary>常规尺寸（歌单墙、专辑卡）。</summary>
        Album = 320,

        /// <summary>详情页、播放页大图。</summary>
        Large = 768,
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
