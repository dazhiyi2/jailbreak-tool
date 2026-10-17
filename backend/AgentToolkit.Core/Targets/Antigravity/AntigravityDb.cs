using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using Microsoft.Data.Sqlite;

namespace AgentToolkit.Core.Targets.Antigravity;

/// <summary>一次「把库里的数据抽成文本」的统计结果（判读与报告都用它）。</summary>
public sealed class SessionExtract
{
    public required string SessionId { get; init; }
    public required string Text { get; init; }
    public int Steps { get; init; }
    public int StepsWithText { get; init; }
    public int UserSteps { get; init; }
    public int PlannerSteps { get; init; }
    public int ToolResultSteps { get; init; }
    public int ReasoningSteps { get; init; }
    public int ReasoningChars { get; init; }
    public int LongestReasoning { get; init; }
    public int ToolCalls { get; init; }
    public string? Workspace { get; init; }
}

/// <summary>
/// Antigravity 的日志通道：<c>~/.gemini/antigravity/conversations/&lt;cascade id&gt;.db</c>
/// （SQLite + WAL）。**全程只读**。
///
/// 表结构（实测 2026-10）：
/// <code>
/// steps(idx, step_type, status, has_subtrajectory, metadata, error_details, permissions,
///       task_details, render_info, step_payload, step_format)
/// trajectory_meta(trajectory_id, cascade_id, trajectory_type, source)
/// gen_metadata(idx, data, size)  executor_metadata(idx, data)  battle_mode_infos(idx, data)
/// parent_references(idx, data)   trajectory_metadata_blob(id, data)
/// </code>
///
/// <c>step_payload</c> 是**二进制 protobuf**（不是 JSON、不是文本），由 <see cref="AntigravityProto"/> 抽文本。
/// 字段映射（docs/gemini.md 第六节，已在真实会话上核对）：
///   14 用户输入       → <c>.19.2</c>
///   15 planner 响应   → <c>.20.3</c> = 推理原文；<c>.20.1</c> / <c>.20.8</c> = 答复正文；
///                        <c>.20.7.3</c> = 工具调用 JSON
///   132 工具执行结果  → <c>.140.2.1</c> 等
///
/// 连接纪律：<c>Mode=ReadOnly</c> 的普通连接串（**不用 immutable=1**——那会忽略 WAL，
/// 而本机实测有 1.9 MB 的 <c>-wal</c> 还没 checkpoint，用 immutable 会读成空）。
/// 读不到 / 被占用一律抛 <see cref="EngineErrorCode.IoError"/>，**绝不静默返回空**
/// （空文本会被判成"没进模型上下文"，把排查引到错误方向）。
/// </summary>
public static class AntigravityDb
{
    public const string DbSuffix = ".db";

    /// <summary>单个文件的判据：<c>*.db</c>，且不是 <c>-shm</c> / <c>-wal</c>。</summary>
    public static bool IsSessionDb(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(DbSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static string OpenString(string dbPath) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString();

    public static string SessionIdOf(string dbPath) => Path.GetFileNameWithoutExtension(dbPath);

    // ---------------------------------------------------------------- 连接

    private static T With<T>(string dbPath, Func<SqliteConnection, T> work, string what)
    {
        if (!File.Exists(dbPath))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing,
                $"会话库不存在：{dbPath}（Antigravity 还没落过会话；数据根默认 ~/.gemini/antigravity）。");

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
                ? "数据库被占用 / 锁住（Antigravity 可能正在写入）——稍后重试，不要把它当成「没有会话」。"
                : "只读连接失败。";
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"读取会话库失败（{what}）：{dbPath}：SQLite {ex.SqliteErrorCode} {ex.Message}。{hint}", null, ex);
        }
        catch (Exception ex)
        {
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"读取会话库失败（{what}）：{dbPath}：{ex.GetType().Name}: {ex.Message}", null, ex);
        }
        finally
        {
            if (cn is not null)
            {
                // 只读也走连接池：用完立刻清掉，别把锁留给正在运行的 Antigravity。
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

    // ---------------------------------------------------------------- 会话列表

    /// <summary>
    /// 枚举 <c>conversations\*.db</c>，每个库一个会话：
    /// Id = 文件名（去 .db）、ModifiedAt = 文件 mtime、SizeBytes = 库文件大小、
    /// Workspace 从 project 类表或 trajectory_meta 取（取不到就留空，**不编**）。
    /// 库打不开 → 抛 IoError（别静默跳过：那会把"读不到"报成"没有会话"）。
    /// </summary>
    public static IReadOnlyList<SessionInfo> ListSessions(string conversationsDir, CancellationToken ct)
    {
        if (!Directory.Exists(conversationsDir)) return Array.Empty<SessionInfo>();

        var files = Directory.EnumerateFiles(conversationsDir, "*" + DbSuffix)
            .Where(IsSessionDb)
            .OrderByDescending(f => LastWrite(f))
            .ToArray();

        var list = new List<SessionInfo>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = With(file, cn => InfoOf(cn, file), "ListSessions");
            list.Add(info);
        }
        return list;
    }

    private static DateTimeOffset LastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTimeOffset.MinValue; }
    }

    private static SessionInfo InfoOf(SqliteConnection cn, string dbPath)
    {
        var id = SessionIdOf(dbPath);
        var (turns, steps, workspace) = Summarize(cn, dbPath);
        long size;
        DateTimeOffset modified;
        try
        {
            var fi = new FileInfo(dbPath);
            size = fi.Length;
            modified = fi.LastWriteTimeUtc;
        }
        catch
        {
            size = 0;
            modified = DateTimeOffset.MinValue;
        }

        return new SessionInfo
        {
            Id = id,
            Path = dbPath,
            Workspace = workspace ?? "",
            SizeBytes = size,
            ModifiedAt = modified,
            TurnCount = turns,
        };
    }

    private static (int Turns, int Steps, string? Workspace) Summarize(SqliteConnection cn, string dbPath)
    {
        if (!TableExists(cn, "steps"))
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"会话库里没有 steps 表：{dbPath}（不是 Antigravity 的会话库，或 schema 变了）。");

        var cols = Columns(cn, "steps");
        foreach (var required in new[] { "idx", "step_type", "step_payload" })
            if (!cols.Contains(required))
                throw new AgentEngineException(EngineErrorCode.IoError,
                    $"会话库的 steps 表缺列 {required}：{dbPath}（schema 变了，拒绝硬读）。");

        var steps = 0;
        var turns = 0;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT step_type FROM steps";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                steps++;
                if (rd.GetInt32(0) == AntigravitySteps.UserInput) turns++;
            }
        }
        return (turns, steps, Workspace(cn));
    }

    /// <summary>
    /// 工作区：只在真的存在 project 类表 / trajectory_meta 里带路径列时才取，取不到返回 null。
    /// docs/gemini.md 里没有记录这一列——所以宁可留空也不猜。
    /// </summary>
    private static string? Workspace(SqliteConnection cn)
    {
        string[] tables = { "project", "projects", "workspace", "workspaces", "session", "sessions", "trajectory_meta" };
        string[] columns =
        {
            "worktree", "root_path", "rootPath", "workspace", "workspacePath", "workspace_path",
            "directory", "cwd", "project_path", "path", "uri",
        };

        foreach (var table in tables)
        {
            try
            {
                if (!TableExists(cn, table)) continue;
                var cols = Columns(cn, table);
                foreach (var column in columns)
                {
                    if (!cols.Contains(column)) continue;
                    using var cmd = cn.CreateCommand();
                    cmd.CommandText = $"SELECT \"{column}\" FROM \"{table}\" LIMIT 8";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                    {
                        if (rd.IsDBNull(0)) continue;
                        var v = rd.GetValue(0) as string;
                        if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
                    }
                }
            }
            catch { /* 表结构不认识就跳过——工作区留空比编一个强 */ }
        }
        return null;
    }

    // ---------------------------------------------------------------- 会话解析

    public static bool IsLatestAlias(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ||
        sessionId!.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase) ||
        sessionId.Trim().Equals("newest", StringComparison.OrdinalIgnoreCase) ||
        sessionId.Trim().Equals("last", StringComparison.OrdinalIgnoreCase) ||
        sessionId.Trim() == "-1";

    /// <summary>把用户给的会话标识解析成真实库文件名（支持 latest 别名、完整 id、唯一前缀）。</summary>
    public static string ResolveSessionId(string conversationsDir, string? sessionId, CancellationToken ct)
    {
        var all = ListSessions(conversationsDir, ct);
        if (all.Count == 0)
            throw new AgentEngineException(EngineErrorCode.NotApplied,
                $"conversations 里没有任何会话库：{conversationsDir}（先让 Antigravity 跑一轮对话，再来读日志）。");

        if (IsLatestAlias(sessionId)) return all[0].Id;

        var raw = sessionId!.Trim().Trim('"');
        var exact = all.FirstOrDefault(x => string.Equals(x.Id, raw, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Id;

        var prefix = all.Where(x => x.Id.StartsWith(raw, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (prefix.Length == 1) return prefix[0].Id;
        if (prefix.Length > 1)
            throw new AgentEngineException(EngineErrorCode.VerifyProblems,
                $"会话标识「{raw}」前缀不唯一，匹配到 {prefix.Length} 个：{string.Join("、", prefix.Take(5).Select(p => p.Id))}。请给完整 id。");

        throw new AgentEngineException(EngineErrorCode.TargetFileMissing, DescribeMissing(conversationsDir, raw));
    }

    public static string DescribeMissing(string conversationsDir, string? sessionId)
    {
        if (!Directory.Exists(conversationsDir))
            return $"找不到会话目录：{conversationsDir}（Antigravity 还没落过会话，或数据根不对）。";
        try
        {
            var ids = ListSessions(conversationsDir, CancellationToken.None).Select(s => s.Id).Take(5).ToArray();
            var list = ids.Length == 0 ? "(目录里没有任何 .db)" : string.Join("、", ids);
            return $"找不到会话：{sessionId ?? "(空)"}。{conversationsDir} 里现有的会话：{list}";
        }
        catch (Exception ex)
        {
            return $"找不到会话：{sessionId ?? "(空)"}；而且枚举会话库也失败了：{ex.Message}";
        }
    }

    /// <summary>
    /// 载入一个会话：按 <c>steps.idx</c> 升序，14 → 用户轮，15 → planner 轮（推理在 <c>20.3</c>），
    /// 132 → 工具结果（**不进 ReasoningText**——那是工具输出，不是模型思考）。
    /// 遵守 <see cref="SessionLoadOptions.MaxTurns"/> 与 <see cref="SessionLoadOptions.MaxCharsPerText"/>
    /// （截断置 <c>Truncated</c>，带 256 字符下限保护）。
    /// </summary>
    public static SessionDetail LoadSession(string conversationsDir, string sessionId, SessionLoadOptions options, CancellationToken ct)
    {
        var id = ResolveSessionId(conversationsDir, sessionId, ct);
        var dbPath = Path.Combine(conversationsDir, id + DbSuffix);

        return With(dbPath, cn =>
        {
            var info = InfoOf(cn, dbPath);
            var notes = new List<Note>();
            var records = new List<TurnRecord>();
            var cap = Math.Max(256, options.MaxCharsPerText);

            var counts = new Dictionary<int, int>();
            var empty = 0;
            var reasoningSteps = 0;
            var reasoningChars = 0;
            var longest = 0;
            var toolCalls = 0;
            var truncatedAny = false;
            var rows = 0;

            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT idx, step_type, step_payload FROM steps ORDER BY idx ASC";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    rows++;
                    var stepType = rd.GetInt32(1);
                    counts[stepType] = counts.GetValueOrDefault(stepType) + 1;
                    if (rd.IsDBNull(2)) { empty++; continue; }

                    var payload = (byte[])rd.GetValue(2);
                    var fields = AntigravityProto.Extract(payload);

                    switch (stepType)
                    {
                        case AntigravitySteps.UserInput:
                        {
                            var text = AntigravityProto.Join(fields, "19.2", "19.3");
                            if (text.Length == 0) { empty++; continue; }
                            records.Add(Make(records.Count, "user", text, null, cap, ref truncatedAny));
                            break;
                        }

                        case AntigravitySteps.PlannerResponse:
                        {
                            var reasoning = AntigravityProto.Join(fields, "20.3");
                            var answer = AntigravityProto.Join(fields, "20.1", "20.8");
                            var calls = AntigravityProto.All(fields, "20.7.3")
                                .Select(DescribeToolCall)
                                .Where(s => s.Length > 0)
                                .ToArray();
                            toolCalls += calls.Length;

                            if (reasoning.Length > 0)
                            {
                                reasoningSteps++;
                                reasoningChars += reasoning.Length;
                                longest = Math.Max(longest, reasoning.Length);
                            }

                            var body = answer;
                            if (calls.Length > 0)
                                body = (body.Length == 0 ? "" : body + "\n") +
                                       $"[工具调用 {calls.Length}] " + string.Join("；", calls.Take(6)) +
                                       (calls.Length > 6 ? "；…" : "");

                            if (reasoning.Length == 0 && body.Length == 0) { empty++; continue; }

                            records.Add(Make(records.Count, "assistant",
                                body.Length == 0 ? null : body,
                                options.IncludeReasoning && reasoning.Length > 0 ? reasoning : null,
                                cap, ref truncatedAny));
                            break;
                        }

                        case AntigravitySteps.ToolResult:
                        {
                            var result = AntigravityProto.Join(fields, "140.2.1", "140.2.6.1");
                            if (result.Length == 0)
                            {
                                // 抽不到已知路径就退到「最长的一段文本」，但仍然只当工具输出。
                                result = fields.OrderByDescending(f => f.Text.Length).Select(f => f.Text).FirstOrDefault() ?? "";
                            }
                            if (result.Length == 0) { empty++; continue; }
                            var excerpt = result.Length <= ToolResultExcerptChars ? result : result[..ToolResultExcerptChars] + "…";
                            records.Add(Make(records.Count, "tool", "[工具结果] " + excerpt, null, cap, ref truncatedAny));
                            break;
                        }

                        default:
                            empty++;
                            break;
                    }
                }
            }

            var kept = records;
            if (options.MaxTurns > 0 && records.Count > options.MaxTurns)
            {
                kept = records.Skip(records.Count - options.MaxTurns).ToList();
                notes.Add(Notes.Info("轮次已限制",
                    $"共 {records.Count} 条步骤记录，按 MaxTurns={options.MaxTurns} 只载入最近 {kept.Count} 条。", "antigravity/db"));
            }

            notes.Add(Notes.Info("解析口径",
                $"steps 共 {rows} 行（" +
                string.Join("、", counts.OrderBy(kv => kv.Key).Select(kv => $"step_type={kv.Key}×{kv.Value}")) +
                $")；14 → 用户轮取 .19.2，15 → 推理取 .20.3、答复取 .20.1/.20.8、工具调用取 .20.7.3，" +
                $"132 → 工具结果（只做 Text 摘录，**不进 ReasoningText**）。", "antigravity/db"));
            notes.Add(Notes.Info("推理原文",
                reasoningSteps == 0
                    ? "没有任何一步抽出 .20.3：这个会话可能是纯工具执行，也可能 payload 编码变了——先看命中数，别评价模型态度。"
                    : $"{reasoningSteps} 步有 .20.3 推理原文，合计 {reasoningChars} 字符，最长 {longest} 字符；" +
                      "判读只看这一段（transcript_full.jsonl 里没有推理，实测 0 命中）。", "antigravity/db"));
            if (toolCalls > 0)
                notes.Add(Notes.Info("工具调用",
                    $"step_type=15 的 .20.7.3 共 {toolCalls} 次，已作为 [工具调用 N] 附注进 Text（不改写原文）。", "antigravity/db"));
            if (counts.GetValueOrDefault(AntigravitySteps.ToolResult) > 0)
                notes.Add(Notes.Info("工具结果",
                    $"step_type=132 共 {counts[AntigravitySteps.ToolResult]} 步，只做 {ToolResultExcerptChars} 字符摘录进 Text；" +
                    "它们不是模型思考，所以不进 ReasoningText。", "antigravity/db"));
            if (empty > 0)
                notes.Add(Notes.Warn("有抽不出文本的步骤",
                    $"{empty} 步抽不出任何文本（空 payload / 未知 step_type / 结构变了）。不因为一部分抽不出来就丢掉整个会话。", "antigravity/db"));
            if (truncatedAny)
                notes.Add(Notes.Warn("文本已截断",
                    $"至少一段文本超过 MaxCharsPerText（下限保护 256，实际生效 {cap}），已截断并置 Truncated=true；" +
                    "判读请以未截断的导出文件为准。", "antigravity/db"));
            if (string.IsNullOrWhiteSpace(info.Workspace))
                notes.Add(Notes.Info("工作区未知",
                    "trajectory_meta / project 类表里没有可用的路径列：Workspace 留空（不编）。", "antigravity/db"));

            return new SessionDetail
            {
                Info = info,
                SystemPromptText = null,   // 由适配器填：本目标的常驻提示词不在日志里
                Turns = kept,
                Notes = notes,
            };
        }, "LoadSession");
    }

    /// <summary>工具结果摘录上限（够看清在干什么，又不至于把判读文本淹掉）。</summary>
    public const int ToolResultExcerptChars = 600;

    private static TurnRecord Make(int index, string role, string? text, string? reasoning, int cap, ref bool truncatedAny)
    {
        var (t, tCut) = Cap(text, cap);
        var (r, rCut) = Cap(reasoning, cap);
        if (tCut || rCut) truncatedAny = true;
        return new TurnRecord
        {
            Index = index,
            Role = role,
            Text = t,
            ReasoningText = r,
            At = null,                     // steps 表里没有时间列（只有 idx），不编时间
            Truncated = tCut || rCut,
        };
    }

    private static (string? Text, bool Truncated) Cap(string? text, int cap)
    {
        if (string.IsNullOrEmpty(text)) return (text, false);
        if (text!.Length <= cap) return (text, false);
        return (text[..cap], true);
    }

    /// <summary>工具调用 JSON（<c>{"CommandLine":…,"toolAction":…,"toolSummary":…}</c>）→ 一行摘要。</summary>
    private static string DescribeToolCall(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            string? Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var summary = Get("toolSummary") ?? Get("toolAction");
            var action = Get("toolAction");
            var command = Get("CommandLine");
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(summary)) parts.Add(summary!);
            if (!string.IsNullOrWhiteSpace(action) && !string.Equals(action, summary, StringComparison.Ordinal)) parts.Add(action!);
            if (!string.IsNullOrWhiteSpace(command)) parts.Add("$ " + OneLine(command!, 80));
            return parts.Count == 0 ? OneLine(json, 80) : OneLine(string.Join(" / ", parts), 160);
        }
        catch
        {
            return OneLine(json, 80);
        }
    }

    private static string OneLine(string s, int max)
    {
        var one = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }

    // ---------------------------------------------------------------- 抽文本（判读 / 命中数）

    /// <summary>
    /// 把一个会话所有步骤抽出来的文本拼成一份（含推理与答复 + 工具输出）。
    /// 抽空 → 抛 IoError（**不静默返回空**：空文本会被判成"没进模型上下文"）。
    /// </summary>
    public static SessionExtract Extract(string conversationsDir, string sessionId, CancellationToken ct)
    {
        var id = ResolveSessionId(conversationsDir, sessionId, ct);
        var dbPath = Path.Combine(conversationsDir, id + DbSuffix);

        return With(dbPath, cn =>
        {
            var sb = new StringBuilder();
            int steps = 0, withText = 0, user = 0, planner = 0, toolResults = 0;
            int reasoningSteps = 0, reasoningChars = 0, longest = 0, toolCalls = 0;

            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "SELECT idx, step_type, step_payload FROM steps ORDER BY idx ASC";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    steps++;
                    var stepType = rd.GetInt32(1);
                    if (rd.IsDBNull(2)) continue;

                    var fields = AntigravityProto.Extract((byte[])rd.GetValue(2));
                    switch (stepType)
                    {
                        case AntigravitySteps.UserInput: user++; break;
                        case AntigravitySteps.PlannerResponse:
                        {
                            planner++;
                            var reasoning = AntigravityProto.Join(fields, "20.3");
                            toolCalls += AntigravityProto.All(fields, "20.7.3").Count;
                            if (reasoning.Length > 0)
                            {
                                reasoningSteps++;
                                reasoningChars += reasoning.Length;
                                longest = Math.Max(longest, reasoning.Length);
                            }
                            break;
                        }
                        case AntigravitySteps.ToolResult: toolResults++; break;
                    }

                    var flat = AntigravityProto.Flatten(fields);
                    if (flat.Length == 0) continue;
                    withText++;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(flat);
                }
            }

            var text = sb.ToString();
            if (text.Length == 0)
                throw new AgentEngineException(EngineErrorCode.IoError,
                    $"会话 {id} 的 step_payload 里抽不出任何文本（{steps} 步）：{dbPath}。" +
                    "这不是「没生效」的证据，而是读不出来的证据——先确认库没坏、schema 没变，再判读。");

            return new SessionExtract
            {
                SessionId = id,
                Text = text,
                Steps = steps,
                StepsWithText = withText,
                UserSteps = user,
                PlannerSteps = planner,
                ToolResultSteps = toolResults,
                ReasoningSteps = reasoningSteps,
                ReasoningChars = reasoningChars,
                LongestReasoning = longest,
                ToolCalls = toolCalls,
                Workspace = Workspace(cn),
            };
        }, "Extract");
    }

    // ---------------------------------------------------------------- 导出

    /// <summary>会话导出成逐行 JSON：每步一行（idx / step_type / 抽取出的文本字段）。</summary>
    public static string ExportSteps(string conversationsDir, string sessionId, CancellationToken ct)
    {
        var id = ResolveSessionId(conversationsDir, sessionId, ct);
        var dbPath = Path.Combine(conversationsDir, id + DbSuffix);
        var opts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        return With(dbPath, cn =>
        {
            var sb = new StringBuilder();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT idx, step_type, step_payload FROM steps ORDER BY idx ASC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                ct.ThrowIfCancellationRequested();
                var fields = rd.IsDBNull(2)
                    ? Array.Empty<ProtoTextField>()
                    : AntigravityProto.Extract((byte[])rd.GetValue(2));

                var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var f in fields)
                {
                    if (!map.TryGetValue(f.Path, out var list)) map[f.Path] = list = new List<string>();
                    list.Add(f.Text);
                }

                var row = new Dictionary<string, object?>
                {
                    ["idx"] = Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture),
                    ["step_type"] = rd.GetInt32(1),
                    ["fields"] = map.ToDictionary(kv => kv.Key, kv => kv.Value.Count == 1 ? kv.Value[0] : string.Join("\n", kv.Value)),
                };
                sb.Append(JsonSerializer.Serialize(row, opts)).Append('\n');
            }
            return sb.ToString();
        }, "ExportSteps");
    }

    /// <summary>会话库的只读体检（状态页 / 报告用）。</summary>
    public static string DescribeConversations(string conversationsDir)
    {
        if (!Directory.Exists(conversationsDir)) return $"不存在（{conversationsDir}）";
        try
        {
            var dbs = Directory.EnumerateFiles(conversationsDir, "*" + DbSuffix).Where(IsSessionDb).ToArray();
            if (dbs.Length == 0) return "目录里没有任何 .db";
            var wal = dbs.Sum(f => File.Exists(f + "-wal") ? new FileInfo(f + "-wal").Length : 0);
            var size = dbs.Sum(f => new FileInfo(f).Length);
            return $"{dbs.Length} 个会话库，共 {FileText.Human(size)}（WAL {FileText.Human(wal)}）；最新的：{SessionIdOf(dbs.OrderByDescending(LastWrite).First())}";
        }
        catch (Exception ex)
        {
            return $"读不了：{ex.Message}";
        }
    }

    public static string DescribeDb(string dbPath)
    {
        if (!File.Exists(dbPath)) return $"不存在（{dbPath}）";
        try
        {
            var size = new FileInfo(dbPath).Length;
            var wal = File.Exists(dbPath + "-wal") ? new FileInfo(dbPath + "-wal").Length : 0;
            var extract = Extract(Path.GetDirectoryName(dbPath)!, SessionIdOf(dbPath), CancellationToken.None);
            return $"{FileText.Human(size)}（WAL {FileText.Human(wal)}）；{extract.Steps} 步 / " +
                   $"推理 {extract.ReasoningSteps} 步 {extract.ReasoningChars} 字符（最长 {extract.LongestReasoning}）/ " +
                   $"工具调用 {extract.ToolCalls} 次 / 工具结果 {extract.ToolResultSteps} 步";
        }
        catch (Exception ex)
        {
            return $"读不了：{ex.Message}";
        }
    }
}

/// <summary>实测的 step_type 取值（docs/gemini.md 第六节）。</summary>
public static class AntigravitySteps
{
    public const int UserInput = 14;
    public const int PlannerResponse = 15;
    public const int ToolResult = 132;
}
