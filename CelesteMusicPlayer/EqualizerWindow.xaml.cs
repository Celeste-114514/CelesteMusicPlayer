using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>10 段均衡器设置窗口。</summary>
    public sealed partial class EqualizerWindow : Window
    {
        private static EqualizerWindow? _instance;

        private static readonly string[] BandLabels =
        {
            "31", "62", "125", "250", "500", "1K", "2K", "4K", "8K", "16K"
        };

        private static readonly (EqualizerPreset Preset, string Label)[] PresetOptions =
        {
            (EqualizerPreset.Flat, "平坦"),
            (EqualizerPreset.Classical, "古典"),
            (EqualizerPreset.Pop, "流行"),
            (EqualizerPreset.Jazz, "爵士"),
            (EqualizerPreset.Rock, "摇滚"),
            (EqualizerPreset.Soft, "柔和"),
            (EqualizerPreset.Bass, "低音增强")
        };

        private readonly List<Slider> _bandSliders = new();
        private readonly List<TextBlock> _bandValueTexts = new();
        private EqualizerState _state = new();
        private bool _loadingUi;

        public static event Action? Applied;

        public EqualizerWindow()
        {
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "均衡器";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new SizeInt32(1112, 678));

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();
            BuildBandSliders();
            InitPresetCombo();
            LoadFromStore();

            Closed += (_, _) =>
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            };
        }

        public static void ShowOrActivate()
        {
            if (_instance != null)
            {
                _instance.LoadFromStore();
                _instance.Activate();
                return;
            }

            _instance = new EqualizerWindow();
            _instance.Activate();
        }

        public static void CloseIfOpen()
        {
            if (_instance == null)
            {
                return;
            }

            EqualizerWindow win = _instance;
            _instance = null;
            win.Close();
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

        private void BuildBandSliders()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            for (int i = 0; i < EqualizerState.BandCount; i++)
            {
                int bandIndex = i;
                var column = new StackPanel
                {
                    Width = 52,
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var valueText = new TextBlock
                {
                    Text = "0",
                    FontSize = 10,
                    Opacity = 0.65,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                _bandValueTexts.Add(valueText);

                var sliderHost = new Grid
                {
                    Width = 40,
                    Height = 170,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var slider = new Slider
                {
                    Minimum = -15,
                    Maximum = 15,
                    StepFrequency = 1,
                    Value = 0,
                    Width = 200,
                    Height = 40,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                    RenderTransform = new RotateTransform { Angle = -90 }
                };
                slider.ValueChanged += (_, e) => BandSlider_ValueChanged(bandIndex, e);
                _bandSliders.Add(slider);
                sliderHost.Children.Add(slider);

                var label = new TextBlock
                {
                    Text = BandLabels[i],
                    FontSize = 11,
                    Opacity = 0.75,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                column.Children.Add(valueText);
                column.Children.Add(sliderHost);
                column.Children.Add(label);
                row.Children.Add(column);
            }

            BandSlidersPanel.Children.Add(row);
        }

        private void InitPresetCombo()
        {
            PresetCombo.Items.Clear();
            foreach ((EqualizerPreset preset, string label) in PresetOptions)
            {
                PresetCombo.Items.Add(new ComboBoxItem { Content = label, Tag = preset });
            }
        }

        private void LoadFromStore()
        {
            _loadingUi = true;
            try
            {
                _state = EqualizerStore.Load();
                for (int i = 0; i < _bandSliders.Count && i < _state.BandGains.Length; i++)
                {
                    _bandSliders[i].Value = _state.BandGains[i];
                    _bandValueTexts[i].Text = FormatGain(_state.BandGains[i]);
                }

                SelectPreset(_state.Preset);
            }
            finally
            {
                _loadingUi = false;
            }
        }

        private void SelectPreset(EqualizerPreset preset)
        {
            for (int i = 0; i < PresetCombo.Items.Count; i++)
            {
                if (PresetCombo.Items[i] is ComboBoxItem { Tag: EqualizerPreset p } && p == preset)
                {
                    PresetCombo.SelectedIndex = i;
                    return;
                }
            }

            PresetCombo.SelectedIndex = 0;
        }

        private static string FormatGain(double gain)
        {
            int rounded = (int)Math.Round(gain);
            return rounded > 0 ? $"+{rounded}" : rounded.ToString();
        }

        private void SyncStateFromSliders()
        {
            for (int i = 0; i < _bandSliders.Count; i++)
            {
                _state.BandGains[i] = _bandSliders[i].Value;
            }
        }

        private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi || PresetCombo.SelectedItem is not ComboBoxItem { Tag: EqualizerPreset preset })
            {
                return;
            }

            _loadingUi = true;
            try
            {
                _state.ApplyPreset(preset);
                for (int i = 0; i < _bandSliders.Count; i++)
                {
                    _bandSliders[i].Value = _state.BandGains[i];
                    _bandValueTexts[i].Text = FormatGain(_state.BandGains[i]);
                }
            }
            finally
            {
                _loadingUi = false;
            }
        }

        private void BandSlider_ValueChanged(int bandIndex, RangeBaseValueChangedEventArgs e)
        {
            if (bandIndex >= 0 && bandIndex < _bandValueTexts.Count)
            {
                _bandValueTexts[bandIndex].Text = FormatGain(e.NewValue);
            }

            if (_loadingUi)
            {
                return;
            }

            _state.Preset = EqualizerPreset.Flat;
            SelectPreset(EqualizerPreset.Flat);
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SyncStateFromSliders();
            EqualizerStore.Save(_state);
            try
            {
                Applied?.Invoke();
            }
            catch
            {
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _loadingUi = true;
            try
            {
                _state = new EqualizerState();
                _state.ApplyPreset(EqualizerPreset.Flat);
                for (int i = 0; i < _bandSliders.Count; i++)
                {
                    _bandSliders[i].Value = _state.BandGains[i];
                    _bandValueTexts[i].Text = FormatGain(_state.BandGains[i]);
                }

                SelectPreset(EqualizerPreset.Flat);
            }
            finally
            {
                _loadingUi = false;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
