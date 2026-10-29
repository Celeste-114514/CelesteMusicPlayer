using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
namespace CelesteMusicPlayer;
public sealed partial class MainWindow
{
    private async Task<object> MiniStylesAsync(JsonElement msg, Microsoft.UI.Xaml.Window host)
    {
        string action=ReadString(msg,"action");
        switch(action)
        {
            case "select": MiniPlayerStyles.Select(ReadString(msg,"id"));break;
            case "save": MiniPlayerStyles.Add(ReadString(msg,"json"));break;
            case "remove": MiniPlayerStyles.Remove(ReadString(msg,"id"));break;
            case "import":
                var open=new FileOpenPicker();open.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(open,WinRT.Interop.WindowNative.GetWindowHandle(host));
                var file=await open.PickSingleFileAsync();if(file==null)return new { canceled=true };
                if(new FileInfo(file.Path).Length>32768)throw new ArgumentException("样式文件不能超过 32 KB");
                MiniPlayerStyles.Add(await File.ReadAllTextAsync(file.Path));break;
            case "export":
                string json=MiniPlayerStyles.Export(ReadString(msg,"id"));
                var save=new FileSavePicker();save.FileTypeChoices.Add("迷你播放器样式",new[]{".json"});save.SuggestedFileName="celeste-mini-style";
                WinRT.Interop.InitializeWithWindow.Initialize(save,WinRT.Interop.WindowNative.GetWindowHandle(host));
                var output=await save.PickSaveFileAsync();if(output==null)return new { canceled=true };await File.WriteAllTextAsync(output.Path,json);break;
            case "get": break;
            default: throw new ArgumentException("未知样式操作");
        }
        return MiniPlayerStyles.Snapshot();
    }
}
