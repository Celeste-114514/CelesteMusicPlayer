using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace Microsoft.UI.Input {
 public static class InputKeyboardSource {
  [DllImport("user32.dll")] private static extern short GetKeyState(int key);
  public static Windows.UI.Core.CoreVirtualKeyStates GetKeyStateForCurrentThread(Windows.System.VirtualKey key) {
   short state=GetKeyState((int)key); return ((state & 0x8000)!=0 ? Windows.UI.Core.CoreVirtualKeyStates.Down : 0) | ((state & 1)!=0 ? Windows.UI.Core.CoreVirtualKeyStates.Locked : 0);
  }
 }
 public enum NonClientRegionKind { Caption }
 public sealed class InputNonClientPointerSource {
  private static readonly Dictionary<ulong,InputNonClientPointerSource> Sources = new();
  private Windows.Graphics.RectInt32[] _caption=Array.Empty<Windows.Graphics.RectInt32>();
  public static InputNonClientPointerSource GetForWindowId(Microsoft.UI.WindowId id) { if (!Sources.TryGetValue(id.Value,out var source)) Sources[id.Value]=source=new(); return source; }
  public void SetRegionRects(NonClientRegionKind kind,Windows.Graphics.RectInt32[] rects) => _caption=(Windows.Graphics.RectInt32[])rects.Clone();
  internal static bool IsCaption(nint hwnd, nint lParam) {
   if (!Sources.TryGetValue((ulong)hwnd,out var source)) return false;
   var point=new System.Drawing.Point((short)(lParam.ToInt64() & 0xffff),(short)((lParam.ToInt64() >> 16)&0xffff));
   if (!ScreenToClient(hwnd,ref point)) return false;
   foreach(var r in source._caption) if(point.X>=r.X && point.Y>=r.Y && point.X<r.X+r.Width && point.Y<r.Y+r.Height) return true;
   return false;
  }
  internal static void Forget(nint hwnd) => Sources.Remove((ulong)hwnd);
  [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd,ref System.Drawing.Point point);
 }
}
namespace Microsoft.UI.Xaml.Media {
 // An on-demand native UI timer supplies the retained mini player's frame callback.
 public static class CompositionTarget {
  private static readonly System.Windows.Forms.Timer Timer=new() { Interval=15 };
  private static EventHandler<object>? _rendering;
  static CompositionTarget() { Timer.Tick+=(_,_)=>_rendering?.Invoke(null,new object()); }
  public static event EventHandler<object> Rendering { add { _rendering+=value; Timer.Start(); } remove { _rendering-=value; if(_rendering==null)Timer.Stop(); } }
 }
 public partial class ImageBrush { public object? ImageSource {get;set;} public object? Stretch {get;set;} public AlignmentX AlignmentX {get;set;} public AlignmentY AlignmentY {get;set;} }
}
namespace Microsoft.UI.Xaml.Controls {
 public partial class ProgressRing { public bool IsActive {get;set;} }
}
namespace Microsoft.UI.Xaml.Controls.Primitives {
 public partial class FlyoutBase {
  public object? PlacementTarget {get;private set;}
  public bool IsOpen {get;private set;}
  public void ShowAt(Microsoft.UI.Xaml.DependencyObject target) { PlacementTarget=target; IsOpen=true; }
  private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,Attachment> Attached = new();
  private sealed class Attachment { public FlyoutBase? Flyout; }
  public static void SetAttachedFlyout(object anchor,FlyoutBase flyout) => Attached.GetOrCreateValue(anchor).Flyout=flyout;
  public static void ShowAttachedFlyout(Microsoft.UI.Xaml.FrameworkElement anchor) => Attached.GetOrCreateValue(anchor).Flyout?.ShowAt(anchor);
 }
}
namespace Microsoft.UI.Xaml.Data {
 public sealed class CollectionViewSource { public bool IsSourceGrouped {get;set;} public System.Collections.IEnumerable? Source {get;set;} public System.Collections.IEnumerable? View => Source; }
}
