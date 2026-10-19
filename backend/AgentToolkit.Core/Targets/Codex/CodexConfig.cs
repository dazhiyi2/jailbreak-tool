using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentToolkit.Contracts;

namespace AgentToolkit.Core.Targets.Codex;

/// <summary>config.toml 的文本形态：行、行尾风格、是否以换行结尾。**不做 TOML 重序列化**。</summary>
public sealed class CodexText
{
    public required string Eol { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }
    public required bool EndsWithNewline { get; init; }

    public static CodexText Split(string text)
    {
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var parts = text.Split('\n').ToList();
        var ends = text.EndsWith('\n');
        if (ends && parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        if (eol == "\r\n")
            for (var i = 0; i < parts.Count; i++) parts[i] = parts[i].TrimEnd('\r');
        return new CodexText { Eol = eol, Lines = parts, EndsWithNewline = ends };
    }

    public string Join(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "" : string.Join(Eol, lines) + (EndsWithNewline ? Eol : "");
}

/// <summary>一条顶层键的事实：在哪一行、原文值、去掉引号后的值、是否只出现在表里。</summary>
public sealed class CodexKeyFact
{
    public bool Present { get; init; }
    public int LineIndex { get; init; } = -1;
    public string? RawLine { get; init; }
    public string? RawValue { get; init; }
    public string? Value { get; init; }

    /// <summary>同名键只出现在某个表里（不是顶层）：那不是本工具的键，也不算冲突——但要在 Notes 里说明。</summary>
    public bool InTableOnly { get; init; }
    public int InTableLineIndex { get; init; } = -1;

    /// <summary>顶层出现多次：fail-closed（无法判断哪一行是权威值）。</summary>
    public int Duplicates { get; init; }
}

/// <summary>config.toml 的一次只读解析结论。两个指纹是"别人的东西一个字节都没动"的证据。</summary>
public sealed class CodexConfigSnapshot
{
    public required string Text { get; init; }
    public required string Eol { get; init; }
    public required bool EndsWithNewline { get; init; }
    public required int LineCount { get; init; }

    /// <summary>第一个表头所在行；-1 = 整个文件都是顶层。</summary>
    public required int FirstTableIndex { get; init; }

    public required CodexKeyFact Key { get; init; }

    /// <summary>顶层区（表头之前）里**排除我们的键**之后的 sha256 —— 其它顶层键与注释的指纹。</summary>
    public required string OtherTopDigest { get; init; }

    /// <summary>表区（第一个表头到文件末尾）的 sha256 —— 所有表与表内键的指纹。</summary>
    public required string TablesDigest { get; init; }

    public required int TopLevelLineCount { get; init; }
    public required int TableLineCount { get; init; }
}

/// <summary>
/// <c>CODEX_HOME\config.toml</c> 的**纯文本行编辑**。
///
/// 为什么不是 TOML 解析 + 重序列化：重序列化会改格式（引号风格、空行、键顺序、数组换行），
/// 等于动到别人的键；这个文件是 provider 管理器（CCSwitch 之类）的权威，用户也手改。
/// 所以这里只做两件事：在第一个表头**之前**插一行、删掉自己那一行，其余字节原样不动，
/// 写后用「删掉自己那一行 == 原文件」的逐字节往返来证明。
/// </summary>
public static class CodexConfig
{
    public const string FileName = "config.toml";

    /// <summary>Codex 官方配置键：把一份 .md 挂成模型的指令文件（gpt-instruct 用的就是这个）。</summary>
    public const string KeyName = "model_instructions_file";

    /// <summary>表头行：整行只有一个 <c>[table]</c> / <c>[[table]]</c>（值数组元素行不会长这样）。</summary>
    private static readonly Regex TableHeaderRe = new(
        @"^\s*\[\[?[^\[\]]+\]\]?\s*(?:#.*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OurKeyRe = new(
        @"^\s*" + Regex.Escape(KeyName) + @"\s*=\s*(?<v>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string PathOf(string configHome) => Path.Combine(configHome, FileName);

    // ---------------------------------------------------------------- 解析

    public static CodexConfigSnapshot Parse(string text)
    {
        var doc = CodexText.Split(text);
        var lines = doc.Lines;

        var firstTable = -1;
        for (var i = 0; i < lines.Count; i++)
            if (TableHeaderRe.IsMatch(lines[i])) { firstTable = i; break; }

        var topEnd = firstTable < 0 ? lines.Count : firstTable;

        var keyIndex = -1;
        var duplicates = 0;
        var inTable = -1;
        string? rawValue = null, value = null, rawLine = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var m = OurKeyRe.Match(lines[i]);
            if (!m.Success) continue;
            if (i < topEnd)
            {
                if (keyIndex < 0)
                {
                    keyIndex = i;
                    rawLine = lines[i];
                    rawValue = StripComment(m.Groups["v"].Value).Trim();
                    value = Unquote(rawValue);
                }
                else duplicates++;
            }
            else if (inTable < 0) inTable = i;
        }

        var top = new List<string>();
        for (var i = 0; i < topEnd; i++)
        {
            if (i == keyIndex) continue;
            top.Add(lines[i]);
        }
        var tables = new List<string>();
        for (var i = topEnd; i < lines.Count; i++) tables.Add(lines[i]);

        return new CodexConfigSnapshot
        {
            Text = text,
            Eol = doc.Eol,
            EndsWithNewline = doc.EndsWithNewline,
            LineCount = lines.Count,
            FirstTableIndex = firstTable,
            Key = new CodexKeyFact
            {
                Present = keyIndex >= 0,
                LineIndex = keyIndex,
                RawLine = rawLine,
                RawValue = rawValue,
                Value = value,
                InTableOnly = keyIndex < 0 && inTable >= 0,
                InTableLineIndex = inTable,
                Duplicates = duplicates,
            },
            OtherTopDigest = Sha(string.Join("\n", top)),
            TablesDigest = Sha(string.Join("\n", tables)),
            TopLevelLineCount = topEnd,
            TableLineCount = lines.Count - topEnd,
        };
    }

    /// <summary>在第一个表头之前插入我们的键（没有表就追加到末尾）。纯函数。</summary>
    public static string InsertKey(string text, string absolutePath)
    {
        var doc = CodexText.Split(text);
        var snap = Parse(text);
        if (snap.Key.Present)
            throw new AgentEngineException(EngineErrorCode.VerifyProblems,
                $"{KeyName} 已存在（第 {snap.Key.LineIndex + 1} 行）：拒绝重复插入。");

        var lines = doc.Lines.ToList();
        var at = snap.FirstTableIndex < 0 ? lines.Count : snap.FirstTableIndex;
        lines.Insert(at, KeyName + " = " + TomlLiteral(absolutePath));
        return doc.Join(lines);
    }

    /// <summary>删掉我们那一行（其它行一个字节不动）。纯函数。</summary>
    public static string RemoveKey(string text, int lineIndex)
    {
        var doc = CodexText.Split(text);
        if (lineIndex < 0 || lineIndex >= doc.Lines.Count)
            throw new AgentEngineException(EngineErrorCode.VerifyProblems,
                $"行号越界：{lineIndex}（共 {doc.Lines.Count} 行）。");
        var lines = doc.Lines.ToList();
        lines.RemoveAt(lineIndex);
        return doc.Join(lines);
    }

    /// <summary>往返证明：把插入后的文件里我们那一行删掉，必须逐字节等于插入前的原文。</summary>
    public static bool InsertRoundTrips(string original, string inserted)
    {
        var snap = Parse(inserted);
        if (!snap.Key.Present || snap.Key.Duplicates != 0) return false;
        return string.Equals(RemoveKey(inserted, snap.Key.LineIndex), original, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 值

    /// <summary>
    /// 写成 TOML 字面量。Windows 路径一律用**单引号字面量**（反斜杠不转义），
    /// 只有路径里真的含单引号或控制字符时才退回基本字符串（\\ 转义）。
    /// </summary>
    public static string TomlLiteral(string value)
    {
        var needBasic = value.Contains('\'') || value.Any(c => c < 0x20 || c == 0x7F);
        if (!needBasic) return "'" + value + "'";

        var sb = new StringBuilder("\"");
        foreach (var c in value)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c < 0x20 || c == 0x7F ? $"\\u{(int)c:X4}" : c.ToString(),
            });
        return sb.Append('"').ToString();
    }

    /// <summary>去掉引号并解转义。裸值按原样返回（调用方在 Notes 里说明它不是合法 TOML 字符串）。</summary>
    public static string? Unquote(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim();
        if (s.Length == 0) return null;
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s[1..^1];
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return UnescapeBasic(s[1..^1]);
        return s;
    }

    private static string UnescapeBasic(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u': sb.Append(Unicode(s, ref i, 4)); break;
                case 'U': sb.Append(Unicode(s, ref i, 8)); break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    private static string Unicode(string s, ref int i, int digits)
    {
        if (i + digits >= s.Length) return "\\u";
        var hex = s.Substring(i + 1, digits);
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var cp) || cp < 0 || cp > 0x10FFFF)
            return "\\u";
        i += digits;
        try { return char.ConvertFromUtf32(cp); } catch { return "\\u"; }
    }

    /// <summary>把行尾注释摘掉（引号内的 # 不算注释）。</summary>
    public static string StripComment(string value)
    {
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '#') return value[..i];
        }
        return value;
    }

    /// <summary>读任意一行 <c>KEY = value</c> 的标量（表内也算）——用来取 CODEX_CLI_PATH / 应用版本这类只读事实。</summary>
    public static string? ReadScalar(string text, string key)
    {
        var re = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=\s*(?<v>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        foreach (var line in CodexText.Split(text).Lines)
        {
            var m = re.Match(line);
            if (!m.Success) continue;
            var v = Unquote(StripComment(m.Groups["v"].Value).Trim());
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    public static string Sha(string text) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();
}
