using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    public enum EqualizerPreset
    {
        Flat,
        Classical,
        Pop,
        Jazz,
        Rock,
        Soft,
        Bass
    }

    public sealed class EqualizerState
    {
        public const int BandCount = 10;

        public EqualizerPreset Preset { get; set; } = EqualizerPreset.Flat;

        /// <summary>10 段增益，范围 -15..15 dB。</summary>
        public double[] BandGains { get; set; } = CreateFlatBands();

        public static double[] CreateFlatBands() => Enumerable.Repeat(0.0, BandCount).ToArray();

        public static double[] GetPresetBands(EqualizerPreset preset) => preset switch
        {
            EqualizerPreset.Classical => new[] { 0.0, 0.0, 0.0, 0.0, 0.0, -2.0, -2.0, -2.0, -3.0, -3.0 },
            EqualizerPreset.Pop => new[] { -1.0, 2.0, 4.0, 4.0, 2.0, 0.0, -1.0, -1.0, -1.0, -1.0 },
            EqualizerPreset.Jazz => new[] { 0.0, 0.0, 1.0, 3.0, 3.0, 3.0, 2.0, 1.0, 1.0, 2.0 },
            EqualizerPreset.Rock => new[] { 4.0, 3.0, 2.0, 1.0, 0.0, 0.0, 1.0, 2.0, 3.0, 4.0 },
            EqualizerPreset.Soft => new[] { 2.0, 1.0, 0.0, 0.0, 0.0, 0.0, -2.0, -2.0, 1.0, 2.0 },
            EqualizerPreset.Bass => new[] { 6.0, 5.0, 4.0, 2.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
            _ => CreateFlatBands()
        };

        public void ApplyPreset(EqualizerPreset preset)
        {
            Preset = preset;
            BandGains = GetPresetBands(preset).ToArray();
        }

        public void Normalize()
        {
            if (BandGains == null || BandGains.Length != BandCount)
            {
                BandGains = CreateFlatBands();
            }

            for (int i = 0; i < BandCount; i++)
            {
                BandGains[i] = Math.Clamp(BandGains[i], -15, 15);
            }
        }
    }

    public static class EqualizerStore
    {
        private const string FileName = "equalizer.json";
        private static EqualizerState? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            string root;
            try
            {
                root = ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
            }

            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static EqualizerState Load()
        {
            lock (Gate)
            {
                if (_cache != null)
                {
                    return Clone(_cache);
                }

                try
                {
                    string path = GetFilePath();
                    if (File.Exists(path))
                    {
                        EqualizerState? loaded = JsonSerializer.Deserialize<EqualizerState>(File.ReadAllText(path));
                        _cache = Normalize(loaded ?? new EqualizerState());
                    }
                    else
                    {
                        _cache = new EqualizerState();
                    }
                }
                catch
                {
                    _cache = new EqualizerState();
                }

                return Clone(_cache);
            }
        }

        public static void Save(EqualizerState state)
        {
            lock (Gate)
            {
                _cache = Normalize(state ?? new EqualizerState());
                SaveCore(_cache);
            }
        }

        private static void SaveCore(EqualizerState state)
        {
            try
            {
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetFilePath(), json);
            }
            catch
            {
            }
        }

        private static EqualizerState Normalize(EqualizerState s)
        {
            s.Normalize();
            return s;
        }

        private static EqualizerState Clone(EqualizerState s) => new()
        {
            Preset = s.Preset,
            BandGains = s.BandGains.ToArray()
        };
    }
}
