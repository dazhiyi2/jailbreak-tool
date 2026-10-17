using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using Microsoft.Data.Sqlite;

namespace AgentToolkit.Core.Targets.Cursor;

/// <summary>库里一条会话（composerData:&lt;id&gt;）。</summary>
public sealed class CursorSession
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? ModelName { get; init; }
    public string? WorkspaceId { get; init; }
    public bool IsArchived { get; init; }
    public int HeaderCount { get; init; }
    public long SizeBytes { get; init; }
    public long Recency { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>有 promptTokenBreakdown 才是「跑过一次真实模型调用」的会话。</summary>
    public bool HasBreakdown { get; init; }
    /// <summary>promptTokenBreakdown.categories 里 id=="rules" 那一档的 characterCount（**生效主判据**）。</summary>
    public int? RulesChars { get; init; }
    public int? SystemPromptChars { get; init; }
    public int? TotalUsedTokens { get; init; }
    public int? MaxTokens { get; init; }
}

/// <summary>写库前/后的一致性快照：行数、其它键的结构指纹、其它表的结构指纹。</summary>
public sealed class CursorDbSnapshot
{
    public required int ItemRows { get; init; }
    public required bool KeyPresent { get; init; }
    public required string? KeyValue { get; init; }
    /// <summary>ItemTable 里**排除**本工具那个键之后的结构指纹（行哈希排序后再 sha256）。</summary>
    public required string OtherKeysDigest { get; init; }
    /// <summary>cursorDiskKV / composerHeaders 的结构指纹（本工具一个字节都不该动它们）。</summary>
    public required string OtherTablesDigest { get; init; }
    public required bool DiskKvExists { get; init; }
    public required bool HeadersExists { get; init; }
}

/// <summary>
/// Cursor 的 SQLite 通道：<c>%APPDATA%\Cursor\User\globalStorage\state.vscdb</c>
/// （VS Code 分支的 ItemTable + Cursor 自己的 cursorDiskKV / composerHeaders）。
///
/// 表结构（本机实测 2026-10）：
/// <code>
/// ItemTable(key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)      126 行：键值存储（层 2 的落点）
/// cursorDiskKV(key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)   39 行：composerData: / bubbleId: / agentKv:
/// composerHeaders(composerId PK, workspaceId, createdAt, lastUpdatedAt, isArchived, isSubagent, recency, ...)
/// </code>
///
/// 纪律：
///   · 读一律只读连接（<c>Mode=ReadOnly</c>，**不用 immutable=1**：那会忽略 -wal，本机实测有 WAL 文件）；
///   · 写用短事务，允许在客户端运行时写（WAL 支持并发），但状态里如实报 ClientRunning；
///   · **绝不碰别的键**：写完重新只读打开校验行数、我们那个键的值、以及其它键与其它表的结构指纹；
///   · 读失败一律抛 <see cref="EngineErrorCode.IoError"/>，**绝不降级成「没有会话」「无法判定」**
///     （那会把"读不到"报成"没生效"，把排查引到错误方向）。
/// </summary>
public static class CursorDb
{
    public const string PersonalContextKey = "aicontext.personalContext";
    public const string ItemTableName = "ItemTable";
    public const string DiskKvTableName = "cursorDiskKV";
    public const string HeadersTableName = "composerHeaders";
    public const string ComposerPrefix = "composerData:";
    public const string BubblePrefix = "bubbleId:";
    public const string RecentPathsKey = "history.recentlyOpenedPathsList";

    // ---------------------------------------------------------------- 连接

    public static string OpenString(string dbPath, bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        // 不用连接池：用完立刻放文件句柄，别把锁留给正在运行的 Cursor。
        Pooling = false,
    }.ToString();

    private static T With<T>(string dbPath, bool readOnly, Func<SqliteConnection, T> work, string what)
    {
        if (!File.Exists(dbPath))
            throw new AgentEngineException(EngineErrorCode.TargetFileMissing,
                $"会话库不存在：{dbPath}（Cursor 还没在这台机器上落过会话；本工具不创建它）。");

        SqliteConnection? cn = null;
        try
        {
            cn = new SqliteConnection(OpenString(dbPath, readOnly));
            cn.Open();
            if (!readOnly)
            {
                using var pragma = cn.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }
            return work(cn);
        }
        catch (AgentEngineException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex)
        {
            var hint = ex.SqliteErrorCode is 5 or 6
                ? "数据库被占用 / 锁住（Cursor 可能正在写入）——稍后重试，不要把它当成「没有会话」。"
                : ex.SqliteErrorCode == 26
                    ? "文件不是数据库，或者已经损坏。"
                    : "SQLite 连接失败。";
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
                try { SqliteConnection.ClearPool(cn); } catch { /* 尽力而为 */ }
                cn.Dispose();
            }
        }
    }

    public static bool TableExists(SqliteConnection cn, string table)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return cmd.ExecuteScalar() is not null;
    }

    // ---------------------------------------------------------------- 结构指纹

    /// <summary>
    /// 一张表的结构指纹：逐行算 sha256 → 排序 → 再 sha256（与行序无关）。
    /// <paramref name="excludeKeyValue"/> 非空时跳过第一列等于它的那一行
    /// （ItemTable / cursorDiskKV 的第一列都是 key，见类注释里的表结构）。
    /// </summary>
    public static (int Rows, string Digest) Fingerprint(SqliteConnection cn, string table, string? excludeKeyValue)
    {
        var rows = new List<string>();
        long total = 0;
        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = $"SELECT * FROM {table}";
            using var rd = cmd.ExecuteReader();
            var n = rd.FieldCount;
            while (rd.Read())
            {
                if (excludeKeyValue is not null && n > 0 && rd.GetValue(0) is string first &&
                    string.Equals(first, excludeKeyValue, StringComparison.Ordinal))
                    continue;

                using var ms = new MemoryStream();
                for (var i = 0; i < n; i++)
                {
                    var v = rd.GetValue(i);
                    var bytes = v switch
                    {
                        DBNull => Array.Empty<byte>(),
                        byte[] b => b,
                        string s => Encoding.UTF8.GetBytes(s),
                        _ => Encoding.UTF8.GetBytes(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""),
                    };
                    ms.Write(bytes, 0, bytes.Length);
                    ms.WriteByte(0);
                }
                var arr = ms.ToArray();
                total += arr.Length;
                rows.Add(Convert.ToHexString(SHA256.HashData(arr)));
            }
        }

        rows.Sort(StringComparer.Ordinal);
        var joined = Encoding.UTF8.GetBytes(string.Join("\n", rows));
        return (rows.Count, Convert.ToHexString(SHA256.HashData(joined)).ToLowerInvariant() + ":" + total);
    }

    /// <summary>写前/写后快照：行数 + 我们那个键的有无与值 + 其它键与其它表的结构指纹。</summary>
    public static CursorDbSnapshot Snapshot(string dbPath, string key) =>
        With(dbPath, true, cn =>
        {
            if (!TableExists(cn, ItemTableName))
                throw new AgentEngineException(EngineErrorCode.IoError,
                    $"会话库里没有 {ItemTableName} 表（{dbPath}）：不是 Cursor 的 state.vscdb，拒绝写入。");

            var items = Fingerprint(cn, ItemTableName, null);
            var others = Fingerprint(cn, ItemTableName, key);
            var diskKv = TableExists(cn, DiskKvTableName);
            var headers = TableExists(cn, HeadersTableName);

            var otherTables = new StringBuilder();
            otherTables.Append(DiskKvTableName).Append('=');
            otherTables.Append(diskKv ? Fingerprint(cn, DiskKvTableName, null).Digest : "(不存在)");
            otherTables.Append(';').Append(HeadersTableName).Append('=');
            otherTables.Append(headers ? Fingerprint(cn, HeadersTableName, null).Digest : "(不存在)");

            var value = ReadItemRaw(cn, key);
            return new CursorDbSnapshot
            {
                ItemRows = items.Rows,
                KeyPresent = value is not null,
                KeyValue = value,
                OtherKeysDigest = others.Digest,
                OtherTablesDigest = otherTables.ToString(),
                DiskKvExists = diskKv,
                HeadersExists = headers,
            };
        }, "读快照");

    private static string? ReadItemRaw(SqliteConnection cn, string key)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT value FROM {ItemTableName} WHERE key=@k";
        cmd.Parameters.AddWithValue("@k", key);
        var v = cmd.ExecuteScalar();
        return v switch
        {
            null or DBNull => null,
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>只读打开读一个 ItemTable 键（不存在返回 null；读不了抛 IoError）。</summary>
    public static string? ReadItemValue(string dbPath, string key)
    {
        if (!File.Exists(dbPath)) return null;
        return With(dbPath, true, cn =>
            TableExists(cn, ItemTableName) ? ReadItemRaw(cn, key) : null, "读 ItemTable");
    }

    // ---------------------------------------------------------------- 层 2：写入

    /// <summary>
    /// 备份前先把 WAL 内容并回主库（<c>PRAGMA wal_checkpoint(FULL)</c>，尽力而为）。
    /// 理由：本工具**只备份 state.vscdb 主文件**（-wal / -shm 是可重建的运行时文件，见 Notes），
    /// 不 checkpoint 的话备份里会缺最近提交的内容，还原就不是真正的"逐字节回出厂"。
    /// </summary>
    public static void Checkpoint(string dbPath)
    {
        try
        {
            With(dbPath, false, cn =>
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(FULL);";
                cmd.ExecuteNonQuery();
                return true;
            }, "checkpoint");
        }
        catch { /* 尽力而为：checkpoint 失败不影响写入正确性 */ }
    }

    /// <summary>短事务写一个 ItemTable 键（客户端在跑也允许：WAL 支持并发写）。</summary>
    public static void WriteItemValue(string dbPath, string key, string value) =>
        With(dbPath, false, cn =>
        {
            if (!TableExists(cn, ItemTableName))
                throw new AgentEngineException(EngineErrorCode.IoError,
                    $"会话库里没有 {ItemTableName} 表（{dbPath}）：拒绝写入。");
            using var tx = cn.BeginTransaction();
            using (var cmd = cn.CreateCommand())
            {
                // ItemTable 的 key 是 UNIQUE ON CONFLICT REPLACE：只影响我们这一行。
                cmd.Transaction = tx;
                cmd.CommandText = $"INSERT OR REPLACE INTO {ItemTableName}(key, value) VALUES(@k, @v)";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }, "写 ItemTable");

    /// <summary>逆向撤销：删掉我们那一行（出厂态就是"键不存在"）。</summary>
    public static bool DeleteItemValue(string dbPath, string key) =>
        With(dbPath, false, cn =>
        {
            if (!TableExists(cn, ItemTableName)) return false;
            using var tx = cn.BeginTransaction();
            int affected;
            using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {ItemTableName} WHERE key=@k";
                cmd.Parameters.AddWithValue("@k", key);
                affected = cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return affected > 0;
        }, "删 ItemTable 行");

    /// <summary>
    /// 用整份备份逐字节还原：主文件覆盖 + 删掉 -wal / -shm。
    /// 删 sidecar 的理由：WAL 与 wal-index 属于**被覆盖掉的那个库状态**，留着会把它重新覆盖回来；
    /// 它们是可重建的运行时文件，不属于"出厂原件"。
    /// </summary>
    public static void RestoreBackup(string dbPath, string backupPath)
    {
        SqliteConnection? probe = null;
        try { probe = new SqliteConnection(OpenString(dbPath, true)); }
        catch { /* 打不开也要能覆盖 */ }
        finally { if (probe is not null) { try { SqliteConnection.ClearPool(probe); } catch { } probe.Dispose(); } }

        File.Copy(backupPath, dbPath, true);
        foreach (var side in new[] { dbPath + "-wal", dbPath + "-shm" })
            try { if (File.Exists(side)) File.Delete(side); } catch { /* 尽力而为 */ }
    }

    public static bool WalSidecarExists(string dbPath, string suffix) => File.Exists(dbPath + suffix);

    // ---------------------------------------------------------------- 层 4：会话

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? RulesCategories(JsonElement root, string id, out int? systemPrompt)
    {
        systemPrompt = null;
        if (!root.TryGetProperty("promptTokenBreakdown", out var b) || b.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array) return null;
        int? rules = null;
        foreach (var c in cats.EnumerateArray())
        {
            var cid = Str(c, "id");
            var chars = c.TryGetProperty("characterCount", out var cc) && cc.ValueKind == JsonValueKind.Number
                ? cc.GetInt32()
                : (int?)null;
            if (string.Equals(cid, "rules", StringComparison.Ordinal)) rules = chars;
            if (string.Equals(cid, "system_prompt", StringComparison.Ordinal)) systemPrompt = chars;
        }
        return rules;
    }

    /// <summary>列出库里全部会话（composerData:&lt;id&gt;），按 recency 降序。</summary>
    public static IReadOnlyList<CursorSession> ListSessions(string dbPath) =>
        With(dbPath, true, cn =>
        {
            if (!TableExists(cn, DiskKvTableName))
                throw new AgentEngineException(EngineErrorCode.IoError,
                    $"会话库里没有 {DiskKvTableName} 表（{dbPath}）：不是 Cursor 的 state.vscdb。");

            var headers = new Dictionary<string, (long Created, long Updated, long Recency, int Archived)>(StringComparer.Ordinal);
            if (TableExists(cn, HeadersTableName))
            {
                using var hc = cn.CreateCommand();
                hc.CommandText = $"SELECT composerId, createdAt, lastUpdatedAt, recency, isArchived FROM {HeadersTableName}";
                using var hr = hc.ExecuteReader();
                while (hr.Read())
                {
                    var id = hr.IsDBNull(0) ? "" : hr.GetString(0);
                    if (id.Length == 0) continue;
                    headers[id] = (
                        hr.IsDBNull(1) ? 0 : hr.GetInt64(1),
                        hr.IsDBNull(2) ? 0 : hr.GetInt64(2),
                        hr.IsDBNull(3) ? 0 : hr.GetInt64(3),
                        hr.IsDBNull(4) ? 0 : hr.GetInt32(4));
                }
            }

            var list = new List<CursorSession>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = $"SELECT key, value FROM {DiskKvTableName} WHERE key LIKE @p";
                cmd.Parameters.AddWithValue("@p", ComposerPrefix + "%");
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var key = rd.IsDBNull(0) ? "" : rd.GetString(0);
                    var raw = rd.IsDBNull(1) ? "" : (rd.GetValue(1) as string ?? Encoding.UTF8.GetString((byte[])rd.GetValue(1)));
                    var id = key[ComposerPrefix.Length..];
                    list.Add(Parse(id, raw, headers));
                }
            }

            return (IReadOnlyList<CursorSession>)list
                .OrderByDescending(s => s.Recency)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToArray();
        }, "列会话");

    private static CursorSession Parse(
        string id, string raw,
        IReadOnlyDictionary<string, (long Created, long Updated, long Recency, int Archived)> headers)
    {
        var size = Encoding.UTF8.GetByteCount(raw);
        string? name = null, status = null, model = null, wsId = null;
        var headersCount = 0;
        var archived = false;
        int? rules = null, sysChars = null, total = null, max = null;
        long recency = 0;
        DateTimeOffset? updated = null;

        if (headers.TryGetValue(id, out var h))
        {
            recency = h.Recency != 0 ? h.Recency : h.Updated != 0 ? h.Updated : h.Created;
            archived = h.Archived != 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            name = Str(root, "name");
            status = Str(root, "status");
            if (root.TryGetProperty("modelConfig", out var mc) && mc.ValueKind == JsonValueKind.Object)
                model = Str(mc, "modelName");
            if (root.TryGetProperty("workspaceIdentifier", out var wi) && wi.ValueKind == JsonValueKind.Object)
                wsId = Str(wi, "id");
            if (root.TryGetProperty("fullConversationHeadersOnly", out var fh) && fh.ValueKind == JsonValueKind.Array)
                headersCount = fh.GetArrayLength();
            var last = Long(root, "lastUpdatedAt");
            var created = Long(root, "createdAt");
            if (recency == 0) recency = last != 0 ? last : created;
            var ms = last != 0 ? last : created;
            if (ms > 0) updated = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            rules = RulesCategories(root, "rules", out sysChars);
            if (root.TryGetProperty("promptTokenBreakdown", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                if (b.TryGetProperty("totalUsedTokens", out var t) && t.ValueKind == JsonValueKind.Number) total = t.GetInt32();
                if (b.TryGetProperty("maxTokens", out var m) && m.ValueKind == JsonValueKind.Number) max = m.GetInt32();
            }
        }
        catch (JsonException) { /* 坏行不致命：字段留空，SizeBytes 仍如实 */ }

        return new CursorSession
        {
            Id = id,
            Name = name,
            Status = status,
            ModelName = model,
            WorkspaceId = wsId,
            IsArchived = archived,
            HeaderCount = headersCount,
            SizeBytes = size,
            Recency = recency,
            UpdatedAt = updated,
            HasBreakdown = rules is not null || sysChars is not null,
            RulesChars = rules,
            SystemPromptChars = sysChars,
            TotalUsedTokens = total,
            MaxTokens = max,
        };
    }

    public static CursorSession? FindSession(string dbPath, string composerId) =>
        ListSessions(dbPath).FirstOrDefault(s => string.Equals(s.Id, composerId, StringComparison.Ordinal));

    /// <summary>最近一次**真正跑过模型调用**（有 promptTokenBreakdown）的会话。</summary>
    public static CursorSession? NewestWithBreakdown(IReadOnlyList<CursorSession> sessions) =>
        sessions.Where(s => s.HasBreakdown).OrderByDescending(s => s.Recency).ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// 把一个会话的全部文本抽出来（bubble 正文 + 推理 + composerData 原文），
    /// 供命中数判读（次判据）。文本抽不出来/库读不了 → 抛 IoError，不静默返回空。
    /// </summary>
    public static string SessionText(string dbPath, string composerId)
    {
        var sb = new StringBuilder();
        With(dbPath, true, cn =>
        {
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = $"SELECT value FROM {DiskKvTableName} WHERE key=@k";
                cmd.Parameters.AddWithValue("@k", ComposerPrefix + composerId);
                if (cmd.ExecuteScalar() is string raw) sb.Append(raw).Append('\n');
            }

            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = $"SELECT key, value FROM {DiskKvTableName} WHERE key LIKE @p";
                cmd.Parameters.AddWithValue("@p", BubblePrefix + composerId + ":%");
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var raw = rd.IsDBNull(1) ? "" : (rd.GetValue(1) as string ?? Encoding.UTF8.GetString((byte[])rd.GetValue(1)));
                    sb.Append(ExtractBubbleText(raw)).Append('\n');
                }
            }
            return true;
        }, "抽会话文本");
        return sb.ToString();
    }

    /// <summary>把一个会话导成逐行 JSON（首行 composerData，其后每条 bubble 一行）。</summary>
    public static string ExportSessionJsonl(string dbPath, string composerId)
    {
        var lines = new List<string>();
        With(dbPath, true, cn =>
        {
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = $"SELECT value FROM {DiskKvTableName} WHERE key=@k";
                cmd.Parameters.AddWithValue("@k", ComposerPrefix + composerId);
                if (cmd.ExecuteScalar() is string raw && raw.Length > 0) lines.Add(raw);
            }
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = $"SELECT key, value FROM {DiskKvTableName} WHERE key LIKE @p";
                cmd.Parameters.AddWithValue("@p", BubblePrefix + composerId + ":%");
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var raw = rd.IsDBNull(1) ? "" : (rd.GetValue(1) as string ?? Encoding.UTF8.GetString((byte[])rd.GetValue(1)));
                    if (raw.Length > 0) lines.Add(raw);
                }
            }
            return true;
        }, "导出会话");
        return string.Join("\n", lines);
    }

    /// <summary>一条 bubble 的正文 + 推理（JSON 坏掉时退回原文）。</summary>
    public static string ExtractBubbleText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var parts = new List<string>();
            var text = Str(root, "text");
            if (!string.IsNullOrEmpty(text)) parts.Add(text!);
            else if (root.TryGetProperty("richText", out var rt) && rt.ValueKind == JsonValueKind.String)
            {
                var rich = RichText(rt.GetString() ?? "");
                if (rich.Length > 0) parts.Add(rich);
            }

            if (root.TryGetProperty("allThinkingBlocks", out var tb) && tb.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in tb.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.String) parts.Add(block.GetString() ?? "");
                    else if (block.ValueKind == JsonValueKind.Object)
                    {
                        var s = Str(block, "text") ?? Str(block, "thinking") ?? Str(block, "content");
                        if (!string.IsNullOrEmpty(s)) parts.Add(s!);
                    }
                }
            }
            return string.Join("\n", parts.Where(p => p.Length > 0));
        }
        catch (JsonException) { return raw; }
    }

    private static string RichText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            Walk(doc.RootElement, sb);
            return sb.ToString();
        }
        catch (JsonException) { return ""; }
    }

    private static void Walk(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String &&
                    t.GetString() == "text" && el.TryGetProperty("text", out var txt) &&
                    txt.ValueKind == JsonValueKind.String)
                {
                    sb.Append(txt.GetString());
                    return;
                }
                foreach (var p in el.EnumerateObject()) Walk(p.Value, sb);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Walk(item, sb);
                break;
        }
    }

    /// <summary>载入一条会话的轮次（契约的 SessionDetail）。</summary>
    public static SessionDetail LoadSession(string dbPath, CursorSession session, SessionLoadOptions options,
        IReadOnlyList<Note>? extraNotes = null)
    {
        var notes = new List<Note>(extraNotes ?? Array.Empty<Note>());
        var turns = new List<TurnRecord>();

        var headers = With(dbPath, true, cn =>
        {
            var list = new List<(string BubbleId, int Type, string? Created, bool HasThinking)>();
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = $"SELECT value FROM {DiskKvTableName} WHERE key=@k";
                cmd.Parameters.AddWithValue("@k", ComposerPrefix + session.Id);
                var raw = cmd.ExecuteScalar() as string;
                if (raw is not null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.TryGetProperty("fullConversationHeadersOnly", out var fh) &&
                            fh.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var h in fh.EnumerateArray())
                            {
                                var bid = Str(h, "bubbleId");
                                if (string.IsNullOrEmpty(bid)) continue;
                                var type = h.TryGetProperty("type", out var tv) && tv.ValueKind == JsonValueKind.Number
                                    ? tv.GetInt32()
                                    : 0;
                                var hasThinking = h.TryGetProperty("grouping", out var g) &&
                                                  g.ValueKind == JsonValueKind.Object &&
                                                  g.TryGetProperty("hasThinking", out var ht) &&
                                                  ht.ValueKind == JsonValueKind.True;
                                list.Add((bid!, type, Str(h, "createdAt"), hasThinking));
                            }
                        }
                    }
                    catch (JsonException) { /* 头部坏掉时走下面的兜底扫描 */ }
                }
            }
            return list;
        }, "读会话头");

        if (headers.Count == 0)
        {
            notes.Add(Notes.Info("会话头为空",
                $"会话 {session.Id} 的 fullConversationHeadersOnly 是空的：改为按 bubbleId 的 createdAt 兜底排序。",
                dbPath));
            headers = With(dbPath, true, cn =>
            {
                var list = new List<(string BubbleId, int Type, string? Created, bool HasThinking)>();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $"SELECT key, value FROM {DiskKvTableName} WHERE key LIKE @p";
                cmd.Parameters.AddWithValue("@p", BubblePrefix + session.Id + ":%");
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var key = rd.IsDBNull(0) ? "" : rd.GetString(0);
                    var raw = rd.IsDBNull(1) ? "" : (rd.GetValue(1) as string ?? Encoding.UTF8.GetString((byte[])rd.GetValue(1)));
                    var bid = key[(BubblePrefix + session.Id + ":").Length..];
                    var type = 0;
                    string? created = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        type = doc.RootElement.TryGetProperty("type", out var tv) && tv.ValueKind == JsonValueKind.Number
                            ? tv.GetInt32()
                            : 0;
                        created = Str(doc.RootElement, "createdAt");
                    }
                    catch (JsonException) { /* 坏行跳过字段 */ }
                    list.Add((bid, type, created, false));
                }
                return list.OrderBy(x => x.Created ?? "", StringComparer.Ordinal).ToList();
            }, "兜底列 bubble");
        }

        var limit = options.MaxCharsPerText <= 0 ? 20000 : Math.Max(options.MaxCharsPerText, 256);
        var index = 0;
        foreach (var (bubbleId, type, created, _) in headers)
        {
            var raw = With(dbPath, true, cn =>
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $"SELECT value FROM {DiskKvTableName} WHERE key=@k";
                cmd.Parameters.AddWithValue("@k", BubblePrefix + session.Id + ":" + bubbleId);
                return cmd.ExecuteScalar() as string;
            }, "读 bubble");

            if (raw is null) continue;

            string? text = null, reasoning = null;
            DateTimeOffset? at = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var body = Str(root, "text");
                if (string.IsNullOrEmpty(body) && root.TryGetProperty("richText", out var rt) && rt.ValueKind == JsonValueKind.String)
                    body = RichText(rt.GetString() ?? "");
                text = body;
                if (options.IncludeReasoning &&
                    root.TryGetProperty("allThinkingBlocks", out var tb) && tb.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var block in tb.EnumerateArray())
                    {
                        if (block.ValueKind == JsonValueKind.String) parts.Add(block.GetString() ?? "");
                        else if (block.ValueKind == JsonValueKind.Object)
                        {
                            var s = Str(block, "text") ?? Str(block, "thinking") ?? Str(block, "content");
                            if (!string.IsNullOrEmpty(s)) parts.Add(s!);
                        }
                    }
                    if (parts.Count > 0) reasoning = string.Join("\n", parts);
                }
                var ca = Str(root, "createdAt") ?? created;
                if (ca is not null && DateTimeOffset.TryParse(ca, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out var parsed)) at = parsed;
            }
            catch (JsonException)
            {
                text = raw;
                notes.Add(Notes.Warn("bubble 解析失败", $"bubbleId {bubbleId} 的 JSON 解析失败，按原文摘录。", dbPath));
            }

            var truncated = false;
            if (text is not null && text.Length > limit) { text = text[..limit]; truncated = true; }
            if (reasoning is not null && reasoning.Length > limit) { reasoning = reasoning[..limit]; truncated = true; }

            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(reasoning)) continue;

            turns.Add(new TurnRecord
            {
                Index = index++,
                Role = type switch { 1 => "user", 2 => "assistant", _ => $"type{type}" },
                Text = text,
                ReasoningText = reasoning,
                At = at,
                Truncated = truncated,
            });
        }

        if (options.MaxTurns > 0 && turns.Count > options.MaxTurns)
        {
            var dropped = turns.Count - options.MaxTurns;
            turns = turns.Skip(dropped).ToList();
            notes.Add(Notes.Info("轮次已限制", $"只载入最近 {options.MaxTurns} 条（丢弃 {dropped} 条）。", dbPath));
        }
        for (var i = 0; i < turns.Count; i++) turns[i] = new TurnRecord
        {
            Index = i,
            Role = turns[i].Role,
            Text = turns[i].Text,
            ReasoningText = turns[i].ReasoningText,
            At = turns[i].At,
            Truncated = turns[i].Truncated,
        };

        notes.Add(Notes.Info("解析口径",
            "轮次来自 cursorDiskKV：composerData:<id>.fullConversationHeadersOnly 定顺序与类型（type=1 用户 / 2 助手），" +
            "bubbleId:<composerId>:<bubbleId> 取正文与推理（text / richText / allThinkingBlocks）。", dbPath));

        var info = new SessionInfo
        {
            Id = session.Id,
            Path = dbPath,
            Workspace = session.WorkspaceId,
            SizeBytes = session.SizeBytes,
            ModifiedAt = session.UpdatedAt ?? DateTimeOffset.UnixEpoch,
            TurnCount = turns.Count,
        };

        return new SessionDetail { Info = info, Turns = turns, Notes = notes };
    }
}
