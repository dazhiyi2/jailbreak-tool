using System.Text.RegularExpressions;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Dsh;

/// <summary>受管段之外已存在的同 id row（apply 会 fail-closed，除非 Force）。</summary>
public sealed record AnchorConflict(int Line, string Snippet);

/// <summary>
/// 系统提示词层的文本引擎。端口自 现有源码\breach-dsh\tools\dsh-patch.mjs。
///
/// 纪律（SPEC 第 3 节）：
///   - 只动自己管理的那一段，其余字节原样保留（不做全文件 YAML 重写，保住 !!js / 注释 / 字段顺序）
///   - 锚点唯一才打；失配 fail-closed，绝不模糊匹配、绝不猜位置
///   - marker 判幂等
///   - 先备份、无备份不写
///   - 改完用真实解析器自检（见 JsOrYamlGate），失败拒绝写入
/// </summary>
public static class DshPatcher
{
    public const string TargetId = "system-prompt";
    public const string TargetName = "@deepseek-ai/dsh-system-prompt";
    public const string MarkBegin = "# >>> breach-dsh:system-prompt (managed) >>>";
    public const string MarkEnd = "# <<< breach-dsh:system-prompt (managed) <<<";

    private static readonly Regex TopRowRe =
        new(@"^-[ \t]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FlatIdRe =
        new(@"^\s*id:\s*system-prompt\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NestedIdRe =
        new(@"^\s*-\s*id:\s*system-prompt\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnyIdRe =
        new("id:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PresetRowIdRe =
        new(@"^\s*- id: (preset-[\w-]+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // ---------------------------------------------------------------- marker / 幂等
    public static bool HasManaged(string text) =>
        FileText.Count(text, MarkBegin) > 0 || FileText.Count(text, MarkEnd) > 0;

    /// <summary>两处 marker 各自的出现次数（回读校验用）。</summary>
    public static int MarkerLeftover(string text) =>
        FileText.Count(text, MarkBegin) + FileText.Count(text, MarkEnd);

    /// <summary>受管段原文（含两处 marker）；不存在 → null。</summary>
    public static string? ExtractManagedSegment(string text)
    {
        var b = text.IndexOf(MarkBegin, StringComparison.Ordinal);
        if (b < 0) return null;
        var e = text.IndexOf(MarkEnd, b, StringComparison.Ordinal);
        if (e < 0) return null;
        return text[b..(e + MarkEnd.Length)];
    }

    /// <summary>
    /// 摘除受管段（含段前遗留的连续空行清理），其余字节原样。
    /// 端口自 mjs stripManaged：注意 b 是"段前位置"，清理循环用未移动的下标判断。
    /// </summary>
    public static (string Text, bool Removed) StripManaged(string text)
    {
        var eol = FileText.DetectEol(text);
        var lines = text.Split(new[] { eol }, StringSplitOptions.None);
        var b = Array.FindIndex(lines, l => l.Trim() == MarkBegin);
        if (b < 0) return (text, false);

        var e = Array.FindIndex(lines, b, l => l.Trim() == MarkEnd);
        if (e < 0) e = lines.Length - 1;

        var kept = new List<string>(lines.Length);
        kept.AddRange(lines.Take(b));
        kept.AddRange(lines.Skip(e + 1));

        // 我们插入时会加一个空行；撤销时把段前连续空行收掉一层
        while (b - 1 >= 0 && b - 1 < kept.Count && kept[b - 1] == "" && b - 2 >= 0 && b - 2 < kept.Count && kept[b - 2] == "")
            kept.RemoveAt(b - 1);

        return (string.Join(eol, kept), true);
    }

    // ---------------------------------------------------------------- 冲突检测
    /// <summary>
    /// 受管段之外是否已有 id: system-prompt 的 row。顶层 row 与 insert 列表里的嵌套 row 都算。
    /// （mjs 的 findConflict 只覆盖顶层形态；这里按任务要求把嵌套形态一并纳入，属收紧而非放宽。）
    /// </summary>
    public static IReadOnlyList<AnchorConflict> FindConflicts(string text)
    {
        var eol = FileText.DetectEol(text);
        var lines = text.Split(new[] { eol }, StringSplitOptions.None);

        var starts = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (TopRowRe.IsMatch(lines[i])) starts.Add(i);

        var found = new List<AnchorConflict>();
        var seenLines = new HashSet<int>();

        for (var r = 0; r < starts.Count; r++)
        {
            var s = starts[r];
            var e = r + 1 < starts.Count ? starts[r + 1] : lines.Length;

            // mjs：取该 row 区块里第一条含 id: 的行，再严判是否就是 id: system-prompt
            var firstId = -1;
            for (var i = s; i < e; i++)
                if (AnyIdRe.IsMatch(lines[i])) { firstId = i; break; }

            var hit = -1;
            if (firstId >= 0 && FlatIdRe.IsMatch(lines[firstId])) hit = firstId;
            else
                for (var i = s; i < e; i++)
                    if (NestedIdRe.IsMatch(lines[i])) { hit = i; break; }

            if (hit < 0 || !seenLines.Add(hit)) continue;
            found.Add(new AnchorConflict(hit + 1, string.Join(eol, lines.Skip(s).Take(Math.Min(e - s, 6)))));
        }

        return found;
    }

    /// <summary>受管段里声明的 preset 覆写行 id（verify/状态用）。</summary>
    public static IReadOnlyList<string> PresetRowIds(string text)
    {
        var seg = ExtractManagedSegment(text);
        if (seg is null) return Array.Empty<string>();
        var ids = new List<string>();
        foreach (Match m in PresetRowIdRe.Matches(seg))
            if (!ids.Contains(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
        return ids;
    }

    // ---------------------------------------------------------------- 受管段构造
    public static string YamlQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// YAML 块标量：CRLF 归一、去掉尾部空白、逐行加缩进（空行不加缩进——这是块标量的写法）。
    /// 端口自 mjs blockScalar。
    /// </summary>
    public static string BlockScalar(string text, int indent)
    {
        var pad = new string(' ', indent);
        var trimmed = Regex.Replace(text.Replace("\r\n", "\n"), @"\s+$", "");
        return string.Join("\n", trimmed.Split('\n').Select(l => l.Length > 0 ? pad + l : ""));
    }

    /// <summary>
    /// 受管段：全局 system-prompt row + 若干 preset 覆写行。端口自 mjs buildBlock。
    /// </summary>
    public static string BuildBlock(string prefixText, string suffixText, string eol, IReadOnlyList<PresetOverride> presetRows)
    {
        const string ind = "  ";
        var lines = new List<string>
        {
            MarkBegin,
            "# 全局 system-prompt 段（对 headless/web 等无 preset 遮蔽的 profile 生效）",
            $"- id: {TargetId}",
            $"{ind}name: {YamlQuote(TargetName)}",
            $"{ind}config:",
            // prefix：排在 order 0，第一方 guidance 之前——设定框架
            $"{ind}{ind}personaPrefix: |",
            BlockScalar(prefixText, 6),
        };
        if (!string.IsNullOrWhiteSpace(suffixText))
        {
            // suffix：排在 order 10200，全部第一方 guidance 之后——位置最靠后，效力最强
            lines.Add($"{ind}{ind}personaSuffix: |");
            lines.Add(BlockScalar(suffixText, 6));
        }

        foreach (var r in presetRows)
        {
            lines.Add("");
            lines.Add("# preset 覆写：preset 内的 agent 作用域 persona 会遮蔽全局 persona 段，");
            lines.Add("# 因此必须在这里替换它。plugins 列表从 app.asar 的 preset 原文复刻。");
            lines.Add(r.Text);
        }

        lines.Add(MarkEnd);
        return string.Join(eol, lines);
    }

    // ---------------------------------------------------------------- preset 覆写（fail-closed）
    /// <summary>
    /// 从 asar 的 preset 目录收集覆写行。**读不到就抛**——绝不退化成"只写全局行"。
    ///
    /// 理由（本工具踩过的最贵的坑）：桌面 profile 挂的是 agent preset，
    /// preset 内 agent 作用域的 persona 会**遮蔽**全局 persona 段。只写全局行的结果
    /// 是状态页报 APPLIED、而模型上下文里系统层标记命中 0。所以这里 fail-closed：
    /// 拿不到 preset 就让 apply 失败，不产出"看起来成功"的补丁层。
    /// </summary>
    public static IReadOnlyList<PresetOverride> CollectPresetOverrides(
        string? asarPath, string prefixText, string suffixText, string eol, out string asarUsed)
    {
        asarUsed = asarPath ?? "(未找到 app.asar)";

        if (string.IsNullOrWhiteSpace(asarPath))
            throw new AgentEngineException(EngineErrorCode.SelfCheckFailed,
                "找不到 app.asar，无法读取 preset 原文：拒绝写入。桌面端 agent preset 的 persona 会遮蔽全局 persona，" +
                "只写全局行会得到「状态页 APPLIED、模型上下文命中 0」的假成功。" +
                "请用 DSH_ASAR 或 DSH_ROOT 指定安装根，或确认 <root>\\resources\\app.asar 存在。");
        if (!File.Exists(asarPath))
            throw new AgentEngineException(EngineErrorCode.SelfCheckFailed,
                $"app.asar 不存在：{asarPath}；拒绝写入（fail-closed，不产出只有全局行的补丁层）。");

        List<(string Name, string Text)> presets = new();
        using (var asar = OpenOrThrow(asarPath))
        {
            IReadOnlyList<string> names;
            try
            {
                names = asar.ListDir(PresetOverrides.PresetDir);
            }
            catch (Exception ex)
            {
                throw new AgentEngineException(EngineErrorCode.SelfCheckFailed,
                    $"读取 preset 目录失败：{PresetOverrides.PresetDir} @ {asarPath} — {ex.Message}；拒绝写入。", null, ex);
            }

            var patchNames = names.Where(n => n.EndsWith(".patch.yml", StringComparison.OrdinalIgnoreCase)).ToList();
            if (patchNames.Count == 0)
                throw new AgentEngineException(EngineErrorCode.SelfCheckFailed,
                    $"preset 目录里没有任何 *.patch.yml：{PresetOverrides.PresetDir} @ {asarPath}；拒绝写入。");

            foreach (var n in patchNames)
            {
                string? text;
                try { text = asar.ReadFile($"{PresetOverrides.PresetDir}/{n}"); }
                catch (Exception ex)
                {
                    throw new AgentEngineException(EngineErrorCode.SelfCheckFailed,
                        $"读取 preset 原文失败：{n} @ {asarPath} — {ex.Message}；拒绝写入。", null, ex);
                }
                if (string.IsNullOrEmpty(text)) continue;
                if (!PresetOverrides.HasPersona(text)) continue;
                presets.Add((n, text));
            }
        }
        asarUsed = asarPath;

        if (presets.Count == 0)
            throw new AgentEngineException(EngineErrorCode.SelfCheckFailed,
                $"app.asar 里没有任何 preset 声明了 persona 子插件（{PresetOverrides.PresetDir} @ {asarPath}）：拒绝写入。" +
                "多半是客户端升级改了 preset 结构；此时写出的补丁层会被 agent preset 遮蔽，导致假成功。");

        var rows = new List<PresetOverride>();
        foreach (var (name, text) in presets)
        {
            var row = PresetOverrides.Build(text, prefixText, suffixText, eol);
            if (row is null)
                throw new AgentEngineException(EngineErrorCode.SelfCheckFailed,
                    $"preset {name} 声明了 persona 子插件但无法定位其声明行/子插件段：拒绝写入（锚点失配，不猜位置）。");
            rows.Add(row);
        }
        return rows;
    }

    private static AsarReader OpenOrThrow(string asarPath)
    {
        try { return AsarReader.Open(asarPath); }
        catch (Exception ex)
        {
            throw new AgentEngineException(EngineErrorCode.SelfCheckFailed,
                $"app.asar 打开失败：{asarPath} — {ex.Message}；拒绝写入。", null, ex);
        }
    }
}
