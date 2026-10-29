using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
namespace CelesteMusicPlayer;
internal sealed class NativeTaskbarPreview : IDisposable
{
    private readonly MainWindow _owner; private readonly nint _hwnd;
    private Bitmap? _cover; private PlaybackSnapshot _state = new();
    private string _track = "!", _key = ""; private int _generation; private bool _disposed;
    internal NativeTaskbarPreview(MainWindow owner,nint hwnd) { _owner=owner;_hwnd=hwnd;Enable();Update(); }
    internal void Enable(){int yes=1;DwmSetWindowAttribute(_hwnd,7,ref yes,4);DwmSetWindowAttribute(_hwnd,10,ref yes,4);}
    internal void Update(){if(_disposed)return;_state=_owner.PlaybackStateSource.Capture();if(_track!=_state.trackId){_track=_state.trackId;_cover?.Dispose();_cover=null;int version=++_generation;_ = LoadCover(_owner.GetCurrentPlayingItem()?.FilePath,version);}string key=_track+_state.playing+_state.favorite+(int)_state.position;if(key!=_key){_key=key;DwmInvalidateIconicBitmaps(_hwnd);}}
    private async Task LoadCover(string? path,int version){Bitmap? image=null;try{image=await Task.Run(async()=>{if(string.IsNullOrEmpty(path))return null;var file=await CoverArtService.GetDesktopCoverFileAsync(path);if(file==null)return null;using var source=Image.FromFile(file);return new Bitmap(source);});if(_disposed||version!=_generation){image?.Dispose();return;}_cover?.Dispose();_cover=image;DwmInvalidateIconicBitmaps(_hwnd);}catch(Exception e){image?.Dispose();StartupLog.WriteException("NativeTaskbarPreview.Cover",e);}}
    internal bool Handle(uint message,nint lParam){if(message!=0x323&&message!=0x326)return false;if(_disposed)return true;try{int width=message==0x323?((int)lParam>>16)&65535:720,height=message==0x323?(int)lParam&65535:420;using var bitmap=Render(Math.Clamp(width,1,1920),Math.Clamp(height,1,1080),_state,_cover);nint dib=bitmap.GetHbitmap(Color.Black);try{if(message==0x323)DwmSetIconicThumbnail(_hwnd,dib,0);else DwmSetIconicLivePreviewBitmap(_hwnd,dib,0,0);}finally{DeleteObject(dib);}}catch(Exception e){StartupLog.WriteException("NativeTaskbarPreview.Paint",e);}return true;}
    internal static Bitmap Render(int width,int height,PlaybackSnapshot state,Bitmap? cover){var bitmap=new Bitmap(width,height,PixelFormat.Format32bppPArgb);using var g=Graphics.FromImage(bitmap);g.Clear(Color.FromArgb(249,246,240));g.SmoothingMode=SmoothingMode.AntiAlias;g.InterpolationMode=InterpolationMode.HighQualityBicubic;float pad=Math.Max(8,Math.Min(width,height)*.06f),art=Math.Max(1,Math.Min(height-pad*2,width*.43f));var rect=new RectangleF(pad,(height-art)/2,art,art);if(cover!=null){float scale=Math.Min(art/cover.Width,art/cover.Height);float w=cover.Width*scale,h=cover.Height*scale;g.DrawImage(cover,new RectangleF(rect.X+(art-w)/2,rect.Y+(art-h)/2,w,h));}else{using var brush=new SolidBrush(Color.FromArgb(228,215,199));g.FillRectangle(brush,rect);using var pen=new Pen(Color.FromArgb(162,135,105),Math.Max(1,art*.012f));g.DrawEllipse(pen,rect.X+art*.2f,rect.Y+art*.2f,art*.6f,art*.6f);}float x=pad*2+art,textWidth=Math.Max(1,width-x-pad);using var ink=new SolidBrush(Color.FromArgb(48,44,40));using var muted=new SolidBrush(Color.FromArgb(137,120,103));using var title=new Font("Segoe UI",Math.Clamp(height*.075f,7,25),FontStyle.Bold,GraphicsUnit.Pixel);using var font=new Font("Segoe UI",Math.Clamp(height*.05f,6,18),FontStyle.Regular,GraphicsUnit.Pixel);using var format=new StringFormat{Trimming=StringTrimming.EllipsisCharacter};g.DrawString(state.title,title,ink,new RectangleF(x,height*.3f,textWidth,height*.24f),format);g.DrawString(state.artist,font,muted,new RectangleF(x,height*.55f,textWidth,height*.18f),format);g.DrawString(state.playing?"正在播放":"已暂停",font,muted,new RectangleF(x,height*.78f,textWidth,height*.15f),format);return bitmap;}
    public void Dispose(){_disposed=true;++_generation;_cover?.Dispose();_cover=null;}
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd,int attr,ref int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmInvalidateIconicBitmaps(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetIconicThumbnail(nint hwnd,nint bitmap,uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetIconicLivePreviewBitmap(nint hwnd,nint bitmap,nint offset,uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
