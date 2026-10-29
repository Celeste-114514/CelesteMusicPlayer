#pragma warning disable CS0436
#pragma warning disable CS0102
#pragma warning disable CS0111

namespace Microsoft.UI.Xaml
{
    public partial class PropertyMetadata : DependencyObject
    {
        public dynamic? DefaultValue { get; set; }
        public PropertyMetadata(object? defaultValue) { DefaultValue = defaultValue; }
        public PropertyMetadata(object? defaultValue, object? callback) { DefaultValue = defaultValue; }
    }
    public partial class Setter : DependencyObject
    {
        public dynamic? Property { get; set; }
        public dynamic? Value { get; set; }
        public Setter() { }
        public Setter(object? property, object? value) { Property = property; Value = value; }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public partial class ItemsStackPanel : Panel { }
    public partial class ItemsWrapGrid : Panel { }
    public partial class FlyoutPresenter : ContentControl { }
    public enum ColorSpectrumShape { Box = 0, Ring = 1 }
    public partial class ColorSpectrum : Control { }
}
namespace Microsoft.UI.Xaml.Media
{
    public partial class PolyLineSegment : DependencyObject
    {
        public global::System.Collections.IList Points { get; } = new global::System.Collections.Generic.List<object?>();
    }
    public partial class ArcSegment : DependencyObject { }
    public partial class BezierSegment : DependencyObject { }
    public partial class LineSegment : DependencyObject { }
}
