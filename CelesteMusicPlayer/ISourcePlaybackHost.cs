using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 在线来源要跟宿主拿的唯一一样东西。
    /// 只有「取消下载」这一条：在途下载的取消令牌挂在播放域手里，
    /// 而播放域这一刀还没搬（见交接文档的剩余清单），所以只能借道。
    /// </summary>
    internal interface ISourcePlaybackHost
    {
        void CancelSourceDownload();
    }
}
