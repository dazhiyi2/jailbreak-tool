using System.Reflection;
using System.Text.Json;
using AgentToolkit.Contracts;

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
public sealed class PolicyTextDef
{
    public required string Name { get; init; }              // prefix / suffix / payload / patch-*
    public required string DisplayName { get; init; }
    public required string FileName { get; init; }
    public required string ResourceSuffix { get; init; }     // 内嵌资源名后缀
    public long? CapBytes { get; init; }
    public bool Editable { get; init; } = true;
    public bool Required { get; init; } = true;
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
