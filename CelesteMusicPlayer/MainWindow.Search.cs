using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
// TagLibSharp：包名 TagLibSharp，命名空间 TagLib
using TagLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;


namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {

        private ListView? FindAncestorListView(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is ListView lv) return lv;
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }


        /// <summary>中文搜索友好归一化：转小写 → 全角转半角 → 繁体转简体，实现容错匹配（搜繁体命中简体、搜全角命中半角等）。</summary>
        private static string SearchNormalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\u3000')
                {
                    sb.Append(' ');
                }
                else if (c >= '\uFF01' && c <= '\uFF5E')
                {
                    sb.Append((char)(c - 0xFEE0)); // 全角 → 半角
                }
                else if (c < 0x20)
                {
                    continue;
                }
                else if (_tradToSimp.TryGetValue(c, out char simp))
                {
                    sb.Append(simp); // 繁体 → 简体
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().ToLowerInvariant();
        }


    }
}
