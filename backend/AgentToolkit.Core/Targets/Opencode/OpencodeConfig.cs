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

    private static string DescribeKind(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "数组",
        JsonObject => "对象",
        JsonValue v => v.GetValueKind().ToString().ToLowerInvariant(),
        _ => node.GetType().Name,
    };
}
