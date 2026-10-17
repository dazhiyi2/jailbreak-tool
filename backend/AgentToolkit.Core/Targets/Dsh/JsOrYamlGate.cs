using AgentToolkit.Core.Support;
using YamlDotNet.RepresentationModel;

namespace AgentToolkit.Core.Targets.Dsh;

/// <summary>
/// 真实 YAML 解析闸门（SPEC 第 3 节第 6 条：改 YAML 必须用真实解析器解析一遍，失败拒绝写入）。
///
/// DSH 的补丁层是 YAML，但里面允许自定义 tag `!!js`（profile 补丁层文件头注释明示），
/// 默认的 schema/反序列化器不认它。这里用 YamlDotNet 的**表示模型**（YamlStream）解析：
/// 它保留 tag 而不做 tag→类型解析，所以 `!!js process.platform === 'win32'` 能原样通过，
/// 同时结构错误（缩进、制表符、重复键、块标量断裂）照样会抛——既有真实解析器的约束力，
/// 又不会因为 `!!js` 误报。**只解析、不改写**：preset 覆写行永远是纯文本变换的产物。
/// </summary>
public static class JsOrYamlGate
{
    /// <summary>解析整份文本。失败时返回首行错误信息（不含堆栈）。空文本按空的顶层序列处理。</summary>
    public static (bool Ok, string? Error, YamlNode? Root) TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (true, null, new YamlSequenceNode());
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0) return (false, "解析结果没有任何文档", null);
            return (true, null, stream.Documents[0].RootNode);
        }
        catch (Exception ex)
        {
            var msg = (ex.Message ?? ex.GetType().Name).Split('\n')[0].Trim();
            return (false, msg, null);
        }
    }

    /// <summary>空文本按"空序列"处理（出厂的空补丁层是合法的顶层数组）。</summary>
    public static bool IsTopLevelSequence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var (ok, _, root) = TryParse(text);
        return ok && root is YamlSequenceNode;
    }

    /// <summary>表示模型 → 普通对象树（标量→string，序列→List，映射→Dictionary），便于断言。</summary>
    public static object? ToPlain(YamlNode node) => node switch
    {
        YamlScalarNode s => s.Value,
        YamlSequenceNode seq => seq.Children.Select(ToPlain).ToList(),
        YamlMappingNode map => map.Children.ToDictionary(
            kv => (kv.Key as YamlScalarNode)?.Value ?? kv.Key.ToString() ?? "",
            kv => ToPlain(kv.Value)),
        _ => null,
    };

    /// <summary>
    /// 端口自 dsh-patch.mjs 的 validateResult：结构检查 + 真实 YAML 解析断言。
    /// 返回问题清单，空 = 通过。
    /// </summary>
    public static IReadOnlyList<string> Validate(string text, IReadOnlyList<string> presetIds)
    {
        var problems = new List<string>();

        if (text.Contains('\t')) problems.Add("结果含制表符（YAML 不允许 tab 缩进）");

        var begin = FileText.Count(text, DshPatcher.MarkBegin);
        var end = FileText.Count(text, DshPatcher.MarkEnd);
        if (begin != 1) problems.Add($"起始 marker 出现 {begin} 次（应为 1）");
        if (end != 1) problems.Add($"结束 marker 出现 {end} 次（应为 1）");

        var seg = DshPatcher.ExtractManagedSegment(text) ?? "";
        foreach (var need in new[] { $"id: {DshPatcher.TargetId}", "name:", "personaPrefix: |" })
            if (!seg.Contains(need, StringComparison.Ordinal)) problems.Add($"管理段缺少 `{need}`");
        foreach (var id in presetIds)
            if (!seg.Contains($"- id: {id}", StringComparison.Ordinal)) problems.Add($"管理段缺少 preset 覆写行 `{id}`");

        var (ok, err, root) = TryParse(text);
        if (!ok)
        {
            problems.Add($"YAML 解析失败：{err}");
            return problems;
        }

        if (root is not YamlSequenceNode seq)
        {
            problems.Add("解析结果不是顶层数组");
            return problems;
        }

        var rows = seq.Children.OfType<YamlMappingNode>().ToList();
        var mine = rows.Where(r => Scalar(r, "id") == DshPatcher.TargetId).ToList();
        if (mine.Count != 1) problems.Add($"解析后 id: {DshPatcher.TargetId} 的 row 有 {mine.Count} 条（应为 1）");

        var personaPrefix = ConfigScalar(mine.FirstOrDefault(), "personaPrefix");
        if (string.IsNullOrWhiteSpace(personaPrefix)) problems.Add("解析后 personaPrefix 为空");

        foreach (var id in presetIds)
        {
            var hit = rows.Where(r => Scalar(r, "id") == id).ToList();
            if (hit.Count != 1)
            {
                problems.Add($"解析后 preset 覆写行 {id} 有 {hit.Count} 条（应为 1）");
                continue;
            }

            var plugins = ConfigChild(hit[0], "plugins") as YamlSequenceNode;
            if (plugins is null || plugins.Children.Count < 2)
                problems.Add($"{id}: plugins 列表异常（{(plugins is null ? "非数组" : plugins.Children.Count.ToString())}）——不能丢工具");

            var persona = plugins?.Children.OfType<YamlMappingNode>()
                .FirstOrDefault(p => Scalar(p, "id") == "persona");
            if (persona is null) problems.Add($"{id}: 缺少 persona 子插件");
            else if (string.IsNullOrWhiteSpace(ConfigScalar(persona, "prefix"))) problems.Add($"{id}: persona.prefix 为空");
        }

        return problems;
    }

    private static string? Scalar(YamlMappingNode? map, string key) => ConfigNode(map, key) is YamlScalarNode s ? s.Value : null;

    private static YamlNode? ConfigNode(YamlMappingNode? map, string key)
    {
        if (map is null) return null;
        return map.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;
    }

    /// <summary>row.config[key]（preset 覆写行的 plugins / persona 都在 row 的 config 下）。</summary>
    private static YamlNode? ConfigChild(YamlMappingNode? row, string key) =>
        ConfigNode(row, "config") is YamlMappingNode cfg ? ConfigNode(cfg, key) : null;

    private static string? ConfigScalar(YamlMappingNode? row, string key) => ConfigChild(row, key) as YamlScalarNode is { } s ? s.Value : null;
}
