// Data carried by retained legacy UI calculations. These types never render XAML.
namespace Microsoft.UI.Xaml {
 public enum TextTrimming { None, CharacterEllipsis, WordEllipsis, Clip }
 public partial class UIElement { public object? Clip { get; set; } }
}
namespace Microsoft.UI.Xaml.Controls {
 public partial class Grid { public double ColumnSpacing {get;set;} }
 public partial class RadioButton { public string GroupName {get;set;} = ""; }
 public partial class TextBlock { public Microsoft.UI.Xaml.TextTrimming TextTrimming {get;set;} }
 public partial class VariableSizedWrapGrid { public double ItemWidth {get;set;} public double ItemHeight {get;set;} public int MaximumRowsOrColumns {get;set;} }
 public partial class ColorPicker {
  public ColorSpectrumShape ColorSpectrumShape {get;set;}
  public bool IsAlphaEnabled {get;set;} public bool IsAlphaSliderVisible {get;set;} public bool IsAlphaTextInputVisible {get;set;}
  public bool IsColorChannelTextInputVisible {get;set;} public bool IsColorSliderVisible {get;set;} public bool IsHexInputVisible {get;set;}
 }
}
namespace Microsoft.UI.Xaml.Media {
 public partial class CompositeTransform { public double CenterX {get;set;} public double CenterY {get;set;} public double TranslateX {get;set;} }
 public partial class RotateTransform { public double Angle {get;set;} }
 public partial class ScaleTransform { public double ScaleX {get;set;} = 1; }
 public partial class PathFigure { public Windows.Foundation.Point StartPoint {get;set;} public bool IsClosed {get;set;} }
}
namespace Microsoft.UI.Xaml.Documents { public partial class Run { public string Text {get;set;} = ""; } }
