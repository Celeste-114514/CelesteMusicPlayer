// 只给冒烟用的替身：同一个 exe，按文件名分两种角色。
//   *…Setup*.exe  → 扮演 NSIS：解析 /D=<目录>，把「主程序」和主界面资源铺进去，退 0；
//   其它名字       → 扮演主程序：只留一个「我被启动了」的标记文件，绝不弹窗。
using System.Diagnostics;
using System.Text;

string? self = Environment.ProcessPath;
string exeName = self == null ? string.Empty : Path.GetFileNameWithoutExtension(self);
var log = new StringBuilder();

void Mark(string text)
{
    log.AppendLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {text}");
}

try
{
    if (exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase))
    {
        // NSIS 的 /D= 是「最后一个参数、不加引号、余下整行都是路径」，
        // 所以必须从原始命令行里截，GetCommandLineArgs() 会按空格切碎。
        string raw = Environment.CommandLine;
        int at = raw.LastIndexOf("/D=", StringComparison.OrdinalIgnoreCase);
        string target = at < 0
            ? Path.Combine(Path.GetTempPath(), "fake-install")
            : raw[(at + 3)..].Trim().Trim('"');
        target = target.TrimEnd('\\');

        Mark($"setup: 装到 {target}");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(target, "WebShell"));

        string appCopy = Path.Combine(target, "CelesteMusicPlayer.exe");
        File.Copy(self!, appCopy, overwrite: true);

        // 框架依赖的 exe 没有 runtimeconfig/deps 就是一堆跑不起来的字节，
        // 按新名字一起铺过去（.NET 宿主只认 <exe名>.runtimeconfig.json）。
        string selfDir = Path.GetDirectoryName(self!)!;
        string selfBase = Path.GetFileNameWithoutExtension(self!);
        foreach (string suffix in new[] { ".runtimeconfig.json", ".deps.json" })
        {
            string src = Path.Combine(selfDir, selfBase + suffix);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(target, "CelesteMusicPlayer" + suffix), overwrite: true);
            }
        }

        File.WriteAllText(Path.Combine(target, "WebShell", "index.html"), "<!doctype html><title>fake</title>");
        Mark("setup: 文件已铺好");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "fake-setup.log"), log.ToString());
        return 0;
    }

    Mark($"app: 被启动了（{Environment.CommandLine}）");
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "app-launched.txt"), log.ToString());
    return 0;
}
catch (Exception ex)
{
    Mark("崩了：" + ex);
    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "fake-setup.log"), log.ToString()); } catch { }
    return 1;
}
