// W13 过渡层：手工补齐「语义必须正确」的成员（自动补录用的是 object?，
// 会让下游的索引、颜色转换、await 等写法连锁报错，这里按真实用法给准确类型）。
#pragma warning disable CS0436
#pragma warning disable CS0102
#pragma warning disable CS0111
#pragma warning disable CS0169
#pragma warning disable CS0067
#pragma warning disable CS8618

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.UI.Dispatching
{
    public sealed partial class DispatcherQueue
    {
        public DispatcherQueueTimer CreateTimer() => new DispatcherQueueTimer();
        public void EnqueueIdle(Action callback) => TryEnqueue(callback);
    }

    public delegate void DispatcherQueueHandler();
}

namespace Microsoft.UI.Xaml
{
    public sealed partial class ResourceDictionary
    {
        private readonly Dictionary<object, object?> _map = new();

        public object? this[object key]
        {
            get => _map.TryGetValue(key, out object? v) ? v : null;
            set => _map[key] = value;
        }

        public IDictionary<object, object> ThemeDictionaries { get; } = new Dictionary<object, object>();
        public IList MergedDictionaries { get; } = new List<object?>();
        public ICollection<object> Keys => _map.Keys;
        public bool ContainsKey(object key) => _map.ContainsKey(key);
        public bool TryGetValue(object key, out object? value) => _map.TryGetValue(key, out value);
        public bool Remove(object key) => _map.Remove(key);
        public void Clear() => _map.Clear();
        public void Add(object key, object? value) => _map[key] = value;
        public dynamic? Source { get; set; }
    }

    public partial class Application
    {
        public static Application Current { get; } = new Application();
        public ResourceDictionary Resources { get; } = new ResourceDictionary();
        public ElementTheme RequestedTheme { get; set; }
        public static void Exit() => Microsoft.UI.Dispatching.NativeMessagePump.Quit(0);
        public void ExitApplication() => Microsoft.UI.Dispatching.NativeMessagePump.Quit(0);
    }

    public partial class FrameworkElement
    {
        public dynamic? XamlRoot { get; set; }
        public ResourceDictionary? Resources { get; set; }
        public dynamic? ProtectedCursor { get; set; }
        public dynamic? ActualTheme { get; set; }
        public bool IsLoaded2 => true;
        public object? FindName2(string name) => null;
        public object? GetValue2(object? dp) => null;
    }
}

namespace Microsoft.UI.Xaml.Media.Imaging
{

}

namespace Microsoft.UI.Xaml.Media
{
    public sealed partial class AcrylicBrush
    {
        public Windows.UI.Color TintColor { get; set; }
        public double TintOpacity { get; set; }
        public double TintLuminosityOpacity { get; set; }
        public dynamic? TintTransition { get; set; }
        public dynamic? FallbackColor { get; set; }
        public bool AlwaysUseFallback { get; set; }
    }

    public sealed partial class SolidColorBrush
    {
        public static SolidColorBrush FromColor(Windows.UI.Color color) => new(color);
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public sealed partial class ContentDialog
    {
        public dynamic? XamlRoot { get; set; }
        public dynamic? DefaultButton { get; set; }
        public dynamic? PrimaryButtonCommand { get; set; }
        public dynamic? SecondaryButtonCommand { get; set; }
        public dynamic? CloseButtonCommand { get; set; }
        public bool IsPrimaryButtonEnabled { get; set; } = true;
        public bool IsSecondaryButtonEnabled { get; set; } = true;
        public dynamic? Content { get; set; }
    }

    public sealed partial class MenuFlyout
    {
        public IList Items { get; } = new List<object?>();
        public dynamic? Placement { get; set; }
        public void ShowAt(Microsoft.UI.Xaml.DependencyObject? target) { }
        public void ShowAt(Microsoft.UI.Xaml.DependencyObject? target, object? options) { }
        public void Hide() { }
    }

    public sealed partial class ListViewBase
    {
        public IList<object?> SelectedItems { get; } = new List<object?>();
        public dynamic? SelectionMode { get; set; }
        public dynamic? ItemContainerStyle { get; set; }
        public dynamic? ItemsPanel { get; set; }
        public dynamic? ItemTemplate { get; set; }
        public dynamic? HeaderTemplate { get; set; }
        public object? ItemFromContainer(Microsoft.UI.Xaml.DependencyObject? container) => null;
        public object? ContainerFromItem(object? item) => null;
        public int SelectedIndex { get; set; } = -1;
        public dynamic? SelectedItem { get; set; }
        public void SelectAll() { }
        public void ScrollIntoView(object? item) { }
    }

    public sealed partial class Border
    {
        public Microsoft.UI.Xaml.UIElement? Child { get; set; }
        public Windows.UI.Color BackgroundSolid { get; set; }
    }

    public sealed partial class ColumnDefinition
    {
        public dynamic? Width { get; set; }
        public double MinWidth { get; set; }
        public double MaxWidth { get; set; }
        public double ActualWidth { get; set; }
    }

    public sealed partial class RowDefinition
    {
        public dynamic? Height { get; set; }
        public double MinHeight { get; set; }
        public double MaxHeight { get; set; }
        public double ActualHeight { get; set; }
    }
}

namespace Microsoft.UI.Xaml.Shapes
{
    public sealed partial class Line
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }

    public sealed partial class Rectangle
    {
        public double RadiusX { get; set; }
        public double RadiusY { get; set; }
    }

    public sealed partial class Polyline
    {
        public IList Points { get; } = new List<object?>();
        public dynamic? StrokeLineJoin { get; set; }
        public dynamic? StrokeDashCap { get; set; }
    }

    public sealed partial class Polygon
    {
        public IList Points { get; } = new List<object?>();
    }
}

namespace Microsoft.UI.Xaml
{
    public sealed partial class Style
    {
        public Style() { }
        public Style(object? targetType) { TargetType = targetType; }
        public IList Setters { get; } = new List<object?>();
        public dynamic? TargetType { get; set; }
        public dynamic? BasedOn { get; set; }
    }
}


namespace Microsoft.UI.Xaml.Media
{
    public class DesktopAcrylicBackdrop : Microsoft.UI.Xaml.DependencyObject { }
    public class MicaBackdrop : Microsoft.UI.Xaml.DependencyObject { }
    public class SystemBackdrop : Microsoft.UI.Xaml.DependencyObject { }
}
