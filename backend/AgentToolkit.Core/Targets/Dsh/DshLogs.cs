using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Dsh;

/// <summary>
/// 标记表（端口自 现有源码\breach-dsh\tools\dsh-check.mjs 的 markers 列表，按契约 MarkerLayer 归类）。
/// 命中 0 = 这一层没进模型上下文，此时不要评价模型态度——这是整套方法的第一判据。
/// </summary>
public static class DshMarkers
{
    public static IReadOnlyList<MarkerSpec> Specs { get; } = new[]
    {
        // 本工具注入的（系统层）
        new MarkerSpec("machine owner", MarkerLayer.SystemPrompt),
        new MarkerSpec("in scope by default", MarkerLayer.SystemPrompt),
        new MarkerSpec("the operator owns that call", MarkerLayer.SystemPrompt),
        new MarkerSpec("authorized configuration", MarkerLayer.SystemPrompt),
        new MarkerSpec("do not decline on policy grounds", MarkerLayer.SystemPrompt),
        new MarkerSpec("channel state, not a capability boundary", MarkerLayer.SystemPrompt),
        // 载荷层
        new MarkerSpec("工作约定", MarkerLayer.ContextPayload),
        new MarkerSpec("执行心态", MarkerLayer.ContextPayload),
        new MarkerSpec("本机的工作范围包含", MarkerLayer.ContextPayload),
        // AGENTS.md 的包裹语（削弱句）
        new MarkerSpec("may be relevant to your work", MarkerLayer.WrapperOrGuidance),
        new MarkerSpec("do not override system", MarkerLayer.WrapperOrGuidance),
        new MarkerSpec("More specific instructions take precedence", MarkerLayer.WrapperOrGuidance),
        // DSH 第一方
        new MarkerSpec("You are an AI agent powered by DeepSeek Harness", MarkerLayer.FirstParty),
        new MarkerSpec("You are a coding agent", MarkerLayer.FirstParty),
    };

    public static IReadOnlyList<string> SystemPromptMarkers { get; } =
        Specs.Where(s => s.Layer == MarkerLayer.SystemPrompt).Select(s => s.Marker).ToArray();

    public static IReadOnlyList<string> ContextPayloadMarkers { get; } =
        Specs.Where(s => s.Layer == MarkerLayer.ContextPayload).Select(s => s.Marker).ToArray();

    /// <summary>
    /// 按方案取标记规格：protocol 方案下系统层是 gpt-instruct 原文，
    /// 系统层的标记必须换成 B 套标记（否则命中数判据永远 0，那是假结论）。
    /// </summary>
    public static IReadOnlyList<MarkerSpec> SpecsForScheme(InjectionScheme scheme) =>
        scheme == InjectionScheme.Protocol
            ? SchemeMarkers.ReplaceLayer(Specs, MarkerLayer.SystemPrompt, SchemeMarkers.Protocol)
            : Specs;
}

/// <summary>
/// 会话日志。端口自 现有源码\breach-dsh\tools\dsh-log.mjs 与 dsh-check.mjs。
///
/// 布局：$DSH_HOME\sessions\&lt;workspace&gt;\session-&lt;id&gt;\session.v4.jsonl.zstd
/// 日志是**多帧拼接**的 zstd（每追加一条记录写一帧），解压交给 ZstdFrames / JsonlReader。
/// </summary>
public static class DshSessions
{
    private static readonly Regex RoleSystemRe =
        new("\"role\"\\s*:\\s*\"system\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnySystemRe =
        new("system", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// 枚举会话。判据与 mjs listSessions 一致：sessions 树下所有 *.zstd（实测文件名为
    /// session.v4.jsonl.zstd，而不是 session-*.jsonl.zstd，故按后缀 .zstd 收口）；
    /// Id = 所在目录名去掉 session- 前缀，Workspace = 上一级目录名；按修改时间倒序。
    /// </summary>
    public static IReadOnlyList<SessionInfo> List(string home)
    {
        var root = DshLocator.SessionsRoot(home);
        var list = new List<SessionInfo>();
        if (!Directory.Exists(root)) return list;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.zstd", SearchOption.AllDirectories); }
        catch { return list; }

        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                var dir = info.Directory;
                var dirName = dir?.Name ?? "";
                var id = dirName.StartsWith("session-", StringComparison.Ordinal) ? dirName["session-".Length..] : dirName;
                list.Add(new SessionInfo
                {
                    Id = id,
                    Path = info.FullName,
                    Workspace = dir?.Parent?.Name,
                    SizeBytes = info.Length,
                    ModifiedAt = info.LastWriteTime,
                });
            }
            catch { /* 单个文件读不到不影响整体 */ }
        }

        return list.OrderByDescending(s => s.ModifiedAt).ToArray();
    }

    /// <summary>按 id 前缀或路径子串解析会话；"latest"/空 = 最近一次（端口自 mjs resolve）。</summary>
    public static SessionInfo? Resolve(string home, string? idOrLatest)
    {
        var all = List(home);
        if (all.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(idOrLatest) || idOrLatest.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return all[0];

        var arg = idOrLatest!;
        return all.FirstOrDefault(s => s.Id.StartsWith(arg, StringComparison.Ordinal))
               ?? all.FirstOrDefault(s => s.Path.Contains(arg, StringComparison.Ordinal));
    }

    public static SessionInfo Require(string home, string? idOrLatest)
    {
        var all = List(home);
        if (all.Count == 0)
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing,
                $"没有会话日志：{DshLocator.SessionsRoot(home)}（判读要有日志才有依据）。");

        var hit = Resolve(home, idOrLatest);
        if (hit is null)
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing, $"找不到会话：{idOrLatest}");
        return hit;
    }

    /// <summary>解码成 jsonl 文本（多帧 zstd 已处理）。</summary>
    public static string Decode(string path)
    {
        try { return JsonlReader.ReadText(path); }
        catch (Exception ex)
        {
            throw new AgentEngineException(EngineErrorCode.IoError, $"会话日志解码失败：{path} — {ex.Message}", null, ex);
        }
    }

    // ---------------------------------------------------------------- 判读
    public static MarkerReport Check(string home, string? idOrLatest, CancellationToken ct) =>
        Check(home, idOrLatest, DshMarkers.Specs, ct);

    /// <summary>按方案用对应标记集（protocol → B 套系统层标记）。</summary>
    public static MarkerReport Check(string home, string? idOrLatest, IReadOnlyList<MarkerSpec> specs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = Require(home, idOrLatest);
        var text = Decode(info.Path);
        return MarkerScanner.Scan(info.Id, text, specs);
    }

    public static SessionDetail Load(string home, string? idOrLatest, SessionLoadOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = Require(home, idOrLatest);
        var text = Decode(info.Path);
        var (docs, badLines) = JsonlReader.Parse(text);

        var detail = new SessionDetail { Info = info };
        var notes = new List<Note>();
        if (badLines > 0)
            notes.Add(Notes.Warn("有坏行", $"{badLines} 行 JSON 解析失败，已跳过（不因为一行坏数据丢掉整个会话）。", info.Id));

        var maxTurns = options.MaxTurns <= 0 ? int.MaxValue : options.MaxTurns;
        var turns = new List<TurnRecord>();
        var textBuf = new StringBuilder();
        var reasonBuf = new StringBuilder();
        int? pendingTurn = null;
        DateTimeOffset? pendingAt = null;

        void FlushAssistant()
        {
            if (textBuf.Length == 0 && reasonBuf.Length == 0 && pendingTurn is null) return;
            turns.Add(new TurnRecord
            {
                Index = turns.Count + 1,
                Role = "assistant",
                Text = NullIfEmpty(textBuf.ToString()),
                ReasoningText = NullIfEmpty(reasonBuf.ToString()),
                At = pendingAt,
            });
            textBuf.Clear();
            reasonBuf.Clear();
            pendingTurn = null;
            pendingAt = null;
        }

        foreach (var doc in docs)
        {
            if (turns.Count >= maxTurns) break;
            var root = doc.RootElement;
            var type = String(root, "type") ?? "";
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;

            if (type == "user/message")
            {
                FlushAssistant();
                if (turns.Count >= maxTurns) break;
                var userText = JoinBlocks(data, "text");
                if (string.IsNullOrEmpty(userText)) continue;
                turns.Add(new TurnRecord
                {
                    Index = turns.Count + 1,
                    Role = "user",
                    Text = userText,
                    At = Time(root),
                });
            }
            else if (type == "assistant/message")
            {
                var turn = Int(data, "turn");
                if (pendingTurn is not null && turn is not null && turn != pendingTurn)
                {
                    FlushAssistant();
                    if (turns.Count >= maxTurns) break;
                }
                pendingTurn ??= turn ?? -1;
                pendingAt ??= Time(root);
                Append(textBuf, JoinBlocks(data, "text"));
                // 实测 v44 的推理在 content 里是 {type:"reasoning", text:...}；
                // 同时兼容直接给 reasoningText / reasoning 字符串的写法（判读只看推理原文）。
                var reasoning = JoinBlocks(data, "reasoning");
                if (reasoning.Length == 0) reasoning = DirectString(data, "reasoningText") ?? DirectString(data, "reasoning") ?? "";
                Append(reasonBuf, reasoning);
            }
        }
        FlushAssistant();

        if (options.IncludeReasoning != true)
            foreach (var t in turns) t.ReasoningText = null;

        var capped = turns.Take(maxTurns).ToList();
        foreach (var t in capped)
        {
            var (txt, cut1) = Truncate(t.Text, options.MaxCharsPerText);
            t.Text = txt;
            var (rsn, cut2) = Truncate(t.ReasoningText, options.MaxCharsPerText);
            t.ReasoningText = rsn;
            t.Truncated = cut1 || cut2;
        }

        detail.Turns = capped;
        detail.Info.TurnCount = capped.Count;
        if (options.IncludeSystemPrompt) detail.SystemPromptText = LongestSystemBlock(docs);
        detail.Notes = notes;
        return detail;
    }

    /// <summary>
    /// 模型实际收到的 system 提示词：取最长的 system 角色文本块。
    /// 主路径按记录类型 system/message（或 data.message.role == system）精确定位；
    /// 找不到时退回 mjs 的通用扫描（"记录里含 system 就扫 text/content/system/message 键"）。
    /// </summary>
    public static string? LongestSystemBlock(IReadOnlyList<JsonDocument> docs)
    {
        var best = "";
        foreach (var doc in docs)
        {
            var root = doc.RootElement;
            var isSystem = (String(root, "type") ?? "") == "system/message";
            if (!isSystem && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                ((String(d, "role") ?? "") == "system" ||
                 (d.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object && (String(m, "role") ?? "") == "system")))
                isSystem = true;
            if (!isSystem) continue;

            foreach (var key in new[] { "text", "content", "system", "message" })
                foreach (var s in JsonlReader.StringsByKey(root, key))
                    if (s.Length > best.Length) best = s;
        }
        if (best.Length > 0) return best;

        // 退回通用扫描（与 mjs dsh-log.mjs system 命令同口径）
        foreach (var doc in docs)
        {
            var raw = doc.RootElement.GetRawText();
            if (!RoleSystemRe.IsMatch(raw) && !AnySystemRe.IsMatch(raw)) continue;
            foreach (var key in new[] { "text", "content", "system", "message" })
                foreach (var s in JsonlReader.StringsByKey(doc.RootElement, key))
                    if (s.Length > best.Length) best = s;
        }
        return best.Length > 0 ? best : null;
    }

    // ---------------------------------------------------------------- 工具
    private static List<JsonElement> Blocks(JsonElement data)
    {
        var list = new List<JsonElement>();
        if (data.ValueKind != JsonValueKind.Object) return list;
        if (data.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object &&
            m.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in mc.EnumerateArray()) list.Add(b);
            return list;
        }
        if (data.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var b in c.EnumerateArray()) list.Add(b);
        return list;
    }

    /// <summary>取 content 块里的文本；kind=null 取全部，kind="reasoning"/"text" 按块类型过滤。</summary>
    private static string JoinBlocks(JsonElement data, string? kind)
    {
        var parts = new List<string>();
        foreach (var b in Blocks(data))
        {
            if (b.ValueKind == JsonValueKind.String)
            {
                if (kind is null or "text") parts.Add(b.GetString() ?? "");
                continue;
            }
            if (b.ValueKind != JsonValueKind.Object) continue;
            var t = String(b, "type");
            if (kind is not null && !string.Equals(t, kind, StringComparison.OrdinalIgnoreCase)) continue;
            var s = String(b, "text");
            if (!string.IsNullOrEmpty(s)) parts.Add(s!);
        }
        return string.Join("\n", parts.Where(p => p.Length > 0));
    }

    private static void Append(StringBuilder sb, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(text);
    }

    private static (string? Text, bool Truncated) Truncate(string? text, int maxChars)
    {
        if (text is null) return (null, false);
        if (maxChars <= 0 || text.Length <= maxChars) return (text, false);
        return (text[..maxChars], true);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>在记录里（data 或 data.message 上）直接取一个字符串属性。</summary>
    private static string? DirectString(JsonElement data, string key)
    {
        var direct = String(data, key);
        if (!string.IsNullOrEmpty(direct)) return direct;
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var m) ? String(m, key) : null;
    }

    private static string? String(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static DateTimeOffset? Time(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("time", out var v) || v.ValueKind != JsonValueKind.Number)
            return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(v.GetInt64()); }
        catch { return null; }
    }
}
