using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CelesteMusicPlayer;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string artifacts = args.Length > 1 && args[0] == "--isolated-child" ? args[1] : args.Length > 0 ? args[0] : AppContext.BaseDirectory;
        Directory.CreateDirectory(artifacts);
        if (args.Length == 0 || args[0] != "--isolated-child") return LaunchIsolated(artifacts);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var host = new ProbeWindow();
        host.Shown += async (_,_) =>
        {
            try { await Run(host, artifacts); }
            catch (Exception error) { File.WriteAllText(Path.Combine(artifacts,"error.txt"),error.ToString()); Environment.ExitCode = 1; }
            finally { host.Close(); }
        };
        Application.Run(host);
        return Environment.ExitCode;
    }
    static int LaunchIsolated(string artifacts)
    {
        // Assign the private desktop at process creation, before STA/OLE creates HWNDs.
        // Never call SwitchDesktop: no test window can appear on the user's desktop.
        string name="CelesteChromeTest-"+Environment.ProcessId;
        nint desktop=CreateDesktopW(name,0,0,0,0x10000000,0);
        if(desktop==0)throw new Exception("CreateDesktop: "+Marshal.GetLastWin32Error());
        try
        {
            var startup=new StartupInfo { Size=Marshal.SizeOf<StartupInfo>(), Desktop="winsta0\\"+name };
            var command=new StringBuilder("\""+Environment.ProcessPath+"\" --isolated-child \""+artifacts+"\"");
            if(!CreateProcessW(null,command,0,0,false,0x08000000,0,null,ref startup,out var process))throw new Exception("CreateProcess: "+Marshal.GetLastWin32Error());
            try
            {
                if(WaitForSingleObject(process.Process,45000)!=0){TerminateProcess(process.Process,2);throw new TimeoutException("Isolated native chrome test timed out");}
                GetExitCodeProcess(process.Process,out uint code);
                string error=Path.Combine(artifacts,"error.txt"),results=Path.Combine(artifacts,"results.txt");
                if(code!=0){if(File.Exists(error))Console.Error.WriteLine(File.ReadAllText(error));return (int)code;}
                Console.WriteLine(File.ReadAllText(results));
                Console.WriteLine("PASS all native/browser geometry and full-width-header checks on isolated desktop");
                return 0;
            }
            finally{CloseHandle(process.Thread);CloseHandle(process.Process);}
        }
        finally{CloseDesktop(desktop);}
    }
    static async Task Run(ProbeWindow host, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        await host.Web.EnsureCoreWebView2Async();
        host.Web.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255,32,32,32);
        host.Web.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
        var loaded = new TaskCompletionSource();
        host.Web.CoreWebView2.NavigationCompleted += (_,_) => loaded.TrySetResult();
        string materialCss = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WebShell", "platform-materials.css"));
        host.Web.CoreWebView2.NavigateToString("""
            <!doctype html><html><meta charset="utf-8"><style>
            *{box-sizing:border-box}html,body{margin:0;width:100%;height:100%;background:transparent}
            header{height:58px;background:#4b4b4b;color:white;padding:18px}
            main{height:calc(100% - 58px);background:#333;color:#eee;padding:30px}
            </style><style>
            """ + materialCss + """
            </style><header class="titlebar"><button style="float:right">Control</button>Native chrome fixture</header>
            <main>Geometry regression fixture</main></html>
            """);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var reports = new List<string>();
        nint caption = 0;
        Point captionPoint = Point.Empty;
        async Task Verify(string step)
        {
            await Task.Delay(160);
            if (host.FormBorderStyle != FormBorderStyle.None) throw new Exception(step + ": managed border model must be None");
            if ((GetWindowLongPtrW(host.Handle,-16).ToInt64() & 0x00c00000) != 0) throw new Exception(step + ": native caption still present");
            var expected = host.RectangleToScreen(host.ClientRectangle);
            var actual = host.Web.RectangleToScreen(host.Web.ClientRectangle);
            if (host.Web.Bounds != host.ClientRectangle || expected != actual) throw new Exception(step + ": panel mismatch " + host.Web.Geometry + " expected=" + expected);
            if (host.WindowState == FormWindowState.Normal)
            {
                int midX = expected.Left + expected.Width / 2;
                int midY = expected.Top + expected.Height / 2;
                if (HitTest(host.Handle, expected.Left - 1, midY) != 10 ||
                    HitTest(host.Handle, expected.Right, midY) != 11 ||
                    HitTest(host.Handle, midX, expected.Top - 1) != 12 ||
                    HitTest(host.Handle, midX, expected.Bottom) != 15)
                    throw new Exception(step + ": native sizing frame no longer hit-tests on all four sides");
            }
            int chrome = 0;
            var captionRects = new List<Rectangle>();
            var failures = new List<string>();
            EnumChildWindows(host.Web.Handle, (hwnd,_) =>
            {
                var name = new StringBuilder(100); GetClassNameW(hwnd,name,name.Capacity);
                if (step == "01-normal" && name.ToString() == "Chrome_WidgetWin_0")
                {
                    GetWindowRect(hwnd, out var region);
                    var rect = Rectangle.FromLTRB(region.Left, region.Top, region.Right, region.Bottom);
                    if (HitTest(hwnd, rect.Left + 3, rect.Top + 3) == 2)
                    {
                        captionRects.Add(rect);
                        var middle = new Point(expected.Left + expected.Width / 2, expected.Top + 20);
                        if (rect.Contains(middle)) { caption = hwnd; captionPoint = middle; }
                    }
                }
                if (name.ToString() == "Chrome_WidgetWin_0" && IsBrowserSurface(hwnd))
                {
                    chrome++; GetWindowRect(hwnd,out var rect);
                    var browser = Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
                    if (browser != expected) failures.Add("browser=" + browser + " expected=" + expected);
                }
                return true;
            },0);
            if (step == "01-normal")
            {
                if (caption == 0) throw new Exception("WebView2 did not create an HTCAPTION region");
                string button = await host.Web.CoreWebView2.ExecuteScriptAsync("""
                    (() => { const rect = document.querySelector('button').getBoundingClientRect();
                        return [rect.x + rect.width / 2, rect.y + rect.height / 2]; })()
                    """);
                using var coordinates = System.Text.Json.JsonDocument.Parse(button);
                var center = coordinates.RootElement;
                var buttonPoint = new Point(expected.Left + (int)(center[0].GetDouble() * host.DeviceDpi / 96),
                    expected.Top + (int)(center[1].GetDouble() * host.DeviceDpi / 96));
                if (captionRects.Exists(rect => rect.Contains(buttonPoint))) throw new Exception("Native caption overlaps the button");
                reports.Add("PASS WebView2 native HTCAPTION regions exclude interactive controls");
            }
            if (chrome == 0 || failures.Count != 0) throw new Exception(step + ": child HWND mismatch " + string.Join(";",failures));
            if (host.WindowState == FormWindowState.Maximized && expected != Screen.FromHandle(host.Handle).WorkingArea) throw new Exception(step + ": maximized client does not match work area " + expected);
            var script = await host.Web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({w:innerWidth,h:innerHeight,dpr:devicePixelRatio})");
            using var stream = new MemoryStream();
            await host.Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);
            stream.Position=0;
            using var bitmap=new Bitmap(stream);
            foreach(int x in new[]{2,bitmap.Width/2,bitmap.Width-3})
            {
                Color pixel=bitmap.GetPixel(x,20);
                if(Math.Abs(pixel.R-75)>2 || Math.Abs(pixel.G-75)>2 || Math.Abs(pixel.B-75)>2) throw new Exception(step+": incomplete header at "+x+" color="+pixel);
            }
            File.WriteAllBytes(Path.Combine(artifacts,step+".png"),stream.ToArray());
            string report=step+" PASS client="+expected+" "+host.Web.Geometry+" viewport="+script;
            reports.Add(report); Console.WriteLine(report);
        }
        host.Bounds=new Rectangle(80,70,1200,800); await Verify("01-normal");
        // Send only to the input-region HWND belonging to our isolated test fixture.
        // Verify the real WebView2 caption path maximizes the top-level Form.
        if (SendMessageTimeoutW(caption, 0xA3, 2, PackPoint(captionPoint.X, captionPoint.Y), 2, 1000, out _) == 0)
            throw new Exception("Native caption double-click timed out");
        await Task.Delay(160);
        if (host.WindowState != FormWindowState.Maximized) throw new Exception("Native caption did not maximize the top-level window");
        await Verify("02-caption-maximize");
        host.WindowState = FormWindowState.Normal;
        for(int cycle=0;cycle<3;cycle++)
        {
            host.WindowState=FormWindowState.Maximized; await Verify($"{cycle}-maximized");
            host.WindowState=FormWindowState.Normal; await Verify($"{cycle}-restored");
            host.Bounds=new Rectangle(170+cycle*37,100+cycle*31,1000+cycle*93,700+cycle*41); await Verify($"{cycle}-top-left-resize");
            // Reproduce the child translation captured during the actual snap/restore failure.
            SetWindowPos(host.Web.Handle,0,-685,-290,host.ClientSize.Width,host.ClientSize.Height,0x0014);
            await Verify($"{cycle}-translated-child");
        }
        File.WriteAllLines(Path.Combine(artifacts,"results.txt"),reports);
        Console.WriteLine("PASS all native/browser geometry and full-width-header checks on isolated desktop");
    }
    internal sealed class ProbeWindow : NativeMainWebWindow
    {
        protected override bool ClientOnlyChrome => true;
        internal readonly NativeWebViewPanel Web = new() { Dock=DockStyle.Fill };
        internal ProbeWindow() : base(DispatcherQueue.GetForCurrentThread())
        {
            Controls.Add(Web); BackColor=Color.FromArgb(32,32,32);
            SetFrameless(Handle); SetMaterial(Handle,1,1);
        }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo { public int Size;public string? Reserved,Desktop,Title;public int X,Y,Width,Height,CharsX,CharsY,Fill,Flags;public short Show,ReservedSize;public nint ReservedPointer,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public nint Process,Thread;public int ProcessId,ThreadId; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessW(string? app,StringBuilder command,nint processAttributes,nint threadAttributes,bool inherit,uint flags,nint environment,string? cwd,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint handle,uint timeout);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint handle,out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(nint handle,uint code);
    static bool IsBrowserSurface(nint window)
    {
        bool found = false;
        EnumChildWindows(window, (child, _) =>
        {
            var name = new StringBuilder(100);
            GetClassNameW(child, name, name.Capacity);
            found = name.ToString() == "Chrome_RenderWidgetHostHWND";
            return !found;
        }, 0);
        return found;
    }
    static int HitTest(nint window, int x, int y)
    {
        return SendMessageTimeoutW(window, 0x84, 0, PackPoint(x, y), 2, 200, out nint result) == 0 ? 0 : (int)result;
    }
    static nint PackPoint(int x, int y) => unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16)));
    [DllImport("user32.dll")] static extern nint SendMessageTimeoutW(nint window,uint message,nint wParam,nint lParam,uint flags,uint timeout,out nint result);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    delegate bool EnumCallback(nint hwnd,nint state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint CreateDesktopW(string name,nint device,nint mode,uint flags,uint access,nint security);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(nint hwnd,EnumCallback callback,nint state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(nint hwnd,StringBuilder text,int count);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] static extern nint GetWindowLongPtrW(nint hwnd,int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int w,int h,uint flags);
}
