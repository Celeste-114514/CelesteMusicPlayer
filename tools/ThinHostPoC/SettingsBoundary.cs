using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace CelesteMusicPlayer.ThinHost;

/// <summary>
/// W6 设置读写边界自检。两项检查都是「以前靠人记、现在靠机器」：
/// 1. Clone 漏拷字段 —— 本项目出过多次「存进文件了，读回来却是默认值」，根因都在这里。
/// 2. 字段表三处（Defs / Readers / Writers）不同步 —— 少一处，网页上那一项就改不动或读不到。
/// 全程只在内存里操作，**不读也不写用户真实的设置文件**。
/// </summary>
internal static class SettingsBoundary
{
    internal sealed record Result(
        int FieldsProbed, int CloneMismatches, int SchemaMismatches, int DefsMissingReader,
        int CloneUnverified, List<string> Details);

    internal static Result Run()
    {
        var details = new List<string>();
        var state = new AppSettingsState();
        var baseline = new AppSettingsState();
        PropertyInfo[] props = typeof(AppSettingsState).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        int probed = 0;

        // —— 1. Clone 完整性 ——
        // 关键：先用字段表登记的 Writer 写**合法值**。很多字段在 Clone 里带白名单夹取
        // （例如 MiniPlayerBackground 只认 Cover/Dark/Frosted/Glow），要是直接造个非法值写进去，
        // 被夹回默认值就会被误判成"漏拷"。Writer 内部已经夹过了，此时 state 里是合法值。
        foreach (WebSettingsSchema.Def def in WebSettingsSchema.Defs)
        {
            if (string.IsNullOrEmpty(def.id)) continue;
            if (!WebSettingsSchema.Writers.TryGetValue(def.id, out Action<AppSettingsState, JsonElement>? writer)) continue;
            object? value = ProbeValue(def);
            if (value is null) continue;
            try { writer(state, JsonSerializer.SerializeToElement(value)); }
            catch (Exception ex) { details.Add("写入 " + def.id + " 失败：" + ex.Message); }
        }

        // Writer 改过哪些属性 = 这些在字段表登记过，值也一定合法，可以严格判定。
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo p in props)
        {
            if (!p.CanRead || !p.CanWrite) continue;
            object? current, fresh;
            try { current = p.GetValue(state); fresh = p.GetValue(baseline); } catch { continue; }
            if (!SameValue(current, fresh)) registered.Add(p.Name);
        }

        // 字段表没覆盖到的属性也照探针设一遍，但它们常常在 Clone 里带白名单夹取
        // （比如 MiniPlayerBackground 只认 Cover/Dark/Frosted/Glow），随便造的值会被夹回默认，
        // 跟"真漏拷"长得一模一样 —— 所以这部分只报出来给人看，不算失败。
        foreach (PropertyInfo p in props)
        {
            if (!p.CanRead || !p.CanWrite) continue;
            if (registered.Contains(p.Name)) continue;
            object? current;
            try { current = p.GetValue(state); } catch { continue; }
            object? probe = DifferentFrom(p.PropertyType, current);
            if (probe is null) continue;
            try { p.SetValue(state, probe); } catch { continue; }
        }

        AppSettingsState copy = AppSettingsStore.Clone(state);
        int cloneBad = 0, cloneUnverified = 0;
        foreach (PropertyInfo p in props)
        {
            if (!p.CanRead || !p.CanWrite) continue;
            object? a = null, b = null;
            try { a = p.GetValue(state); b = p.GetValue(copy); } catch { continue; }
            if (SameValue(a, b)) continue;
            if (registered.Contains(p.Name))
            {
                cloneBad++;
                details.Add("Clone 漏拷字段 " + p.Name + "（已登记、值合法）：源=" + Show(a) + " 副本=" + Show(b));
            }
            else
            {
                cloneUnverified++;
                details.Add("Clone 待确认 " + p.Name + "：源=" + Show(a) + " 副本=" + Show(b) + "（未登记在字段表，可能是白名单夹取而不是漏拷）");
            }
        }
        probed = registered.Count;
        details.Insert(0, $"字段表登记并写入 {registered.Count} 项；未登记 {props.Count(p => p.CanRead && p.CanWrite) - registered.Count} 项（只提示不判失败）");

        // —— 2. 字段表三处同步 + 读写往返：读出来的值写回去，再读一遍必须一样 ——
        var probeState = new AppSettingsState();
        int schemaBad = 0;
        foreach (KeyValuePair<string, Func<AppSettingsState, object?>> reader in WebSettingsSchema.Readers)
        {
            if (!WebSettingsSchema.Writers.TryGetValue(reader.Key, out Action<AppSettingsState, JsonElement>? writer))
            {
                schemaBad++; details.Add("字段 " + reader.Key + " 有 Reader 没 Writer（网页上改不动）");
                continue;
            }
            object? before;
            try { before = reader.Value(probeState); }
            catch (Exception ex) { schemaBad++; details.Add("字段 " + reader.Key + " 读取异常：" + ex.Message); continue; }

            try { writer(probeState, JsonSerializer.SerializeToElement(before)); }
            catch (Exception ex) { schemaBad++; details.Add("字段 " + reader.Key + " 写入异常：" + ex.Message); continue; }

            object? after;
            try { after = reader.Value(probeState); }
            catch (Exception ex) { schemaBad++; details.Add("字段 " + reader.Key + " 写后读取异常：" + ex.Message); continue; }

            if (!SameValue(before, after))
            {
                schemaBad++;
                details.Add("字段 " + reader.Key + " 读写不闭环：" + Show(before) + " → " + Show(after));
            }
        }

        int defsMissing = 0;
        foreach (WebSettingsSchema.Def def in WebSettingsSchema.Defs)
        {
            if (string.IsNullOrEmpty(def.id)) continue;
            if (WebSettingsSchema.Readers.ContainsKey(def.id)) continue;
            defsMissing++;
            details.Add("字段表登记了 " + def.id + " 但没有 Reader（网页上读不到值）");
        }

        return new Result(probed, cloneBad, schemaBad, defsMissing, cloneUnverified, details);
    }

    /// <summary>按字段表的 kind 造一个**合法**的探测值，交给 Writer 写进去。</summary>
    private static object? ProbeValue(WebSettingsSchema.Def def) => def.kind switch
    {
        "bool" => true,
        // 取上界：和默认值的差距最大，Clone 一旦漏拷就必然露出来。
        "slider" => def.step >= 1 ? (object)(int)Math.Max(def.min, def.max) : Math.Max(def.min, def.max),
        "select" => def.options is { Length: > 0 } ? def.options[0].Split('|')[0] : null,
        "text" => "probe-text",
        "color" => "#12ab34",
        "folder" => Path.Combine(Path.GetTempPath(), "celeste-probe"),
        "file" => Path.Combine(Path.GetTempPath(), "celeste-probe.txt"),
        _ => null
    };

    /// <summary>造一个跟当前值不同的值。造不出来就返回 null（该属性跳过，不算通过）。</summary>
    private static object? DifferentFrom(Type type, object? current)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(bool)) return current is bool b ? !b : true;
        if (t == typeof(string)) return current is string s && s.Length > 0 ? s + "-probe" : "probe";
        if (t == typeof(int)) return current is int i ? i + 1 : 7;
        if (t == typeof(long)) return current is long l ? l + 1 : 7L;
        if (t == typeof(double)) return current is double d ? d + 1.5 : 1.5;
        if (t == typeof(float)) return current is float f ? f + 1.5f : 1.5f;
        if (t == typeof(decimal)) return current is decimal m ? m + 1 : 7m;
        if (t == typeof(DateTime)) return DateTime.UnixEpoch;
        if (t == typeof(TimeSpan)) return TimeSpan.FromMinutes(7);
        if (t.IsEnum)
        {
            Array values = Enum.GetValues(t);
            foreach (object? v in values)
            {
                if (!Equals(v, current)) return v;
            }
            return null;
        }
        if (t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
        {
            // 集合给一个空实例即可：Clone 若漏拷，副本会保持默认（通常也是空），
            // 所以这里再补一个元素，才能区分「拷了」和「恰好都空」。
            try
            {
                object list = Activator.CreateInstance(t)!;
                Type item = t.GetGenericArguments()[0];
                object? element = DifferentFrom(item, null);
                if (element is null) return null;
                t.GetMethod("Add")!.Invoke(list, new[] { element });
                return list;
            }
            catch { return null; }
        }
        return null;
    }

    private static bool SameValue(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string || a.GetType().IsPrimitive || a is decimal || a.GetType().IsEnum) return a.Equals(b);
        if (a is IEnumerable ea && b is IEnumerable eb) return ea.Cast<object>().SequenceEqual(eb.Cast<object>());
        return a.Equals(b);
    }

    private static string Show(object? value) => value switch
    {
        null => "(null)",
        string s => "\"" + s + "\"",
        IEnumerable e => "[" + string.Join(",", e.Cast<object>().Select(x => x?.ToString() ?? "")) + "]",
        _ => value.ToString() ?? ""
    };
}
