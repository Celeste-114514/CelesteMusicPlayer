using System;
using System.IO;
using CelesteMusicPlayer;

internal static class Program
{
    private static int _fail;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (!ok) _fail++;
    }

    private static bool Pattern(byte[] buf, int bytes, byte l, byte r)
    {
        for (int i = 0; i < bytes; i += 2)
        {
            if (buf[i] != l || buf[i + 1] != r) return false;
        }
        return true;
    }

    private static int Main()
    {
        string dir = AppContext.BaseDirectory;

        // ---- DSF：块交错，LSB-first ----
        string dsf = Path.Combine(dir, "sample.dsf");
        var h = DsdNativeReader.Peek(dsf);
        Check(h.RateHz == 2822400, "DSF 位时钟");
        Check(h.Channels == 2, "DSF 声道数");
        Check(h.LsbFirst, "DSF LSB-first");
        Check(h.Kind == DsdNativeReader.ContainerKind.Dsf, "DSF 容器类型");
        Check(h.BlockSize == 4096, "DSF 块大小");
        Check(h.TotalFrames == 8192, "DSF 总帧数");

        using (var r = DsdNativeReader.Open(dsf))
        {
            var buf = new byte[64];
            Check(r.Read(buf, 0, 64) == 64, "DSF 读满 64 字节");
            Check(Pattern(buf, 64, 0x11, 0x22), "DSF L/R 交织正确");

            r.SeekSample(8 * 5000);                 // 第 2 块内偏移 904 帧
            Check(r.Read(buf, 0, 64) == 64 && Pattern(buf, 64, 0x11, 0x22), "DSF seek 后交织仍正确");
            Check(r.CurrentFrame == 5032, "DSF seek 后帧号正确");
        }

        // ---- DFF：逐字节声道交错，大端头，恒 MSB-first ----
        string dff = Path.Combine(dir, "sample.dff");
        var hd = DsdNativeReader.Peek(dff);
        Check(hd.RateHz == 2822400, "DFF 位时钟（大端）");
        Check(hd.Channels == 2, "DFF 声道数");
        Check(!hd.LsbFirst, "DFF 恒 MSB-first");
        Check(hd.Kind == DsdNativeReader.ContainerKind.Dff, "DFF 容器类型");
        Check(hd.TotalFrames == 4096, "DFF 总帧数");

        using (var r = DsdNativeReader.Open(dff))
        {
            var buf = new byte[64];
            Check(r.Read(buf, 0, 64) == 64 && Pattern(buf, 64, 0x11, 0x22), "DFF 逐字节声道交错");
            r.SeekSample(8 * 1000);
            Check(r.Read(buf, 0, 64) == 64 && Pattern(buf, 64, 0x11, 0x22), "DFF seek 后交错正确");
            Check(r.CurrentFrame == 1032, "DFF seek 后帧号正确");
        }

        // ---- 6 声道 DFF：只取前两声道 ----
        string d6 = Path.Combine(dir, "sample6.dff");
        var h6 = DsdNativeReader.Peek(d6);
        Check(h6.Channels == 6, "6ch DFF 声道数");
        using (var r = DsdNativeReader.Open(d6))
        {
            var buf = new byte[64];
            Check(r.Read(buf, 0, 64) == 64 && Pattern(buf, 64, 0x11, 0x22), "6ch DFF 只取前两声道");
        }

        Console.WriteLine(_fail == 0 ? "ALL_PASS" : "FAILED=" + _fail);
        return _fail == 0 ? 0 : 1;
    }
}
