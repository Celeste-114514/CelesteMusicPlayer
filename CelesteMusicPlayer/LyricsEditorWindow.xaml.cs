using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>LRC 歌词编辑：时间偏移、保存 .lrc、嵌入音频。</summary>
    public sealed partial class LyricsEditorWindow : Window
    {
        private static readonly Regex TimestampRegex = new(
            @"\[(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?\]",
            RegexOptions.Compiled);

        private readonly string _audioPath;
        private string? _lrcPath;

        public LyricsEditorWindow(string audioPath)
        {
            _audioPath = audioPath ?? string.Empty;
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "歌词编辑";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new SizeInt32(760, 640));

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();
            LoadLyrics();
        }

        public static void Show(string audioPath)
        {
            var window = new LyricsEditorWindow(audioPath);
            window.Activate();
        }

        private void ConfigureTitleBarButtons()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        private void ApplyBackdropFromSettings()
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (s.EnableFrostedGlass)
            {
                FrostedGlass.ApplyWindowBackdrop(this);
            }
            else
            {
                SystemBackdrop = null;
            }
        }

        private void LoadLyrics()
        {
            AudioPathText.Text = _audioPath;
            _lrcPath = FindLrcPath(_audioPath);
            if (_lrcPath != null && File.Exists(_lrcPath))
            {
                try
                {
                    LyricsTextBox.Text = File.ReadAllText(_lrcPath, Encoding.UTF8);
                    return;
                }
                catch
                {
                }
            }

            LyricsTextBox.Text = string.Empty;
        }

        private static string? FindLrcPath(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            string dir = Path.GetDirectoryName(audioPath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(audioPath);
            string candidate = Path.Combine(dir, name + ".lrc");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string alt = Path.ChangeExtension(audioPath, ".lrc");
            return File.Exists(alt) ? alt : candidate;
        }

        internal static string ShiftLrcTimestamps(string content, double deltaSeconds)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            return TimestampRegex.Replace(content, match =>
            {
                if (!TryParseTimestamp(match, out double seconds))
                {
                    return match.Value;
                }

                double shifted = Math.Max(0, seconds + deltaSeconds);
                return FormatTimestamp(shifted);
            });
        }

        private static bool TryParseTimestamp(Match match, out double totalSeconds)
        {
            totalSeconds = 0;
            if (!int.TryParse(match.Groups[1].Value, out int min) ||
                !int.TryParse(match.Groups[2].Value, out int sec))
            {
                return false;
            }

            double ms = 0;
            if (match.Groups[3].Success)
            {
                string frac = match.Groups[3].Value;
                if (frac.Length == 1)
                {
                    ms = int.Parse(frac, CultureInfo.InvariantCulture) * 100;
                }
                else if (frac.Length == 2)
                {
                    ms = int.Parse(frac, CultureInfo.InvariantCulture) * 10;
                }
                else
                {
                    ms = int.Parse(frac[..Math.Min(3, frac.Length)], CultureInfo.InvariantCulture);
                }
            }

            totalSeconds = min * 60 + sec + ms / 1000.0;
            return true;
        }

        private static string FormatTimestamp(double totalSeconds)
        {
            if (totalSeconds < 0)
            {
                totalSeconds = 0;
            }

            int min = (int)(totalSeconds / 60);
            double secPart = totalSeconds - min * 60;
            int sec = (int)secPart;
            int centiseconds = (int)Math.Round((secPart - sec) * 100);
            if (centiseconds >= 100)
            {
                centiseconds = 0;
                sec++;
                if (sec >= 60)
                {
                    sec = 0;
                    min++;
                }
            }

            return FormattableString.Invariant($"[{min:D2}:{sec:D2}.{centiseconds:D2}]");
        }

        private void ShiftEarlierButton_Click(object sender, RoutedEventArgs e)
        {
            LyricsTextBox.Text = ShiftLrcTimestamps(LyricsTextBox.Text, -0.5);
        }

        private void ShiftLaterButton_Click(object sender, RoutedEventArgs e)
        {
            LyricsTextBox.Text = ShiftLrcTimestamps(LyricsTextBox.Text, 0.5);
        }

        private void SaveLrcButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _lrcPath ??= Path.Combine(
                    Path.GetDirectoryName(_audioPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(_audioPath) + ".lrc");
                File.WriteAllText(_lrcPath, LyricsTextBox.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                _ = ShowMessageAsync("保存失败", ex.Message);
            }
        }

        private void EmbedButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string plainLyrics = StripTimestamps(LyricsTextBox.Text);
                TagEditorService.SaveEmbeddedLyrics(_audioPath, plainLyrics);
            }
            catch (Exception ex)
            {
                _ = ShowMessageAsync("嵌入失败", ex.Message);
            }
        }

        private static string StripTimestamps(string lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (string rawLine in lrc.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string line = TimestampRegex.Replace(rawLine, string.Empty).Trim();
                if (line.Length > 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                    }

                    sb.Append(line);
                }
            }

            return sb.ToString();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
        {
            ContentDialog dialog = new()
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}
