using System.Text.Json;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Cursor;

/// <summary>生效判定的三种状态——**必须分开报**，不能把"读不到"混成"没生效"。</summary>
public enum EffectivenessState
{
    /// <summary>库里没有会话、没有 promptTokenBreakdown、或没有注入前基线：无法判定（不是没生效）。</summary>
    Undetermined = 0,
    /// <summary>rules 档字符数按预期精确增长。</summary>
    Effective = 1,
    /// <summary>有会话、有基线，但 rules 档一个字符都没变：装上了但没生效。</summary>
    InstalledButNotEffective = 2,
    /// <summary>有增长，但幅度跟预期对不上：如实报数字，不硬判。</summary>
    Partial = 3,
}

/// <summary>一次定量判读的全部数字（给 EffectivenessEvidence 用，不许只给结论不给数字）。</summary>
public sealed class EffectivenessReading
{
    public required EffectivenessState State { get; init; }
    public required string Verdict { get; init; }

    public int SessionCount { get; init; }
    public int SessionsWithBreakdown { get; init; }
    public string? SessionId { get; init; }

    /// <summary>最近一次真实会话的 rules 档字符数（注入后的当前值）。</summary>
    public int? RulesChars { get; init; }
    /// <summary>最近一次真实会话的 system_prompt 档字符数——**只作对照，不判生效**。</summary>
    public int? SystemPromptChars { get; init; }
    /// <summary>注入前基线（apply 时记下的 rules 档字符数）。</summary>
    public int? BaselineRulesChars { get; init; }
    /// <summary>实测增长量。</summary>
    public int? Delta { get; init; }

    /// <summary>我们写进去的字符数：层 2（个人规则）= PersonalChars。</summary>
    public int PersonalChars { get; init; }
    /// <summary>层 3（项目规则）正文（frontmatter 之后）的字符数。</summary>
    public int PayloadBodyChars { get; init; }
    /// <summary>层 3 整份文件（含 frontmatter）的字符数。</summary>
    public int PayloadFileChars { get; init; }
    /// <summary>预期增长区间：正文口径 / 整份文件口径（frontmatter 是否进 rules 档未实测，故给区间）。</summary>
    public int ExpectedMin { get; init; }
    public int ExpectedMax { get; init; }

    public bool InstalledButNotEffective => State == EffectivenessState.InstalledButNotEffective;
}

/// <summary>
/// 层 4（只读日志层）的生效判据。
///
/// **主判据是定量的**：<c>composerData:&lt;id&gt;.promptTokenBreakdown.categories</c> 里
/// <c>id=="rules"</c> 那一档的 <c>characterCount</c>，与 apply 时记下的基线比，应当精确增长
/// 「层 2 + 层 3 写进去的字符数」（docs/cursor.md 第二节、第六节）。
///
/// **不许拿 system_prompt 那一档判生效**：那 4,099 字符由 Cursor 后端下发，本地无明文，
/// 注入前后不会变。它只作对照输出。
///
/// 三态纪律（上一轮同类缺陷刚修过，这里不重犯）：
///   · 库里没有会话 → **无法判定**，不是"命中 0 = 没生效"；
///   · 库读失败 → 抛 IoError（在 <see cref="CursorDb"/> 里），**不许降级成无法判定**；
///   · 有会话且有基线但 rules 档没变化 → 那才是"装上了但没生效"。
/// </summary>
public static class CursorEffectiveness
{
    public static EffectivenessReading Read(
        IReadOnlyList<CursorSession> sessions,
        CursorBaseline? baseline,
        int personalChars,
        int payloadBodyChars,
        int payloadFileChars)
    {
        var withBreakdown = sessions.Count(s => s.HasBreakdown);
        var newest = CursorDb.NewestWithBreakdown(sessions);
        var expectedMin = personalChars + payloadBodyChars;
        var expectedMax = personalChars + payloadFileChars;

        EffectivenessReading Make(EffectivenessState state, string verdict, int? rules, int? sys, int? delta) => new()
        {
            State = state,
            Verdict = verdict,
            SessionCount = sessions.Count,
            SessionsWithBreakdown = withBreakdown,
            SessionId = newest?.Id,
            RulesChars = rules,
            SystemPromptChars = sys,
            BaselineRulesChars = baseline?.RulesCharacterCount,
            Delta = delta,
            PersonalChars = personalChars,
            PayloadBodyChars = payloadBodyChars,
            PayloadFileChars = payloadFileChars,
            ExpectedMin = expectedMin,
            ExpectedMax = expectedMax,
        };

        if (sessions.Count == 0)
            return Make(EffectivenessState.Undetermined,
                "库里没有会话：无法判定生效（**不是**「命中 0 = 没生效」——没有会话时判据不成立）。", null, null, null);

        if (newest is null)
            return Make(EffectivenessState.Undetermined,
                $"库里有 {sessions.Count} 个会话，但没有任何一个带 promptTokenBreakdown（没跑过真实模型调用）：无法判定生效。",
                null, null, null);

        var rules = newest.RulesChars;
        if (baseline?.RulesCharacterCount is not int b)
            return Make(EffectivenessState.Undetermined,
                $"最近会话（{newest.Id}）的 rules 档是 {rules?.ToString() ?? "读不到"} 字符，" +
                "但本工具没有它的注入前基线（apply 时库里还没有带 breakdown 的会话）：无法判定，" +
                $"只能报当前值（预期注入后应比出厂值多 {expectedMin}~{expectedMax} 字符）。",
                rules, newest.SystemPromptChars, null);

        var delta = rules is null ? (int?)null : rules.Value - b;
        if (delta is null)
            return Make(EffectivenessState.Undetermined,
                $"最近会话（{newest.Id}）有 promptTokenBreakdown，但 categories 里没有 id==\"rules\" 那一档：无法判定。",
                null, newest.SystemPromptChars, null);

        if (delta.Value == 0)
            return Make(EffectivenessState.InstalledButNotEffective,
                $"装上了但没生效：最近会话（{newest.Id}）的 rules 档 {b} → {rules} 字符，**没有变化**。" +
                "多半是这个会话早于注入（提示词已经组装过了）——开一个新会话再发一轮，然后重新判读。" +
                "（system_prompt 档 " + (newest.SystemPromptChars?.ToString() ?? "读不到") + " 字符不会变：它是服务端下发的，不用于判生效。）",
                rules, newest.SystemPromptChars, delta);

        if (delta.Value >= expectedMin && delta.Value <= expectedMax)
            return Make(EffectivenessState.Effective,
                $"已生效（定量）：最近会话（{newest.Id}）的 rules 档 {b} → {rules} 字符，增长 {delta.Value}，" +
                $"落在预期增长区间 [{expectedMin}, {expectedMax}]（层 2 个人规则 {personalChars} + 层 3 项目规则正文 {payloadBodyChars}" +
                $"~整份文件 {payloadFileChars}）。",
                rules, newest.SystemPromptChars, delta);

        return Make(EffectivenessState.Partial,
            $"有增长但幅度与预期不符：最近会话（{newest.Id}）的 rules 档 {b} → {rules} 字符，增长 {delta.Value}，" +
            $"预期区间 [{expectedMin}, {expectedMax}]（层 2 {personalChars} + 层 3 正文 {payloadBodyChars}~整份 {payloadFileChars}）。" +
            "如实报数字，不硬判：先核对层 2 的值是否真的在库里、层 3 的 alwaysApply 是否为 true。",
            rules, newest.SystemPromptChars, delta);
    }
}

/// <summary>
/// 注入前基线：apply 时把「最近一次真实会话的 rules 档字符数」记下来，之后才有东西可比。
/// 落在本工具自己的数据目录（&lt;DataDir&gt;\cursor-rules-baseline.json），不碰目标客户端的任何文件。
/// </summary>
public sealed class CursorBaseline
{
    public const string FileName = "cursor-rules-baseline.json";

    public string? SessionId { get; init; }
    public int? RulesCharacterCount { get; init; }
    public int? SystemPromptCharacterCount { get; init; }
    public string? RecordedAt { get; init; }
    public int PersonalChars { get; init; }
    public int PayloadBodyChars { get; init; }
    public int PayloadFileChars { get; init; }
    public string? PersonalSha256 { get; init; }
    public string? PayloadSha256 { get; init; }
    public string? Note { get; init; }

    public static string PathOf(string dataDir) => Path.Combine(dataDir, FileName);

    public static CursorBaseline? Load(string dataDir)
    {
        try
        {
            var path = PathOf(dataDir);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(FileText.Read(path));
            var r = doc.RootElement;
            int? Int(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32()
                : null;
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            return new CursorBaseline
            {
                SessionId = Str("sessionId"),
                RulesCharacterCount = Int("rulesCharacterCount"),
                SystemPromptCharacterCount = Int("systemPromptCharacterCount"),
                RecordedAt = Str("recordedAt"),
                PersonalChars = Int("personalChars") ?? 0,
                PayloadBodyChars = Int("payloadBodyChars") ?? 0,
                PayloadFileChars = Int("payloadFileChars") ?? 0,
                PersonalSha256 = Str("personalSha256"),
                PayloadSha256 = Str("payloadSha256"),
                Note = Str("note"),
            };
        }
        catch { return null; }
    }

    public void Save(string dataDir)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sessionId"] = SessionId,
            ["rulesCharacterCount"] = RulesCharacterCount,
            ["systemPromptCharacterCount"] = SystemPromptCharacterCount,
            ["recordedAt"] = RecordedAt ?? DateTimeOffset.Now.ToString("o"),
            ["personalChars"] = PersonalChars,
            ["payloadBodyChars"] = PayloadBodyChars,
            ["payloadFileChars"] = PayloadFileChars,
            ["personalSha256"] = PersonalSha256,
            ["payloadSha256"] = PayloadSha256,
            ["note"] = Note,
        }, new JsonSerializerOptions { WriteIndented = true });
        FileText.Write(PathOf(dataDir), json);
    }

    public static void Delete(string dataDir)
    {
        try
        {
            var path = PathOf(dataDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 尽力而为 */ }
    }
}
