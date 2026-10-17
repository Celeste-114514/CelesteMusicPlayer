// 令牌测试皮肤 · 第四条腿：形状（圆角）的共享运行时
//
// 定位：主界面 XAML 一个字不改，令牌化只在「测试皮肤（跟随主题文件配色）」里生效。
//   圆角不做"把 XAML 里的 CornerRadius 换成令牌引用"那种逐页迁移，而是在令牌皮肤
//   激活时运行时把控件树上写死的圆角按档位重新映射到令牌值，退出皮肤原样还原。
//
// 2026-10-17 第二轮（用户实测"有的按钮没跟着变方角"）扩成多窗口共享运行时：
//   · 旧实现只挂主窗口的 RootShell。用户选主题时全程守在**设置窗口**里，那是另一个
//     独立窗口，形状腿根本走不到 → 那里面的按钮全部没变（用户说的"有的按钮"）。
//     debug 日志实证：遍历 437 节点全在主窗口子树。
//   · 动态内容漏网：设置页 UserControl / 飞出面版 / 弹窗里的按钮是遍历之后才出生的，
//     激活时那一轮覆盖不到。修法：激活后再延后一帧补跑一轮（同抽屉动画那招，
//     WinUI 里刚 Visible 的子树同帧没长全）。
//   · 窗口级原值字典：每个注册窗口各存一份原值，互不串；窗口关闭即反注册并清字典。
//
// 安全边界（沿用旧实现的约定）：
//   · 只在令牌皮肤激活期间跑；切回经典/图景/极客立刻还原，其它皮肤零影响。
//   · 原值先存后改，退出必还原；重复进入不会叠加（已记录的不再覆盖原值）。
//   · 档位默认值与现状一致（16/10/8/1.5/19/6/4/15/18 各有对应档），所以刚切过去看
//     不出变化；改主题文件里的 Radius.* 才看得出效果，这正是测试皮肤要验的东西。
//   · 列表项虚拟化回收，新行不经过这里，列表行圆角暂不在令牌化范围内（阶段 2 再收）。
//   · 复合圆角（"0,8,8,0" 这类有方向的）不处理：它有方向语义，不是档位问题。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 形状腿的进程级运行时：维护"哪些窗口注册了令牌皮肤"以及每个窗口的原值字典。
    /// 主窗口与设置窗口都通过它接入，遍历逻辑只有一份。
    /// </summary>
    public static class TokenShapeRuntime
    {
        /// <summary>注册表：窗口 → 根元素提供器（Window 的 Content 可能被整体换掉，用委托取）。</summary>
        private static readonly Dictionary<Window, Func<FrameworkElement?>> _hosts = new();

        /// <summary>每个窗口的原值字典（进入皮肤前的圆角，退出时按它还原）。</summary>
        private static readonly Dictionary<Window, Dictionary<DependencyObject, CornerRadius>> _originals = new();

        /// <summary>类型 → 有没有 CornerRadius 属性（反射结果缓存，避免每次切皮肤都反射几千次）。</summary>
        private static readonly Dictionary<Type, PropertyInfo?> CornerPropertyCache = new();

        /// <summary>遍历上限：单窗口静态控件实际约几百到两千，给足余量防止极端情况卡住。</summary>
        private const int ShapeWalkLimit = 6000;

        // ---------------- 注册 / 反注册 ----------------

        /// <summary>已挂接（Attach 过）的窗口。防止同一窗口重复挂事件。</summary>
        private static readonly HashSet<Window> _attached = new();

        /// <summary>
        /// 把窗口接入形状腿：构造函数里调一行即可。窗口每次被激活时补跑一次自己的圆角
        /// （打开设置窗口、播放列表窗口、EQ 窗口等都覆盖到），关闭时自动反注册并清字典。
        /// 非令牌模式下调用是空转（Apply 被 IsActive 挡掉），不需要调用方判断。
        /// </summary>
        public static void Attach(Window window)
        {
            if (window == null || !_attached.Add(window))
            {
                return;
            }

            window.Activated += OnWindowActivated;
            window.Closed += OnWindowClosed;
        }

        private static void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (sender is not Window window || !IsActive())
            {
                return;
            }

            // 只补跑自己：主窗口 RefreshTokenSkin 走 ApplyAll（全部窗口）
            Register(window, () => window.Content as FrameworkElement);
            ApplyWindow(window, () => window.Content as FrameworkElement);
        }

        private static void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (sender is not Window window)
            {
                return;
            }

            window.Activated -= OnWindowActivated;
            window.Closed -= OnWindowClosed;
            Unregister(window);
            _attached.Remove(window);
        }

        /// <summary>令牌皮肤是否激活（非激活一律不应用，切回经典 RevertAll 还原）。</summary>
        private static bool IsActive()
        {
            try
            {
                return AppSettingsStore.Load().UiStyleMode == MainWindow.UiStyleToken;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>注册一个窗口参与令牌形状腿（幂等：同窗口重复注册=刷新根元素提供器）。</summary>
        public static void Register(Window window, Func<FrameworkElement?> rootProvider)
        {
            if (window == null || rootProvider == null)
            {
                return;
            }

            _hosts[window] = rootProvider;
        }

        /// <summary>
        /// 反注册：先尽量还原该窗口的圆角，再丢弃它的原值字典。窗口关闭时调，
        /// 元素已销毁时还原失败也无所谓（反正窗口都没了），字典必须清掉防止内存留存。
        /// </summary>
        public static void Unregister(Window window)
        {
            if (window == null)
            {
                return;
            }

            RevertWindow(window);
            _hosts.Remove(window);
        }

        // ---------------- 应用 / 还原 ----------------

        /// <summary>对所有已注册窗口跑一遍形状腿（含原主题刷新后的重接，由调用方先 Revert）。</summary>
        public static void ApplyAll()
        {
            foreach (KeyValuePair<Window, Func<FrameworkElement?>> host in _hosts.ToList())
            {
                ApplyWindow(host.Key, host.Value);
            }
        }

        /// <summary>还原所有已注册窗口的圆角（幂等）。</summary>
        public static void RevertAll()
        {
            foreach (Window window in _originals.Keys.ToList())
            {
                RevertWindow(window);
            }
        }

        private static void ApplyWindow(Window window, Func<FrameworkElement?> rootProvider)
        {
            FrameworkElement? root = null;
            try
            {
                root = rootProvider();
            }
            catch
            {
                return; // 根元素已随窗口销毁
            }

            if (root == null)
            {
                return;
            }

            Dictionary<DependencyObject, CornerRadius> originals =
                _originals.TryGetValue(window, out Dictionary<DependencyObject, CornerRadius>? existing)
                    ? existing
                    : new Dictionary<DependencyObject, CornerRadius>();
            _originals[window] = originals;

            bool debug = ShapeDebugEnabled();
            int changed = 0;
            int walked = 0;
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            var detail = debug ? new List<string>(32) : null;
            var byKind = debug ? new Dictionary<string, int>(StringComparer.Ordinal) : null;

            try
            {
                while (stack.Count > 0 && walked < ShapeWalkLimit)
                {
                    DependencyObject node = stack.Pop();
                    walked++;

                    if (node is FrameworkElement fe && TryMapCorner(fe, originals))
                    {
                        changed++;
                        if (debug)
                        {
                            string label = string.IsNullOrEmpty(fe.Name) ? "-" : fe.Name;
                            double before = originals[fe].TopLeft;
                            double after = fe.GetType().GetProperty("CornerRadius")?
                                .GetValue(fe) is CornerRadius cr ? cr.TopLeft : double.NaN;
                            string kind = $"{fe.GetType().Name} {before}->{after:0.##}";
                            detail!.Add($"[{label}] {kind}");
                            byKind![kind] = byKind.TryGetValue(kind, out int c) ? c + 1 : 1;
                        }
                    }

                    int children = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < children; i++)
                    {
                        stack.Push(VisualTreeHelper.GetChild(node, i));
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TokenShapeRuntime.ApplyWindow", caught);
            }

            StartupLog.Write($"[TokenSkin] 形状腿：窗口 {window.GetType().Name} 遍历 {walked} 个节点，接上令牌 {changed} 处圆角");

            if (debug && detail!.Count > 0)
            {
                foreach (KeyValuePair<string, int> kv in byKind!.OrderByDescending(k => k.Value))
                {
                    StartupLog.Write($"[TokenSkin][shape-debug] {kv.Value,3} x {kv.Key}");
                }
                for (int i = 0; i < detail.Count && i < 64; i += 8)
                {
                    StartupLog.Write("[TokenSkin][shape-debug] " +
                        string.Join(" | ", detail.Skip(i).Take(8)));
                }
            }
        }

        private static void RevertWindow(Window window)
        {
            if (!_originals.TryGetValue(window, out Dictionary<DependencyObject, CornerRadius>? originals))
            {
                return;
            }

            int restored = 0;
            foreach (KeyValuePair<DependencyObject, CornerRadius> pair in originals)
            {
                try
                {
                    PropertyInfo? prop = CornerPropertyOf(pair.Key.GetType());
                    prop?.SetValue(pair.Key, pair.Value);
                    restored++;
                }
                catch
                {
                    // 元素可能已随页面切换销毁：还原失败就跳过，反正字典随后整本丢弃
                }
            }

            _originals.Remove(window);

            if (restored > 0)
            {
                StartupLog.Write($"[TokenSkin] 形状腿：窗口 {window.GetType().Name} 已还原 {restored} 处圆角");
            }
        }

        // ---------------- 档位映射 ----------------

        /// <summary>
        /// 圆角档位表：档位值 ↔ 令牌名。按主界面实测分布定的（16/10/8/1.5/19/6/4/15/18）。
        /// 给定控件当前圆角，取最接近的档位，再用该档位令牌的值覆盖。
        /// </summary>
        private static readonly (double Value, string Token)[] CornerSteps =
        {
            (1.5, CelesteTokenCatalog.RadiusHairline),
            (2.0, CelesteTokenCatalog.RadiusMicro),
            (3.0, CelesteTokenCatalog.RadiusXSmall),
            (4.0, CelesteTokenCatalog.RadiusSmall),
            (6.0, CelesteTokenCatalog.RadiusTile),
            (8.0, CelesteTokenCatalog.RadiusMedium),
            (10.0, CelesteTokenCatalog.RadiusChip),
            (12.0, CelesteTokenCatalog.RadiusLarge),
            (14.0, CelesteTokenCatalog.RadiusCover),
            (16.0, CelesteTokenCatalog.RadiusPill),
            (18.0, CelesteTokenCatalog.RadiusSheet),
        };

        /// <summary>单个控件：把它的圆角换成对应档位令牌的值。返回是否真的改了。</summary>
        private static bool TryMapCorner(FrameworkElement element, Dictionary<DependencyObject, CornerRadius> originals)
        {
            PropertyInfo? prop = CornerPropertyOf(element.GetType());
            if (prop == null)
            {
                return false;
            }

            // 列表项是虚拟化回收的，不进令牌化范围（见文件头"已知边界"）
            if (element is Microsoft.UI.Xaml.Controls.ListViewItem
                || element is Microsoft.UI.Xaml.Controls.GridViewItem
                || element is Microsoft.UI.Xaml.Controls.ComboBoxItem
                || element is Microsoft.UI.Xaml.Controls.MenuFlyoutItem)
            {
                return false;
            }

            CornerRadius current;
            try
            {
                current = (CornerRadius)prop.GetValue(element)!;
            }
            catch
            {
                return false;
            }

            // 复合圆角（四角不同）不处理：它有方向语义，不是档位问题
            if (!IsUniform(current))
            {
                return false;
            }

            string? token = NearestStepToken(current.TopLeft);
            if (token == null)
            {
                return false; // 0 或超出档位范围（如圆形头像 50+）：保持原样
            }

            double? tokenValue = MainWindow.ResolveTokenSkinDouble(token);
            if (tokenValue == null)
            {
                return false; // 主题文件没给值/解析失败：保持原样
            }

            var target = new CornerRadius(tokenValue.Value);
            if (Math.Abs(target.TopLeft - current.TopLeft) < 0.01)
            {
                return false; // 值一样，不动（也就不必记原值）
            }

            // 原值只记一次：重复进入皮肤不会被"改过的值"覆盖掉真原值
            if (!originals.ContainsKey(element))
            {
                originals[element] = current;
            }

            prop.SetValue(element, target);
            return true;
        }

        private static bool IsUniform(CornerRadius radius)
        {
            return Math.Abs(radius.TopLeft - radius.TopRight) < 0.01
                && Math.Abs(radius.TopLeft - radius.BottomRight) < 0.01
                && Math.Abs(radius.TopLeft - radius.BottomLeft) < 0.01;
        }

        /// <summary>当前圆角值 → 最接近的档位令牌名。0 与超范围返回 null（保持原样）。</summary>
        private static string? NearestStepToken(double value)
        {
            if (value <= 0.01)
            {
                return null;
            }

            string? best = null;
            double bestGap = double.MaxValue;
            foreach ((double step, string token) in CornerSteps)
            {
                double gap = Math.Abs(step - value);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = token;
                }
            }

            // 离最近档位超过 2px 就不硬套（比如 25 这种异常值或圆形头像），保持原样
            return bestGap > 2.0 ? null : best;
        }

        private static PropertyInfo? CornerPropertyOf(Type type)
        {
            if (CornerPropertyCache.TryGetValue(type, out PropertyInfo? cached))
            {
                return cached;
            }

            PropertyInfo? prop = type.GetProperty("CornerRadius");
            prop = (prop != null && prop.PropertyType == typeof(CornerRadius) && prop.CanRead && prop.CanWrite)
                ? prop
                : null;

            CornerPropertyCache[type] = prop;
            return prop;
        }

        /// <summary>
        /// 调试开关：在 %LOCALAPPDATA%\CelesteMusicPlayer\ 下放一个 shape-debug.on 空文件，
        /// 形状腿就把「每一个被改的元素（类型/名字/旧值→新值）」打进日志。
        /// 用来回答"到底哪些圆角被令牌改了"。没有开关时只打汇总行，零噪音。
        /// </summary>
        private static bool ShapeDebugEnabled()
        {
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return System.IO.File.Exists(System.IO.Path.Combine(dir, "CelesteMusicPlayer", "shape-debug.on"));
            }
            catch
            {
                return false;
            }
        }
    }
}
