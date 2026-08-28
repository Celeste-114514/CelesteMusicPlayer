using System;
using System.IO;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 通用 JSON 状态文件读写 + 深拷贝工具，供各 <c>xxxStore</c> 复用，
    /// 消除每个 store 重复的「文件存在检查 / 反序列化 / 序列化写文件 / catch 吞异常 / 手写 Clone」样板。
    /// 路径语义沿用 AppSettingsStore 固定目录的文件对象；不负责缓存（缓存仍由各 store 自持）。
    /// </summary>
    internal static class JsonFile
    {
        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        /// <summary>读取 JSON 文件为 <typeparamref name="T"/>；文件缺失/解析失败返回 <paramref name="fallback"/>。</summary>
        public static T Read<T>(string path, T fallback)
            where T : class
        {
            try
            {
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path);
                    T? v = JsonSerializer.Deserialize<T>(raw);
                    if (v != null)
                    {
                        return v;
                    }
                }
            }
            catch
            {
                // 文件损坏或不可读 → 静默回退默认，避免崩溃。
            }

            return fallback;
        }

        /// <summary>把 <paramref name="value"/> 序列化为缩进 JSON 写入 <paramref name="path"/>（先建目录）。</summary>
        public static void Write<T>(string path, T value)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, JsonSerializer.Serialize(value, Indented));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("JsonFile.cs", caught); }
        }

        /// <summary>基于 JSON 往返的深拷贝：返回 <paramref name="value"/> 的独立副本，防止外部修改污染缓存。</summary>
        public static T DeepClone<T>(T value)
        {
            if (value == null)
            {
                return value;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Indented))!;
            }
            catch
            {
                return value;
            }
        }
    }
}
