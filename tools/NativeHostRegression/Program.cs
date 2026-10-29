using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
namespace Microsoft.UI.Xaml { public delegate void AnyEventHandler(dynamic sender,dynamic args); }
namespace CelesteMusicPlayer { internal static class StartupLog { public static void WriteException(string where,Exception error) => throw new Exception(where,error); } }
internal static class Program {
 [STAThread] private static int Main() {
  bool native=CheckNativeWindow();
  int owner=Environment.CurrentManagedThreadId, once=0, repeated=0, canceled=0;
  bool fromWorker=false, wrongThread=false;
  var order=new List<string>();var queue=DispatcherQueue.GetForCurrentThread();
  queue.TryEnqueue(DispatcherQueuePriority.Low,()=>order.Add("low"));
  queue.TryEnqueue(DispatcherQueuePriority.High,()=>order.Add("high"));
  queue.TryEnqueue(()=>order.Add("normal"));
  var single=queue.CreateTimer();single.Interval=TimeSpan.FromMilliseconds(15);single.IsRepeating=false;
  single.Tick+=(_,_)=>{once++;wrongThread|=Environment.CurrentManagedThreadId!=owner;};single.Start();
  var repeat=queue.CreateTimer();repeat.Interval=TimeSpan.FromMilliseconds(20);
  repeat.Tick+=(_,_)=>{repeated++;wrongThread|=Environment.CurrentManagedThreadId!=owner;if(repeated==3)repeat.Stop();};repeat.Start();
  var cancel=queue.CreateTimer();cancel.Interval=TimeSpan.FromMilliseconds(10);cancel.Tick+=(_,_)=>canceled++;cancel.Start();cancel.Stop();
  Task.Run(()=>{if(queue.HasThreadAccess)wrongThread=true;queue.TryEnqueue(()=>{fromWorker=Environment.CurrentManagedThreadId==owner;});});
  var finish=queue.CreateTimer();finish.Interval=TimeSpan.FromMilliseconds(300);finish.IsRepeating=false;finish.Tick+=(_,_)=>NativeMessagePump.Quit();finish.Start();
  NativeMessagePump.Run();
  bool ok=native && once==1 && repeated==3 && canceled==0 && fromWorker && !wrongThread && !single.IsRunning && string.Join(",",order)=="high,normal,low";
  Console.WriteLine($"{(ok?"PASS":"FAIL")} native={native} once={once} repeat={repeated} canceled={canceled} worker={fromWorker} wrongThread={wrongThread} priority={string.Join(",",order)}");
  return ok?0:1;
 }
 [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint ReadStyle(nint hwnd,int index);
 private static bool CheckNativeWindow() {
  using var form=new System.Windows.Forms.Form { ShowInTaskbar=false };
  nint handle=form.Handle;
  var window=Microsoft.UI.Windowing.AppWindow.GetFromWindowId(new Microsoft.UI.WindowId((ulong)handle));
  var presenter=(Microsoft.UI.Windowing.OverlappedPresenter)window.Presenter;
  presenter.SetBorderAndTitleBar(true,true);
  window.TitleBar.PreferredHeightOption=Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed;
  bool caption=(ReadStyle(handle,-16).ToInt64()&0x00C00000)==0;
  presenter.IsResizable=false;bool resize=(ReadStyle(handle,-16).ToInt64()&0x00040000)==0;
  presenter.IsAlwaysOnTop=true;bool top=form.TopMost;
  presenter.IsAlwaysOnTop=false;top&=!form.TopMost;
  var before=window.Position;var size=window.Size;
  window.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
  var screen=System.Windows.Forms.Screen.FromHandle(handle).Bounds;
  bool fullscreen=window.Size.Width==screen.Width && window.Size.Height==screen.Height;
  window.SetPresenter(presenter);
  bool restored=window.Position.X==before.X && window.Position.Y==before.Y && window.Size.Width==size.Width && window.Size.Height==size.Height;
  Console.WriteLine($"caption={caption} resize={resize} top={top} fullscreen={fullscreen} restored={restored} visible={form.Visible} size={window.Size.Width}x{window.Size.Height} before={size.Width}x{size.Height} screen={screen.Width}x{screen.Height}");
  return caption && resize && top && fullscreen && restored && !form.Visible;
 }

}
