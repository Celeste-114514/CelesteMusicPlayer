using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer;

public sealed partial class MainWindow
{
    private async Task<object> WebDspOpraAsync(string operation, JsonElement msg)
    {
        var status = await _opra.EnsureLoadedAsync(operation == "opraRefresh");
        if (operation is "opraInit" or "opraRefresh")
        {
            var history = OpraHistoryStore.Load();
            return new { status, vendors = _opra.GetTopVendors(10000).Select(v => new { id=v.Id, name=v.Name, count=_opra.GetProductCountByVendor(v.Id) }), recent=history.Recent, favorites=history.Favorites, enabled=_opraApplied && EqState.Current.Enabled, activeName=_opraApplied ? EqState.Current.PresetName : "" };
        }
        if (operation == "opraSearch")
            return new { products = await _opra.SearchAsync(ReadString(msg,"query"), 200) };
        if (operation == "opraPresets")
            return new { presets = _opra.GetEqsForProduct(ReadString(msg,"productId")) };
        string id = ReadString(msg,"eqId");
        var correction = _opra.BuildCorrection(id) ?? throw new ArgumentException("校正曲线不可用，请重新选择型号与预设。");
        if (operation == "opraApply")
        {
            ApplyOpraCurve(correction.Curve);
            NoteOpraApplied(correction);
            return new { ok=true };
        }
        if (operation == "opraFavorite")
            return new { favorite=OpraHistoryStore.ToggleFavorite(new OpraHistoryItem(correction.EqId,correction.ProductId,correction.ProductName,correction.VendorName,correction.Author,correction.ImportedBandCount,correction.Curve.PreampDb)) };
        if (operation == "opraPreview")
            return new { eqId=id, title=correction.VendorName+" "+correction.ProductName, author=correction.Author, details=correction.Details, preampDb=correction.Curve.PreampDb, count=correction.ImportedBandCount, warnings=correction.Warnings, favorite=OpraHistoryStore.IsFavorite(id), bands=correction.Curve.Bands.Select(b=>new { freq=b.FrequencyHz,gain=b.GainDb,q=b.Q,type=b.FilterType.ToString(),enabled=b.Enabled }) };
        throw new ArgumentException("不支持的耳机校正操作。");

    }

    // 歌剧墙服务实例（2026-10-31 从 MainWindow.Opra.cs 搬来）：唯一的用户是 WebOpra 网页命令。
    private readonly OpraService _opra = new();

}
