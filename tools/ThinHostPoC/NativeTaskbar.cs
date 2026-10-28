using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace CelesteMusicPlayer.ThinHost;

internal sealed class NativeTaskbar : IDisposable
{
    internal static readonly int ButtonCreatedMessage=(int)RegisterWindowMessageW("TaskbarButtonCreated");
    private ITaskbarList3? _list;
    private readonly List<nint> _icons=new();
    internal bool ApiReady=>_list!=null;
    internal bool ButtonsRegistered{get;private set;}
    internal void EnsureApi()
    {
        if(_list!=null)return;
        _list=(ITaskbarList3)new TaskbarList();_list.HrInit();
    }
    internal void Register(nint hwnd)
    {
        EnsureApi();
        if(_icons.Count==0)for(int i=0;i<3;i++)_icons.Add(CreateIcon(i));
        string[] names={"模拟上一首","模拟播放/暂停","模拟下一首"};
        var buttons=Enumerable.Range(0,3).Select(i=>new ThumbButton{mask=2|4,id=(uint)(2001+i),icon=_icons[i],tip=names[i]}).ToArray();
        int hr=_list!.ThumbBarAddButtons(hwnd,3,buttons);
        if(hr<0)Marshal.ThrowExceptionForHR(hr);
        ButtonsRegistered=true;
    }
    internal void Progress(nint hwnd,double value,double total,bool paused)
    {
        EnsureApi();_list!.SetProgressState(hwnd,paused?8:2);
        _list.SetProgressValue(hwnd,(ulong)Math.Max(0,value),(ulong)Math.Max(1,total));
    }
    private static nint CreateIcon(int index)
    {
        using var bitmap=new Bitmap(20,20);
        using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.Transparent);graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var pen=new Pen(Color.FromArgb(125,125,125),2);
        if(index==1){graphics.DrawLine(pen,7,4,7,16);graphics.DrawLine(pen,13,4,13,16);}
        else if(index==0){graphics.DrawLine(pen,4,4,4,16);graphics.DrawPolygon(pen,new[]{new Point(15,4),new Point(7,10),new Point(15,16)});}
        else{graphics.DrawLine(pen,16,4,16,16);graphics.DrawPolygon(pen,new[]{new Point(5,4),new Point(13,10),new Point(5,16)});}
        return bitmap.GetHicon();
    }
    public void Dispose()
    {
        if(_list!=null){Marshal.ReleaseComObject(_list);_list=null;}
        foreach(nint icon in _icons)DestroyIcon(icon);_icons.Clear();ButtonsRegistered=false;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ThumbButton
    {
        public uint mask,id,bitmap;public nint icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string tip;public uint flags;
    }
    [ComImport,Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]private class TaskbarList{}
    [ComImport,Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();void AddTab(nint h);void DeleteTab(nint h);void ActivateTab(nint h);void SetActiveAlt(nint h);
        void MarkFullscreenWindow(nint h,[MarshalAs(UnmanagedType.Bool)]bool full);
        void SetProgressValue(nint h,ulong value,ulong total);void SetProgressState(nint h,int state);
        void RegisterTab(nint tab,nint mdi);void UnregisterTab(nint tab);void SetTabOrder(nint tab,nint before);void SetTabActive(nint tab,nint mdi);
        [PreserveSig]int ThumbBarAddButtons(nint h,uint count,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)]ThumbButton[] buttons);
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessageW(string value);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
}
