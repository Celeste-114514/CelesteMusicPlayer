using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>智能播放列表行的 UI 视图模型（ListView 绑定用）。</summary>
    public sealed class SmartPlaylistRow
    {
        public string Name { get; set; } = string.Empty;
        public string Rule { get; set; } = string.Empty;
        public SmartPlaylistDef? Def { get; set; }
    }

    /// <summary>
    /// 智能播放列表管理窗口：列出已保存规则、新建/编辑/删除，并把规则解析结果灌入播放队列。
    /// 通过 owner 传入的两个回调（播放 / 加入队列）与 MainWindow 解耦。
    /// </summary>
    public sealed partial class SmartPlaylistWindow : Window
    {
        private readonly Action<List<string>> _playPaths;
        private readonly Action<List<string>> _addPaths;
        private readonly ObservableCollection<SmartPlaylistRow> _rows = new();
        private SmartPlaylistDef? _editing;

        public SmartPlaylistWindow(Window owner, Action<List<string>> playPaths, Action<List<string>> addPaths)
        {
            _playPaths = playPaths;
            _addPaths = addPaths;
            InitializeComponent();
            PlaylistList.ItemsSource = _rows;
            RefreshList();
            NewButton_Click(null, null);
        }

        private void RefreshList()
        {
            _rows.Clear();
            foreach (var d in SmartPlaylistStore.LoadAll())
            {
                _rows.Add(new SmartPlaylistRow { Name = d.Name, Rule = SmartPlaylistStore.Describe(d), Def = d });
            }
        }

        private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool has = PlaylistList.SelectedItem is SmartPlaylistRow;
            PlayButton.IsEnabled = has;
            AddButton.IsEnabled = has;
            DeleteButton.IsEnabled = has;
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistList.SelectedItem is SmartPlaylistRow row && row.Def != null)
            {
                var paths = SmartPlaylistStore.Resolve(row.Def);
                StatusText.Text = $"已按当前曲库生成 {paths.Count} 首，开始播放…";
                _playPaths(paths);
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistList.SelectedItem is SmartPlaylistRow row && row.Def != null)
            {
                var paths = SmartPlaylistStore.Resolve(row.Def);
                StatusText.Text = $"已按当前曲库生成 {paths.Count} 首，加入队列。";
                _addPaths(paths);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistList.SelectedItem is SmartPlaylistRow row && row.Def != null)
            {
                SmartPlaylistStore.Remove(row.Def.Id);
                RefreshList();
                PlaylistList.SelectedItem = null;
                StatusText.Text = "已删除。";
            }
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            _editing = new SmartPlaylistDef();
            NameBox.Text = string.Empty;
            KindCombo.SelectedIndex = 0;
            LimitBox.Value = 100;
            ArgText.Text = "4";
            UpdateArgControl();
            StatusText.Text = "新建：填写名称与规则后点「保存」。";
        }

        private void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateArgControl();
        }

        private void UpdateArgControl()
        {
            var kind = CurrentKind();
            if (kind == SmartPlaylistKind.Rating)
            {
                ArgText.Visibility = Visibility.Visible;
                ArgCombo.Visibility = Visibility.Collapsed;
            }
            else if (kind == SmartPlaylistKind.Genre || kind == SmartPlaylistKind.Artist)
            {
                ArgCombo.Visibility = Visibility.Visible;
                ArgText.Visibility = Visibility.Collapsed;
                ArgCombo.Items.Clear();
                var vals = kind == SmartPlaylistKind.Genre
                    ? LibraryDb.GetDistinctGenres()
                    : LibraryDb.GetDistinctArtists();
                foreach (var v in vals)
                {
                    ArgCombo.Items.Add(new ComboBoxItem { Content = v, Tag = v });
                }

                ArgCombo.SelectedIndex = ArgCombo.Items.Count > 0 ? 0 : -1;
            }
            else if (kind == SmartPlaylistKind.Decade)
            {
                ArgCombo.Visibility = Visibility.Visible;
                ArgText.Visibility = Visibility.Collapsed;
                ArgCombo.Items.Clear();
                var decades = LibraryDb.GetDistinctYears()
                    .Select(y => (y / 10) * 10)
                    .Distinct()
                    .OrderBy(y => y)
                    .ToList();
                foreach (var d in decades)
                {
                    ArgCombo.Items.Add(new ComboBoxItem { Content = d + "s", Tag = d.ToString() });
                }

                ArgCombo.SelectedIndex = ArgCombo.Items.Count > 0 ? 0 : -1;
            }
            else
            {
                ArgText.Visibility = Visibility.Collapsed;
                ArgCombo.Visibility = Visibility.Collapsed;
            }
        }

        private SmartPlaylistKind CurrentKind()
        {
            if (KindCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag
                && Enum.TryParse<SmartPlaylistKind>(tag, out var k))
            {
                return k;
            }

            return SmartPlaylistKind.Rating;
        }

        private string CurrentArg()
        {
            var kind = CurrentKind();
            if (kind == SmartPlaylistKind.Rating) return ArgText.Text.Trim();
            if (kind is SmartPlaylistKind.Genre or SmartPlaylistKind.Artist or SmartPlaylistKind.Decade)
            {
                return ArgCombo.SelectedItem is ComboBoxItem ci ? (ci.Tag as string) ?? string.Empty : string.Empty;
            }

            return string.Empty;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _editing ??= new SmartPlaylistDef();
                _editing.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "智能播放列表" : NameBox.Text.Trim();
                _editing.Kind = CurrentKind();
                _editing.Argument = CurrentArg();
                _editing.Limit = (int)Math.Clamp(LimitBox.Value, 1, 2000);
                SmartPlaylistStore.Upsert(_editing);
                _editing = null;
                RefreshList();
                StatusText.Text = "已保存。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("SmartPlaylistWindow.xaml.cs", caught);
            }
        }
    }
}
