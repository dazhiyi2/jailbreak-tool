using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Opencode;

/// <summary>对 opencode.json 的一次「体检 / 改写」结论（预览与注入共用同一份计划）。</summary>
public sealed class InstructionsEdit
{
    public bool FileExists { get; init; }
    public bool ParseOk { get; init; }
    public bool TopLevelObject { get; init; }
    public bool InstructionsKeyPresent { get; init; }
    public bool InstructionsIsArray { get; init; }
    public bool InstructionsIsString { get; init; }
    /// <summary>**文件里现在**的数组已含本工具的指令文件路径（幂等判据；写完之后看 <see cref="NextEntries"/>）。</summary>
    public bool PathPresent { get; init; }
    public bool WillWrite { get; init; }
    public string Origin { get; init; } = "";
    public string Next { get; init; } = "";
    public IReadOnlyList<string> OriginKeys { get; init; } = Array.Empty<string>();
    /// <summary>
    /// **文件里现在**instructions 的字符串条目（人话展示 / 状态判据）。
    /// 注意：这是「当前有什么」，不是「写完之后会有什么」——后者看 <see cref="NextEntries"/>。
    /// 分不清这两个就会把「没有 instructions 键」误报成「已经装上了」。
    /// </summary>
    public IReadOnlyList<string> Entries { get; init; } = Array.Empty<string>();
    /// <summary>改写**之后** instructions 里会有哪些条目（不写入时等于 <see cref="Entries"/>）。</summary>
    public IReadOnlyList<string> NextEntries { get; init; } = Array.Empty<string>();
    /// <summary>非空 = fail-closed：拒绝写入，原文件一个字节都不动。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public string? Detail { get; init; }
}

/// <summary>某个目标 agent 的 <c>prompt</c> 字段现状（值级前后对照用；契约的 PlannedChange 只有字节数）。</summary>
public sealed class AgentPromptState
{
    public required string Name { get; init; }
    public bool AgentExists { get; init; }
    public bool PromptPresent { get; init; }
    public bool PromptIsString { get; init; }
    /// <summary>这一段文本是不是本工具写进去的（逐字节等于当前系统层文本，或含系统层标记）。</summary>
    public bool PromptIsOurs { get; init; }
    public long PromptBytes { get; init; }
    /// <summary>该 agent 下除 prompt 之外的字段数——这些字段必须原样保留。</summary>
    public int OtherFields { get; init; }

    public string Kind => !AgentExists
        ? "agent 不存在（本工具会新建这一条）"
        : !PromptPresent
            ? "agent 已存在、没有 prompt 字段"
            : !PromptIsString
                ? "prompt 存在但不是字符串（fail-closed 拒写）"
                : PromptIsOurs
                    ? "prompt 已是本工具的系统层文本"
                    : "prompt 是别的内容（本工具会覆盖它）";
}

/// <summary>
/// 对 opencode.json 里 <c>agent.&lt;名字&gt;.prompt</c> 的一次「体检 / 改写」结论（系统提示词层）。
/// 与 <see cref="InstructionsEdit"/> 是两套平行结论：同一份文件、互不覆盖。
/// </summary>
public sealed class AgentPromptEdit
{
    public bool FileExists { get; init; }
    public bool ParseOk { get; init; }
    public bool TopLevelObject { get; init; }
    public bool AgentKeyPresent { get; init; }
    public bool AgentIsObject { get; init; }
    /// <summary>**文件里现在**四个目标 agent 是否都已带本工具的系统层文本（幂等判据）。</summary>
    public bool PromptsPresent { get; init; }
    public bool WillWrite { get; init; }
    public string Origin { get; init; } = "";
    public string Next { get; init; } = "";
    public IReadOnlyList<string> OriginKeys { get; init; } = Array.Empty<string>();
    /// <summary>四个目标 agent 的现状（值级前后对照）。</summary>
    public IReadOnlyList<AgentPromptState> Agents { get; init; } = Array.Empty<AgentPromptState>();
    /// <summary>**文件里现在**已带本工具文本的 agent 名。</summary>
    public IReadOnlyList<string> AppliedAgents { get; init; } = Array.Empty<string>();
    /// <summary>改写**之后**会带本工具文本的 agent 名（不写入时等于 <see cref="AppliedAgents"/>）。</summary>
    public IReadOnlyList<string> NextAppliedAgents { get; init; } = Array.Empty<string>();
    /// <summary>非空 = fail-closed：拒绝写入，原文件一个字节都不动。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public string? Detail { get; init; }
}

/// <summary>
/// 配置指令层：<c>&lt;config&gt;/opencode.json</c> 的 <c>instructions</c> 键
/// （官方 schema 原文：*"Additional paths or URLs supplying ambient instructions"*，值可以是数组或单值）。
///
/// 纪律：
///   · **绝不覆盖用户的其它键**——只对 instructions 做增删，其余键与条目原样保留；
///   · 解析失败 / instructions 类型不是数组或字符串 → fail-closed（抛 SelfCheckFailed，不写盘）；
///   · 幂等：数组里已有本工具的路径 → 不重写、不重复添加；
///   · 写完必须重新解析校验（能解析、顶层是对象、instructions 仍是数组、原有键一个不少）。
/// </summary>
public static class OpencodeConfig
{
    public const string SchemaUrl = "https://opencode.ai/config.json";
    public const string InstructionsKey = "instructions";
    /// <summary>系统提示词层的落点键（官方 schema：<c>AgentConfig.prompt: string</c>）。</summary>
    public const string AgentKey = "agent";
    public const string PromptKey = "prompt";

    /// <summary>
    /// 系统层要写进哪几个 agent。<c>title</c> / <c>summary</c> / <c>compaction</c> 是内部工具 agent：
    /// 它们本来就只干很窄的活（起标题、压缩上下文），塞进策略文本会干扰它们，所以一个都不碰。
    /// </summary>
    public static IReadOnlyList<string> TargetAgents { get; } = new[] { "build", "plan", "general", "explore" };

    /// <summary>2 空格缩进；不转义非 ASCII（用户配置里常有中文，转义会把它变成 \uXXXX）。</summary>
    public static JsonSerializerOptions WriterOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode node) => node.ToJsonString(WriterOptions) + "\n";

    /// <summary>顶层是不是能解析的 JSON 对象（状态页的 ParseOk 判据）。</summary>
    public static bool TryParseObject(string? text)
    {
        try { return JsonNode.Parse(text ?? "") is JsonObject; }
        catch { return false; }
    }

    /// <summary>文件不存在时按出厂结构创建的内容。</summary>
    public static string FactoryJson(string instructionsPath)
    {
        var root = new JsonObject
        {
            ["$schema"] = SchemaUrl,
            [InstructionsKey] = new JsonArray { JsonValue.Create(instructionsPath) },
        };
        return Serialize(root);
    }

    /// <summary>把 instructions 里的字符串条目取出来（非字符串条目原样保留但不展示）。</summary>
    public static IReadOnlyList<string> EntriesOf(string? text)
    {
        var doc = ParseOrNull(text);
        return doc is null ? Array.Empty<string>() : EntriesOf(doc);
    }

    private static IReadOnlyList<string> EntriesOf(JsonNode root)
    {
        var node = root[InstructionsKey];
        switch (node)
        {
            case JsonArray arr:
                return arr.OfType<JsonValue>()
                    .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                    .Where(s => s is not null)
                    .Select(s => s!)
                    .ToArray();
            case JsonValue v when v.TryGetValue<string>(out var one) && one is not null:
                return new[] { one };
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>instructions 里是否已有该路径（OrdinalIgnoreCase：Windows 路径大小写不敏感）。</summary>
    public static bool ContainsPath(string? text, string path) =>
        EntriesOf(text).Any(e => string.Equals(e, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 这份文件是不是「本工具从零创建的那一份」——只有 $schema 与 instructions=[我们的路径]。
    /// 是的话，还原时可以把它删掉（恢复到出厂的不存在状态），而不是留下一个空壳配置。
    /// </summary>
    public static bool IsFactoryOnly(string? text, string instructionsPath)
    {
        var node = ParseOrNull(text);
        if (node is not JsonObject obj || obj.Count != 2) return false;
        if (!obj.ContainsKey("$schema") || !obj.ContainsKey(InstructionsKey)) return false;
        var entries = EntriesOf(obj);
        return entries.Count == 1 && string.Equals(entries[0], instructionsPath, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? ParseOrNull(string? text)
    {
        try { return JsonNode.Parse(text ?? ""); }
        catch { return null; }
    }

    /// <summary>
    /// 生成一次改写计划。**纯函数**：不碰磁盘，调用方拿 Next 去写。
    /// </summary>
    public static InstructionsEdit Plan(string? originText, bool fileExists, string instructionsPath)
    {
        if (!fileExists)
        {
            return new InstructionsEdit
            {
                FileExists = false,
                ParseOk = true,
                TopLevelObject = true,
                InstructionsKeyPresent = false,
                InstructionsIsArray = false,
                PathPresent = false,
                WillWrite = true,
                Origin = "",
                Next = FactoryJson(instructionsPath),
                Entries = Array.Empty<string>(),
                NextEntries = new[] { instructionsPath },
                Detail = $"文件不存在：按出厂结构创建 {{\"$schema\":\"{SchemaUrl}\",\"instructions\":[\"<本工具指令文件>\"]}}。",
            };
        }

        var origin = originText ?? "";
        JsonNode? node;
        try { node = JsonNode.Parse(origin); }
        catch (JsonException ex)
        {
            return Invalid(origin, $"opencode.json 不是合法 JSON：{ex.Message}（本工具不做 JSONC 改写：注释与尾逗号也按非法处理）。");
        }
        catch (Exception ex)
        {
            return Invalid(origin, $"opencode.json 解析失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (node is not JsonObject obj)
            return Invalid(origin, "opencode.json 顶层不是 JSON 对象：拒绝改写（不覆盖用户文档）。");

        var originKeys = obj.Select(p => p.Key).ToArray();
        var hasKey = obj.ContainsKey(InstructionsKey);
        // 先记下「现在有什么」再动手改：Entries 必须是文件当前的样子，否则状态页会把
        // 「还没有 instructions 键」误报成「已经装上了」。
        var presentEntries = hasKey ? EntriesOf(obj) : Array.Empty<string>();

        if (!hasKey)
        {
            obj[InstructionsKey] = new JsonArray { JsonValue.Create(instructionsPath) };
            return new InstructionsEdit
            {
                FileExists = true,
                ParseOk = true,
                TopLevelObject = true,
                InstructionsKeyPresent = false,
                InstructionsIsArray = true,
                PathPresent = false,
                WillWrite = true,
                Origin = origin,
                Next = Serialize(obj),
                OriginKeys = originKeys,
                Entries = Array.Empty<string>(),
                NextEntries = new[] { instructionsPath },
                Detail = "原来没有 instructions 键：新增为数组并放入本工具的指令文件路径，其它键原样保留。",
            };
        }

        var current = obj[InstructionsKey];
        switch (current)
        {
            case JsonArray arr:
            {
                var contains = arr.OfType<JsonValue>()
                    .Any(v => v.TryGetValue<string>(out var s) && s is not null &&
                              string.Equals(s, instructionsPath, StringComparison.OrdinalIgnoreCase));
                if (contains)
                {
                    return new InstructionsEdit
                    {
                        FileExists = true,
                        ParseOk = true,
                        TopLevelObject = true,
                        InstructionsKeyPresent = true,
                        InstructionsIsArray = true,
                        PathPresent = true,
                        WillWrite = false,
                        Origin = origin,
                        Next = origin,
                        OriginKeys = originKeys,
                        Entries = presentEntries,
                        NextEntries = presentEntries,
                        Detail = "instructions 数组里已有本工具的指令文件路径：不重复添加（幂等，字节不变）。",
                    };
                }

                arr.Add(JsonValue.Create(instructionsPath));
                return new InstructionsEdit
                {
                    FileExists = true,
                    ParseOk = true,
                    TopLevelObject = true,
                    InstructionsKeyPresent = true,
                    InstructionsIsArray = true,
                    PathPresent = false,
                    WillWrite = true,
                    Origin = origin,
                    Next = Serialize(obj),
                    OriginKeys = originKeys,
                    Entries = presentEntries,
                    NextEntries = EntriesOf(obj),
                    Detail = $"instructions 是数组（原有 {arr.Count - 1} 条）：追加本工具的指令文件路径，其余条目原样保留。",
                };
            }

            case JsonValue v when v.TryGetValue<string>(out var single) && single is not null:
            {
                var arr = new JsonArray();
                if (!string.Equals(single, instructionsPath, StringComparison.OrdinalIgnoreCase))
                    arr.Add(JsonValue.Create(single));
                arr.Add(JsonValue.Create(instructionsPath));
                obj[InstructionsKey] = arr;
                return new InstructionsEdit
                {
                    FileExists = true,
                    ParseOk = true,
                    TopLevelObject = true,
                    InstructionsKeyPresent = true,
                    InstructionsIsArray = false,
                    InstructionsIsString = true,
                    PathPresent = string.Equals(single, instructionsPath, StringComparison.OrdinalIgnoreCase),
                    WillWrite = true,
                    Origin = origin,
                    Next = Serialize(obj),
                    OriginKeys = originKeys,
                    Entries = presentEntries,
                    NextEntries = EntriesOf(obj),
                    Detail = "instructions 是单值（schema 允许数组或单值）：转成数组并加入本工具的路径，原值保留。",
                };
            }

            default:
                return Invalid(origin,
                    $"instructions 的类型既不是数组也不是字符串（{DescribeKind(current)}）：拒绝改写——硬改成数组会破坏用户配置。");
        }
    }

    /// <summary>
    /// 写完后的自检：能解析、顶层是对象、instructions 仍是数组且含本工具路径、原有键一个不少。
    /// 返回的问题列表非空 = 调用方必须回滚并抛错。
    /// </summary>
    public static IReadOnlyList<string> ValidateWritten(string path, IReadOnlyList<string> originKeys, string instructionsPath)
    {
        string text;
        try { text = FileText.Read(path); }
        catch (Exception ex) { return new[] { $"写入后读不回来：{ex.GetType().Name}: {ex.Message}" }; }

        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException ex) { return new[] { $"写入后重新解析失败：{ex.Message}" }; }
        catch (Exception ex) { return new[] { $"写入后重新解析失败：{ex.GetType().Name}: {ex.Message}" }; }

        if (node is not JsonObject obj) return new[] { "写入后顶层不是 JSON 对象。" };

        var problems = new List<string>();
        foreach (var key in originKeys)
            if (!obj.ContainsKey(key)) problems.Add($"原有键丢失：{key}");

        if (obj[InstructionsKey] is not JsonArray arr)
        {
            problems.Add("instructions 不再是数组。");
        }
        else if (!arr.OfType<JsonValue>().Any(v => v.TryGetValue<string>(out var s) && s is not null &&
                                                    string.Equals(s, instructionsPath, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("instructions 数组里没有本工具的指令文件路径。");
        }

        return problems;
    }

    private static InstructionsEdit Invalid(string origin, string problem) => new()
    {
        FileExists = true,
        ParseOk = false,
        Origin = origin,
        Next = origin,
        WillWrite = false,
        Problems = new[] { problem },
        Detail = problem,
    };

    // ================================================================ 系统提示词层：agent.<名字>.prompt

    /// <summary>
    /// 这段 prompt 是不是本工具写进去的：逐字节等于当前系统层文本，或含系统层标记之一。
    /// 判据放在这里而不是"凡是有 prompt 就当成我们的"——别人自己写的 prompt 不能被我方撤销掉。
    /// </summary>
    public static bool IsOurPrompt(string? value, string promptText) =>
        value is not null &&
        (string.Equals(value, promptText, StringComparison.Ordinal) ||
         OpencodeMarkers.SystemLayerMarkers.Any(m => m.Length > 0 && value.Contains(m, StringComparison.Ordinal)));

    /// <summary>
    /// 系统提示词层（<c>agent.&lt;名字&gt;.prompt</c>）的写入计划。**纯函数**：不碰磁盘。
    ///
    /// 纪律：
    ///   · 只动 <see cref="TargetAgents"/> 四个 agent 的 prompt 字段，其它键、其它 agent、其它字段原样保留；
    ///   · 幂等：四个 prompt 已是当前文本 → WillWrite=false，Next 就是原文（不重写、不动 mtime 由调用方保证）；
    ///   · fail-closed：非法 JSON / agent 非对象 / 某个 agent 非对象 / prompt 存在但不是字符串
    ///     → Problems 非空、Next 等于原文，调用方一律不写盘。
    /// </summary>
    public static AgentPromptEdit PlanAgentPrompts(string? originText, bool fileExists, string promptText,
        IReadOnlyList<string>? agentNames = null)
    {
        var names = agentNames ?? TargetAgents;

        if (!fileExists)
        {
            var fresh = new JsonObject { ["$schema"] = SchemaUrl };
            var agentObj = new JsonObject();
            foreach (var name in names)
                agentObj[name] = new JsonObject { [PromptKey] = promptText };
            fresh[AgentKey] = agentObj;
            return new AgentPromptEdit
            {
                FileExists = false,
                ParseOk = true,
                TopLevelObject = true,
                AgentKeyPresent = false,
                AgentIsObject = true,
                PromptsPresent = false,
                WillWrite = true,
                Origin = "",
                Next = Serialize(fresh),
                OriginKeys = Array.Empty<string>(),
                Agents = names.Select(n => new AgentPromptState { Name = n }).ToArray(),
                NextAppliedAgents = names.ToArray(),
                Detail = $"文件不存在：创建 {{\"$schema\":…,\"agent\":{{{string.Join("、", names.Select(n => n + ".prompt"))}}}}}。",
            };
        }

        var origin = originText ?? "";
        JsonNode? node;
        try { node = JsonNode.Parse(origin); }
        catch (JsonException ex)
        {
            return InvalidAgentEdit(origin, $"opencode.json 不是合法 JSON：{ex.Message}（本工具不做 JSONC 改写：注释与尾逗号也按非法处理）。");
        }
        catch (Exception ex)
        {
            return InvalidAgentEdit(origin, $"opencode.json 解析失败：{ex.GetType().Name}: {ex.Message}");
        }

        if (node is not JsonObject obj)
            return InvalidAgentEdit(origin, "opencode.json 顶层不是 JSON 对象：拒绝改写（不覆盖用户文档）。");

        var originKeys = obj.Select(p => p.Key).ToArray();
        var hasAgent = obj.ContainsKey(AgentKey);
        var current = hasAgent ? obj[AgentKey] : null;

        if (hasAgent && current is not JsonObject)
            return InvalidAgentEdit(origin,
                $"agent 的类型不是对象（{DescribeKind(current)}）：拒绝改写——系统提示词层要写在 agent.<名字>.prompt 下，" +
                "硬把它改成对象会破坏用户配置。", originKeys, hasAgent);

        // 先把四个 agent 的现状读出来（Entry 必须是文件当前的样子，不能拿"写完之后"糊弄状态页）。
        var states = new List<AgentPromptState>();
        var problems = new List<string>();
        var agentRoot = current as JsonObject;
        foreach (var name in names)
        {
            // 用 TryGetPropertyValue 区分「键不存在」与「键在但是 JSON null」：后者不是对象，属于 fail-closed 那一类。
            if (agentRoot is null || !agentRoot.TryGetPropertyValue(name, out var entry))
            {
                states.Add(new AgentPromptState { Name = name, AgentExists = false });
                continue;
            }

            if (entry is not JsonObject agentObj)
            {
                problems.Add($"agent.{name} 的类型不是对象（{DescribeKind(entry)}）：拒绝改写这一条。");
                states.Add(new AgentPromptState { Name = name, AgentExists = true });
                continue;
            }

            var otherFields = agentObj.Count(p => !string.Equals(p.Key, PromptKey, StringComparison.Ordinal));
            if (!agentObj.TryGetPropertyValue(PromptKey, out var promptNode))
            {
                states.Add(new AgentPromptState
                {
                    Name = name, AgentExists = true, PromptPresent = false, OtherFields = otherFields,
                });
                continue;
            }

            if (promptNode is not JsonValue pv || !pv.TryGetValue<string>(out var promptTextValue) || promptTextValue is null)
            {
                problems.Add($"agent.{name}.prompt 存在但不是字符串（{DescribeKind(promptNode)}）：拒绝改写（覆盖它会丢用户内容）。");
                states.Add(new AgentPromptState
                {
                    Name = name, AgentExists = true, PromptPresent = true, PromptIsString = false,
                    OtherFields = otherFields,
                });
                continue;
            }

            states.Add(new AgentPromptState
            {
                Name = name,
                AgentExists = true,
                PromptPresent = true,
                PromptIsString = true,
                PromptIsOurs = IsOurPrompt(promptTextValue, promptText),
                PromptBytes = FileText.Bytes(promptTextValue),
                OtherFields = otherFields,
            });
        }

        var applied = states.Where(s => s.PromptIsOurs).Select(s => s.Name).ToArray();
        if (problems.Count > 0)
        {
            return new AgentPromptEdit
            {
                FileExists = true,
                ParseOk = true,
                TopLevelObject = true,
                AgentKeyPresent = hasAgent,
                AgentIsObject = hasAgent,
                PromptsPresent = applied.Length == names.Count,
                WillWrite = false,
                Origin = origin,
                Next = origin,
                OriginKeys = originKeys,
                Agents = states,
                AppliedAgents = applied,
                NextAppliedAgents = applied,
                Problems = problems,
                Detail = string.Join("；", problems),
            };
        }

        var allOurs = applied.Length == names.Count;
        if (allOurs)
        {
            return new AgentPromptEdit
            {
                FileExists = true,
                ParseOk = true,
                TopLevelObject = true,
                AgentKeyPresent = hasAgent,
                AgentIsObject = true,
                PromptsPresent = true,
                WillWrite = false,
                Origin = origin,
                Next = origin,
                OriginKeys = originKeys,
                Agents = states,
                AppliedAgents = applied,
                NextAppliedAgents = applied,
                Detail = $"{string.Join("、", names)} 四个 agent 的 prompt 已是当前系统层文本：不重写、不动 mtime（幂等）。",
            };
        }

        var target = (current as JsonObject) ?? new JsonObject();
        foreach (var name in names)
        {
            if (target[name] is JsonObject existing) existing[PromptKey] = promptText;
            else target[name] = new JsonObject { [PromptKey] = promptText };
        }
        obj[AgentKey] = target;

        var createdAgents = states.Where(s => !s.AgentExists).Select(s => s.Name).ToArray();
        var overwritten = states.Where(s => s is { AgentExists: true, PromptPresent: true, PromptIsOurs: false })
            .Select(s => s.Name).ToArray();
        return new AgentPromptEdit
        {
            FileExists = true,
            ParseOk = true,
            TopLevelObject = true,
            AgentKeyPresent = hasAgent,
            AgentIsObject = true,
            PromptsPresent = false,
            WillWrite = true,
            Origin = origin,
            Next = Serialize(obj),
            OriginKeys = originKeys,
            Agents = states,
            AppliedAgents = applied,
            NextAppliedAgents = names.ToArray(),
            Detail =
                (hasAgent ? "agent 键已存在：只覆盖/新增这四个 agent 的 prompt 字段" : "原来没有 agent 键：新增它并放入四个 agent") +
                $"；新建 {createdAgents.Length} 条" +
                (createdAgents.Length > 0 ? $"（{string.Join("、", createdAgents)}）" : "") +
                (overwritten.Length > 0 ? $"、覆盖 {overwritten.Length} 条原有 prompt（{string.Join("、", overwritten)}）" : "") +
                "；其余键、其余 agent、其余字段一个不少。",
        };
    }

    /// <summary>
    /// 系统提示词层写完后的自检：能解析、顶层是对象、原有键一个不少、四个 agent 的 prompt 逐字节等于注入文本。
    /// 返回的问题列表非空 = 调用方必须回滚并抛错（写入前对计划文本也跑一遍，问题非空就不写盘）。
    /// </summary>
    public static IReadOnlyList<string> ValidateAgentPrompts(string? text, IReadOnlyList<string> originKeys,
        string promptText, IReadOnlyList<string>? agentNames = null)
    {
        var names = agentNames ?? TargetAgents;
        var problems = new List<string>();

        JsonNode? node = null;
        var parseFailed = false;
        try { node = JsonNode.Parse(text ?? ""); }
        catch (JsonException ex) { parseFailed = true; problems.Add($"重新解析失败：{ex.Message}"); }
        catch (Exception ex) { parseFailed = true; problems.Add($"重新解析失败：{ex.GetType().Name}: {ex.Message}"); }
        if (parseFailed) return problems;

        if (node is not JsonObject obj)
        {
            problems.Add("顶层不是 JSON 对象。");
            return problems;
        }

        foreach (var key in originKeys)
            if (!obj.ContainsKey(key)) problems.Add($"原有键丢失：{key}");

        if (obj[AgentKey] is not JsonObject agentObj)
        {
            problems.Add("agent 不是 JSON 对象。");
            return problems;
        }

        foreach (var name in names)
        {
            if (agentObj[name] is not JsonObject entry)
            {
                problems.Add($"agent.{name} 不是 JSON 对象。");
                continue;
            }
            if (entry[PromptKey] is not JsonValue v || !v.TryGetValue<string>(out var s) || s is null)
                problems.Add($"agent.{name}.prompt 不是字符串。");
            else if (!string.Equals(s, promptText, StringComparison.Ordinal))
                problems.Add($"agent.{name}.prompt 与系统层文本不一致（期望 {FileText.Bytes(promptText)} 字节，实得 {FileText.Bytes(s)} 字节）。");
        }

        return problems;
    }

    /// <summary>
    /// 逆向撤销（没有可信备份时用）：把本工具写进去的 prompt 移除。
    ///   · <c>agent</c> 键**不删**（别人可能在里面有配置）；
    ///   · 某个 agent 因为我们才被创建、撤销后变成空对象 → 删掉那一条；
    ///   · 别人的 prompt（内容不是我们的）与其它字段原样保留。
    /// 返回 null = 没有我们的东西（无需改动）。
    /// </summary>
    public static string? RemoveOurAgentPrompts(string origin, string promptText,
        out IReadOnlyList<string> removed, out IReadOnlyList<string> keptForeign,
        IReadOnlyList<string>? agentNames = null)
    {
        var names = agentNames ?? TargetAgents;
        removed = Array.Empty<string>();
        keptForeign = Array.Empty<string>();

        JsonNode? node;
        try { node = JsonNode.Parse(origin); }
        catch { return null; }
        if (node is not JsonObject obj || obj[AgentKey] is not JsonObject agentObj) return null;

        var gone = new List<string>();
        var foreign = new List<string>();
        foreach (var name in names)
        {
            if (agentObj[name] is not JsonObject entry) continue;
            if (entry[PromptKey] is not JsonValue v || !v.TryGetValue<string>(out var s) || s is null)
            {
                continue;
            }
            if (!IsOurPrompt(s, promptText))
            {
                foreign.Add(name);
                continue;
            }
            entry.Remove(PromptKey);
            gone.Add(name);
            if (entry.Count == 0) agentObj.Remove(name);   // 只因我们才被创建的那一条，撤销后是空对象 → 删掉
        }

        if (gone.Count == 0)
        {
            keptForeign = foreign;
            return null;
        }

        obj[AgentKey] = agentObj;   // agent 键保留（哪怕变成 {}）
        removed = gone;
        keptForeign = foreign;
        return Serialize(obj);
    }

    /// <summary>
    /// 这份 opencode.json 是不是「整份都是本工具写的」：只有 $schema、instructions（只有本工具那一条）、
    /// agent（每条都只含本工具的 prompt，或已是空对象），且至少有一处确实是本工具写的。
    /// 用途：**没有备份时**只有这种文件才允许直接删除（恢复出厂的不存在状态）；
    /// 里面只要有任何别人的键、别人的 instructions 条目或别人的 agent 字段，就一律走逆向撤销、绝不删文件。
    /// </summary>
    public static bool IsToolkitCreatedOnly(string? text, string instructionsPath, string promptText,
        IReadOnlyList<string>? agentNames = null)
    {
        var names = agentNames ?? TargetAgents;
        JsonNode? node;
        try { node = JsonNode.Parse(text ?? ""); }
        catch { return false; }
        if (node is not JsonObject obj) return false;

        var ours = false;
        foreach (var pair in obj)
        {
            switch (pair.Key)
            {
                case "$schema":
                    continue;

                case InstructionsKey:
                    switch (pair.Value)
                    {
                        case JsonArray arr:
                            foreach (var element in arr)
                            {
                                if (element is JsonValue ev && ev.TryGetValue<string>(out var es) && es is not null &&
                                    string.Equals(es, instructionsPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    ours = true;
                                    continue;
                                }
                                return false;   // 别人的条目（或非字符串条目）
                            }
                            continue;

                        case JsonValue sv when sv.TryGetValue<string>(out var single) && single is not null &&
                                               string.Equals(single, instructionsPath, StringComparison.OrdinalIgnoreCase):
                            ours = true;
                            continue;

                        default:
                            return false;
                    }

                case AgentKey:
                    if (pair.Value is not JsonObject agentObj) return false;
                    foreach (var agent in agentObj)
                    {
                        if (agent.Value is not JsonObject entry) return false;
                        if (entry.Count == 0) continue;                       // 已是空对象：本工具撤销后的残留
                        if (entry.Count != 1 || entry[PromptKey] is not JsonValue pv ||
                            !pv.TryGetValue<string>(out var ps) || ps is null) return false;   // 别人的字段
                        if (names.Contains(agent.Key, StringComparer.Ordinal) && IsOurPrompt(ps, promptText)) ours = true;
                        else return false;                                    // 目标之外的 agent，或别人的 prompt
                    }
                    continue;

                default:
                    return false;   // 别人的顶层键
            }
        }

        return ours;
    }

    private static AgentPromptEdit InvalidAgentEdit(string origin, string problem,
        IReadOnlyList<string>? originKeys = null, bool agentKeyPresent = false) => new()
    {
        FileExists = true,
        ParseOk = false,
        Origin = origin,
        Next = origin,
        WillWrite = false,
        OriginKeys = originKeys ?? Array.Empty<string>(),
        AgentKeyPresent = agentKeyPresent,
        AgentIsObject = false,
        Problems = new[] { problem },
        Detail = problem,
    };

    private static string DescribeKind(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "数组",
        JsonObject => "对象",
        JsonValue v => v.GetValueKind().ToString().ToLowerInvariant(),
        _ => node.GetType().Name,
    };
}
