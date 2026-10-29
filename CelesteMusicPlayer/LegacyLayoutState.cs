// State used by retained legacy view code.
namespace Microsoft.UI.Xaml.Controls {
 public partial class Canvas {
  private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Microsoft.UI.Xaml.UIElement, Position> Positions = new();
  private sealed class Position { public double Left, Top; public int Z; }
  public static void SetLeft(Microsoft.UI.Xaml.UIElement element, double value) => Positions.GetOrCreateValue(element).Left = value;
  public static void SetTop(Microsoft.UI.Xaml.UIElement element, double value) => Positions.GetOrCreateValue(element).Top = value;
  public static double GetLeft(Microsoft.UI.Xaml.UIElement element) => Positions.GetOrCreateValue(element).Left;
  public static double GetTop(Microsoft.UI.Xaml.UIElement element) => Positions.GetOrCreateValue(element).Top;
  public static void SetZIndex(Microsoft.UI.Xaml.UIElement element, int value) => Positions.GetOrCreateValue(element).Z = value;
 }
}
namespace Microsoft.UI.Xaml.Media {
 public partial class RectangleGeometry { public Windows.Foundation.Rect Rect {get;set;} }
 public partial class LinearGradientBrush { public Windows.Foundation.Point StartPoint {get;set;} public Windows.Foundation.Point EndPoint {get;set;} }
 public partial class LineSegment { public Windows.Foundation.Point Point {get;set;} }
 public partial class BezierSegment { public Windows.Foundation.Point Point1 {get;set;} public Windows.Foundation.Point Point2 {get;set;} public Windows.Foundation.Point Point3 {get;set;} }
}
