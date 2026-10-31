// TagLib 时长探针：把「曲库会显示多久」和「ffmpeg 解码出来多久」分开量。
//
// 用法：
//   dotnet run --project tools/TagLibDurationProbe -- <文件...>
//
// 输出每行：文件名  dur=<TagLib 算的秒>  sr=采样率  ch=声道  br=码率(TagLib 认的)
//
// 为什么要它：曲库的时长全部来自 TagLib。怀疑「显示的时长不对」时，先确认是 TagLib 读错了，
// 还是我们换算错了——2026-10-31 查 TTA 就是这么定性的（TTA 是 TagLib 根本打不开，走自己的解析；
// 而 22.05kHz 的 mp3 则是 TagLib 把码率认成 56k，时长算短一半，属于上游问题）。
//
// 对照组用打包在程序里的 ffmpeg 解一遍音频取 time=，那个是真实时长。
using System;
using System.Linq;

foreach (string path in args)
{
    Console.Write(System.IO.Path.GetFileName(path) + "\t");
    try
    {
        using TagLib.File file = TagLib.File.Create(path);
        TagLib.Properties properties = file.Properties;
        Console.Write("dur=" + properties.Duration.TotalSeconds.ToString("0.###"));
        Console.Write("\tsr=" + properties.AudioSampleRate);
        Console.Write("\tch=" + properties.AudioChannels);
        Console.Write("\tbr=" + properties.AudioBitrate);
        Console.WriteLine();
    }
    catch (Exception caught)
    {
        Console.WriteLine("ERR " + caught.GetType().Name + ": " + caught.Message);
    }
}
