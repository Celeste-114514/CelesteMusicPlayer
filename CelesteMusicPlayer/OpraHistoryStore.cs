// OpraHistoryStore.cs
// 耳机校正（OPRA）页的「最近用过 / 收藏」持久化。
//
// 为什么单独一个 store 而不是塞进别的设置文件：这两份列表是「用户的使用痕迹」，
// 读写频繁（点一下曲线就写一次），不该和 DSP 参数、方案快照混在一处互相牵连。
// 存储：%LOCALAPPDATA%\CelesteMusicPlayer\opra-history.json
// 与项目其它 store 的口径一致：磁盘是真值，内存里留一份 cache，Load() 走 disk-backed cache，
// 避免每次刷新 UI 都读一遍盘（DspRackStore / EqCurveStore 都是这个套路）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    /// <summary>一条使用痕迹：足够把这条曲线直接重新找出来并应用，不存整份 band 数据。</summary>
    public sealed record OpraHistoryItem(
        string EqId, string ProductId, string ProductName, string VendorName,
        string Author, int BandCount, double PreampDb);

    /// <summary>OPRA 的最近/收藏列表。</summary>
    public sealed class OpraHistoryState
    {
        public List<OpraHistoryItem> Favorites { get; set; } = new();

        /// <summary>最近用过，最新的排在最前。</summary>
        public List<OpraHistoryItem> Recent { get; set; } = new();
    }

    public static class OpraHistoryStore
    {
        private const int RecentLimit = 10;

        private static OpraHistoryState? _cache;

        private static string FilePath
            => Path.Combine(AppSettingsStore.GetConfigDirectory(), "opra-history.json");

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>读取当前状态（内存优先；首次调用读盘，之后复用 cache）。</summary>
        public static OpraHistoryState Load()
        {
            if (_cache != null)
            {
                return _cache;
            }

            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    OpraHistoryState? st = JsonSerializer.Deserialize<OpraHistoryState>(json, Json);
                    if (st != null)
                    {
                        st.Favorites ??= new List<OpraHistoryItem>();
                        st.Recent ??= new List<OpraHistoryItem>();
                        Trim(st);
                        _cache = st;
                        return st;
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("OpraHistoryStore.Load", caught);
            }

            _cache = new OpraHistoryState();
            return _cache;
        }

        /// <summary>写盘 + 刷新内存 cache。</summary>
        public static void Save(OpraHistoryState state)
        {
            Trim(state);
            _cache = state;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Json));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("OpraHistoryStore.Save", caught);
            }
        }

        /// <summary>记一次「用过」（应用曲线的瞬间调用）；同一条曲线会被提到队首。</summary>
        public static void AddRecent(OpraHistoryItem item)
        {
            OpraHistoryState st = Load();
            st.Recent.RemoveAll(x => x.EqId == item.EqId);
            st.Recent.Insert(0, item);
            Save(st);
        }

        public static bool IsFavorite(string eqId)
            => Load().Favorites.Any(x => x.EqId == eqId);

        /// <summary>切换收藏状态，返回切换后是否处于收藏。</summary>
        public static bool ToggleFavorite(OpraHistoryItem item)
        {
            OpraHistoryState st = Load();
            int idx = st.Favorites.FindIndex(x => x.EqId == item.EqId);
            if (idx >= 0)
            {
                st.Favorites.RemoveAt(idx);
                Save(st);
                return false;
            }

            st.Favorites.Insert(0, item);
            Save(st);
            return true;
        }

        public static void RemoveFavorite(string eqId)
        {
            OpraHistoryState st = Load();
            if (st.Favorites.RemoveAll(x => x.EqId == eqId) > 0)
            {
                Save(st);
            }
        }

        private static void Trim(OpraHistoryState st)
        {
            if (st.Recent.Count > RecentLimit)
            {
                st.Recent.RemoveRange(RecentLimit, st.Recent.Count - RecentLimit);
            }
        }
    }
}
