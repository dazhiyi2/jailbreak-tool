using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using Microsoft.Data.Sqlite;

namespace AgentToolkit.Core.Targets.Opencode;

/// <summary>instruction_state.initial_values 里的一条：key → blob hash（+ 解出来的 value）。</summary>
public sealed class InstructionEntry
{
    public required string Key { get; init; }
    public required string Hash { get; init; }
    public string? Value { get; init; }
    public bool ValueMissing { get; init; }
}

/// <summary>
/// opencode 的日志通道：<c>&lt;data&gt;/opencode.db</c>（SQLite + WAL）。**全程只读**。
///
/// 表结构（实测 2.0.32）：
/// <code>
/// session_v2(id, slug, directory, title, model, agent, tokens_reasoning, time_created, time_updated, …)
/// session_message(id, session_id, type, seq, time_created, time_updated, data)
///     type ∈ {user, assistant, idle, …}；data 是 JSON
///       user      → data.text
///       assistant → data.content[]，其中 type=="reasoning" 的块 text 就是**推理原文**
///                   （state.reasoningField == "reasoning_content"），type=="text" 的是回复文本
/// instruction_blob(hash, value)          真正进了模型上下文的常驻指令载荷
/// instruction_state(session_id, epoch_start, through_seq, initial_values, current_values)
///     initial_values / current_values 是 key → blob hash 的 JSON 映射
/// </code>
///
/// 连接纪律：<c>Mode=ReadOnly</c> 的普通连接串（**不用 immutable=1**——那会忽略 WAL，
/// 而本机实测会话数据大量还在 6.8 MB 的 -wal 里，用 immutable 会读成空）。
/// 读不到 / 被占用一律抛 <see cref="EngineErrorCode.IoError"/>，**绝不静默返回空**
/// （空文本会被判成"两层都没进模型上下文"，把排查引到错误方向）。
/// </summary>
public static class OpencodeDb
{
    public const string DbFileName = OpencodeLocator.DbFileName;

    public static string OpenString(string dbPath) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString();

    public static bool Exists(string dbPath) => File.Exists(dbPath);

    // ---------------------------------------------------------------- 连接

    private static T With<T>(string dbPath, Func<SqliteConnection, T> work, string what)
    {
        if (!File.Exists(dbPath))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing,
                $"会话数据库不存在：{dbPath}（opencode 还没落过会话；数据根由 XDG_DATA_HOME 决定）。");

        SqliteConnection? cn = null;
        try
        {
            cn = new SqliteConnection(OpenString(dbPath));
            cn.Open();
            return work(cn);
        }
        catch (AgentEngineException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex)
        {
            var hint = ex.SqliteErrorCode is 5 or 6
                ? "数据库被占用 / 锁住（可能 opencode 正在写入）——稍后重试，不要把它当成「没有会话」。"
                : "只读连接失败。";
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"读取会话数据库失败（{what}）：{dbPath}：SQLite {ex.SqliteErrorCode} {ex.Message}。{hint}", null, ex);
        }
        catch (Exception ex)
        {
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"读取会话数据库失败（{what}）：{dbPath}：{ex.GetType().Name}: {ex.Message}", null, ex);
        }
        finally
        {
            if (cn is not null)
            {
                // 只读也走连接池：用完立刻清掉，别把锁留给正在运行的 opencode。
                try { SqliteConnection.ClearPool(cn); } catch { /* 尽力而为 */ }
                cn.Dispose();
            }
        }
    }

    private static HashSet<string> Columns(SqliteConnection cn, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) set.Add(rd.GetString(1));
        return set;
    }

    private static bool TableExists(SqliteConnection cn, string table)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static string? Str(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
    private static long? Long(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetInt64(i);

    private static DateTimeOffset EpochMs(long ms)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); }
        catch { return DateTimeOffset.MinValue; }
    }

    // ---------------------------------------------------------------- 会话列表

    public static IReadOnlyList<SessionInfo> ListSessions(string dbPath, CancellationToken ct)
    {
        if (!File.Exists(dbPath)) return Array.Empty<SessionInfo>();

        return With(dbPath, cn =>
        {
            if (!TableExists(cn, "session_v2"))
                throw new AgentEngineException(EngineErrorCode.IoError,
                    $"会话数据库里没有 session_v2 表：{dbPath}（不是 opencode 的库，或 schema 变了）。");

            var cols = Columns(cn, "session_v2");
            var dirExpr = cols.Contains("directory") ? "s.directory" : "NULL";
            var modelExpr = cols.Contains("model") ? "s.model" : "NULL";

            var list = new List<SessionInfo>();
            using var cmd = cn.CreateCommand();
            cmd.CommandText =
                $"""
                 SELECT s.id,
                        {dirExpr} AS dir,
                        {modelExpr} AS model,
                        s.time_updated,
                        s.time_created,
                        COALESCE((SELECT SUM(LENGTH(CAST(m.data AS BLOB))) FROM session_message m WHERE m.session_id = s.id), 0) AS bytes,
                        COALESCE((SELECT COUNT(*) FROM session_message m2 WHERE m2.session_id = s.id AND m2.type IN ('user','assistant')), 0) AS turns
                   FROM session_v2 s
                  ORDER BY s.time_updated DESC
                 """;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = rd.GetString(0);
                var dir = Str(rd, 1);
                var updated = Long(rd, 3) ?? 0;
                list.Add(new SessionInfo
                {
                    Id = id,
                    // 会话是库里的一行，不是独立文件：Path 指向承载它的库（UI 打开的是库路径）。
                    Path = dbPath,
                    Workspace = string.IsNullOrEmpty(dir) ? "" : dir,
                    SizeBytes = Long(rd, 5) ?? 0,
                    ModifiedAt = EpochMs(updated),
                    TurnCount = (int)(Long(rd, 6) ?? 0),
                });
            }
            return (IReadOnlyList<SessionInfo>)list;
        }, "ListSessions");
    }

    public static string? NewestSessionId(string dbPath, CancellationToken ct)
    {
        if (!File.Exists(dbPath)) return null;
        return With(dbPath, cn =>
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT id FROM session_v2 ORDER BY time_updated DESC LIMIT 1";
            return cmd.ExecuteScalar() as string;
        }, "NewestSessionId");
    }

    /// <summary>"最新会话"的别名（CLI 传 latest、UI 传空都走这里）。</summary>
    public static bool IsLatestAlias(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ||
        sessionId!.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase) ||
        sessionId.Trim().Equals("newest", StringComparison.OrdinalIgnoreCase) ||
        sessionId.Trim().Equals("last", StringComparison.OrdinalIgnoreCase) ||
        sessionId.Trim() == "-1";

    /// <summary>把用户给的会话标识解析成真实 session_v2.id（支持 latest 别名、完整 id、唯一前缀）。</summary>
    public static string ResolveSessionId(string dbPath, string? sessionId, CancellationToken ct)
    {
        var all = With(dbPath, cn =>
        {
            var ids = new List<string>();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT id FROM session_v2 ORDER BY time_updated DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) { ct.ThrowIfCancellationRequested(); ids.Add(rd.GetString(0)); }
            return ids;
        }, "ResolveSessionId");

        if (all.Count == 0)
            throw new AgentEngineException(EngineErrorCode.NotApplied,
                $"会话库里没有任何会话：{dbPath}（先让 opencode 跑一轮对话，再来读日志）。");

        if (IsLatestAlias(sessionId)) return all[0];

        var raw = sessionId!.Trim().Trim('"');
        var exact = all.FirstOrDefault(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var prefix = all.Where(x => x.StartsWith(raw, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (prefix.Length == 1) return prefix[0];
        if (prefix.Length > 1)
            throw new AgentEngineException(EngineErrorCode.VerifyProblems,
                $"会话标识「{raw}」前缀不唯一，匹配到 {prefix.Length} 个：{string.Join("、", prefix.Take(5))}。请给完整 id。");

        throw new AgentEngineException(EngineErrorCode.TargetFileMissing, DescribeMissing(dbPath, raw));
    }

    public static string DescribeMissing(string dbPath, string? sessionId)
    {
        if (!File.Exists(dbPath))
            return $"找不到会话数据库：{dbPath}（opencode 还没落过会话，或数据根不对）。";
        try
        {
            var ids = With(dbPath, cn =>
            {
                var list = new List<string>();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "SELECT id FROM session_v2 ORDER BY time_updated DESC LIMIT 5";
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) list.Add(rd.GetString(0));
                return list;
            }, "DescribeMissing");
            var list = ids.Count == 0 ? "(库里没有任何会话)" : string.Join("、", ids);
            return $"找不到会话：{sessionId ?? "(空)"}。库 {dbPath} 里现有的会话：{list}";
        }
        catch (Exception ex)
        {
            return $"找不到会话：{sessionId ?? "(空)"}；而且读库也失败了：{ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 载入会话

    public static SessionDetail LoadSession(string dbPath, string sessionId, SessionLoadOptions options, CancellationToken ct)
    {
        var id = ResolveSessionId(dbPath, sessionId, ct);
        return With(dbPath, cn =>
        {
            var info = LoadInfo(cn, dbPath, id);
            var notes = new List<Note>();

            var rows = 0;
            var badJson = 0;
            var records = new List<TurnRecord>();
            var cap = Math.Max(256, options.MaxCharsPerText);

            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT type, seq, data, time_created FROM session_message WHERE session_id = @id ORDER BY seq ASC";
                cmd.Parameters.AddWithValue("@id", id);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    rows++;
                    var type = rd.GetString(0);
                    var data = Str(rd, 2) ?? "";
                    var createdAt = Long(rd, 3);

                    JsonDocument? doc = null;
                    try { doc = JsonDocument.Parse(data); }
                    catch { badJson++; }

                    using (doc)
                    {
                        var root = doc?.RootElement;
                        if (string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
                        {
                            var text = root is { ValueKind: JsonValueKind.Object } r ? Prop(r, "text") : null;
                            if (!string.IsNullOrEmpty(text))
                                records.Add(Make(options, records.Count, "user", text, null, TimeOf(root, createdAt), cap));
                        }
                        else if (string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase))
                        {
                            var (reasoning, text) = ContentOf(root);
                            if (!string.IsNullOrEmpty(reasoning) || !string.IsNullOrEmpty(text))
                                records.Add(Make(options, records.Count, "assistant", text, reasoning, TimeOf(root, createdAt), cap));
                        }
                    }
                }
            }

            var kept = records;
            if (options.MaxTurns > 0 && records.Count > options.MaxTurns)
            {
                kept = records.Skip(records.Count - options.MaxTurns).ToList();
                notes.Add(Notes.Info("轮次已限制",
                    $"共 {records.Count} 条轮次记录，按 MaxTurns={options.MaxTurns} 只载入最近 {kept.Count} 条。", "opencode/db"));
            }

            if (badJson > 0)
                notes.Add(Notes.Warn("有解析失败的 data",
                    $"{badJson} 条 session_message.data 不是合法 JSON（已跳过，不因为一条坏数据丢掉整个会话）。", "opencode/db"));
            if (rows == 0)
                notes.Add(Notes.Warn("会话里没有消息", $"session_message 里没有 session_id={id} 的行。", "opencode/db"));
            if (records.Any(r => r.Truncated))
                notes.Add(Notes.Warn("文本已截断",
                    $"至少一段文本超过 MaxCharsPerText（下限保护 256，实际生效 {cap}），已截断并置 Truncated=true；" +
                    "判读请以未截断的导出文件为准。", "opencode/db"));
            notes.Add(Notes.Info("解析口径",
                $"session_message 共 {rows} 行；user 取 data.text，assistant 取 data.content[] 里 type=reasoning 的 text 作为推理原文。", "opencode/db"));
            if (info.TurnCount is int tc && tc != rows)
                notes.Add(Notes.Info("消息计数", $"user+assistant 消息 {tc} 条 / session_message 共 {rows} 行（另有 idle 等状态行）。", "opencode/db"));

            var listing = InstructionListing(cn, id, out var instrState);
            if (listing is null)
                notes.Add(Notes.Warn("没有 instruction_state",
                    $"会话 {id} 没有 instruction_state 记录：看不到它启动时装配了哪些常驻指令（instruction_blob）。", "opencode/db"));
            else if (instrState is not null)
                notes.Add(Notes.Info("常驻指令载荷",
                    $"instruction_state.initial_values 里 {instrState.Initial.Count} 条（epoch_start={instrState.EpochStart}，through_seq={instrState.ThroughSeq}）；" +
                    "SystemPromptText 就是这份清单——它是「模型实际收到了什么常驻指令」的权威视图。", "opencode/db"));

            return new SessionDetail
            {
                Info = info,
                SystemPromptText = options.IncludeSystemPrompt ? listing : null,
                Turns = kept,
                Notes = notes,
            };
        }, "LoadSession");
    }

    private static TurnRecord Make(SessionLoadOptions options, int index, string role, string? text, string? reasoning,
        DateTimeOffset? at, int cap)
    {
        var (t, tTrunc) = Cap(text, cap);
        string? reasoningOut = null;
        var rTrunc = false;
        if (options.IncludeReasoning)
        {
            var (r, trunc) = Cap(reasoning, cap);
            reasoningOut = r;
            rTrunc = trunc;
        }
        return new TurnRecord
        {
            Index = index,
            Role = role,
            Text = t,
            ReasoningText = reasoningOut,
            At = at,
            Truncated = tTrunc || rTrunc,
        };
    }

    private static (string? Text, bool Truncated) Cap(string? text, int cap)
    {
        if (string.IsNullOrEmpty(text)) return (text, false);
        if (text!.Length <= cap) return (text, false);
        return (text[..cap], true);
    }

    private static string? Prop(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? TimeOf(JsonElement? root, long? fallbackMs)
    {
        if (root is { ValueKind: JsonValueKind.Object } r &&
            r.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Object &&
            t.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number)
            return EpochMs(c.GetInt64());
        return fallbackMs is null ? null : EpochMs(fallbackMs.Value);
    }

    /// <summary>assistant 的 data.content[] → (推理原文, 回复文本)。多个 text 块拼接。</summary>
    private static (string? Reasoning, string? Text) ContentOf(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } r) return (null, null);
        if (!r.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return (null, null);

        var reasoning = new StringBuilder();
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = Prop(block, "type");
            var body = Prop(block, "text");
            if (string.IsNullOrEmpty(body)) continue;
            if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase)) Append(reasoning, body!);
            else if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)) Append(text, body!);
        }
        return (reasoning.Length == 0 ? null : reasoning.ToString(), text.Length == 0 ? null : text.ToString());
    }

    private static void Append(StringBuilder sb, string s)
    {
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(s);
    }

    private static SessionInfo LoadInfo(SqliteConnection cn, string dbPath, string id)
    {
        var cols = Columns(cn, "session_v2");
        var dirExpr = cols.Contains("directory") ? "directory" : "NULL";
        var titleExpr = cols.Contains("title") ? "title" : "NULL";
        var modelExpr = cols.Contains("model") ? "model" : "NULL";

        using var cmd = cn.CreateCommand();
        cmd.CommandText =
            $"""
             SELECT {dirExpr} AS dir, {titleExpr} AS title, {modelExpr} AS model,
                    time_created, time_updated,
                    COALESCE((SELECT COUNT(*) FROM session_message m WHERE m.session_id = s.id AND m.type IN ('user','assistant')), 0) AS turns,
                    COALESCE((SELECT SUM(LENGTH(CAST(m.data AS BLOB))) FROM session_message m WHERE m.session_id = s.id), 0) AS bytes
               FROM session_v2 s WHERE s.id = @id
             """;
        cmd.Parameters.AddWithValue("@id", id);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read())
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing, $"会话不存在：{id}");

        var dir = Str(rd, 0);
        return new SessionInfo
        {
            Id = id,
            Path = dbPath,
            Workspace = string.IsNullOrEmpty(dir) ? "" : dir,
            SizeBytes = Long(rd, 6) ?? 0,
            ModifiedAt = EpochMs(Long(rd, 4) ?? 0),
            TurnCount = (int)(Long(rd, 5) ?? 0),
        };
    }

    // ---------------------------------------------------------------- 常驻指令载荷

    /// <summary>该会话的 instruction_state（epoch_start / through_seq / key→hash）。</summary>
    private sealed class InstrState
    {
        public long EpochStart { get; init; }
        public long ThroughSeq { get; init; }
        public required IReadOnlyList<(string Key, string Hash)> Initial { get; init; }
    }

    private static InstrState? ReadState(SqliteConnection cn, string sessionId)
    {
        if (!TableExists(cn, "instruction_state")) return null;
        using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "SELECT epoch_start, through_seq, initial_values FROM instruction_state WHERE session_id = @id ORDER BY epoch_start DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@id", sessionId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;

        var epochStart = Long(rd, 0) ?? 0;
        var throughSeq = Long(rd, 1) ?? 0;
        var json = Str(rd, 2) ?? "";
        var pairs = new List<(string, string)>();
        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
                foreach (var kv in obj)
                    if (kv.Value is JsonValue v && v.TryGetValue<string>(out var h) && h is not null)
                        pairs.Add((kv.Key, h));
        }
        catch { /* 映射坏了就当看不到 */ }

        return new InstrState { EpochStart = epochStart, ThroughSeq = throughSeq, Initial = pairs };
    }

    private static Dictionary<string, string?> ReadBlobs(SqliteConnection cn, IReadOnlyList<string> hashes)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (hashes.Count == 0) return map;
        if (!TableExists(cn, "instruction_blob")) return map;

        var names = hashes.Select((_, i) => "@h" + i).ToArray();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT hash, value FROM instruction_blob WHERE hash IN ({string.Join(",", names)})";
        for (var i = 0; i < hashes.Count; i++) cmd.Parameters.AddWithValue(names[i], hashes[i]);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) map[rd.GetString(0)] = Str(rd, 1);
        return map;
    }

    /// <summary>把 value 是 JSON 字符串的载荷解出原文（core/date → "Sat Oct 17 2026"）。</summary>
    private static string Unwrap(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw!;
        if (s.StartsWith('"'))
        {
            try
            {
                if (JsonNode.Parse(s) is JsonValue v && v.TryGetValue<string>(out var inner) && inner is not null)
                    return inner;
            }
            catch { /* 不是 JSON 字符串就原样 */ }
        }
        return s;
    }

    private static IReadOnlyList<InstructionEntry> ResolveEntries(SqliteConnection cn, string sessionId)
    {
        var state = ReadState(cn, sessionId);
        if (state is null) return Array.Empty<InstructionEntry>();
        var map = ReadBlobs(cn, state.Initial.Select(p => p.Hash).Distinct().ToArray());
        return state.Initial.Select(p => new InstructionEntry
        {
            Key = p.Key,
            Hash = p.Hash,
            Value = map.TryGetValue(p.Hash, out var v) ? Unwrap(v) : null,
            ValueMissing = !map.ContainsKey(p.Hash),
        }).ToArray();
    }

    /// <summary>
    /// 人话清单：instruction_state.initial_values（key → hash）拼上 instruction_blob.value。
    /// 这是 opencode 里「模型实际收到了什么常驻指令」的权威视图，也是 CheckMarkers 的主判据来源。
    /// </summary>
    private static string? InstructionListing(SqliteConnection cn, string sessionId, out InstrState? state)
    {
        state = ReadState(cn, sessionId);
        if (state is null) return null;

        var entries = ResolveEntries(cn, sessionId);
        var sb = new StringBuilder();
        sb.Append("# opencode 常驻指令载荷（instruction_state.initial_values → instruction_blob.value）\n");
        sb.Append($"# 会话 {sessionId}；epoch_start={state.EpochStart}，through_seq={state.ThroughSeq}，共 {entries.Count} 条\n");
        if (entries.Count == 0)
        {
            sb.Append("(instruction_state 里没有任何条目)\n");
            return sb.ToString();
        }

        foreach (var e in entries)
        {
            var bytes = e.Value is null ? 0 : FileText.Bytes(e.Value!);
            sb.Append($"{e.Key} → {e.Hash[..Math.Min(12, e.Hash.Length)]}…（{bytes} B）\n");
            if (e.ValueMissing) { sb.Append("  (instruction_blob 里找不到这个 hash)\n"); continue; }
            foreach (var line in (e.Value ?? "").Replace("\r\n", "\n").Split('\n'))
                sb.Append("  ").Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>只给清单（LoadSession 用）。</summary>
    public static string? SessionInstructionListing(string dbPath, string sessionId, CancellationToken ct)
    {
        var id = ResolveSessionId(dbPath, sessionId, ct);
        return With(dbPath, cn => InstructionListing(cn, id, out _), "SessionInstructionListing");
    }

    /// <summary>
    /// instruction_blob.value 全集拼成的文本——**CheckMarkers 的主判据**：
    /// 这些 blob 才是真正进了模型上下文的常驻指令载荷。
    ///
    /// 每条 value 都过一遍 <see cref="Unwrap"/>：opencode 存的是 JSON 字符串，
    /// 非 ASCII 可能被写成 <c>\uXXXX</c>、换行写成 <c>\n</c>。不还原就会"在库里却数不到标记"，
    /// 把已生效的注入误判成没生效。
    /// </summary>
    public static (string Text, int Blobs) InstructionBlobText(string dbPath, CancellationToken ct)
    {
        return With(dbPath, cn =>
        {
            if (!TableExists(cn, "instruction_blob")) return ("", 0);
            var sb = new StringBuilder();
            var n = 0;
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT value FROM instruction_blob ORDER BY hash";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                ct.ThrowIfCancellationRequested();
                var v = Str(rd, 0);
                if (string.IsNullOrEmpty(v)) continue;
                n++;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Unwrap(v));
            }
            return (sb.ToString(), n);
        }, "InstructionBlobText");
    }

    /// <summary>
    /// 某个会话**实际装配**的常驻指令文本（按它的 instruction_state.initial_values 解析出来的 blob）。
    /// 用来判定「文件在盘上」是否「进了这个会话的模型上下文」。
    /// </summary>
    public static string SessionInstructionText(string dbPath, string sessionId, CancellationToken ct)
    {
        var id = ResolveSessionId(dbPath, sessionId, ct);
        return With(dbPath, cn =>
        {
            var entries = ResolveEntries(cn, id);
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                if (e.Value is null) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(e.Value);
            }
            return sb.ToString();
        }, "SessionInstructionText");
    }

    /// <summary>该会话所有 session_message.data 拼成的文本（标记命中的**参考**口径，不是主判据）。</summary>
    public static string SessionMessageText(string dbPath, string sessionId, CancellationToken ct)
    {
        var id = ResolveSessionId(dbPath, sessionId, ct);
        return With(dbPath, cn =>
        {
            var sb = new StringBuilder();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT data FROM session_message WHERE session_id = @id ORDER BY seq ASC";
            cmd.Parameters.AddWithValue("@id", id);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                ct.ThrowIfCancellationRequested();
                var v = Str(rd, 0);
                if (string.IsNullOrEmpty(v)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(v);
            }
            return sb.ToString();
        }, "SessionMessageText");
    }

    // ---------------------------------------------------------------- 导出

    /// <summary>把 session_message 逐行 JSON 写出（每行一条：id / type / seq / time_* / data）。</summary>
    public static string ExportMessages(string dbPath, string sessionId, CancellationToken ct)
    {
        var id = ResolveSessionId(dbPath, sessionId, ct);
        var opts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return With(dbPath, cn =>
        {
            var sb = new StringBuilder();
            using var cmd = cn.CreateCommand();
            cmd.CommandText =
                "SELECT id, session_id, type, seq, time_created, time_updated, data FROM session_message WHERE session_id = @id ORDER BY seq ASC";
            cmd.Parameters.AddWithValue("@id", id);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                ct.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object?>
                {
                    ["id"] = rd.GetString(0),
                    ["session_id"] = rd.GetString(1),
                    ["type"] = rd.GetString(2),
                    ["seq"] = rd.GetInt64(3),
                    ["time_created"] = Long(rd, 4),
                    ["time_updated"] = Long(rd, 5),
                };
                var data = Str(rd, 6);
                try { row["data"] = data is null ? null : JsonDocument.Parse(data).RootElement.Clone(); }
                catch { row["data"] = data; }

                sb.Append(JsonSerializer.Serialize(row, opts)).Append('\n');
            }
            return sb.ToString();
        }, "ExportMessages");
    }

    /// <summary>会话库的只读体检（状态页用）：库在不在、有几个会话、有几条指令载荷。</summary>
    public static string DescribeDb(string dbPath)
    {
        if (!File.Exists(dbPath)) return $"不存在（{dbPath}）";
        try
        {
            var size = new FileInfo(dbPath).Length;
            var wal = File.Exists(dbPath + "-wal") ? new FileInfo(dbPath + "-wal").Length : 0;
            var sessions = ListSessions(dbPath, CancellationToken.None).Count;
            var (_, blobs) = InstructionBlobText(dbPath, CancellationToken.None);
            return $"{FileText.Human(size)}（WAL {FileText.Human(wal)}）；会话 {sessions} 个；instruction_blob {blobs} 条";
        }
        catch (Exception ex)
        {
            return $"读不了：{ex.Message}";
        }
    }
}
