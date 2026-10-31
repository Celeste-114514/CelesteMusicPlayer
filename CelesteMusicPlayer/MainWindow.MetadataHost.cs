using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十刀（2026-10-31）：曲目元数据宿主。
/// 从壳（ShellPages）转活的「打开标签编辑器」统一入口。
/// 网页浏览动作（WebBrowseActions）借用的行为入口住这里。
/// 标签保存后的元数据刷新暂留壳（连着 ApplyCategoryView 分类大域）。
/// </summary>
public sealed partial class MainWindow
{
        /// <summary>
        /// 从任何地方跳到主界面的「标签」板块并打开指定文件：右键菜单、播放页、设置健康检查、
        /// WebUI 的 edittags 命令原来都是弹独立 TagEditorWindow（阶段 6 收尾后已删），现在统一走这里。
        /// 文件不在标签列表里的会先加进来（后台读标签），再全部选中并停在「快速编辑」子页。
        /// </summary>
        internal void OpenTagEditForFiles(IEnumerable<string>? paths)
        {
            DispatcherQueue.TryEnqueue(() => {
                OpenWebShell();
                _webShellWindow?.RevealTagFiles(paths ?? Array.Empty<string>());
            });
        }
    }
