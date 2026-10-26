using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 系统已安装字体族清单，给网页的字体选择器用。
    ///
    /// 为什么读注册表而不是列 Fonts 目录里的 .ttf：
    /// 文件名和字体族名不是一回事（msyh.ttc 里的族名是「Microsoft YaHei」）。
    /// 把文件名当字体名塞给 CSS，浏览器找不到就静默回退，用户会以为"选了没反应"。
    /// 注册表的值名里带的就是族名；一个 TTC 里有多个族时 Windows 用「 & 」把它们连起来。
    /// </summary>
    internal static class FontCatalog
    {
        /// <summary>常用字体前置，保证选择器一打开就能挑到像样的；注册表读不到时也不至于空着。</summary>
        private static readonly string[] Preferred =
        {
            "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "宋体", "黑体", "楷体",
            "Cascadia Mono", "Consolas", "Georgia", "Times New Roman"
        };

        private static string[]? _cache;
        private static readonly object Gate = new();

        /// <summary>
        /// 全部字体族，常用的排在前面，其余按名字排序。
        /// 结果缓存在内存里 —— 字体在一台机器上不会中途变。
        /// </summary>
        internal static string[] Families()
        {
            lock (Gate)
            {
                if (_cache != null) return _cache;

                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in new[]
                {
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Fonts"
                })
                {
                    ReadInto(Registry.LocalMachine, path, found);
                    ReadInto(Registry.CurrentUser, path, found);
                }

                var ordered = new List<string>();
                foreach (string name in Preferred)
                {
                    if (found.Remove(name)) ordered.Add(name);
                }
                ordered.AddRange(found.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));

                // 选择器里几百项就够用了；系统字体异常多时截断，避免快照过大
                _cache = ordered.Take(400).ToArray();
                return _cache;
            }
        }

        private static void ReadInto(RegistryKey root, string path, HashSet<string> into)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(path);
                if (key == null) return;
                foreach (string raw in key.GetValueNames())
                {
                    string name = raw.Trim();
                    if (name.Length == 0) continue;

                    // 「Arial (TrueType)」→ 「Arial」；去掉注册表里的格式后缀
                    int bracket = name.LastIndexOf('(');
                    if (bracket > 0) name = name.Substring(0, bracket).TrimEnd();
                    if (name.Length == 0) continue;

                    // 「SimSun & NSimSun」→ 两个族
                    foreach (string part in name.Split('&'))
                    {
                        string family = part.Trim();
                        // 「@Microsoft YaHei」是竖排版变体，普通文本用不到
                        if (family.Length == 0 || family.StartsWith("@", StringComparison.Ordinal)) continue;
                        if (family.Any(char.IsControl)) continue;
                        into.Add(family);
                    }
                }
            }
            catch (Exception caught)
            {
                // 读不到就少几个字体，不能因此让外观页打不开
                StartupLog.WriteException("FontCatalog.Read " + path, caught);
            }
        }
    }
}
