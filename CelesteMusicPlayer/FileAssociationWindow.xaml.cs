using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 「文件关联」窗口：列出全部可播放的音频格式，让用户逐项选择要关联哪些
    /// （Bandizip / Bandiview 的做法）。
    ///
    /// 几个刻意的设计：
    /// - 界面一律照「选项设置」窗口的规范来：自绘标题栏、内容列 680 居中、
    ///   行 = 左标题+灰色说明 / 右 ToggleSwitch、底部一条分隔线 + 右对齐按钮。
    /// - 可关联的格式一律以 <see cref="QuickPlayLauncher.PlayableExtensions"/> 为准，
    ///   和"双击能播哪些格式"共用一份清单，不会出现关联了却播不了。
    /// - 默认只勾推荐的一批（<see cref="FileAssociationHelper.RecommendedExtensions"/>），
    ///   不默认全选：.iso/.cue 这类格式关联过来会抢别的软件的默认打开方式。
    /// - 应用时先注册勾选的，再把"这次没勾、但之前关联过本程序"的解除掉，
    ///   并且只删默认值指向本程序的键，别人的关联不动。
    /// </summary>
    public sealed partial class FileAssociationWindow : Window
    {
        /// <summary>一个可关联的格式：Ext 是真实扩展名，Name 是左侧标题，Desc 是标题下的灰色说明。</summary>
        private sealed record FormatItem(string Ext, string Name, string Desc);

        private sealed record FormatGroup(string Title, string Hint, FormatItem[] Items);

        private static readonly FormatGroup[] Groups =
        {
            new("常见格式", "日常最常遇到的几种，建议全选", new[]
            {
                new FormatItem(".mp3", ".mp3", "MP3 · 最通用的有损格式"),
                new FormatItem(".m4a", ".m4a", "M4A / ALAC · Apple 常用"),
                new FormatItem(".aac", ".aac", "AAC · 流媒体常用"),
                new FormatItem(".wma", ".wma", "WMA · Windows Media"),
                new FormatItem(".wav", ".wav", "WAV · 未压缩"),
                new FormatItem(".flac", ".flac", "FLAC · 无损压缩"),
                new FormatItem(".ogg", ".ogg", "OGG · Vorbis"),
                new FormatItem(".opus", ".opus", "OPUS · 高效有损"),
            }),
            new("无损与大文件", "无损压缩、未压缩的高采样率音频", new[]
            {
                new FormatItem(".ape", ".ape", "APE · Monkey's Audio"),
                new FormatItem(".wv", ".wv", "WV · WavPack"),
                new FormatItem(".tta", ".tta", "TTA · 无损"),
                new FormatItem(".mpc", ".mpc", "MPC · Musepack"),
                new FormatItem(".tak", ".tak", "TAK · 无损"),
                new FormatItem(".aif", ".aif", "AIF · 苹果未压缩"),
                new FormatItem(".aiff", ".aiff", "AIFF · 苹果未压缩"),
            }),
            new("DSD / SACD", "DSD 音频与 SACD 光盘镜像", new[]
            {
                new FormatItem(".dsf", ".dsf", "DSF · DSD（Sony）"),
                new FormatItem(".dff", ".dff", "DFF · DSD（Philips）"),
                new FormatItem(".iso", ".iso", "ISO · SACD 光盘镜像"),
            }),
            new("播放列表", "整轨文件的分轨信息表", new[]
            {
                new FormatItem(".cue", ".cue", "CUE · 分轨表"),
            }),
            new("其它", "较少用到，按需勾选", new[]
            {
                new FormatItem(".mp2", ".mp2", "MP2 · MPEG 音频"),
                new FormatItem(".amr", ".amr", "AMR · 语音录音"),
                new FormatItem(".au", ".au", "AU · Sun 音频"),
                new FormatItem(".oga", ".oga", "OGA · OGG 音频"),
                new FormatItem(".mka", ".mka", "MKA · Matroska 音频"),
                new FormatItem(".mod", ".mod", "MOD · 模块音乐"),
                new FormatItem(".s3m", ".s3m", "S3M · Scream Tracker"),
                new FormatItem(".xm", ".xm", "XM · FastTracker"),
            }),
        };

        // 行控件用 ToggleSwitch 而不是勾选框 —— 这是「选项设置」窗口里所有布尔项的统一控件，
        // 右侧带「关 / 开」字样，视觉上和那个窗口是一套。
        private readonly Dictionary<string, ToggleSwitch> _toggles = new(StringComparer.OrdinalIgnoreCase);
        private string _executablePath = string.Empty;
        private bool _suppressToggleEvents;

        private static FileAssociationWindow? _instance;

        /// <summary>
        /// 打开「文件关联」窗口；已经开着就把它提到前台，不重复开第二个。
        /// 设置界面和首次启动引导都走这里。
        /// </summary>
        public static void ShowOrActivate()
        {
            if (_instance != null)
            {
                try
                {
                    _instance.Activate();
                    return;
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationWindow.ShowOrActivate", caught);
                    _instance = null;
                }
            }

            _instance = new FileAssociationWindow();
            _instance.Activate();
        }

        public FileAssociationWindow()
        {
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "文件关联";

            // 标题栏与「选项设置」窗口同一套写法：自绘标题栏（48 高那一条），
            // 按钮透明底 + 浅色前景，跟深色主题一致。
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 1000));

            ApplyBackdropFromSettings();

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindowTitleBar titleBar = AppWindow.TitleBar;
                titleBar.ButtonBackgroundColor = TransparentColor;
                titleBar.ButtonInactiveBackgroundColor = TransparentColor;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
                titleBar.ButtonHoverForegroundColor = WhiteColor;
                titleBar.ButtonPressedForegroundColor = WhiteColor;
            }

            _executablePath = Environment.ProcessPath
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "CelesteMusicPlayer.exe");

            BuildGroups();
            LoadCurrentSelection();
            RefreshStatus();

            Closed += (_, _) =>
            {
                if (ReferenceEquals(_instance, this)) _instance = null;
            };
        }

        // 直接给 ARGB 值，不走 Colors 静态类 —— 那样要同时 using Microsoft.UI 和 Windows.UI，
        // 两边的 Colors 容易撞名。
        private static Color TransparentColor => Color.FromArgb(0, 0, 0, 0);

        private static Color WhiteColor => Color.FromArgb(255, 255, 255, 255);

        /// <summary>跟「选项设置」一样，按设置决定用不用毛玻璃背景。</summary>
        private void ApplyBackdropFromSettings()
        {
            try
            {
                AppSettingsState s = AppSettingsStore.Load();
                if (s.EnableFrostedGlass) FrostedGlass.ApplyWindowBackdrop(this);
                else SystemBackdrop = null;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationWindow.ApplyBackdrop", caught);
            }
        }

        /// <summary>
        /// 按分组铺出开关行，行样式对齐「选项设置」窗口：
        /// 左边是标题 + 一行灰色小字说明，开关靠右（带「关 / 开」字样）。
        /// 点标题那一片也能切换 —— 27 项里挨个去够右边的小开关太累了。
        /// </summary>
        private void BuildGroups()
        {
            foreach (FormatGroup group in Groups)
            {
                var section = new StackPanel { Spacing = 10 };
                section.Children.Add(new TextBlock
                {
                    Text = group.Title,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold
                });
                section.Children.Add(new TextBlock
                {
                    Text = group.Hint,
                    FontSize = 12,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.WrapWholeWords
                });

                foreach (FormatItem item in group.Items)
                {
                    var row = new Grid { ColumnSpacing = 16 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var text = new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        // 想点文字也能切换，就得有非空背景，否则命中测试收不到点击
                        Background = new SolidColorBrush(TransparentColor)
                    };
                    text.Children.Add(new TextBlock
                    {
                        Text = item.Name,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                    text.Children.Add(new TextBlock
                    {
                        Text = item.Desc,
                        FontSize = 12,
                        Opacity = 0.65,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });

                    var toggle = new ToggleSwitch
                    {
                        Tag = item.Ext,
                        OffContent = "关",
                        OnContent = "开",
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    toggle.Toggled += FormatToggle_Toggled;
                    _toggles[item.Ext] = toggle;

                    text.Tapped += (_, _) => toggle.IsOn = !toggle.IsOn;

                    Grid.SetColumn(toggle, 1);
                    row.Children.Add(text);
                    row.Children.Add(toggle);
                    section.Children.Add(row);
                }

                GroupHost.Children.Add(section);
            }
        }

        /// <summary>回显当前关联状态：已关联过的就打开；一次都没关联过的给推荐默认值。</summary>
        private void LoadCurrentSelection()
        {
            List<string> associated = FileAssociationHelper.GetAssociatedExtensions();

            _suppressToggleEvents = true;
            try
            {
                if (associated.Count > 0)
                {
                    foreach (string ext in associated) SetToggled(ext, true);
                }
                else
                {
                    foreach (string ext in FileAssociationHelper.RecommendedExtensions) SetToggled(ext, true);
                }
            }
            finally
            {
                _suppressToggleEvents = false;
            }

            UpdateSummary();
        }

        private void SetToggled(string ext, bool value)
        {
            if (_toggles.TryGetValue(ext, out ToggleSwitch? toggle)) toggle.IsOn = value;
        }

        private void SetAllToggled(bool value, IEnumerable<string>? only = null)
        {
            _suppressToggleEvents = true;
            try
            {
                var target = only?.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, ToggleSwitch> pair in _toggles)
                {
                    pair.Value.IsOn = target == null || target.Contains(pair.Key);
                }
            }
            finally
            {
                _suppressToggleEvents = false;
            }

            UpdateSummary();
        }

        private void FormatToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleEvents) return;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int total = _toggles.Count;
            int picked = _toggles.Values.Count(t => t.IsOn);
            SelectionSummaryText.Text = $"已选择 {picked} / {total} 个格式";
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e) => SetAllToggled(true);

        private void SelectNoneButton_Click(object sender, RoutedEventArgs e) => SetAllToggled(false);

        private void SelectRecommendedButton_Click(object sender, RoutedEventArgs e)
            => SetAllToggled(false, FileAssociationHelper.RecommendedExtensions);

        /// <summary>顶部状态：本版本能不能做关联 / 关联指到了哪个 exe / 有没有指到用不了的版本。</summary>
        private void RefreshStatus()
        {
            bool selfPackaged = FileAssociationHelper.IsPackagedLayout(_executablePath);
            PickExecutableButton.Visibility = selfPackaged ? Visibility.Visible : Visibility.Collapsed;

            string? registered = FileAssociationHelper.GetRegisteredExecutable();
            bool registeredPackaged = registered != null && FileAssociationHelper.IsPackagedLayout(registered);

            if (registeredPackaged)
            {
                SetStatus("⚠ 当前文件关联指向的程序不能用来打开文件（调试/打包版）：\n" + registered
                          + "\n请点下面的「手动选择程序文件…」换成免安装版或安装后的正式版。", ok: false);
            }
            else if (selfPackaged)
            {
                SetStatus("⚠ 你现在运行的是调试（打包）版本，Windows 不允许它作为文件的默认打开程序，"
                          + "双击音频不会有任何反应。\n请点下面的「手动选择程序文件…」，换成 Release（免安装）版或安装后的正式版本。",
                          ok: false);
            }
            else if (!string.IsNullOrEmpty(registered))
            {
                SetStatus("✓ 当前关联指向：\n" + registered, ok: true);
            }
            else
            {
                SetStatus("✓ 当前版本可用于文件关联（尚未关联任何格式）。", ok: true);
            }
        }

        private void SetStatus(string message, bool ok)
        {
            StatusText.Text = message;
            StatusText.Foreground = ok
                ? new SolidColorBrush(Color.FromArgb(255, 76, 175, 80))
                : new SolidColorBrush(Color.FromArgb(255, 220, 80, 60));
            StatusText.Visibility = Visibility.Visible;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            List<string> picked = _toggles
                .Where(pair => pair.Value.IsOn)
                .Select(pair => pair.Key)
                .ToList();

            try
            {
                if (picked.Count == 0)
                {
                    FileAssociationHelper.Unregister();
                    AppliedHintText.Text = "已取消全部文件关联。";
                    RefreshStatus();
                    return;
                }

                // 1) 先按勾选写注册表（ProgId + 这些扩展名）
                FileAssociationHelper.Register(_executablePath, picked);

                // 2) 再解除"这次没勾"的格式中、默认值指向本程序的那些。
                //    注意顺序：必须先注册后解除 —— Unregister() 会把 ProgId 整个删掉。
                var stale = FileAssociationHelper.CommonExtensions
                    .Where(ext => !picked.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (stale.Count > 0) FileAssociationHelper.UnregisterExtensions(stale);

                AppliedHintText.Text = $"已应用：关联 {picked.Count} 个格式。";
                RefreshStatus();
            }
            catch (Exception ex)
            {
                AppliedHintText.Text = string.Empty;
                SetStatus("关联失败：" + ex.Message, ok: false);
            }
        }

        private async void UnregisterAllButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ContentDialog confirm = new()
                {
                    Title = "取消全部关联？",
                    Content = "将移除 Celeste Music Player 对所有音频格式的关联，之后双击音频不会再打开本程序。",
                    PrimaryButtonText = "取消关联",
                    CloseButtonText = "算了",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };

                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

                FileAssociationHelper.Unregister();
                SetAllToggled(false);
                AppliedHintText.Text = "已取消全部文件关联。";
                RefreshStatus();
            }
            catch (Exception ex)
            {
                SetStatus("取消失败：" + ex.Message, ok: false);
            }
        }

        /// <summary>让用户挑一个 .exe（把关联指到能用的那个版本上）。</summary>
        private async void PickExecutableButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
                };
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".exe");

                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null || string.IsNullOrEmpty(file.Path)) return;

                _executablePath = file.Path;
                RefreshStatus();
            }
            catch (Exception ex)
            {
                SetStatus("选择失败：" + ex.Message, ok: false);
            }
        }

        private void OpenDefaultAppsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationWindow.OpenDefaultApps", caught);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try { Close(); }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FileAssociationWindow.Close", caught); }
        }
    }
}
