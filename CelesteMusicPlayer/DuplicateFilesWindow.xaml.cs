using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>重复文件列表项（供 x:Bind 使用）。</summary>
    public sealed class DuplicateFileItem
    {
        public string FileName { get; }

        public string FilePath { get; }

        public string SizeText { get; }

        public bool IsSeparator { get; }

        public DuplicateFileItem(string path, long size, bool isSeparator)
        {
            IsSeparator = isSeparator;
            FilePath = path;
            FileName = isSeparator ? "──── 重复组 ────" : Path.GetFileName(path);
            SizeText = isSeparator ? string.Empty : FormatSize(size);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1048576)
            {
                return (bytes / 1048576.0).ToString("0.0") + " MB";
            }

            return Math.Max(1, bytes / 1024) + " KB";
        }
    }

    /// <summary>重复文件检测窗口：按文件名+大小分组扫描媒体库，可打开位置或移入回收站。</summary>
    public sealed partial class DuplicateFilesWindow : Window
    {
        private static DuplicateFilesWindow? _instance;
        private readonly MainWindow _owner;
        private readonly List<DuplicateFileItem> _items = new();

        public DuplicateFilesWindow(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            Title = "重复文件检测";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new SizeInt32(720, 560));

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();

            Closed += (_, _) =>
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            };

            _ = ScanAsync();
        }

        public static void ShowOrActivate(MainWindow owner)
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }

            _instance = new DuplicateFilesWindow(owner);
            _instance.Activate();
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

        private async Task ScanAsync()
        {
            ScanProgress.Visibility = Visibility.Visible;
            StatusText.Text = "正在扫描媒体库…";

            IReadOnlyList<PlaylistItem> tracks = _owner.LibraryTracks;
            var result = await Task.Run(() =>
            {
                var list = new List<DuplicateFileItem>();
                var nameGroups = tracks
                    .GroupBy(t => Path.GetFileName(t.FilePath).ToLowerInvariant())
                    .Where(g => g.Count() > 1);
                foreach (var nameGroup in nameGroups)
                {
                    var sizeGroups = nameGroup
                        .GroupBy(t => SafeLength(t.FilePath))
                        .Where(g => g.Count() > 1)
                        .OrderBy(g => g.Key);
                    foreach (var sizeGroup in sizeGroups)
                    {
                        var sorted = sizeGroup
                            .OrderBy(t => t.FilePath, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                        list.Add(new DuplicateFileItem(sorted[0].FilePath, sizeGroup.Key, isSeparator: true));
                        foreach (PlaylistItem t in sorted)
                        {
                            list.Add(new DuplicateFileItem(t.FilePath, sizeGroup.Key, isSeparator: false));
                        }
                    }
                }

                return list;
            });

            _items.Clear();
            _items.AddRange(result);
            ResultList.ItemsSource = null;
            ResultList.ItemsSource = _items;
            ScanProgress.Visibility = Visibility.Collapsed;

            int groupCount = result.Count(i => i.IsSeparator);
            StatusText.Text = groupCount == 0
                ? "未发现重复文件"
                : $"发现 {groupCount} 组重复文件，共 {result.Count - groupCount} 个文件";
        }

        private static long SafeLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return -1;
            }
        }

        private void ResultList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            DuplicateFileItem? item = FindItem(source);
            if (item == null || item.IsSeparator)
            {
                return;
            }

            var flyout = new MenuFlyout();
            var open = new MenuFlyoutItem { Text = "打开文件位置" };
            open.Icon = new FontIcon { Glyph = "\uE8DA" };
            open.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
                }
                catch
                {
                }
            };
            flyout.Items.Add(open);

            var del = new MenuFlyoutItem { Text = "从磁盘删除（回收站）" };
            del.Icon = new FontIcon { Glyph = "\uE74D" };
            del.Click += async (_, _) => await DeleteFileAsync(item);
            flyout.Items.Add(del);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
        }

        private static DuplicateFileItem? FindItem(DependencyObject start)
        {
            DependencyObject? current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.DataContext is DuplicateFileItem item)
                {
                    return item;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private async Task DeleteFileAsync(DuplicateFileItem item)
        {
            var dialog = new ContentDialog
            {
                Title = "从磁盘删除",
                Content = $"确定将以下文件移到回收站吗？\n\n{item.FilePath}",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!MoveToRecycleBin(item.FilePath))
            {
                StatusText.Text = "删除失败";
                return;
            }

            _items.Remove(item);
            ResultList.ItemsSource = null;
            ResultList.ItemsSource = _items;
            StatusText.Text = "已删除：" + Path.GetFileName(item.FilePath);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        private static bool MoveToRecycleBin(string path)
        {
            try
            {
                SHFILEOPSTRUCT op = new()
                {
                    wFunc = 3,
                    pFrom = path + "\0\0",
                    fFlags = 0x40 | 0x10 | 0x04
                };
                return SHFileOperation(ref op) == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
