using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using CelesteMusicPlayer;

internal static class Program
{
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] private static extern int GetGuiResources(nint process, uint flag);
    private static void Check(bool value, string note) { if (!value) throw new Exception(note); }
    private static void Click(NativeDesktopPopup window, float x, float y) => SendMessageW(window.Handle, 0x202, 0, (nint)(((int)(y * window.Scale) << 16) | (ushort)(x * window.Scale)));
    [STAThread]
    private static void Main()
    {
        // Windows are created hidden and never shown/activated. Messages target only this process.
        var clock=Stopwatch.StartNew();
        using var popup=new NativeDesktopPopup(false);
        long coldMs=clock.ElapsedMilliseconds;
        Check(!popup.Visible,"Constructor showed a window");
        int clicks=0,doubles=0,keys=0,paints=0; Exception? failure=null;
        popup.Click=_=>clicks++; popup.DoubleClick=_=>doubles++; popup.Key=_=>keys++;
        popup.Failed=ex=>failure=ex;
        popup.Paint=(g,w,h)=>{paints++; DesktopPopupDrawing.Fill(g,Color.Beige,new RectangleF(0,0,w,h)); DesktopPopupDrawing.Icon(g,"togglePlay",new RectangleF(8,8,20,20),Color.Black);};
        Click(popup,15,15); SendMessageW(popup.Handle,0x203,0,0); Click(popup,15,15);
        Check(clicks==1&&doubles==1,"Double-click generated a second single click");
        SendMessageW(popup.Handle,0x100,27,0); SendMessageW(popup.Handle,0x10,0,0); SendMessageW(popup.Handle,0xf,0,0);
        Check(keys==2&&paints==1&&failure==null,"Native key/close/paint dispatch failed: "+failure);
        TestMenu();
        TestTaskbar();
        int before=GetGuiResources(Process.GetCurrentProcess().Handle,0);
        for(int i=0;i<40;i++){using var item=new NativeDesktopPopup(false); SendMessageW(item.Handle,0xf,0,0);}
        int after=GetGuiResources(Process.GetCurrentProcess().Handle,0);
        Check(after<=before+3,$"GDI objects leaked: {before} -> {after}");
        Console.WriteLine($"PASS: actual hidden HWND input/paint, menu -> command adapter, DPI/fit geometry, disabled/unknown/separator guards, keyboard and resource release; cold native HWND {coldMs}ms, GDI {before}->{after}");
    }
    private static void TestTaskbar()
    {
        var owner=new MainWindow();
        using var bar=new TaskbarPlayerWindow(owner,true);
        var native=(NativeDesktopPopup)typeof(TaskbarPlayerWindow).GetField("_window",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(bar)!;
        Check(!native.Visible,"Suspended taskbar must start hidden");
        float width=NativeDesktopPopup.Bounds(native.Handle).Width/native.Scale;
        var actions=new[]{"previous","togglePlay","next"};
        for(int i=0;i<3;i++) { Click(native,width-80+i*27,20); Check(owner.LastAction==actions[i],"Native taskbar button failed: "+actions[i]); }
        owner.LastAction="";
        typeof(TaskbarPlayerWindow).GetField("_state",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(bar,new PlaybackSnapshot{busy=true});
        Click(native,width-80,20);Check(owner.LastAction=="","Busy taskbar dispatched transport");
        SendMessageW(native.Handle,0x203,0,(nint)(20<<16|20));
        Check(owner.LastAction=="showMain","Native taskbar double-click did not restore main window");
        Click(native,20,20);
        typeof(TaskbarPlayerWindow).GetField("_state",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(bar,new PlaybackSnapshot{title="歌曲标题 A long song name",artist="演唱者 Artist",playing=true,duration=100,position=35});
        using var bitmap=new Bitmap(720,96);
        using(var graphics=Graphics.FromImage(bitmap)){graphics.ScaleTransform(2,2);native.Paint!(graphics,360,48);}
        bitmap.Save("outputs/native-taskbar.png");
    }
    private static void TestMenu()
    {
        var owner=new MainWindow(); var commands=new TrayCommandAdapter(owner);
        using var menu=new TrayMenuWindow();
        var items=TrayMenuModel.Build(owner,null);
        items.Insert(1,new TrayMenuModel.Item{label="disabled",actionId="exit",enabled=false});
        items.Insert(2,new TrayMenuModel.Item{label="unknown",actionId="https://invalid.example/"});
        var native=(NativeDesktopPopup)typeof(TrayMenuWindow).GetField("_window",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(menu)!;
        menu.Invoked+=action=>{if(action!="openLogs")commands.Invoke(action);};
        int forwarded=0;menu.Invoked+=_=>forwarded++;
        foreach(float textScale in new[]{.8f,1f,1.5f})
        foreach(float fit in new[]{1f,.65f})
        {
            menu.UpdateContent(items,new Dictionary<string,string>{{"text-scale",textScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"light");
            typeof(TrayMenuWindow).GetField("_fit",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(menu,fit);
            var layout=new DesktopMenuLayout(items.Select(i=>i.separator),textScale);
            for(int i=0;i<items.Count;i++)
            {
                int before=forwarded; var item=items[i];var row=layout.Rows[i];
                Click(native,100*fit,(row.Top+row.Height/2)*fit);
                bool allowed=!item.separator&&item.enabled&&TrayMenuModel.IsKnownAction(item.actionId);
                Check(forwarded-before==(allowed?1:0),$"Row click mismatch at {i}, scale {textScale}, fit {fit}");
                if(allowed&&item.actionId!="openLogs")Check(owner.LastAction==item.actionId,"Wrong command target: "+item.actionId);
            }
            int previous=forwarded; Click(native,0,0); Click(native,100,layout.Height+50);
            Check(forwarded==previous,"Background clicks invoked a command");
        }
        // Last item is Exit: Up from no selection, then Enter.
        foreach(string mode in new[]{"light","dark"})
        {
            var renderItems=TrayMenuModel.Build(owner,null);
            menu.UpdateContent(renderItems,new(),mode);
            var layout=new DesktopMenuLayout(renderItems.Select(i=>i.separator),1);
            using var bitmap=new Bitmap(564,(int)(layout.Height*2));
            using(var graphics=Graphics.FromImage(bitmap)){graphics.ScaleTransform(2,2);native.Paint!(graphics,282,layout.Height);}
            Directory.CreateDirectory("outputs");bitmap.Save("outputs/native-tray-"+mode+".png");
        }
        menu.UpdateContent(items,new(),"dark"); SendMessageW(native.Handle,0x100,38,0); SendMessageW(native.Handle,0x100,13,0);
        Check(owner.LastAction=="exit","Keyboard could not invoke Exit");
        Check(owner.Exits==7,"Exit callback did not execute exactly once per click/key");
        Check(menu.IsReady&&!menu.IsVisible,"Menu should be immediately ready and hidden");
    }
}
