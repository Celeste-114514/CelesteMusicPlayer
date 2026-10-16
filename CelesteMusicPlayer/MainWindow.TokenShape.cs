// 令牌测试皮肤 · 第四条腿：形状（圆角）
//
// 定位（2026-10-17，用户拍板）：
//   **主界面 XAML 一个字不改**，令牌化只在「测试皮肤（跟随主题文件配色）」里生效。
//   所以这里不做"把 XAML 里的 CornerRadius 换成令牌引用"那种逐页迁移，
//   而是在测试皮肤激活时**运行时**把控件树上写死的圆角按档位重新映射到令牌值，
//   退出皮肤时原样还原（原值记在 _shapeOriginal 里）。
//
// 为什么这样是安全的：
//   · 只在 UiStyleMode="Token" 时跑；切回经典/图景/极客立即还原，其它皮肤零影响。
//   · 原值先存后改，退出必还原；重复进入不会叠加（已记录的不再覆盖原值）。
//   · 档位默认值与现状一致（16/10/8/1.5/19…各有对应档），
//     所以刚切过去看起来**没有变化**；改主题文件里的 Radius.* 才看得出效果——
//     这正是"测试皮肤"要验的东西：**圆角是不是真的由数据驱动了**。
//
// 已知边界：
//   · 只处理静态结构（面板/按钮/卡片）。列表行是虚拟化回收的，新行不会经过这里，
//     所以列表行的圆角暂不在令牌化范围内（阶段 2 再收，届时走 XAML 引用令牌）。
//   · 复合圆角（"0,8,8,0" 这类有方向的）不处理：它不是档位问题。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 圆角档位表：档位值 ↔ 令牌名。按主界面实测分布定的（16/10/8/1.5/19/6/4/15/18…）。
        /// 给定控件当前圆角，取**最接近**的档位，再用该档位令牌的值覆盖。
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

        /// <summary>进入皮肤前的原始圆角（退出时按它还原）。</summary>
        private readonly Dictionary<DependencyObject, CornerRadius> _shapeOriginal =
            new();

        /// <summary>类型 → 有没有 CornerRadius 属性（反射结果缓存，避免每次切皮肤都反射几千次）。</summary>
        private static readonly Dictionary<Type, PropertyInfo?> CornerPropertyCache =
            new();

        /// <summary>遍历上限：主窗口静态控件实际约几百到两千，给足余量防止极端情况卡住。</summary>
        private const int ShapeWalkLimit = 6000;

        /// <summary>
        /// 调试开关：在 %LOCALAPPDATA%\CelesteMusicPlayer\ 下放一个 shape-debug.on 空文件，
        /// 形状腿就把「每一个被改的元素（类型/名字/旧值→新值）」打进日志。
        /// 用来回答"到底哪些圆角被令牌改了"——没有开关时只打汇总行，零噪音。
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

        /// <summary>
        /// 测试皮肤激活时调用：把控件树上的圆角接到令牌上。
        /// 失败不影响皮肤其它部分（宁可圆角不变，也不把界面改崩）。
        /// </summary>
        internal void ApplyTokenShapes()
        {
            try
            {
                if (RootShell == null)
                {
                    return;
                }

                bool debug = ShapeDebugEnabled();
                int changed = 0;
                int walked = 0;
                var stack = new Stack<DependencyObject>();
                stack.Push(RootShell);
                var detail = debug ? new List<string>(64) : null;
                var byKind = debug ? new Dictionary<string, int>(StringComparer.Ordinal) : null;

                while (stack.Count > 0 && walked < ShapeWalkLimit)
                {
                    DependencyObject node = stack.Pop();
                    walked++;

                    if (node is FrameworkElement fe && TryMapCorner(fe))
                    {
                        changed++;
                        if (debug)
                        {
                            string label = string.IsNullOrEmpty(fe.Name) ? "-" : fe.Name;
                            double before = _shapeOriginal[fe].TopLeft;
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

                StartupLog.Write($"[TokenSkin] 形状腿：遍历 {walked} 个节点，接上令牌 {changed} 处圆角");

                if (debug && detail!.Count > 0)
                {
                    foreach (KeyValuePair<string, int> kv in byKind!.OrderByDescending(k => k.Value))
                    {
                        StartupLog.Write($"[TokenSkin][shape-debug] {kv.Value,3} × {kv.Key}");
                    }
                    for (int i = 0; i < detail.Count && i < 64; i += 8)
                    {
                        StartupLog.Write("[TokenSkin][shape-debug] " +
                            string.Join(" | ", detail.Skip(i).Take(8)));
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ApplyTokenShapes", caught);
            }
        }

        /// <summary>退出测试皮肤时调用：把圆角还原成进入前的值（幂等，可重复调用）。</summary>
        internal void RevertTokenShapes()
        {
            try
            {
                foreach (KeyValuePair<DependencyObject, CornerRadius> pair in _shapeOriginal)
                {
                    PropertyInfo? prop = CornerPropertyOf(pair.Key.GetType());
                    prop?.SetValue(pair.Key, pair.Value);
                }

                if (_shapeOriginal.Count > 0)
                {
                    StartupLog.Write($"[TokenSkin] 形状腿：已还原 {_shapeOriginal.Count} 处圆角");
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RevertTokenShapes", caught);
            }
            finally
            {
                _shapeOriginal.Clear();
            }
        }

        /// <summary>单个控件：把它的圆角换成对应档位令牌的值。返回是否真的改了。</summary>
        private bool TryMapCorner(FrameworkElement element)
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
                return false; // 0 或超出档位范围：保持原样
            }

            double? tokenValue = ResolveTokenSkinDouble(token);
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
            if (!_shapeOriginal.ContainsKey(element))
            {
                _shapeOriginal[element] = current;
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

            // 离最近档位超过 2px 就不硬套（比如 25 这种异常值），保持原样
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
        /// 取一个长度类令牌的当前值（按生效主题文件）。解析失败返回 null，
        /// 调用方据此保持原值——宁可这个令牌不生效，也不要把界面改坏。
        /// </summary>
        internal static double? ResolveTokenSkinDouble(string tokenName)
        {
            var window = Instance;
            if (window == null)
            {
                return null;
            }

            try
            {
                bool dark = window.ResolveIsDark();
                string name = AppSettingsStore.Load().CelesteThemeName ?? "";
                string key = name + "|" + (dark ? "D" : "L");
                if (_tokenColorTheme == null || _tokenColorKey != key)
                {
                    _tokenColorTheme = window.LoadCelesteActiveTheme() ?? CelesteThemeStore.CreateDefault();
                    _tokenColorKey = key;
                    _tokenColorDark = dark;
                }

                string? raw = CelesteThemeStore.Resolve(_tokenColorTheme, tokenName, _tokenColorDark);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? value
                    : (double?)null;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ResolveTokenSkinDouble", caught);
                return null;
            }
        }
    }
}
