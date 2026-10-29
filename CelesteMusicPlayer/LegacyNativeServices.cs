// Native lifecycle for the retained service adapter. No WinUI runtime or XAML window.
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
namespace Microsoft.UI.Xaml {
 public partial class Window {
  public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
  public FrameworkElement? Content {get;set;}
  public string Title {get;set;} = "";
  public ResourceDictionary Resources {get;} = new();
  private readonly System.Windows.Forms.NativeWindow _serviceHandle = new();
  private bool _closed;
  public nint NativeHandle => _serviceHandle.Handle;
  public Microsoft.UI.Windowing.AppWindow AppWindow {get;}
  public object? SystemBackdrop {get;set;}
  public bool ExtendsContentIntoTitleBar {get;set;}
  public event EventHandler<WindowActivatedEventArgs>? Activated;
  public event EventHandler<WindowEventArgs>? Closed;
  public Window() {
   _serviceHandle.CreateHandle(new System.Windows.Forms.CreateParams { Caption = "Celeste service", Style = unchecked((int)0x80000000), ExStyle = 0x80 });
   AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(new Microsoft.UI.WindowId((ulong)NativeHandle));
  }
  public void Activate() => Activated?.Invoke(this,new());
  public void SetTitleBar(object? element) { } // only the web host has visible chrome
  public void Close() { if (_closed) return; _closed=true; Closed?.Invoke(this,new()); _serviceHandle.DestroyHandle(); }
 }
 public partial class FrameworkElement {
  public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
  public event AnyEventHandler? ActualThemeChanged;
 }
 public partial class UIElement {
  public static readonly object KeyDownEvent = new(), PointerPressedEvent = new(), PointerReleasedEvent = new(), PointerCaptureLostEvent = new();
  public event AnyEventHandler? GettingFocus;
  public Windows.Foundation.Size DesiredSize {get;set;}
  public void Measure(Windows.Foundation.Size availableSize) { DesiredSize=availableSize; }
  public void Arrange(Windows.Foundation.Rect rect) { }
  public void RaiseLoaded() => Loaded?.Invoke(this,new RoutedEventArgs());
 }
}
namespace Microsoft.UI.Xaml.Media.Imaging {
 public partial class BitmapSource { public byte[] EncodedBytes {get; protected set;} = Array.Empty<byte>(); }
 public sealed partial class BitmapImage {
  public int DecodePixelWidth {get;set;} public int DecodePixelHeight {get;set;}
  public async Task SetSourceAsync(Windows.Storage.Streams.IRandomAccessStream source) {
   using var memory = new MemoryStream(); await source.AsStreamForRead().CopyToAsync(memory); SetBytes(memory.ToArray());
  }
  public void SetSource(Windows.Storage.Streams.IRandomAccessStream source) {
   using var memory = new MemoryStream(); source.AsStreamForRead().CopyTo(memory); SetBytes(memory.ToArray());
  }
  private void SetBytes(byte[] bytes) {
   using var data = SkiaSharp.SKData.CreateCopy(bytes); using var codec = SkiaSharp.SKCodec.Create(data);
   if (codec == null) throw new InvalidDataException("Image cannot be decoded.");
   EncodedBytes=bytes; PixelWidth=codec.Info.Width; PixelHeight=codec.Info.Height;
  }
 }
}
namespace Microsoft.UI.Xaml.Media {
 public partial class PathGeometry { public System.Collections.Generic.List<PathFigure> Figures {get;} = new(); public object? Transform {get;set;} }
}
namespace Microsoft.UI.Xaml.Shapes {
 public sealed partial class Path { public object? Data {get;set;} public object? StrokeLineJoin {get;set;} public object? StrokeStartLineCap {get;set;} public object? StrokeEndLineCap {get;set;} }
}
namespace Microsoft.UI.Xaml.Controls {
 public partial class QuadraticBezierSegment { public Windows.Foundation.Point Point1 {get;set;} public Windows.Foundation.Point Point2 {get;set;} }
 public enum NumberBoxSpinButtonPlacementMode { Hidden, Compact, Inline }
 public partial class NumberBox { public NumberBoxSpinButtonPlacementMode SpinButtonPlacementMode {get;set;} }
 public partial class MenuFlyoutSubItem { public string Text {get;set;} = ""; }
 public partial class ToggleMenuFlyoutItem { public string Text {get;set;} = ""; public object? Icon {get;set;} public object? Tag {get;set;} public bool IsChecked {get;set;} }
 public partial class FlyoutShowOptions { public Windows.Foundation.Point Position {get;set;} public Primitives.FlyoutPlacementMode Placement {get;set;} }
 public partial class Flyout { public Primitives.FlyoutPlacementMode Placement {get;set;} }
 public class ControlTemplate : Microsoft.UI.Xaml.DependencyObject { }
 public partial class Control { public ControlTemplate? Template {get;set;} }
 public partial class ItemIndexRange { public ItemIndexRange(int firstIndex,uint length) { FirstIndex=firstIndex; Length=length; } public int FirstIndex {get;} public uint Length {get;} }

}
namespace Microsoft.UI.Xaml.Controls.Primitives {
 public enum FlyoutPlacementMode { Auto, Top, Bottom, Left, Right, Full, TopEdgeAlignedLeft, TopEdgeAlignedRight, BottomEdgeAlignedLeft, BottomEdgeAlignedRight, LeftEdgeAlignedTop, LeftEdgeAlignedBottom, RightEdgeAlignedTop, RightEdgeAlignedBottom }
 public partial class Popup {
  public object? XamlRoot {get;set;} public Microsoft.UI.Xaml.UIElement? Child {get;set;} public bool IsOpen {get;set;} public bool IsHitTestVisible {get;set;}
  public double HorizontalOffset {get;set;} public double VerticalOffset {get;set;}
 }
}
namespace Microsoft.UI.Xaml.Media.Animation {
 public readonly struct RepeatBehavior { public static RepeatBehavior Forever => new(); }
}
