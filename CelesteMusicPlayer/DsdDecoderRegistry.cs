using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DSD 解码器注册中心（插件机制）。
    /// 内置一个内建解析器（DSF/DFF → 1-bit，开箱即用）；同时允许注册外部解码器插件
    /// （实现 <see cref="IDsDDecoder"/>），安装时放入解码器插件的二进制/目录即可自动发现并优先使用。
    /// </summary>
    internal static class DsdDecoderRegistry
    {
        private static readonly List<IDsDDecoder> _decoders = new();
        private static bool _initialized;

        /// <summary>注册一个解码器插件（可在运行时动态加入）。</summary>
        public static void Register(IDsDDecoder decoder)
        {
            lock (_decoders)
            {
                if (decoder != null && !_decoders.Contains(decoder))
                {
                    _decoders.Add(decoder);
                }
            }
        }

        /// <summary>枚举当前可用的解码器描述（供设置界面展示"安装时选择"哪个）。</summary>
        public static IReadOnlyList<string> DecoderNames
        {
            get
            {
                EnsureInitialized();
                lock (_decoders)
                {
                    return _decoders.Select(d => d.GetType().Name).ToList();
                }
            }
        }

        /// <summary>为给定文件选一个能解码的插件（优先已注册/已发现的外部插件，最后内建保底）。</summary>
        public static IDsDDecoder? Resolve(string path)
        {
            EnsureInitialized();
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            lock (_decoders)
            {
                // 后注册的在最前 = 外部/首选插件优先；内建始终保障有兜底
                for (int i = _decoders.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (_decoders[i].CanDecode(path))
                        {
                            return _decoders[i];
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            // 保底：内建解析器
            Register(new BuiltInDsdDecoder());
        }
    }
}
