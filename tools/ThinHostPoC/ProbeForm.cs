using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CelesteMusicPlayer.ThinHost;

internal sealed class ProbeForm : Form, IMainWindowSurface
{
    private const string Origin="https://celeste.poc/index.html";
    private readonly bool _selfTest;
    private readonly WebView2 _web=new() { Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(245,243,239) };
    private readonly ProbeSession _session=new();
    private readonly MainWindowCoordinator _windows;
    private readonly NativeTaskbar _taskbar=new();
    private Task? _initialization;
    private readonly TaskCompletionSource<bool> _pageTest=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Passed { get; private set; }
    internal ProbeForm(bool selfTest)
    {
        _selfTest=selfTest;_windows=new(this);
        Text="Celeste W5 · 独立薄宿主验证（无播放功能）";ClientSize=new(980,680);
        MinimumSize=new(600,450);StartPosition=FormStartPosition.CenterScreen;
        ShowInTaskbar=!selfTest;Icon=SystemIcons.Application;
        Controls.Add(_web);
        Shown+=async(_,_)=>{try { await InitializeAsync(); } catch(Exception ex) { Program.Record(ex.ToString()); MessageBox.Show(this,ex.Message,"W5 初始化失败"); } };
        FormClosed+=(_,_)=>{_web.Dispose();_taskbar.Dispose();Program.Record("Window closed; browser control and taskbar resources released");};
    }
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(!_selfTest&&value);
    private Task InitializeAsync()=>_initialization??=InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        _= _web.Handle;
        Program.Record("STA="+Thread.CurrentThread.GetApartmentState()+" HWND="+Handle);
        var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Program.RunDirectory,"WebView2"));
        await _web.EnsureCoreWebView2Async(environment);
        var core=_web.CoreWebView2;
        core.Settings.IsWebMessageEnabled=true;core.Settings.AreDefaultContextMenusEnabled=false;
        core.Settings.AreDevToolsEnabled=false;core.Settings.IsStatusBarEnabled=false;
        core.SetVirtualHostNameToFolderMapping("celeste.poc",Path.Combine(AppContext.BaseDirectory,"Web"),CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting+=(_,e)=>{if(e.Uri!=Origin)e.Cancel=true;};
        core.NewWindowRequested+=(_,e)=>e.Handled=true;
        core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
        core.WebMessageReceived+=OnMessage;
        var loaded=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted+=(_,e)=>loaded.TrySetResult(e.IsSuccess);
        core.Navigate(Origin);
        if(await Task.WhenAny(loaded.Task,Task.Delay(20000))!=loaded.Task||!await loaded.Task)throw new Exception("PoC page failed to load");
        Program.Record("WebView2 navigation completed");
        _taskbar.EnsureApi();Program.Record("ITaskbarList3 API initialized");
    }
    private void OnMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? id=null;
        try
        {
            if(args.Source!=Origin||args.WebMessageAsJson.Length>16384)return;
            using var doc=JsonDocument.Parse(args.WebMessageAsJson);var root=doc.RootElement;
            if(root.GetProperty("kind").GetString()!="celeste.poc.command"||root.GetProperty("version").GetInt32()!=1)return;
            id=root.GetProperty("requestId").GetString();if(id?.Length is not (>0 and <=80))return;
            string action=root.GetProperty("action").GetString()??"";
            if(action=="selftestComplete")
            {
                _pageTest.TrySetResult(root.GetProperty("passed").GetBoolean());Reply(id,true,new { received=true });return;
            }
            if(action is "window.restore" or "window.minimize" or "window.close")
            {
                if(_selfTest)throw new InvalidOperationException("Visible window actions are disabled in hidden tests");
                Reply(id,true,new { accepted=true });
                BeginInvoke(new Action(()=>{if(action=="window.restore")_windows.Show();else if(action=="window.minimize")WindowState=FormWindowState.Minimized;else _windows.Close();}));
                return;
            }
            object state=_session.Handle(action);
            Reply(id,true,state);UpdateTaskbar();Program.Record("Bridge: "+action+" request="+id);
        }
        catch(Exception ex){Program.Record("Bridge rejected: "+ex.Message);if(id!=null)Reply(id,false,new {error=ex.Message});}
    }
    private void Reply(string id,bool ok,object data)=>_web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {kind="celeste.poc.reply",version=1,requestId=id,ok,data}));
    private void UpdateTaskbar()
    {
        if(_selfTest||!_taskbar.ButtonsRegistered)return;
        var state=_session.Capture();_taskbar.Progress(Handle,state.position,state.duration,!state.playing);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if(_selfTest)return;
        if(m.Msg==NativeTaskbar.ButtonCreatedMessage)
        {
            try{_taskbar.Register(Handle);UpdateTaskbar();Program.Record("Thumbnail buttons registered on visible HWND");}
            catch(Exception ex){Program.Record("Taskbar registration failed: "+ex);}
        }
        if(m.Msg==0x111&&((m.WParam.ToInt64()>>16)&0xffff)==0x1800)
        {
            string? action=(m.WParam.ToInt64()&0xffff) switch {2001=>"previous",2002=>"togglePlay",2003=>"next",_=>null};
            if(action!=null)BeginInvoke(new Action(()=>{_session.Commands.TryExecute(action);UpdateTaskbar();if(_web.CoreWebView2!=null)_web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {kind="celeste.poc.state",state=_session.Capture()}));}));
        }
    }
    internal async Task SelfTestAsync()
    {
        try
        {
            await InitializeAsync();
            await _web.CoreWebView2.ExecuteScriptAsync("window.runHostTests()");
            if(await Task.WhenAny(_pageTest.Task,Task.Delay(20000))!=_pageTest.Task||!await _pageTest.Task)throw new Exception("Real WebView2 bridge self-test failed/timed out");
            string[] forbidden={"Microsoft.UI.Xaml","Microsoft.WindowsAppSDK","Microsoft.WinUI"};
            string[] modules=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetName().Name??"").ToArray();
            if(modules.Any(m=>forbidden.Any(f=>m.Contains(f,StringComparison.OrdinalIgnoreCase))))throw new Exception("WinUI dependency loaded");

            // W6：设置读写边界。上面已经证明没加载 WinUI，而 AppSettingsStore/WebSettingsSchema
            // 同样编译进了这个宿主 —— 说明设置读写对非 WinUI 宿主可用。
            SettingsBoundary.Result boundary=SettingsBoundary.Run();
            Program.Record($"Settings boundary: probed={boundary.FieldsProbed} cloneMismatch={boundary.CloneMismatches} schemaMismatch={boundary.SchemaMismatches} defsMissingReader={boundary.DefsMissingReader}");
            foreach(string line in boundary.Details.Take(20))Program.Record("  settings: "+line);
            if(boundary.FieldsProbed==0)throw new Exception("Settings boundary probe touched no field; check aborted");
            if(boundary.CloneMismatches>0||boundary.SchemaMismatches>0||boundary.DefsMissingReader>0)
                throw new Exception("Settings boundary check failed: "+string.Join(" | ",boundary.Details.Take(6)));

            // W6 第二步：曲库/媒体库边界。临时库 + 临时 JSON 目录，全程不碰用户真实曲库/设置。
            LibraryBoundary.Result library=LibraryBoundary.Run();
            Program.Record($"Library boundary: checks={library.Checks} passed={library.Passed}");
            foreach(string line in library.Details)Program.Record("  library: "+line);
            if(!library.Passed)throw new Exception("Library boundary check failed: "+string.Join(" | ",library.Details.Where(d=>d.Contains("FAIL")).Take(6)));

            // W6 第三步：歌词/CUE/M3U 解析链边界。临时目录，全程不碰用户真实文件。
            ParsingBoundary.Result parsing=ParsingBoundary.Run();
            Program.Record($"Parsing boundary: checks={parsing.Checks} passed={parsing.Passed}");
            foreach(string line in parsing.Details)Program.Record("  parsing: "+line);
            if(!parsing.Passed)throw new Exception("Parsing boundary check failed: "+string.Join(" | ",parsing.Details.Where(d=>d.Contains("FAIL")).Take(6)));

            // W6 第四步：播放顺序/队列/浏览规则/续播与 OPRA 历史。纯逻辑 + 临时文件。
            ServicesBoundary.Result services=ServicesBoundary.Run();
            Program.Record($"Services boundary: checks={services.Checks} passed={services.Passed}");
            foreach(string line in services.Details)Program.Record("  services: "+line);
            if(!services.Passed)throw new Exception("Services boundary check failed: "+string.Join(" | ",services.Details.Where(d=>d.Contains("FAIL")).Take(6)));

            Passed=true;Program.Record("PASS: actual WebView2 JS→C#→JS roundtrip using W1 contracts; WinUI not loaded");
            Program.WriteReport(true,new {runtime=Environment.Version.ToString(),modules,nativeTaskbarApi=_taskbar.ApiReady,settingsBoundary=boundary,libraryBoundary=library,parsingBoundary=parsing,servicesBoundary=services,visibleTaskbarButtons="manual verification pending",simulation=true});
        }
        catch(Exception ex){Program.Record(ex.ToString());Program.WriteReport(false,new {error=ex.ToString()});}
        finally{Close();}
    }
    bool IMainWindowSurface.Exists=>!IsDisposed;
    bool IMainWindowSurface.IsMinimized=>WindowState==FormWindowState.Minimized;
    void IMainWindowSurface.Create()=>throw new InvalidOperationException("PoC uses a single native form");
    void IMainWindowSurface.Restore()=>WindowState=FormWindowState.Normal;
    void IMainWindowSurface.Show()=>Show();void IMainWindowSurface.Activate()=>Activate();
    void IMainWindowSurface.HideLegacyOwner(){} // This host has no legacy owner at all.
    void IMainWindowSurface.Navigate(string page){} // Single diagnostics page in W5.
    void IMainWindowSurface.Close()=>Close();
}
