using System;
using System.IO;
using System.Linq;
namespace CelesteMusicPlayer;
internal static class QuickStartupIntent
{
    internal static readonly string[] Extensions = { ".mp3", ".wav", ".m4a", ".flac", ".wma", ".ogg", ".aac", ".ape", ".wv", ".tta", ".mpc", ".tak", ".opus", ".dsf", ".dff", ".iso", ".mp2", ".amr", ".au", ".mod", ".s3m", ".xm", ".aif", ".aiff", ".oga", ".mka", ".cue", ".m3u", ".m3u8" };
    internal static bool IsPlayable(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    internal static string[] Files(string[] args) => args.Select(p => p.Trim().Trim('"')).Where(p => !p.StartsWith("-") && File.Exists(p) && IsPlayable(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
