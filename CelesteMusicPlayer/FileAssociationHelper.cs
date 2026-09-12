using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace CelesteMusicPlayer
{
    public static class FileAssociationHelper
    {
        public const string ProgId = "CelesteMusicPlayer.Audio";

        /// <summary>
        /// 注册的文件类型。刻意共用 QuickPlayLauncher.PlayableExtensions，
        /// 保证「关联了哪些格式」和「双击能播哪些格式」永远是一份清单，不会出现关联了却播不了。
        /// </summary>
        private static readonly string[] CommonAudioExtensions = QuickPlayLauncher.PlayableExtensions;

        public static IReadOnlyList<string> CommonExtensions => CommonAudioExtensions;

        /// <summary>
        /// 推荐默认勾选的格式：日常最常遇到的一批。
        ///
        /// 为什么不默认全选：.iso / .cue / .mod 这类格式关联过来会抢别的软件的饭碗
        /// （比如 .iso 本来是虚拟光驱的），应该让用户明确点一下才生效。
        /// </summary>
        public static readonly string[] RecommendedExtensions =
        {
            ".mp3", ".m4a", ".aac", ".wma", ".wav", ".flac", ".ogg", ".opus"
        };

        /// <summary>
        /// 读出当前已经关联到本程序的扩展名（用于勾选框回显「现在关联了哪些」）。
        /// 判据：HKCU\Software\Classes\.xxx 的默认值等于本程序的 ProgId。
        /// </summary>
        public static List<string> GetAssociatedExtensions()
        {
            var result = new List<string>();
            try
            {
                using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
                if (classes == null) return result;

                foreach (string ext in CommonAudioExtensions)
                {
                    string normalized = NormalizeExtension(ext);
                    using RegistryKey? extKey = classes.OpenSubKey(normalized);
                    if (extKey?.GetValue(string.Empty) as string == ProgId)
                    {
                        result.Add(normalized);
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationHelper.cs", caught);
            }

            return result;
        }

        /// <summary>
        /// 只解除这些扩展名的关联，保留 ProgId 本身与其它扩展名。
        ///
        /// 用途：用户在清单里取消勾选了某几个格式。这种情况**不能**直接调 <see cref="Unregister(IEnumerable{string}?)"/>，
        /// 那会把 ProgId 整个删掉，连带刚注册好的格式一起失效。
        /// 另外只删"默认值指向本程序"的键，避免把用户原本指向别的播放器的关联误删。
        /// </summary>
        public static void UnregisterExtensions(IEnumerable<string> extensions)
        {
            try
            {
                using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

                foreach (string ext in extensions)
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
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationHelper.cs", caught); }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationHelper.cs", caught);
            }
        }

        public static void Register(string executablePath, IEnumerable<string>? extensions = null)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw new FileNotFoundException("Executable not found.", executablePath);
            }

            string exe = Path.GetFullPath(executablePath);

            // 打包版（VS 调试用的 Debug 输出）不能被注册表文件关联启动：
            // 这种 exe 依赖 MSIX 包标识，直接由 shell 拉起会静默退出（表现就是"双击没反应"），
            // 注册了也只是白写注册表。这里直接拒绝，让上层引导用户换成免安装版/安装版。
            if (IsPackagedLayout(exe))
            {
                throw new InvalidOperationException(
                    "这个路径下的是打包（调试）版本，Windows 不允许它作为文件的默认打开程序，"
                    + "双击会没有反应。请改用免安装版或安装后的正式版本。");
            }

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
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationHelper.cs", caught); }
            }

            try
            {
                classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationHelper.cs", caught); }
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

        /// <summary>
        /// 读出注册表里文件关联当前指向的 exe 路径（没注册过 / 读不出来返回 null）。
        ///
        /// 用途：判断「当前关联有没有指到一个用不了的程序上」——
        /// 把文件关联手动设成调试（打包）版的 exe 是双击音频没反应的典型原因，
        /// 设置界面据此给出明确提示，而不是让用户对着"没反应"干瞪眼。
        /// </summary>
        public static string? GetRegisteredExecutable()
        {
            try
            {
                using RegistryKey? command = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\{ProgId}\shell\open\command");

                if (command?.GetValue(string.Empty) as string is not string line
                    || string.IsNullOrWhiteSpace(line))
                {
                    return null;
                }

                // 值形如 "C:\...\CelesteMusicPlayer.exe" "%1"：取引号里的那段
                string text = line.Trim();
                if (text.StartsWith('"'))
                {
                    int end = text.IndexOf('"', 1);
                    return end > 1 ? text.Substring(1, end - 1) : null;
                }

                int space = text.IndexOf(' ');
                return space > 0 ? text.Substring(0, space) : text;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationHelper.cs", caught);
                return null;
            }
        }

        /// <summary>
        /// exe 是不是 MSIX 打包布局里的可执行文件（判据：同目录存在 AppxManifest.xml）。
        ///
        /// 为什么需要这个判断：打包版 exe 依赖"应用包标识"才能启动，
        /// Windows 无法通过注册表的 shell\open\command 拉起它 —— 双击只会静默退出，没有任何反应。
        /// VS 里按 F5 调试（Debug 配置）产出的就是这种版本，所以它天然不能用来做文件关联。
        /// 只有免安装版（WindowsPackageType=None，Release 或发布包）和安装后的正式版才可以。
        /// </summary>
        public static bool IsPackagedLayout(string executablePath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
                return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "AppxManifest.xml"));
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationHelper.cs", caught);
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
