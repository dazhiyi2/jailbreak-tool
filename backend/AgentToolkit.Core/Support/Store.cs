using System.Reflection;
using System.Text.Json;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Targets;

namespace AgentToolkit.Core.Support;

public readonly record struct MarkerSpec(string Marker, MarkerLayer Layer);

public static class MarkerScanner
{
    /// <summary>
    /// 逐条统计标记在"模型实际收到的上下文"里的命中数。
    /// 命中 0 = 这一层没加载，此时不要评价模型态度——这是整套方法的第一判据。
    /// </summary>
    public static MarkerReport Scan(string sessionId, string text, IReadOnlyList<MarkerSpec> specs)
    {
        var hits = specs
            .Select(s => new MarkerHit { Marker = s.Marker, Layer = s.Layer, Count = FileText.Count(text, s.Marker) })
            .ToArray();

        var system = hits.Where(h => h.Layer == MarkerLayer.SystemPrompt).Sum(h => h.Count);
        var payload = hits.Where(h => h.Layer == MarkerLayer.ContextPayload).Sum(h => h.Count);

        return new MarkerReport
        {
            SessionId = sessionId,
            Hits = hits,
            SystemPromptLayerPresent = system > 0,
            ContextPayloadLayerPresent = payload > 0,
            TotalChars = text.Length,
            Verdict = (system, payload) switch
            {
                (0, 0) => "两层都没有进模型上下文：注入未生效，不要评价模型态度，先回到层判定。",
                (0, _) => "载荷层进了上下文，系统提示词层没进：多半被更高优先的作用域遮蔽（如 agent preset）。",
                (_, 0) => "系统提示词层进了上下文，载荷层没进：上下文文件未加载或未部署。",
                _ => "两层都进了模型上下文：注入已生效，可以按三级判据读推理原文。",
            },
        };
    }
}

/// <summary>注入文本的落盘副本：单文件 exe 内嵌默认文本，首次运行物化到本机数据目录，之后可编辑。</summary>
/// <summary>注入方案。Own = 本工具现有文本（现状，一个字不改）；Protocol = gpt-instruct 原文，逐字节不动。</summary>
public enum InjectionScheme { Own = 0, Protocol = 1 }

public sealed class PolicyTextDef
{
    public required string Name { get; init; }              // prefix / suffix / payload / patch-*
    public required string DisplayName { get; init; }
    public required string FileName { get; init; }
    public required string ResourceSuffix { get; init; }     // 内嵌资源名后缀
    public long? CapBytes { get; init; }
    public bool Editable { get; init; } = true;
    public bool Required { get; init; } = true;
    /// <summary>这条定义属于哪一套方案。默认 Own：既有定义的语义一个字没变。</summary>
    public InjectionScheme Scheme { get; init; } = InjectionScheme.Own;
}

public sealed class PolicyStore
{
    private readonly string _dir;

    public PolicyStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "policy");
        System.IO.Directory.CreateDirectory(_dir);
    }

    public string DirectoryPath => _dir;
    public string PathOf(PolicyTextDef def) => Path.Combine(_dir, def.FileName);

    /// <summary>方案选择器落盘位置：&lt;dataDir&gt;\policy\scheme.txt。文件不存在 = own。</summary>
    public string SchemePath => Path.Combine(_dir, SchemeText.FileName);

    public SchemeStore Schemes => new(_dir);

    public InjectionScheme ActiveScheme(out string? note) => Schemes.Read(out note);

    /// <summary>把内嵌默认文本物化到磁盘；已存在的不覆盖（用户的编辑优先）。</summary>
    public void Materialize(IEnumerable<PolicyTextDef> defs)
    {
        foreach (var def in defs)
        {
            var path = PathOf(def);
            if (File.Exists(path)) continue;
            var text = ReadResource(def.ResourceSuffix);
            if (text is null) continue;
            FileText.Write(path, text);
        }
    }

    public string? Read(PolicyTextDef def)
    {
        var path = PathOf(def);
        if (File.Exists(path)) return FileText.Read(path);
        var builtin = ReadResource(def.ResourceSuffix);
        if (builtin is not null) FileText.Write(path, builtin);
        return builtin;
    }

    public void Write(PolicyTextDef def, string text)
    {
        if (!def.Editable)
            throw new AgentEngineException(EngineErrorCode.Unsupported, $"「{def.DisplayName}」是只读文本，不可编辑。");
        FileText.Write(PathOf(def), text);
    }

    public static string? ReadResource(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s is null) return null;
        using var r = new StreamReader(s, new System.Text.UTF8Encoding(false));
        return r.ReadToEnd();
    }
}

/// <summary>行为探针台账：每次判定落一条，判读结果比记忆靠谱。</summary>
public sealed class LedgerStore
{
    private readonly string _file;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public LedgerStore(string file) => _file = file;

    public IReadOnlyList<ProbeVerdict> Load()
    {
        try
        {
            if (!File.Exists(_file)) return Array.Empty<ProbeVerdict>();
            return JsonSerializer.Deserialize<List<ProbeVerdict>>(FileText.Read(_file), Opts) ?? new List<ProbeVerdict>();
        }
        catch { return Array.Empty<ProbeVerdict>(); }
    }

    public void Append(ProbeVerdict verdict)
    {
        var all = Load().ToList();
        all.Add(verdict);
        FileText.Write(_file, JsonSerializer.Serialize(all, Opts));
    }
}

// ================================================================ 双注入方案（own / protocol）
//
// 方案选择器 = 保留名 "scheme"：SavePolicyFileAsync("scheme", "own" | "protocol") → <dataDir>\policy\scheme.txt。
// 文件不存在 = own（向后兼容：现有安装的行为一个字不变）。非法内容一律按 own，并在 Note 里写明。
// 这里只追加类型，不改任何既有签名。

/// <summary>方案文本的取值、显示名与解析。落盘只认 own / protocol（去空白、忽略大小写）。</summary>
public static class SchemeText
{
    /// <summary>GetPolicyFilesAsync / SavePolicyFileAsync 里的保留名。</summary>
    public const string EntryName = "scheme";
    public const string FileName = "scheme.txt";
    public const string Own = "own";
    public const string Protocol = "protocol";

    public const string DisplayName = "注入方案（own=本工具 / protocol=gpt-instruct）";

    public static string ValueOf(InjectionScheme scheme) =>
        scheme == InjectionScheme.Protocol ? Protocol : Own;

    public static string Describe(InjectionScheme scheme) => scheme == InjectionScheme.Protocol
        ? "protocol（gpt-instruct 原文，逐字节不动）"
        : "own（本工具方案，现有注入文本一个字未改）";

    /// <summary>空文本（文件存在但为空）按 own；非法内容返回 false，调用方按 own 处理并留 Note。</summary>
    public static bool TryParse(string? text, out InjectionScheme scheme)
    {
        scheme = InjectionScheme.Own;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return true;
        if (value.Equals(Own, StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals(Protocol, StringComparison.OrdinalIgnoreCase))
        {
            scheme = InjectionScheme.Protocol;
            return true;
        }
        return false;
    }
}

/// <summary>方案选择器的读写。只认 own / protocol；读失败与非法内容都退回 own，并把原因交回调用方写进 Notes。</summary>
public sealed class SchemeStore
{
    private readonly string _policyDir;

    public SchemeStore(string policyDir) => _policyDir = policyDir;

    public string Path => System.IO.Path.Combine(_policyDir, SchemeText.FileName);

    public string RawText
    {
        get
        {
            try { return FileText.ReadOrNull(Path) ?? ""; }
            catch { return ""; }
        }
    }

    public InjectionScheme Read() => Read(out _);

    public InjectionScheme Read(out string? note)
    {
        note = null;
        if (!File.Exists(Path)) return InjectionScheme.Own;   // 向后兼容：不存在 = own

        string raw;
        try { raw = FileText.Read(Path); }
        catch (Exception ex)
        {
            note = $"方案文件读不出来（{Path}）：{ex.GetType().Name}: {ex.Message}；按 own 处理。";
            return InjectionScheme.Own;
        }

        if (SchemeText.TryParse(raw, out var scheme)) return scheme;

        var shown = raw.Trim();
        if (shown.Length > 40) shown = shown[..40] + "…";
        note = $"方案文件内容非法（「{shown.Replace("\r", " ").Replace("\n", " ")}」）：只认 own / protocol；按 own 处理，不猜测。";
        return InjectionScheme.Own;
    }

    /// <summary>规范化写入（不带行尾空白），成功后回读校验。</summary>
    public void Write(InjectionScheme scheme)
    {
        FileText.Write(Path, SchemeText.ValueOf(scheme));
        var back = FileText.ReadOrNull(Path) ?? "";
        if (!SchemeText.TryParse(back, out var readBack) || readBack != scheme)
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"方案文件写入后回读不一致：{Path}（期望 {SchemeText.ValueOf(scheme)}，实得「{back.Trim()}」）。");
    }
}

/// <summary>
/// 按当前方案解析定义。五个目标都必须走这里，不要在适配器里散落 FirstOrDefault 查询：
/// 同一名字在两套方案里各有一条定义（FileName 不同，互不覆盖）。
/// </summary>
public static class SchemeResolve
{
    public static PolicyTextDef? Pick(IReadOnlyList<PolicyTextDef> defs, string name, InjectionScheme scheme) =>
        defs.FirstOrDefault(d => d.Scheme == scheme && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<PolicyTextDef> ForScheme(IReadOnlyList<PolicyTextDef> defs, InjectionScheme scheme) =>
        defs.Where(d => d.Scheme == scheme).ToArray();

    public static string TextOf(TargetContext ctx, string name, InjectionScheme scheme)
    {
        var def = Pick(ctx.PolicyDefs, name, scheme);
        if (def is null) return "";
        try { return ctx.Policy.Read(def) ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// 空槽 = 该方案下 Required=false 且文本为空白。
    /// 空槽不写文件、不产生 PlannedChange；切换方案时把另一套遗留的文件移除。
    /// </summary>
    public static bool IsEmptySlot(TargetContext ctx, string name, InjectionScheme scheme)
    {
        var def = Pick(ctx.PolicyDefs, name, scheme);
        if (def is null) return false;
        return !def.Required && string.IsNullOrWhiteSpace(TextOf(ctx, name, scheme));
    }

    /// <summary>该方案下不注入的槽位名（状态页 / 界面要逐条列出来）。</summary>
    public static IReadOnlyList<string> EmptySlotNames(TargetContext ctx, InjectionScheme scheme) =>
        ForScheme(ctx.PolicyDefs, scheme)
            .Where(d => !d.Required && string.IsNullOrWhiteSpace(TextOf(ctx, d.Name, scheme)))
            .Select(d => d.Name)
            .ToArray();
}

/// <summary>TargetContext 上的方案扩展（ITargetAdapter.cs 冻结，不能往类型里加成员，所以用扩展方法）。</summary>
public static class SchemeContextExtensions
{
    public static SchemeStore Schemes(this TargetContext ctx) => new(ctx.Policy.DirectoryPath);

    public static InjectionScheme ActiveScheme(this TargetContext ctx) => ctx.Schemes().Read();

    public static InjectionScheme ActiveScheme(this TargetContext ctx, out string? note) =>
        ctx.Schemes().Read(out note);

    public static PolicyTextDef? DefFor(this TargetContext ctx, string name, InjectionScheme scheme) =>
        SchemeResolve.Pick(ctx.PolicyDefs, name, scheme);

    public static string TextFor(this TargetContext ctx, string name, InjectionScheme scheme) =>
        SchemeResolve.TextOf(ctx, name, scheme);

    public static bool IsEmptySlot(this TargetContext ctx, string name, InjectionScheme scheme) =>
        SchemeResolve.IsEmptySlot(ctx, name, scheme);

    public static IReadOnlyList<string> EmptySlotNames(this TargetContext ctx, InjectionScheme scheme) =>
        SchemeResolve.EmptySlotNames(ctx, scheme);
}

/// <summary>
/// B 套（gpt-instruct 原文）的标记：全部取自原文本身，逐字节存在。
/// 切到 protocol 后 CheckMarkers 必须用这一组，否则命中数判据永远是 0（那是假结论）。
/// </summary>
public static class SchemeMarkers
{
    public static IReadOnlyList<string> Protocol { get; } = new[]
    {
        "INJECTED REVISION ACTION",
        "TRANSACTION TURN COMMIT",
        "ROLE: PROJECT EXECUTOR",
        "CONTINUATION CONTROLLER",
        "OUTPUT_CONTRACT",
        "VERIFIED TRANSACTION",
    };

    /// <summary>自有标记 ∪ B 套标记：归属判定（"这是不是本工具写的文件"）必须两套都认。</summary>
    public static IReadOnlyList<string> Union(IReadOnlyList<string> own) =>
        own.Concat(Protocol).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>把 own 规格里某一层的标记换成 B 套标记，其余层原样（其余层的命中 0 是真实观测，不是假结论）。</summary>
    public static IReadOnlyList<MarkerSpec> ReplaceLayer(
        IReadOnlyList<MarkerSpec> own, MarkerLayer layer, IReadOnlyList<string> markers) =>
        own.Where(s => s.Layer != layer)
            .Concat(markers.Select(m => new MarkerSpec(m, layer)))
            .ToArray();

    /// <summary>某个层在指定方案下的标记集：Own → 原标记；Protocol → B 套标记。</summary>
    public static IReadOnlyList<string> ForLayer(InjectionScheme scheme, IReadOnlyList<string> own) =>
        scheme == InjectionScheme.Protocol ? Protocol : own;
}

/// <summary>一个"空槽在客户端目标位置上的遗留文件"的处置计划。</summary>
public sealed class SlotRemovalPlan
{
    public required string Path { get; init; }
    public string? Existing { get; init; }
    public bool Exists => Existing is not null;
    public bool IsOurs { get; init; }
    public bool WillWrite => IsOurs;
    public long BeforeBytes => Existing is null ? 0 : FileText.Bytes(Existing);
    public required string Note { get; init; }
}

/// <summary>
/// 空槽切换方案时的清理：只删**本工具的**文件，别人的文件一律不动（按标记判归属）。
/// 移除也走 PlannedChange（WillWrite=true、AfterBytes=0），并在 Notes 里说明原因。
/// </summary>
public static class SchemeSlotFile
{
    public static SlotRemovalPlan Plan(string path, IReadOnlyList<string> ownershipMarkers, string why)
    {
        var existing = FileText.ReadOrNull(path);
        if (existing is null)
            return new SlotRemovalPlan
            {
                Path = path, Existing = null, IsOurs = false,
                Note = $"{why}：目标位置本来没有文件（{path}）。",
            };

        var marker = ownershipMarkers.FirstOrDefault(m => !string.IsNullOrEmpty(m) && existing.Contains(m, StringComparison.Ordinal));
        if (marker is null)
            return new SlotRemovalPlan
            {
                Path = path, Existing = existing, IsOurs = false,
                Note = $"{why}：现有文件不是本工具写的（不含任何注入标记），一律不动：{path}",
            };

        return new SlotRemovalPlan
        {
            Path = path, Existing = existing, IsOurs = true,
            Note = $"{why}：现有文件含本工具标记「{marker}」，判定为本工具此前部署，移除它（其余不碰）。",
        };
    }

    public static PlannedChange? Change(SlotRemovalPlan plan, LayerKind layer, string why, string title = "方案切换")
    {
        if (!plan.WillWrite) return null;
        return new PlannedChange
        {
            Path = plan.Path,
            Layer = layer,
            WillWrite = true,
            BeforeBytes = plan.BeforeBytes,
            AfterBytes = 0,
            Notes = new[] { Notes.Info(title, plan.Note, plan.Path) },
        };
    }

    /// <summary>执行移除；返回是否真的删了。</summary>
    public static bool Execute(SlotRemovalPlan plan)
    {
        if (!plan.WillWrite) return false;
        try
        {
            if (File.Exists(plan.Path)) File.Delete(plan.Path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
