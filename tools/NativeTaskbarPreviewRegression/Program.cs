using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using CelesteMusicPlayer;
using System.Runtime.InteropServices;
class Program {
 [DllImport("user32.dll")] static extern int GetGuiResources(nint process,uint flags);
 [STAThread] static void Main(){using var form=new System.Windows.Forms.Form();using var preview=new NativeTaskbarPreview(new MainWindow(),form.Handle);var state=new PlaybackSnapshot{title="封面缩略图 · 很长的曲名",artist="Celeste",playing=true};using var art=new Bitmap(240,120);using(var g=Graphics.FromImage(art))g.Clear(Color.DarkOrange);using(var b=NativeTaskbarPreview.Render(320,180,state,art)){b.Save("outputs/w10-thumbnail.png",ImageFormat.Png);if(b.GetPixel(30,90).ToArgb()==b.GetPixel(319,179).ToArgb())throw new Exception("cover not rendered");}GC.Collect();GC.WaitForPendingFinalizers();int before=GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle,0);for(int i=0;i<100;i++){using var b=NativeTaskbarPreview.Render(i%2==0?320:1,i%2==0?180:1,state,i%2==0?art:null);if(!preview.Handle(0x323,(nint)((320<<16)|180)))throw new Exception("DWM request not handled");}int after=GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle,0);if(after>before+2)throw new Exception($"GDI leak {before}->{after}");Console.WriteLine($"PASS native cover/placeholder rendering, tiny dimensions, hidden DWM requests and GDI {before}->{after}");}
}
namespace CelesteMusicPlayer { internal sealed class MainWindow { internal IPlaybackStateSource PlaybackStateSource=new Source(); internal Track? GetCurrentPlayingItem()=>null; } internal sealed class Track{internal string FilePath="";} internal sealed class Source:IPlaybackStateSource { public PlaybackSnapshot Capture()=>new(); } internal static class CoverArtService{internal static Task<string?> GetDesktopCoverFileAsync(string path)=>Task.FromResult<string?>(null);}internal static class StartupLog{internal static void WriteException(string source,Exception error)=>throw new Exception(source,error);} }
