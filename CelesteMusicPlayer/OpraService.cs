using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>OPRA 数据库状态。</summary>
    public sealed record OpraStatus(string Source, DateTimeOffset? FetchedAt, int VendorCount, int ProductCount, int EqCount);

    /// <summary>OPRA 厂商。</summary>
    public sealed record OpraVendor(string Id, string Name, string? LogoPath, string? Blurb);

    /// <summary>OPRA 产品（耳机/设备型号）。</summary>
    public sealed record OpraProduct(string Id, string VendorId, string Name, string? Subtype, string? AssetPath, string SearchText);

    /// <summary>OPRA 参数化 EQ 曲线（一条校正曲线）。</summary>
    public sealed class OpraEq
    {
        public string Id = "";
        public string ProductId = "";
        public string Author = "OPRA";
        public string? Details;
        public string? Link;
        public double PreampDb;
        public List<OpraEqBand>? Bands;
    }

    /// <summary>OPRA 滤波段（与播放器 EqBand 字段对齐：type/frequency/gain_db/q/slope）。</summary>
    public sealed record OpraEqBand(string Type, double Frequency, double? GainDb, double? Q, double? Slope);

    /// <summary>耳机校正结果：预览信息 + 可应用的 EqCurveState。</summary>
    public sealed record OpraCorrection(
        string EqId, string ProductId, string ProductName, string? ProductSubtype,
        string VendorId, string VendorName, string Author, string? Details, string? Link,
        EqCurveState Curve, int OriginalBandCount, int ImportedBandCount,
        int SkippedBandCount, IReadOnlyList<string> Warnings);

    /// <summary>
    /// 耳机校正（OPRA）服务：Roon 的开源耳机 EQ 数据库。
    /// 数据源 https://opra.roonlabs.net/database_v1.jsonl 下载后缓存到
    /// %LOCALAPPDATA%\CelesteMusicPlayer\opra\database_v1.jsonl，按需懒加载解析。
    /// 曲线（parametric_eq）在应用时映射为播放器的 EqCurveState（任意 band 列表 + preamp）。
    /// </summary>
    public sealed class OpraService
    {
        public const string DatabaseUrl = "https://opra.roonlabs.net/database_v1.jsonl";
        private const string AssetBaseUrl = "https://opra.roonlabs.net/";

        private static readonly Lazy<HttpClient> Http = new(() =>
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CelesteMusicPlayer/1.0 (headphone correction)");
            return c;
        });

        private sealed class Db
        {
            public Dictionary<string, OpraVendor> Vendors = new();
            public Dictionary<string, OpraProduct> Products = new();
            public Dictionary<string, OpraEq> Eqs = new();
            public Dictionary<string, List<OpraEq>> EqsByProductId = new();
            public OpraStatus Status = new("empty", null, 0, 0, 0);
        }

        private readonly string _cacheDir;
        private Db? _db;

        public OpraService(string? cacheDir = null)
        {
            _cacheDir = cacheDir ?? Path.Combine(AppSettingsStore.GetConfigDirectory(), "opra");
        }

        private string CachePath => Path.Combine(_cacheDir, "database_v1.jsonl");
        private string MetaPath => CachePath + ".meta.json";

        private static string NormalizeSearchText(string value)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in value.Normalize(System.Text.NormalizationForm.FormKD))
            {
                // 去组合音调标记 -> 转小写 -> 只留 a-z0-9 与 CJK
                if (c >= '\u0300' && c <= '\u036f')
                {
                    continue;
                }

                char lower = char.ToLowerInvariant(c);
                bool keep = (lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9')
                    || (lower >= '\u4e00' && lower <= '\u9fff');
                sb.Append(keep ? lower : ' ');
            }

            return sb.ToString().Trim();
        }

        private static double Clamp(double v, double min, double max)
            => Math.Max(min, Math.Min(max, v));

        /// <summary>加载（下载或读缓存）并解析数据库。refresh=强制联网刷新。</summary>
        public async Task<OpraStatus> EnsureLoadedAsync(bool refresh = false, CancellationToken ct = default)
        {
            if (_db != null && !refresh)
            {
                return _db.Status;
            }

            string? raw = null;
            string source;
            DateTimeOffset? fetchedAt = null;

            if (refresh || !File.Exists(CachePath))
            {
                try
                {
                    byte[] bytes = await Http.Value.GetByteArrayAsync(DatabaseUrl, ct).ConfigureAwait(false);
                    raw = System.Text.Encoding.UTF8.GetString(bytes);
                    fetchedAt = DateTimeOffset.UtcNow;
                    Directory.CreateDirectory(_cacheDir);
                    File.WriteAllText(CachePath, raw);
                    try { File.WriteAllText(MetaPath, JsonSerializer.Serialize(new { fetchedAt = fetchedAt.Value.ToString("O") })); } catch { }
                    source = "network";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    if (!File.Exists(CachePath))
                    {
                        throw;
                    }

                    source = "cache";
                }
            }
            else
            {
                source = "cache";
            }

            if (raw == null && File.Exists(CachePath))
            {
                raw = File.ReadAllText(CachePath);
                fetchedAt = ReadCachedFetchedAt();
                source = "cache";
            }

            _db = raw == null ? ParseDatabase("", "empty", null) : ParseDatabase(raw, source, fetchedAt);
            return _db.Status;
        }

        private DateTimeOffset? ReadCachedFetchedAt()
        {
            try
            {
                string meta = File.ReadAllText(MetaPath);
                using var doc = JsonDocument.Parse(meta);
                if (doc.RootElement.TryGetProperty("fetchedAt", out JsonElement fa)
                    && DateTimeOffset.TryParse(fa.GetString(), out DateTimeOffset d))
                {
                    return d;
                }
            }
            catch
            {
            }

            return null;
        }

        private Db ParseDatabase(string raw, string source, DateTimeOffset? fetchedAt)
        {
            var db = new Db();
            foreach (string lineRaw in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = lineRaw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    string? type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
                    string? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
                    if (!root.TryGetProperty("data", out JsonElement data) || string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    if (type == "vendor")
                    {
                        string name = ReadText(data, "name") ?? id;
                        string? logo = ReadText(data, "logo");
                        string? blurb = ReadText(data, "blurb");
                        db.Vendors[id] = new OpraVendor(id, name, logo, blurb);
                    }
                    else if (type == "product")
                    {
                        string? vendorId = ReadText(data, "vendor_id");
                        string? name = ReadText(data, "name");
                        if (string.IsNullOrEmpty(vendorId) || string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        string? assetPath = ReadText(data, "line_art_96x64_png") ?? ReadText(data, "line_art_svg");
                        string vendorName = db.Vendors.TryGetValue(vendorId, out OpraVendor? v) ? v.Name : vendorId;
                        db.Products[id] = new OpraProduct(id, vendorId, name,
                            ReadText(data, "subtype"), assetPath,
                            NormalizeSearchText($"{vendorName} {name} {id.Replace(':', ' ').Replace('_', ' ')}"));
                    }
                    else if (type == "eq")
                    {
                        string? eqType = ReadText(data, "type");
                        if (eqType != "parametric_eq")
                        {
                            continue;
                        }

                        string? productId = ReadText(data, "product_id");
                        if (!data.TryGetProperty("parameters", out JsonElement pars)
                            || !pars.TryGetProperty("bands", out JsonElement bandsEl)
                            || bandsEl.ValueKind != JsonValueKind.Array
                            || string.IsNullOrEmpty(productId))
                        {
                            continue;
                        }

                        var bands = new List<OpraEqBand>();
                        foreach (JsonElement b in bandsEl.EnumerateArray())
                        {
                            string? btype = ReadText(b, "type");
                            double? freq = ReadNumber(b, "frequency");
                            if (string.IsNullOrEmpty(btype) || freq == null)
                            {
                                continue;
                            }

                            bands.Add(new OpraEqBand(btype, freq.Value,
                                ReadNumber(b, "gain_db"), ReadNumber(b, "q"), ReadNumber(b, "slope")));
                        }

                        if (bands.Count == 0)
                        {
                            continue;
                        }

                        var eq = new OpraEq
                        {
                            Id = id,
                            ProductId = productId,
                            Author = ReadText(data, "author") ?? "OPRA",
                            Details = ReadText(data, "details"),
                            Link = ReadText(data, "link"),
                            PreampDb = ReadNumber(pars, "gain_db") ?? 0,
                            Bands = bands,
                        };
                        db.Eqs[id] = eq;
                        if (!db.EqsByProductId.TryGetValue(productId, out List<OpraEq>? list))
                        {
                            list = new List<OpraEq>();
                            db.EqsByProductId[productId] = list;
                        }

                        list.Add(eq);
                    }
                }
                catch
                {
                    // 单行解析失败忽略
                }
            }

            db.Status = new OpraStatus(source, fetchedAt, db.Vendors.Count, db.Products.Count, db.Eqs.Count);
            return db;
        }

        private static string? ReadText(JsonElement obj, string prop)
        {
            if (obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? s = el.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }

            return null;
        }

        private static double? ReadNumber(JsonElement obj, string prop)
        {
            if (obj.TryGetProperty(prop, out JsonElement el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
                {
                    return d;
                }

                if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out double ds))
                {
                    return ds;
                }
            }

            return null;
        }

        private static int ScoreProduct(string[] tokens, OpraProduct product)
        {
            string haystack = product.SearchText ?? NormalizeSearchText(product.Name + " " + product.Id);
            if (!tokens.All(t => haystack.Contains(t)))
            {
                return -1;
            }

            string productName = NormalizeSearchText(product.Name);
            int score = 0;
            foreach (string token in tokens)
            {
                if (productName == token || haystack == token)
                {
                    score += 120;
                }
                else if (productName.StartsWith(token))
                {
                    score += 70;
                }
                else
                {
                    score += 20;
                }
            }

            return score;
        }

        /// <summary>按名称搜索耳机/设备型号（返回有曲线的产品）。</summary>
        public async Task<List<OpraSearchResult>> SearchAsync(string query, int limit = 16, bool refresh = false, CancellationToken ct = default)
        {
            await EnsureLoadedAsync(refresh, ct).ConfigureAwait(false);
            if (_db == null || query == null)
            {
                return new List<OpraSearchResult>();
            }

            string[] tokens = NormalizeSearchText(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || NormalizeSearchText(query).Length < 2)
            {
                return new List<OpraSearchResult>();
            }

            var list = new List<OpraTarget>();
            foreach (OpraProduct p in _db.Products.Values)
            {
                if (!_db.EqsByProductId.ContainsKey(p.Id))
                {
                    continue;
                }

                int score = ScoreProduct(tokens, p);
                if (score < 0)
                {
                    continue;
                }

                list.Add(new OpraTarget(p, score, _db.Vendors.TryGetValue(p.VendorId, out OpraVendor? v) ? v : new OpraVendor(p.VendorId, p.VendorId, null, null)));
            }

            limit = Math.Max(1, Math.Min(30, limit));
            return list
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Vendor.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Product.Name, StringComparer.Ordinal)
                .Take(limit)
                .Select(x => new OpraSearchResult(x.Product.Id, x.Product.Name, x.Product.Subtype,
                    x.Vendor.Id, x.Vendor.Name,
                    x.Product.AssetPath != null ? AssetBaseUrl + x.Product.AssetPath : null,
                    _db.EqsByProductId[x.Product.Id].Count))
                .ToList();
        }

        private sealed record OpraTarget(OpraProduct Product, int Score, OpraVendor Vendor);

        /// <summary>获取某产品的全部曲线。</summary>
        public List<OpraProductEqSummary> GetEqsForProduct(string productId)
        {
            if (_db == null || !_db.EqsByProductId.TryGetValue(productId, out List<OpraEq>? eqs))
            {
                return new List<OpraProductEqSummary>();
            }

            var product = _db.Products.TryGetValue(productId, out OpraProduct? p) ? p : null;
            var vendor = product != null && _db.Vendors.TryGetValue(product.VendorId, out OpraVendor? v) ? v : null;
            return eqs.Select(eq => new OpraProductEqSummary(eq.Id, eq.Author, eq.PreampDb, eq.Bands?.Count ?? 0))
                .ToList();
        }

        /// <summary>把指定曲线生成为可应用的 EqCurveState（照搬 ECHO createPreview 的 band 映射+preamp）。</summary>
        public OpraCorrection? BuildCorrection(string eqId)
        {
            if (_db == null || !_db.Eqs.TryGetValue(eqId, out OpraEq? eq) || eq.Bands == null)
            {
                return null;
            }

            if (!_db.Products.TryGetValue(eq.ProductId, out OpraProduct? product))
            {
                return null;
            }

            OpraVendor vendor = _db.Vendors.TryGetValue(product.VendorId, out OpraVendor? v) ? v : new OpraVendor(product.VendorId, product.VendorId, null, null);

            var warnings = new List<string>();
            var curve = new EqCurveState
            {
                Enabled = true,
                PreampDb = 0,
                PresetId = "opra-" + SanitizeId(eq.Id),
                PresetName = ("耳机校正 - " + vendor.Name + " / " + product.Name + " / " + eq.Author).Length > 64
                    ? ("耳机校正 - " + vendor.Name + " / " + product.Name + " / " + eq.Author)[..64]
                    : "耳机校正 - " + vendor.Name + " / " + product.Name + " / " + eq.Author,
                Bands = new List<EqBand>()
            };

            int imported = 0;
            foreach (OpraEqBand input in eq.Bands)
            {
                EqFilterType? ft = MapFilterType(input.Type);
                if (ft == null)
                {
                    continue;
                }

                double frequencyHz = Clamp(input.Frequency, 20, 20000);
                double gainDb = ft.Value == EqFilterType.LowPass || ft.Value == EqFilterType.HighPass
                    ? 0
                    : Clamp(input.GainDb ?? 0, -15, 15);
                double q = Clamp(input.Q ?? (input.Slope.HasValue ? 0.707 : 1.0), 0.1, 12.0);

                curve.Bands.Add(new EqBand
                {
                    Enabled = true,
                    FrequencyHz = frequencyHz,
                    GainDb = gainDb,
                    Q = q,
                    FilterType = ft.Value
                });
                imported++;
            }

            curve.PreampDb = Clamp(eq.PreampDb, -24, 12);

            if (curve.Bands.Count == 0)
            {
                curve = null;
                return null;
            }

            return new OpraCorrection(
                eq.Id, product.Id, product.Name, product.Subtype, vendor.Id, vendor.Name,
                eq.Author, eq.Details, eq.Link, curve, eq.Bands.Count, imported, 0, warnings);
        }

        private static EqFilterType? MapFilterType(string type) => type switch
        {
            "peak_dip" => EqFilterType.Peaking,
            "low_shelf" => EqFilterType.LowShelf,
            "high_shelf" => EqFilterType.HighShelf,
            "low_pass" => EqFilterType.LowPass,
            "high_pass" => EqFilterType.HighPass,
            "band_stop" => EqFilterType.Notch,
            _ => null
        };

        private static string SanitizeId(string value)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in value.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
                {
                    sb.Append(c);
                }
                else if (c != ' ')
                {
                    sb.Append('-');
                }
            }

            string s = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(s) ? "eq" : (s.Length > 48 ? s[..48] : s);
        }
    }

    /// <summary>搜索命中的产品。</summary>
    public sealed record OpraSearchResult(
        string ProductId, string Name, string? Subtype, string VendorId, string VendorName,
        string? AssetUrl, int EqCount);

    /// <summary>某产品的单条曲线摘要。</summary>
    public sealed record OpraProductEqSummary(string EqId, string Author, double PreampDb, int BandCount)
    {
        /// <summary>列表显示：滤波段数 + 预增益。</summary>
        public string EqCountLabel =>
            $"{BandCount} 段 · 预增益 {(PreampDb >= 0 ? "+" : "")}{PreampDb:0.##} dB";
    }
}
