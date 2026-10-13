using System;
using System.Globalization;
using System.Threading.Tasks;

namespace TagFixtures;

/// <summary>
/// 手动运行：dotnet run --project tools/TagFixtures -- [--out 目录] [--ffmpeg 路径] [--keep]
/// 默认输出到仓库根 _tagfixtures（已存在则整体重建）。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = new Generator.Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    options.OutDir = args[++i];
                    break;
                case "--ffmpeg":
                    options.FfmpegPath = args[++i];
                    break;
                case "--keep":
                    options.Force = false;
                    break;
                case "-h":
                case "--help":
                    Console.WriteLine("用法：dotnet run --project tools/TagFixtures -- [--out 目录] [--ffmpeg 路径] [--keep]");
                    Console.WriteLine("  --out     输出目录（默认 _tagfixtures，已存在则整体重建；--keep 则不清空）");
                    Console.WriteLine("  --ffmpeg  ffmpeg.exe 路径（默认自动查找仓库 Assets 或 PATH）");
                    return 0;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}（--help 看用法）");
                    return 2;
            }
        }

        Console.WriteLine($"输出目录：{System.IO.Path.GetFullPath(options.OutDir)}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Generator.GenerateAsync(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"生成失败：{ex}");
            return 1;
        }
        Console.WriteLine($"耗时 {sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} 秒");
        return 0;
    }
}
