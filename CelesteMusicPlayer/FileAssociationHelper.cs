using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace CelesteMusicPlayer
{
    public static class FileAssociationHelper
    {
        public const string ProgId = "CelesteMusicPlayer.Audio";

        private static readonly string[] CommonAudioExtensions =
        {
            ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".wma", ".aac"
        };

        public static IReadOnlyList<string> CommonExtensions => CommonAudioExtensions;

        public static void Register(string executablePath, IEnumerable<string>? extensions = null)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw new FileNotFoundException("Executable not found.", executablePath);
            }

            string exe = Path.GetFullPath(executablePath);
            string icon = exe + ",0";

            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using RegistryKey progId = classes.CreateSubKey(ProgId);
            progId.SetValue(string.Empty, "Celeste Music Player Audio");
            progId.SetValue("FriendlyTypeName", "Celeste Music Player Audio");

            using RegistryKey defaultIcon = progId.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue(string.Empty, icon);

            using RegistryKey shell = progId.CreateSubKey(@"shell\open");
            shell.SetValue(string.Empty, "Play with Celeste Music Player");
            using RegistryKey command = shell.CreateSubKey("command");
            command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");

            foreach (string ext in extensions ?? CommonAudioExtensions)
            {
                string normalized = NormalizeExtension(ext);
                using RegistryKey extKey = classes.CreateSubKey(normalized);
                extKey.SetValue(string.Empty, ProgId);
            }
        }

        public static void Unregister(IEnumerable<string>? extensions = null)
        {
            using RegistryKey classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true)
                ?? Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            foreach (string ext in extensions ?? CommonAudioExtensions)
            {
                string normalized = NormalizeExtension(ext);
                try
                {
                    using RegistryKey? extKey = classes.OpenSubKey(normalized, writable: true);
                    if (extKey?.GetValue(string.Empty) as string == ProgId)
                    {
                        classes.DeleteSubKeyTree(normalized, throwOnMissingSubKey: false);
                    }
                }
                catch
                {
                }
            }

            try
            {
                classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }

        public static bool IsRegistered()
        {
            try
            {
                using RegistryKey? progId = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}");
                return progId != null;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeExtension(string ext)
        {
            ext = ext.Trim();
            if (!ext.StartsWith('.'))
            {
                ext = "." + ext;
            }

            return ext.ToLowerInvariant();
        }
    }
}
