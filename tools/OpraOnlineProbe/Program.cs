using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CelesteMusicPlayer;

namespace OpraOnlineProbe
{
    /// <summary>
    /// 实测 OPRA 在线数据链路，不经过界面。三步：
    ///   1. 用真实缓存目录加载现有缓存（离线可解析性）；
    ///   2. 用临时目录强制联网下载一次（在线可用性，不动用户缓存）；
    ///   3. 搜索 + 取曲线 + 映射成 EQ 状态（和网页点「应用」走同一条路径）。
    /// 退出码 0 = 全通；1 = 有断言失败；2 = 网络不可用（此时应报"在线数据源不可达"而不是"功能坏了"）。
    /// </summary>
    internal static class Program
    {
        private static int _fails;

        private static void Check(bool ok, string what, string detail = "")
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what + (detail.Length > 0 ? "  [" + detail + "]" : ""));
            if (!ok) _fails++;
        }

        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            int rounds = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 2;

            string realCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CelesteMusicPlayer", "opra");

            // ---- 1. cache path -----------------------------------------------------------
            Console.WriteLine("[1] 本地缓存加载  " + realCache);
            try
            {
                var svc = new OpraService();
                var status = await svc.EnsureLoadedAsync(refresh: false);
                Console.WriteLine("    Status: Source=" + status.Source + " Vendors=" + status.VendorCount +
                                  " Products=" + status.ProductCount + " Eqs=" + status.EqCount +
                                  " FetchedAt=" + (status.FetchedAt?.ToString("u") ?? "-"));
                Check(status.ProductCount > 100, "缓存里能解析出产品", status.ProductCount.ToString());
                Check(status.EqCount > 100, "缓存里能解析出校正曲线", status.EqCount.ToString());
                Check(status.VendorCount > 10, "缓存里能解析出厂商", status.VendorCount.ToString());
                if (status.Source == "network") Console.WriteLine("    note: 缓存不存在，这次是从网络拉的");
                else if (status.Source == "empty") Check(false, "缓存来源", "empty");
            }
            catch (Exception ex)
            {
                Check(false, "缓存加载抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            // ---- 2. live download into a scratch dir -------------------------------------
            string scratch = Path.Combine(Path.GetTempPath(), "celeste-opra-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Console.WriteLine("[2] 联网下载（临时目录，不动用户缓存）  " + scratch);
            bool online = false;
            try
            {
                var svc = new OpraService(scratch);
                var t0 = DateTime.UtcNow;
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var status = await svc.EnsureLoadedAsync(refresh: true, cts.Token);
                double sec = (DateTime.UtcNow - t0).TotalSeconds;
                long bytes = 0;
                string file = Path.Combine(scratch, "database_v1.jsonl");
                if (File.Exists(file)) bytes = new FileInfo(file).Length;
                Console.WriteLine("    Status: Source=" + status.Source + " Products=" + status.ProductCount +
                                  " Eqs=" + status.EqCount + "  用时=" + sec.ToString("0.0") + "s  体积=" +
                                  (bytes / 1048576.0).ToString("0.0") + " MB");
                online = status.Source == "network" && status.ProductCount > 100;
                Check(status.Source == "network", "真的从 opra.roonlabs.net 下载成功", status.Source);
                Check(online, "在线数据可解析", "Products=" + status.ProductCount);
            }
            catch (Exception ex)
            {
                Check(false, "联网下载失败", ex.GetType().Name + ": " + ex.Message);
            }

            // ---- 3. search / curve / mapping (repeatable) --------------------------------
            string[] queries = { "HD 600", "Sony WH-1000XM5", "Moondrop Aria", "K371", "AirPods Pro" };
            Console.WriteLine("[3] 搜索 + 曲线映射（用真实缓存服务，跑 " + rounds + " 轮防偶发）");
            OpraService lib;
            try
            {
                lib = new OpraService();
                await lib.EnsureLoadedAsync(refresh: false);
            }
            catch (Exception ex)
            {
                Check(false, "重建服务失败", ex.Message);
                return 1;
            }

            for (int round = 1; round <= rounds; round++)
            {
                foreach (string q in queries)
                {
                    var hits = await lib.SearchAsync(q, 8);
                    string top = hits.Count > 0 ? hits[0].VendorName + " / " + hits[0].Name : "-";
                    Check(hits.Count > 0, "搜索「" + q + "」有结果", "命中 " + hits.Count + " 条，首条 " + top);
                    if (hits.Count == 0) continue;

                    var first = hits[0];
                    var eqs = lib.GetEqsForProduct(first.ProductId);
                    Check(eqs.Count > 0, "  该产品有曲线", first.Name + " " + eqs.Count + " 条");
                    if (eqs.Count == 0) continue;

                    var correction = lib.BuildCorrection(eqs[0].EqId);
                    if (correction == null) { Check(false, "  曲线能映射成 EQ 状态", eqs[0].EqId); continue; }
                    int bands = correction.Curve.Bands.Count;
                    Check(bands > 0, "  曲线能映射成 EQ 状态",
                          correction.ProductName + " 段数=" + bands + " preamp=" + correction.Curve.PreampDb.ToString("0.##") +
                          "dB 导入=" + correction.ImportedBandCount + " 跳过=" + correction.SkippedBandCount);
                    Check(correction.EqId.Length > 0 && correction.VendorName.Length > 0, "  归属信息完整",
                          correction.VendorName + " / " + correction.ProductName);
                }
            }

            // ---- verdict -----------------------------------------------------------------
            try { Directory.Delete(scratch, true); } catch { }
            Console.WriteLine();
            if (_fails == 0)
            {
                Console.WriteLine("OPRA_ONLINE_OK");
                return 0;
            }
            Console.WriteLine("OPRA_ONLINE_FAILS=" + _fails + (online ? "" : "  (网络不可达，先怀疑网络/代理而不是代码)"));
            return online ? 1 : 2;
        }
    }
}
