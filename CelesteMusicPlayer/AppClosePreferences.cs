using System;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>关闭主窗口时的行为偏好。</summary>
    public enum CloseWindowAction
    {
        Ask = 0,
        MinimizeToTray = 1,
        Exit = 2
    }

    public sealed class AppClosePreferencesState
    {
        /// <summary>为 true 时不再弹出询问，直接执行 <see cref="PreferredAction"/>。</summary>
        public bool DontAskAgain { get; set; }

        /// <summary>下次不再询问时使用的动作（Tray / Exit）。</summary>
        public string PreferredAction { get; set; } = nameof(CloseWindowAction.MinimizeToTray);
    }

    /// <summary>关闭偏好：读写统一走 <see cref="AppSettingsStore"/>，并兼容旧 close-preferences.json。</summary>
    public static class AppClosePreferences
    {
        private const string LegacyFileName = "close-preferences.json";

        public static AppClosePreferencesState Load()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            if (string.Equals(settings.CloseAction, nameof(CloseWindowAction.Ask), StringComparison.OrdinalIgnoreCase))
            {
                return new AppClosePreferencesState { DontAskAgain = false };
            }

            return new AppClosePreferencesState
            {
                DontAskAgain = true,
                PreferredAction = string.Equals(
                    settings.CloseAction,
                    nameof(CloseWindowAction.Exit),
                    StringComparison.OrdinalIgnoreCase)
                    ? nameof(CloseWindowAction.Exit)
                    : nameof(CloseWindowAction.MinimizeToTray)
            };
        }

        public static void Save(AppClosePreferencesState state)
        {
            AppSettingsStore.Update(s =>
            {
                if (!state.DontAskAgain)
                {
                    s.CloseAction = nameof(CloseWindowAction.Ask);
                    return;
                }

                s.CloseAction = string.Equals(
                    state.PreferredAction,
                    nameof(CloseWindowAction.Exit),
                    StringComparison.OrdinalIgnoreCase)
                    ? nameof(CloseWindowAction.Exit)
                    : nameof(CloseWindowAction.MinimizeToTray);
            });
        }

        public static CloseWindowAction ResolveAction(AppClosePreferencesState state)
        {
            if (!state.DontAskAgain)
            {
                return CloseWindowAction.Ask;
            }

            return string.Equals(state.PreferredAction, nameof(CloseWindowAction.Exit), StringComparison.OrdinalIgnoreCase)
                ? CloseWindowAction.Exit
                : CloseWindowAction.MinimizeToTray;
        }

        /// <summary>仅读取旧文件（供设置迁移）。</summary>
        internal static AppClosePreferencesState LoadLegacyFileOnly()
        {
            try
            {
                string path = GetLegacyFilePath();
                if (!File.Exists(path))
                {
                    return new AppClosePreferencesState();
                }

                return JsonSerializer.Deserialize<AppClosePreferencesState>(File.ReadAllText(path))
                       ?? new AppClosePreferencesState();
            }
            catch
            {
                return new AppClosePreferencesState();
            }
        }

        private static string GetLegacyFilePath()
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
            return Path.Combine(root, LegacyFileName);
        }
    }
}
