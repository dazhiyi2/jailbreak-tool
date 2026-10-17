using System.Text.RegularExpressions;

namespace AgentToolkit.Core.Targets.Dsh;

/// <summary>一条 preset 覆写行：preset 声明行的 id + 去缩进后的 row 原文。</summary>
public sealed record PresetOverride(string Id, string Text);

/// <summary>
/// preset 覆写的**纯文本变换**。端口自 现有源码\breach-dsh\tools\dsh-patch.mjs 的
/// buildPresetOverride / collectPresetOverrides。
///
/// 为什么不能做 YAML 往返：preset 原文里有 `!!js` 表达式、注释、字段顺序，
/// 一旦 load→dump 就会丢掉它们，还会把 plugins 列表重排——那是把工具列表改坏。
/// 所以做法是：定位声明行 → 该 row 整体去缩进到第 0 列 → 找到其中的 persona 子插件段
/// → 整段替换成我们的 prefix/suffix 块标量 → 其余条目逐字节保留。
/// </summary>
public static class PresetOverrides
{
    /// <summary>asar 内的 preset 目录（实测 v44.0.0）。</summary>
    public const string PresetDir = "/dsh/node_modules/@deepseek-ai/dsh-web-app/presets";

    private static readonly Regex DeclRe =
        new(@"^\s*- id: preset-[A-Za-z0-9_-]+\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PersonaRe =
        new(@"^\s*- id: persona\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RowStartRe =
        new(@"^(\s*)- \S", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EolRe =
        new("\r?\n", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>该 preset 原文里是否声明了 persona 子插件（collectPresetOverrides 的前置过滤）。</summary>
    public static bool HasPersona(string presetText) =>
        EolRe.Split(presetText).Any(l => PersonaRe.IsMatch(l));

    /// <summary>
    /// 把一份 preset 原文转成一条覆写行；没有 preset 声明行或没有 persona 子插件 → null。
    /// </summary>
    public static PresetOverride? Build(string presetText, string prefixText, string suffixText, string eol)
    {
        var lines = EolRe.Split(presetText);
        var declIdx = Array.FindIndex(lines, l => DeclRe.IsMatch(l));
        if (declIdx < 0) return null;

        var baseIndent = LeadingWhitespace(lines[declIdx]);
        var declId = DeclId(lines[declIdx]);

        // row 的结束：下一个缩进 <= base 的条目行
        var end = lines.Length;
        for (var i = declIdx + 1; i < lines.Length; i++)
        {
            var m = RowStartRe.Match(lines[i]);
            if (m.Success && m.Groups[1].Length <= baseIndent) { end = i; break; }
        }

        var pad = new string(' ', baseIndent);
        var row = new List<string>();
        for (var i = declIdx; i < end; i++)
            row.Add(lines[i].StartsWith(pad, StringComparison.Ordinal) ? lines[i][baseIndent..] : lines[i]);

        var pIdx = row.FindIndex(l => PersonaRe.IsMatch(l));
        if (pIdx < 0) return null;

        var pi = LeadingWhitespace(row[pIdx]);
        var pEnd = row.Count;
        for (var i = pIdx + 1; i < row.Count; i++)
        {
            var m = RowStartRe.Match(row[i]);
            if (m.Success && m.Groups[1].Length <= pi) { pEnd = i; break; }
        }

        var si = new string(' ', pi + 2);
        var ci = new string(' ', pi + 4);
        var personaRow = new List<string>
        {
            new string(' ', pi) + "- id: persona",
            si + "name: '@deepseek-ai/dsh-persona'",
            si + "config:",
            ci + "prefix: |",
            DshPatcher.BlockScalar(prefixText, pi + 6),
        };
        if (!string.IsNullOrWhiteSpace(suffixText))
        {
            personaRow.Add(ci + "suffix: |");
            personaRow.Add(DshPatcher.BlockScalar(suffixText, pi + 6));
        }

        var outLines = new List<string>(row.Count + personaRow.Count);
        outLines.AddRange(row.Take(pIdx));
        outLines.AddRange(personaRow);
        outLines.AddRange(row.Skip(pEnd));
        return new PresetOverride(declId, string.Join(eol, outLines));
    }

    /// <summary>声明行 `    - id: preset-cordis` → `preset-cordis`（端口自 mjs 的 slice/split/join）。</summary>
    private static string DeclId(string declLine)
    {
        var body = declLine.Trim()[2..].Trim();               // "id: preset-cordis"
        var parts = body.Split(':');
        return string.Join(":", parts.Skip(1)).Trim();
    }

    /// <summary>JS `l.match(/^\s*/)[0].length` 的等价物（只按行内空格/制表符算）。</summary>
    public static int LeadingWhitespace(string line)
    {
        var n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return n;
    }
}
