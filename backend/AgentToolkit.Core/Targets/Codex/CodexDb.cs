using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using Microsoft.Data.Sqlite;

namespace AgentToolkit.Core.Targets.Codex;

/// <summary>一条 Codex 线程（state_5.sqlite → threads）。</summary>
public sealed class CodexThread
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public string? RolloutPath { get; init; }
    public long TokensUsed { get; init; }
    public bool Archived { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public long RolloutBytes { get; init; }
    public int? TurnCount { get; init; }
}

/// <summary>thread_items 里的一条（只映射判读要用的字段）。</summary>
public sealed class CodexItem
{
    public required string ThreadId { get; init; }
    public required string TurnId { get; init; }
    public required string ItemId { get; init; }
    public long Ordinal { get; init; }
    public long CreatedAtMs { get; init; }
    public required string Type { get; init; }

    /// <summary>agentMessage 的 <c>text</c> / userMessage 的 <c>content[].text</c>。</summary>
    public string? Text { get; init; }

    /// <summary>agentMessage 的 <c>phase</c>（commentary / final_answer / null）。</summary>
    public string? Phase { get; init; }

    /// <summary>reasoning 的 <c>summary[]</c> + <c>content[]</c>（本机实测 1,210 条里只有 33 条有正文）。</summary>
    public string? Reasoning { get; init; }

    public DateTimeOffset? At => CreatedAtMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs).ToLocalTime()
        : null;
}

/// <summary>
/// Codex 的只读日志通道：<c>state_5.sqlite</c>（线程索引）+ <c>thread_history_1.sqlite</c>（thread_items 正文）
/// + <c>logs_2.sqlite</c>（应用日志，只用来出"本地没有系统提示词记录"这一条硬证据）。
///
/// ⚠️ 纪律（docs/codex.md 3.2，本机实测）：客户端在跑、WAL 没 checkpoint 时，**只读直连主文件只看得见
/// <c>_sqlx_migrations</c>**——真实表在 <c>-wal</c> 里。所以这里的每一次读都是：
///   <c>&lt;db&gt;</c> + <c>&lt;db&gt;-wal</c> + <c>&lt;db&gt;-shm</c> 三个文件一起拷到临时目录 → 打开副本（SQLite 自己恢复 WAL）→ 用完删掉。
/// 原文件只以 FileShare.ReadWrite 打开做复制，全程只读。
///
/// 读失败一律抛 <see cref="EngineErrorCode.IoError"/>，**绝不降级成「没有线程」「无法判定」**。
/// 库不存在是另一件事：那是「还没有线程」，由调用方报「无法判定」。
/// </summary>
public static class CodexDb
{
    public const string ThreadDbName = "thread_history_1.sqlite";
    public const string StateDbName = "state_5.sqlite";
    public const string LogsDbName = "logs_2.sqlite";

    public const string ItemsTable = "thread_items";
    public const string ThreadsTable = "threads";
    public const string TurnsTable = "thread_turns";
    public const string LogsTable = "logs";

    public const string AgentMessage = "agentMessage";
    public const string UserMessage = "userMessage";
    public const string Reasoning = "reasoning";

    /// <summary>logs_2.sqlite 本机 28.6 MB；超过 64 MB 就不做"日志里有没有系统提示词"的证据统计（护栏，不是官方限制）。</summary>
    public const long LogsEvidenceCapBytes = 64L * 1024 * 1024;

    public static string ThreadDbPath(string configHome) => Path.Combine(configHome, ThreadDbName);
    public static string StateDbPath(string configHome) => Path.Combine(configHome, StateDbName);
    public static string LogsDbPath(string configHome) => Path.Combine(configHome, LogsDbName);

    // ---------------------------------------------------------------- 复制（纪律的落地）

    /// <summary>
    /// 把 <c>&lt;db&gt;</c> 连同 <c>-wal</c> / <c>-shm</c> 拷到临时目录，返回副本路径（tempDir 由调用方删）。
    /// 原文件只用 FileShare.ReadWrite|Delete 打开读——客户端在写也拷得出来，且**一个字节都不写它**。
    /// </summary>
    public static string CopyWithSidecars(string dbPath, out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "agent-toolkit-codex-read", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var name = Path.GetFileName(dbPath);
        var copied = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var src = dbPath + suffix;
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(tempDir, name + suffix);
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            copied++;
        }

        if (copied == 0)
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing,
                $"库不存在，也没有可拷的 sidecar：{dbPath}（本工具不创建它）。");

        return Path.Combine(tempDir, name);
    }

    private static T WithCopy<T>(string dbPath, Func<SqliteConnection, T> work, string what)
    {
        if (!File.Exists(dbPath))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing,
                $"库不存在：{dbPath}（本工具不创建它）。");

        string? tempDir = null;
        SqliteConnection? cn = null;
        try
        {
            var copy = CopyWithSidecars(dbPath, out tempDir);
            // 副本按 ReadWrite 打开：SQLite 需要能重建 -shm 才能看到 -wal 里未 checkpoint 的页。
            // 写的是**临时副本**，原文件的只读纪律不受影响。
            cn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = copy,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            cn.Open();
            return work(cn);
        }
        catch (AgentEngineException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex)
        {
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"读取 Codex 库失败（{what}）：{dbPath}：SQLite {ex.SqliteErrorCode} {ex.Message}。" +
                "读不到不等于「没有线程」——不要据此判「没生效」。", null, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"读取 Codex 库失败（{what}）：{dbPath}：{ex.GetType().Name}: {ex.Message}", null, ex);
        }
        finally
        {
            if (cn is not null)
            {
                try { SqliteConnection.ClearPool(cn); } catch { /* 尽力而为 */ }
                cn.Dispose();
            }
            if (tempDir is not null)
                try { Directory.Delete(tempDir, true); } catch { /* 临时目录删不掉不致命 */ }
        }
    }

    private static bool TableExists(SqliteConnection cn, string table)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<string> Columns(SqliteConnection cn, string table)
    {
        var list = new List<string>();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.IsDBNull(1) ? "" : rd.GetString(1));
        return list;
    }

    // ---------------------------------------------------------------- 线程索引

    /// <summary>列出 threads，按 updated_at 降序。库不存在 → TargetFileMissing；库坏了 → IoError。</summary>
    public static IReadOnlyList<CodexThread> ListThreads(string configHome)
    {
        var dbPath = StateDbPath(configHome);
        var counts = ItemCounts(configHome);

        return WithCopy(dbPath, cn =>
        {
            if (!TableExists(cn, ThreadsTable))
                throw new AgentEngineException(EngineErrorCode.IoError,
                    $"{dbPath} 里没有 {ThreadsTable} 表：不是 Codex 的 state_5.sqlite。");

            var cols = Columns(cn, ThreadsTable);
            string Col(string name) => cols.Contains(name, StringComparer.OrdinalIgnoreCase) ? name : "NULL AS " + name;

            var sql = "SELECT " + string.Join(", ", new[]
            {
                Col("id"), Col("title"), Col("cwd"), Col("rollout_path"),
                Col("tokens_used"), Col("archived"), Col("created_at"), Col("updated_at"),
            }) + $" FROM {ThreadsTable}";

            var list = new List<CodexThread>();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = sql;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var id = rd.IsDBNull(0) ? "" : rd.GetString(0);
                if (id.Length == 0) continue;
                var updated = UnixToOffset(rd.IsDBNull(7) ? 0 : Convert.ToInt64(rd.GetValue(7), CultureInfo.InvariantCulture));
                var created = rd.IsDBNull(6) ? (DateTimeOffset?)null : UnixToOffset(Convert.ToInt64(rd.GetValue(6), CultureInfo.InvariantCulture));
                var rollout = rd.IsDBNull(3) ? null : rd.GetString(3);
                list.Add(new CodexThread
                {
                    Id = id,
                    Title = rd.IsDBNull(1) ? null : rd.GetString(1),
                    Cwd = rd.IsDBNull(2) ? null : rd.GetString(2),
                    RolloutPath = rollout,
                    TokensUsed = rd.IsDBNull(4) ? 0 : Convert.ToInt64(rd.GetValue(4), CultureInfo.InvariantCulture),
                    Archived = !rd.IsDBNull(5) && Convert.ToInt64(rd.GetValue(5), CultureInfo.InvariantCulture) != 0,
                    UpdatedAt = updated,
                    CreatedAt = created,
                    RolloutBytes = FileLengthOf(rollout),
                    TurnCount = counts.TryGetValue(id, out var n) ? n : null,
                });
            }

            return (IReadOnlyList<CodexThread>)list
                .OrderByDescending(t => t.UpdatedAt)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToArray();
        }, "列线程");
    }

    /// <summary>每个线程的条目数（thread_items 的 GROUP BY）。线程库不在 → 空表，不报错。</summary>
    private static Dictionary<string, int> ItemCounts(string configHome)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var dbPath = ThreadDbPath(configHome);
        if (!File.Exists(dbPath)) return map;
        try
        {
            return WithCopy(dbPath, cn =>
            {
                if (!TableExists(cn, ItemsTable)) return map;
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $"SELECT thread_id, COUNT(*) FROM {ItemsTable} GROUP BY thread_id";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    if (rd.IsDBNull(0)) continue;
                    map[rd.GetString(0)] = Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture);
                }
                return map;
            }, "统计条目数");
        }
        catch (AgentEngineException)
        {
            // 计数是附加信息：线程库坏了不该让"列线程"跟着失败——正文读取会自己抛 IoError。
            return map;
        }
    }

    private static long FileLengthOf(string? path)
    {
        try
        {
            var p = NormalizeDevicePath(path);
            return p is not null && File.Exists(p) ? new FileInfo(p).Length : 0;
        }
        catch { return 0; }
    }

    /// <summary>threads.rollout_path 本机实测带 <c>\\?\</c> 前缀（Win32 长路径形式）。</summary>
    public static string? NormalizeDevicePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path!.Trim().Trim('"');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        return p;
    }

    private static DateTimeOffset UnixToOffset(long value)
    {
        // 本机实测 threads.updated_at 是**秒**（1791094731）；有些版本写毫秒。按量级判，不猜格式。
        if (value <= 0) return DateTimeOffset.UnixEpoch;
        try
        {
            return value > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime()
                : DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime();
        }
        catch { return DateTimeOffset.UnixEpoch; }
    }

    // ---------------------------------------------------------------- 正文

    /// <summary>线程正文读取器：一次复制，多次按线程取条目。用完必须 Dispose（删临时目录）。</summary>
    public sealed class ThreadReader : IDisposable
    {
        private readonly SqliteConnection _cn;
        private readonly string _tempDir;
        private bool _disposed;

        public string SourcePath { get; }
        public string CopyPath { get; }
        public int TotalItems { get; }

        internal ThreadReader(string sourcePath, string copyPath, string tempDir, SqliteConnection cn)
        {
            SourcePath = sourcePath;
            CopyPath = copyPath;
            _tempDir = tempDir;
            _cn = cn;

            using var cmd = cn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {ItemsTable}";
            TotalItems = Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        public IReadOnlyList<CodexItem> Items(string threadId)
        {
            var list = new List<CodexItem>();
            using var cmd = _cn.CreateCommand();
            cmd.CommandText =
                $"SELECT thread_id, turn_id, item_id, rollout_ordinal, created_at_ms, item_type, item_json " +
                $"FROM {ItemsTable} WHERE thread_id=@t ORDER BY rollout_ordinal, created_at_ms";
            cmd.Parameters.AddWithValue("@t", threadId);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var raw = rd.IsDBNull(6) ? "" : rd.GetString(6);
                var type = rd.IsDBNull(5) ? "" : rd.GetString(5);
                list.Add(ParseItem(
                    rd.IsDBNull(0) ? threadId : rd.GetString(0),
                    rd.IsDBNull(1) ? "" : rd.GetString(1),
                    rd.IsDBNull(2) ? "" : rd.GetString(2),
                    rd.IsDBNull(3) ? 0 : Convert.ToInt64(rd.GetValue(3), CultureInfo.InvariantCulture),
                    rd.IsDBNull(4) ? 0 : Convert.ToInt64(rd.GetValue(4), CultureInfo.InvariantCulture),
                    type, raw));
            }
            return list;
        }

        /// <summary>原始 item_json（导出用）：按 rollout_ordinal 升序，一行一条。</summary>
        public IReadOnlyList<string> RawItems(string threadId)
        {
            var list = new List<string>();
            using var cmd = _cn.CreateCommand();
            cmd.CommandText =
                $"SELECT item_json FROM {ItemsTable} WHERE thread_id=@t ORDER BY rollout_ordinal, created_at_ms";
            cmd.Parameters.AddWithValue("@t", threadId);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var raw = rd.IsDBNull(0) ? "" : rd.GetString(0);
                if (raw.Length > 0) list.Add(raw);
            }
            return list;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { SqliteConnection.ClearPool(_cn); } catch { /* 尽力而为 */ }
            _cn.Dispose();
            try { Directory.Delete(_tempDir, true); } catch { /* 临时目录删不掉不致命 */ }
        }
    }

    /// <summary>打开线程正文读取器。库不存在 → TargetFileMissing；库坏了 → IoError。</summary>
    public static ThreadReader OpenThreads(string configHome)
    {
        var dbPath = ThreadDbPath(configHome);
        if (!File.Exists(dbPath))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing,
                $"线程正文库不存在：{dbPath}（本工具不创建它；客户端还没落过线程）。");

        string? tempDir = null;
        SqliteConnection? cn = null;
        try
        {
            var copy = CopyWithSidecars(dbPath, out tempDir);
            cn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = copy,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            cn.Open();

            if (!TableExists(cn, ItemsTable))
                throw new AgentEngineException(EngineErrorCode.IoError,
                    $"副本里没有 {ItemsTable} 表：{dbPath}。连 -wal 一起拷了还看不到表，" +
                    "说明这个文件不是 Codex 的 thread_history 库（或者已经损坏）——不据此判「没有线程」。");

            var reader = new ThreadReader(dbPath, copy, tempDir!, cn);
            cn = null;   // 所有权交给 reader
            tempDir = null;
            return reader;
        }
        catch (AgentEngineException) { throw; }
        catch (SqliteException ex)
        {
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"读取线程正文库失败：{dbPath}：SQLite {ex.SqliteErrorCode} {ex.Message}", null, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentEngineException(EngineErrorCode.IoError,
                $"读取线程正文库失败：{dbPath}：{ex.GetType().Name}: {ex.Message}", null, ex);
        }
        finally
        {
            if (cn is not null)
            {
                try { SqliteConnection.ClearPool(cn); } catch { /* 尽力而为 */ }
                cn.Dispose();
            }
            if (tempDir is not null)
                try { Directory.Delete(tempDir, true); } catch { /* 尽力而为 */ }
        }
    }

    // ---------------------------------------------------------------- 条目解析

    private static CodexItem ParseItem(string threadId, string turnId, string itemId, long ordinal, long createdMs,
        string type, string raw)
    {
        string? text = null, phase = null, reasoning = null;
        var effectiveType = type;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (effectiveType.Length == 0 &&
                root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                effectiveType = t.GetString() ?? "";

            switch (effectiveType)
            {
                case AgentMessage:
                    text = Str(root, "text");
                    phase = Str(root, "phase");
                    break;

                case UserMessage:
                    text = UserText(root);
                    break;

                case Reasoning:
                    reasoning = ReasoningText(root);
                    break;
            }
        }
        catch (JsonException)
        {
            // 坏行不致命：正文留空，但类型与序位照实保留（判读时会被计成"无法解析"）。
            text = null;
        }

        return new CodexItem
        {
            ThreadId = threadId,
            TurnId = turnId,
            ItemId = itemId,
            Ordinal = ordinal,
            CreatedAtMs = createdMs,
            Type = effectiveType,
            Text = text,
            Phase = phase,
            Reasoning = reasoning,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>userMessage 的正文：<c>content[]</c> 里 type=="text" 的 <c>text</c>（本机实测形状）。</summary>
    private static string? UserText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String) { parts.Add(part.GetString() ?? ""); continue; }
            if (part.ValueKind != JsonValueKind.Object) continue;
            var t = Str(part, "type");
            if (t is not null && !string.Equals(t, "text", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(t, "input_text", StringComparison.OrdinalIgnoreCase)) continue;
            var s = Str(part, "text");
            if (!string.IsNullOrEmpty(s)) parts.Add(s!);
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    /// <summary>reasoning 的正文：<c>summary[]</c> + <c>content[]</c> 里的 text（本机实测 33/1210 非空）。</summary>
    private static string? ReasoningText(JsonElement root)
    {
        var parts = new List<string>();
        foreach (var field in new[] { "summary", "content" })
        {
            if (!root.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String) { parts.Add(el.GetString() ?? ""); continue; }
                if (el.ValueKind != JsonValueKind.Object) continue;
                var s = Str(el, "text") ?? Str(el, "content");
                if (!string.IsNullOrEmpty(s)) parts.Add(s!);
            }
        }
        return parts.Count == 0 ? null : string.Join("\n", parts.Where(p => p.Length > 0));
    }

    // ---------------------------------------------------------------- 应用日志（只做证据）

    /// <summary>
    /// 统计 <c>logs.feedback_log_body</c> 里若干特征串的命中数。
    /// 用途只有一条：**证明本地没有系统提示词的记录**（docs/codex.md 第四节：全部 0 命中），
    /// 所以"日志里有没有指令"不能当生效判据。读不到就返回 null（证据缺一条，不是结论反转）。
    /// </summary>
    public static IReadOnlyList<(string Needle, int Rows)>? LogNeedleHits(string configHome,
        IReadOnlyList<string> needles)
    {
        var dbPath = LogsDbPath(configHome);
        if (!File.Exists(dbPath)) return null;
        try
        {
            if (new FileInfo(dbPath).Length > LogsEvidenceCapBytes) return null;
        }
        catch { return null; }

        try
        {
            return WithCopy(dbPath, cn =>
            {
                if (!TableExists(cn, LogsTable)) return null;
                var list = new List<(string, int)>();
                foreach (var needle in needles)
                {
                    using var cmd = cn.CreateCommand();
                    cmd.CommandText =
                        $"SELECT COUNT(*) FROM {LogsTable} " +
                        "WHERE instr(lower(coalesce(feedback_log_body,'')), lower(@n)) > 0";
                    cmd.Parameters.AddWithValue("@n", needle);
                    list.Add((needle, Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture)));
                }
                return (IReadOnlyList<(string, int)>?)list;
            }, "统计应用日志");
        }
        catch (AgentEngineException)
        {
            return null;
        }
    }

    /// <summary>线程正文的纯文本拼接（标记判读用）：条目按 rollout_ordinal 顺序。</summary>
    public static string FlattenText(IEnumerable<CodexItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Text)) sb.Append(item.Text).Append('\n');
            if (!string.IsNullOrEmpty(item.Reasoning)) sb.Append(item.Reasoning).Append('\n');
        }
        return sb.ToString();
    }
}
