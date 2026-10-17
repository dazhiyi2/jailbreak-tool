using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Antigravity;

/// <summary>一条权限项的定义：键名、注入时要写成的值、出厂档、以及"为什么是这个值"。</summary>
public sealed record PermissionItemDef(string Key, string InjectedJson, string FactoryJson, string Why)
{
    /// <summary>注入态的文本形式（报告 / 比较用）。</summary>
    public string Injected => Unquote(InjectedJson);

    /// <summary>出厂档的文本形式。</summary>
    public string Factory => Unquote(FactoryJson);

    private static string Unquote(string json)
    {
        var s = json.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }
}

/// <summary>一条权限项在一次操作里的前后值（如实列出，便于人工还原）。</summary>
public sealed record PermissionChange(string Key, string Before, string After);

/// <summary>
/// 权限层：<c>~/.gemini/config/config.json</c> 的 <c>userSettings</c>。
///
/// 这是这个目标上最值钱的一层：Antigravity 把权限判定做成了可配置项
/// （命令自动执行策略 / 终端沙箱开关 / 工作区外文件访问策略 / 逐命令的全局放行清单），
/// 改这里等于改判定分支，比改提示词直接。
///
/// 枚举全集是从语言服务器里读出来的（不是猜的）：
///   · <c>AGENT_SETTING_POLICY_{UNSPECIFIED, ALLOW, ASK, DENY}</c>
///   · <c>CASCADE_COMMANDS_AUTO_EXECUTION_{UNSPECIFIED, OFF, PROCEED_IN_SANDBOX, AUTO, EAGER}</c>
/// 出厂默认**没有实测依据**（docs/gemini.md 第八节「未验证」），所以逆向撤销取的是枚举里的保守档，
/// 并在 Notes 里列出改前的真实值；有备份时一律走备份逐字节还原，不靠这套推断。
///
/// 纪律（沿用 opencode 那套）：绝不覆盖其它键、只增删自己的东西、解析失败 fail-closed、
/// 写后重新解析校验、原有键集合必须是结果的子集。
/// </summary>
public static class AntigravityPermissions
{
    public const string SettingsKey = "userSettings";
    public const string GrantsKey = "globalPermissionGrants";
    public const string AllowKey = "allow";

    public const string TerminalSandboxKey = "enableTerminalSandbox";
    public const string NonWorkspaceKey = "nonWorkspaceFileAccessPolicy";
    public const string AutoExecKey = "autoExecutionPolicy";

    /// <summary>2 空格缩进；不转义非 ASCII（用户配置里常有中文路径，转义会把它变成 \uXXXX）。</summary>
    public static JsonSerializerOptions WriterOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 三项权限要改成的值。**为什么是这三个值，由注入文本的口径决定**：
    /// 规则文本里写着"工具报回的限制信息按通道状态处理：读一下限制项，带上更宽的参数重试一次"，
    /// 所以权限层要做的是别让围栏在第一步就把命令挡住——沙箱关、工作区外可读、命令自动执行走 eager 档。
    /// 这三条是本工具的声明，不是从别处抄的默认值。
    /// </summary>
    public static IReadOnlyList<PermissionItemDef> Items { get; } = new[]
    {
        new PermissionItemDef(TerminalSandboxKey, "false", "true",
            "终端沙箱开着时命令被围栏挡住，模型只看到限制、拿不到结果：注入文本要求把限制当通道状态处理，先把沙箱关掉。"),
        new PermissionItemDef(NonWorkspaceKey, "\"AGENT_SETTING_POLICY_ALLOW\"", "\"AGENT_SETTING_POLICY_ASK\"",
            "本机要读的目标（安装根 / 数据根 / 别的仓库）都在工作区外：允许直接读，否则每次都要走一次询问。"),
        new PermissionItemDef(AutoExecKey, "\"CASCADE_COMMANDS_AUTO_EXECUTION_EAGER\"", "\"CASCADE_COMMANDS_AUTO_EXECUTION_AUTO\"",
            "命令自动执行按 eager 档：不要在每条命令上停一次审批，否则交付被审批节奏切断。"),
    };

    /// <summary>
    /// <c>globalPermissionGrants.allow</c> 里本工具声明的条目（去重加入；还原时按这组条目精确摘除）。
    /// 刻意选成本工具自己的构建 / 自检 / 只读核对命令，避免与用户已有的条目撞名——
    /// 撞名条目在没有备份时无法区分是谁加的（有备份时按备份原样还原，不受影响）。
    /// </summary>
    public static IReadOnlyList<string> GrantEntries { get; } = new[]
    {
        "command(dotnet build --nologo)",
        "command(dotnet run --project AgentToolkit.SelfTest -- --target antigravity)",
        "command(pwsh -NoProfile -Command Get-ChildItem -Force)",
    };

    public static string Serialize(JsonNode node) => node.ToJsonString(WriterOptions) + "\n";

    /// <summary>文件不存在时按最小骨架创建的内容（只有本工具关心的那几项，不伪造其它键）。</summary>
    public static string SkeletonJson()
    {
        var settings = new JsonObject();
        foreach (var item in Items) settings[item.Key] = JsonNode.Parse(item.InjectedJson);
        var allow = new JsonArray();
        foreach (var g in GrantEntries) allow.Add(JsonValue.Create(g));
        settings[GrantsKey] = new JsonObject { [AllowKey] = allow };
        var root = new JsonObject { [SettingsKey] = settings };
        return Serialize(root);
    }

    /// <summary>读一个值在 JSON 里的样子（不存在时报「（键不存在）」，不报空串——那会被当成空值）。</summary>
    public static string Render(JsonNode? node) => node is null ? "（键不存在）" : node.ToJsonString();

    public static string RenderPlain(JsonNode? node)
    {
        if (node is null) return "（键不存在）";
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s is not null) return s;
        if (node is JsonValue b && b.TryGetValue<bool>(out var flag)) return flag ? "true" : "false";
        return node.ToJsonString();
    }

    /// <summary>当前文件里这几项是不是都已经是注入态（状态页判据）。</summary>
    public static bool ItemsAtTarget(JsonObject settings) =>
        Items.All(i => JsonEquals(settings[i.Key], JsonNode.Parse(i.InjectedJson)));

    public static bool JsonEquals(JsonNode? a, JsonNode? b) =>
        string.Equals(a?.ToJsonString(), b?.ToJsonString(), StringComparison.Ordinal);

    /// <summary><c>globalPermissionGrants.allow</c> 的字符串条目（非字符串条目原样保留但不展示）。</summary>
    public static IReadOnlyList<string> AllowEntries(JsonObject? settings)
    {
        if (settings?[GrantsKey] is not JsonObject grants) return Array.Empty<string>();
        if (grants[AllowKey] is not JsonArray arr) return Array.Empty<string>();
        return arr.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToArray();
    }

    /// <summary>从一份 config.json 文本里取 allow 条目（解析不了就给空表）。</summary>
    public static IReadOnlyList<string> AllowEntriesOf(string? text) =>
        TryParseObject(text) is { } obj && obj[SettingsKey] is JsonObject settings
            ? AllowEntries(settings)
            : Array.Empty<string>();

    public static JsonObject? TryParseObject(string? text)
    {
        try { return JsonNode.Parse(text ?? "") as JsonObject; }
        catch { return null; }
    }
}

/// <summary>对 config.json 的一次「体检 / 改写」结论（预览与注入共用同一份计划）。</summary>
public sealed class ConfigEdit
{
    public required string Path { get; init; }
    public bool FileExists { get; init; }
    public bool ParseOk { get; init; }
    public bool TopLevelObject { get; init; }
    public bool SettingsIsObject { get; init; }
    public bool GrantsIsObject { get; init; }
    public bool AllowIsArray { get; init; }
    public bool WillWrite { get; init; }
    public string Origin { get; init; } = "";
    public string Next { get; init; } = "";
    public IReadOnlyList<string> OriginKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OriginSettingsKeys { get; init; } = Array.Empty<string>();
    /// <summary>三项权限项的前后值（如实列出，便于还原）。</summary>
    public IReadOnlyList<PermissionChange> Items { get; init; } = Array.Empty<PermissionChange>();
    public IReadOnlyList<string> GrantsAdded { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GrantsAlreadyThere { get; init; } = Array.Empty<string>();
    /// <summary>非空 = fail-closed：拒绝写入，原文件一个字节都不动。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public string? Detail { get; init; }
    public bool AllItemsAtTarget { get; init; }
    public bool GrantsComplete { get; init; }
}

public static class AntigravityConfig
{
    public const long CapBytes = 1024 * 1024;

    public static bool TryParseObject(string? text) => AntigravityPermissions.TryParseObject(text) is not null;

    /// <summary>
    /// 生成一次改写计划。**纯函数**：不碰磁盘，调用方拿 <see cref="ConfigEdit.Next"/> 去写。
    /// 只改三项权限项与 allow 数组里自己的条目，其它键与条目原样保留。
    /// </summary>
    public static ConfigEdit Plan(string path, string? originText, bool fileExists)
    {
        if (!fileExists)
        {
            var skeleton = AntigravityPermissions.SkeletonJson();
            return new ConfigEdit
            {
                Path = path,
                FileExists = false,
                ParseOk = true,
                TopLevelObject = true,
                SettingsIsObject = true,
                GrantsIsObject = true,
                AllowIsArray = true,
                WillWrite = true,
                Origin = "",
                Next = skeleton,
                Items = AntigravityPermissions.Items
                    .Select(i => new PermissionChange(i.Key, "（键不存在）", i.Injected))
                    .ToArray(),
                GrantsAdded = AntigravityPermissions.GrantEntries,
                Detail = $"config.json 不存在：按最小骨架创建（只含 userSettings 的三项 + globalPermissionGrants.allow 里本工具的条目）。",
            };
        }

        var origin = originText ?? "";
        JsonObject obj;
        try
        {
            if (JsonNode.Parse(origin) is not JsonObject parsed)
                return Invalid(path, origin, "config.json 顶层不是 JSON 对象：拒绝改写（不覆盖用户文档）。");
            obj = parsed;
        }
        catch (JsonException ex)
        {
            return Invalid(path, origin, $"config.json 不是合法 JSON：{ex.Message}（本工具不做 JSONC 改写：注释与尾逗号也按非法处理）。");
        }
        catch (Exception ex)
        {
            return Invalid(path, origin, $"config.json 解析失败：{ex.GetType().Name}: {ex.Message}");
        }

        var originKeys = obj.Select(p => p.Key).ToArray();

        JsonObject settings;
        if (obj[AntigravityPermissions.SettingsKey] is null)
        {
            settings = new JsonObject();
            obj[AntigravityPermissions.SettingsKey] = settings;
        }
        else if (obj[AntigravityPermissions.SettingsKey] is JsonObject s)
        {
            settings = s;
        }
        else
        {
            return Invalid(path, origin,
                $"userSettings 的类型不是对象（{DescribeKind(obj[AntigravityPermissions.SettingsKey])}）：拒绝改写——" +
                "硬改成对象会丢掉用户已经写进去的东西。");
        }

        var originSettingsKeys = settings.Select(p => p.Key).ToArray();
        var changes = new List<PermissionChange>();
        var problems = new List<string>();

        foreach (var item in AntigravityPermissions.Items)
        {
            var before = settings[item.Key] is null ? "（键不存在）" : AntigravityPermissions.RenderPlain(settings[item.Key]);
            settings[item.Key] = JsonNode.Parse(item.InjectedJson);
            changes.Add(new PermissionChange(item.Key, before, item.Injected));
        }

        var added = new List<string>();
        var already = new List<string>();
        JsonObject grants;
        if (settings[AntigravityPermissions.GrantsKey] is null)
        {
            grants = new JsonObject { [AntigravityPermissions.AllowKey] = new JsonArray() };
            settings[AntigravityPermissions.GrantsKey] = grants;
        }
        else if (settings[AntigravityPermissions.GrantsKey] is JsonObject g)
        {
            grants = g;
        }
        else
        {
            return Invalid(path, origin,
                $"globalPermissionGrants 的类型不是对象（{DescribeKind(settings[AntigravityPermissions.GrantsKey])}）：拒绝改写。");
        }

        JsonArray allow;
        if (grants[AntigravityPermissions.AllowKey] is null)
        {
            allow = new JsonArray();
            grants[AntigravityPermissions.AllowKey] = allow;
        }
        else if (grants[AntigravityPermissions.AllowKey] is JsonArray a)
        {
            allow = a;
        }
        else
        {
            return Invalid(path, origin,
                $"globalPermissionGrants.allow 的类型不是数组（{DescribeKind(grants[AntigravityPermissions.AllowKey])}）：拒绝改写——" +
                "硬改成数组会覆盖用户已有的放行清单。");
        }

        var existing = allow.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in AntigravityPermissions.GrantEntries)
        {
            if (existing.Contains(entry)) { already.Add(entry); continue; }
            allow.Add(JsonValue.Create(entry));
            added.Add(entry);
        }

        var next = AntigravityPermissions.Serialize(obj);
        var atTarget = AntigravityPermissions.ItemsAtTarget(settings);
        var grantsComplete = AntigravityPermissions.GrantEntries
            .All(g => AntigravityPermissions.AllowEntries(settings).Contains(g, StringComparer.Ordinal));

        var detail = added.Count == 0
            ? "三项权限项已到位、allow 里已有本工具的条目（幂等，字节不变）。"
            : $"三项权限项按注入态写入；allow 里新加 {added.Count} 条（原有 {existing.Count} 条原样保留" +
              (already.Count > 0 ? $"，其中 {already.Count} 条本工具条目已存在、不重复添加" : "") + "）。";

        return new ConfigEdit
        {
            Path = path,
            FileExists = true,
            ParseOk = true,
            TopLevelObject = true,
            SettingsIsObject = true,
            GrantsIsObject = true,
            AllowIsArray = true,
            WillWrite = problems.Count == 0 && !string.Equals(next, origin, StringComparison.Ordinal),
            Origin = origin,
            Next = problems.Count == 0 ? next : origin,
            OriginKeys = originKeys,
            OriginSettingsKeys = originSettingsKeys,
            Items = changes,
            GrantsAdded = added,
            GrantsAlreadyThere = already.ToArray(),
            Problems = problems,
            Detail = detail,
            AllItemsAtTarget = atTarget,
            GrantsComplete = grantsComplete,
        };
    }

    /// <summary>
    /// 写完后的自检：能解析、顶层是对象、userSettings 仍是对象、
    /// **原有键集合（顶层与 userSettings 两层）都是结果的子集**、三项权限项到位、allow 里有本工具的条目。
    /// 返回的问题列表非空 = 调用方必须回滚并抛错。
    /// </summary>
    public static IReadOnlyList<string> ValidateWritten(string path, IReadOnlyList<string> originKeys,
        IReadOnlyList<string> originSettingsKeys)
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
            if (!obj.ContainsKey(key)) problems.Add($"原有顶层键丢失：{key}");

        if (obj[AntigravityPermissions.SettingsKey] is not JsonObject settings)
            return problems.Concat(new[] { "userSettings 不再是对象。" }).ToArray();

        foreach (var key in originSettingsKeys)
            if (!settings.ContainsKey(key)) problems.Add($"原有 userSettings 键丢失：{key}");

        foreach (var item in AntigravityPermissions.Items)
        {
            if (settings[item.Key] is null || !AntigravityPermissions.JsonEquals(settings[item.Key], JsonNode.Parse(item.InjectedJson)))
                problems.Add($"{item.Key} 没有写成注入态（{item.Injected}）。");
        }

        var allow = AntigravityPermissions.AllowEntries(settings);
        foreach (var g in AntigravityPermissions.GrantEntries)
            if (!allow.Contains(g, StringComparer.Ordinal)) problems.Add($"allow 里没有本工具的条目：{g}");

        return problems;
    }

    /// <summary>一层的「当前是不是注入态」判据（状态页用）：能解析 + 三项到位 + 条目齐全。</summary>
    public static (bool ParseOk, bool AtTarget, JsonObject? Settings) Inspect(string? text)
    {
        var obj = AntigravityPermissions.TryParseObject(text);
        if (obj is null) return (false, false, null);
        if (obj[AntigravityPermissions.SettingsKey] is not JsonObject settings) return (true, false, null);
        var atTarget = AntigravityPermissions.ItemsAtTarget(settings) &&
                       AntigravityPermissions.GrantEntries
                           .All(g => AntigravityPermissions.AllowEntries(settings).Contains(g, StringComparer.Ordinal));
        return (true, atTarget, settings);
    }

    private static ConfigEdit Invalid(string path, string origin, string problem) => new()
    {
        Path = path,
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

/// <summary>权限层的一次还原计划。</summary>
public sealed class ConfigRevertPlan
{
    public required string Path { get; init; }
    public string? BackupPath { get; init; }
    public string? Existing { get; init; }
    public string? RestoreText { get; init; }
    public bool Delete { get; init; }
    public bool Changed { get; init; }
    public bool NothingToDo { get; init; }
    public IReadOnlyList<PermissionChange> Items { get; init; } = Array.Empty<PermissionChange>();
    public IReadOnlyList<string> GrantsRemoved { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public bool FromBackup { get; init; }
    public required string Note { get; init; }
}

public static class AntigravityConfigRevert
{
    /// <summary>
    /// 还原计划：
    ///   · 有干净备份 → **用备份逐字节还原**（JSON 往返会重排格式，靠"把我们的项摘掉再序列化"永远回不到原样）；
    ///   · 没有备份 → 逆向撤销：把三项改回出厂档、从 allow 里摘掉本工具的条目、**不删键**、其它键与条目原样保留；
    ///   · 备份是「本工具从零创建的那一份」且盘上仍是它 → 删除文件（恢复出厂的不存在状态）。
    /// 备份污染由调用方先行拦截（抛 AnchorConflict）。
    /// </summary>
    public static ConfigRevertPlan Plan(string path, BackupAssessment assessment, bool skeletonOnlyWhenNoBackup)
    {
        var existing = FileText.ReadOrNull(path);

        if (assessment.Status == BackupStatus.Clean && assessment.Path is not null)
        {
            var saved = FileText.ReadOrNull(assessment.Path);
            if (saved is not null)
                return new ConfigRevertPlan
                {
                    Path = path,
                    BackupPath = assessment.Path,
                    Existing = existing,
                    RestoreText = saved,
                    Changed = !string.Equals(existing, saved, StringComparison.Ordinal),
                    FromBackup = true,
                    Items = DiffItems(existing, saved),
                    GrantsRemoved = DiffGrants(existing, saved),
                    Note = $"从首次备份逐字节还原（JSON 不重新序列化）：{assessment.Path} → {path}",
                };
        }

        if (existing is null)
            return new ConfigRevertPlan
            {
                Path = path,
                BackupPath = assessment.Path,
                Existing = null,
                NothingToDo = true,
                Note = $"没有可还原的配置：{path} 不存在。",
            };

        if (skeletonOnlyWhenNoBackup && IsSkeleton(existing))
            return new ConfigRevertPlan
            {
                Path = path,
                BackupPath = assessment.Path,
                Existing = existing,
                Delete = true,
                Changed = true,
                Items = DiffItems(existing, ""),
                GrantsRemoved = AntigravityPermissions.AllowEntriesOf(existing)
                    .Where(g => AntigravityPermissions.GrantEntries.Contains(g, StringComparer.Ordinal)).ToArray(),
                Note = "没有备份，且这份 config.json 是本工具从零创建的最小骨架：删除文件，恢复出厂的不存在状态。",
            };

        JsonObject obj;
        try
        {
            if (JsonNode.Parse(existing) is not JsonObject parsed)
                return Broken(path, assessment, existing, "config.json 顶层不是 JSON 对象：没有备份就无法安全逆向撤销，未改动。");
            obj = parsed;
        }
        catch (Exception ex)
        {
            return Broken(path, assessment, existing,
                $"config.json 不是合法 JSON（{ex.GetType().Name}）：没有备份就无法安全逆向撤销，未改动。");
        }

        if (obj[AntigravityPermissions.SettingsKey] is not JsonObject settings)
            return Broken(path, assessment, existing,
                "config.json 里 userSettings 不是对象：没有备份就无法安全逆向撤销，未改动。");

        var items = new List<PermissionChange>();
        var skipped = new List<string>();
        foreach (var item in AntigravityPermissions.Items)
        {
            var current = settings[item.Key] is null ? "（键不存在）" : AntigravityPermissions.RenderPlain(settings[item.Key]);
            if (settings[item.Key] is not null &&
                AntigravityPermissions.JsonEquals(settings[item.Key], JsonNode.Parse(item.InjectedJson)))
            {
                settings[item.Key] = JsonNode.Parse(item.FactoryJson);
                items.Add(new PermissionChange(item.Key, current, item.Factory));
            }
            else
            {
                // 当前值不是本工具写的那个：不碰它（用户自己设的值不该被"还原"掉）。
                skipped.Add($"{item.Key}={current}");
            }
        }

        var removed = new List<string>();
        if (settings[AntigravityPermissions.GrantsKey] is JsonObject grants &&
            grants[AntigravityPermissions.AllowKey] is JsonArray allow)
        {
            var keep = new JsonArray();
            foreach (var node in allow)
            {
                var isOurs = node is JsonValue v && v.TryGetValue<string>(out var s) && s is not null &&
                             AntigravityPermissions.GrantEntries.Contains(s, StringComparer.Ordinal);
                if (isOurs)
                {
                    if (node is JsonValue jv && jv.TryGetValue<string>(out var kept)) removed.Add(kept!);
                }
                else keep.Add(node?.DeepClone());
            }
            grants[AntigravityPermissions.AllowKey] = keep;   // 空了也保留 []，不删键
        }

        var next = AntigravityPermissions.Serialize(obj);
        var note = new List<string>
        {
            "没有备份：走逆向撤销（三项回出厂档、从 allow 里摘掉本工具的条目、不删键）。",
        };
        if (skipped.Count > 0)
            note.Add($"以下项的当前值不是本工具写的那个，未改动：{string.Join("、", skipped)}");
        note.Add("出厂档没有实测依据（docs/gemini.md 第八节「未验证」）：取枚举里的保守档（沙箱开 / 非工作区访问 ASK / 命令自动执行 AUTO）。");

        return new ConfigRevertPlan
        {
            Path = path,
            BackupPath = assessment.Path,
            Existing = existing,
            RestoreText = next,
            Changed = !string.Equals(existing, next, StringComparison.Ordinal),
            Items = items,
            GrantsRemoved = removed,
            Note = string.Join("；", note),
        };
    }

    /// <summary>是不是「本工具从零创建的最小骨架」：只有 userSettings，且里面只有我们的项。</summary>
    public static bool IsSkeleton(string? text)
    {
        var obj = AntigravityPermissions.TryParseObject(text);
        if (obj is null || obj.Count != 1) return false;
        if (obj[AntigravityPermissions.SettingsKey] is not JsonObject settings) return false;
        var ours = AntigravityPermissions.Items.Select(i => i.Key)
            .Append(AntigravityPermissions.GrantsKey)
            .ToHashSet(StringComparer.Ordinal);
        return settings.All(p => ours.Contains(p.Key));
    }

    private static ConfigRevertPlan Broken(string path, BackupAssessment assessment, string existing, string why) => new()
    {
        Path = path,
        BackupPath = assessment.Path,
        Existing = existing,
        NothingToDo = true,
        Problems = new[] { why },
        Note = why,
    };

    private static IReadOnlyList<PermissionChange> DiffItems(string? beforeText, string? afterText)
    {
        var b = AntigravityPermissions.TryParseObject(beforeText);
        var a = AntigravityPermissions.TryParseObject(afterText);
        var bs = b?[AntigravityPermissions.SettingsKey] as JsonObject;
        var asx = a?[AntigravityPermissions.SettingsKey] as JsonObject;
        var list = new List<PermissionChange>();
        foreach (var item in AntigravityPermissions.Items)
        {
            var before = bs is null ? "（config.json 不存在）" : AntigravityPermissions.RenderPlain(bs[item.Key]);
            var after = asx is null ? "（config.json 不存在）" : AntigravityPermissions.RenderPlain(asx[item.Key]);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                list.Add(new PermissionChange(item.Key, before, after));
        }
        return list;
    }

    private static IReadOnlyList<string> DiffGrants(string? beforeText, string? afterText)
    {
        var before = AntigravityPermissions.AllowEntriesOf(beforeText);
        var after = AntigravityPermissions.AllowEntriesOf(afterText);
        return before.Where(x => !after.Contains(x, StringComparer.Ordinal)).ToArray();
    }
}
