using System.Diagnostics;
using System.Text;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Zcode;

/// <summary>词法配平结果。</summary>
public sealed class JsBalanceResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public int ErrorOffset { get; init; } = -1;
    public int ErrorLine { get; init; }
    /// <summary>字符串里出现裸换行、模板/块注释未闭合等"软问题"的计数（配平本身仍可判定）。</summary>
    public int SoftIssues { get; init; }
    /// <summary>Token 流里的括号指纹：<c>( ) [ ] { }</c> 六个计数 + 最大深度。用于与原文做差分比对。</summary>
    public int[] Fingerprint { get; init; } = Array.Empty<int>();
}

/// <summary>补丁后自检的完整结论。</summary>
public sealed class JsGateOutcome
{
    public bool Ok { get; init; }
    public bool Balanced { get; init; }
    public bool SizeOk { get; init; }
    public bool? NodeCheckRan { get; init; }
    public bool? NodeCheckOk { get; init; }
    public string? NodePath { get; init; }
    public string Detail { get; init; } = "";
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();
}

/// <summary>
/// 改完自检（SPEC 第 3 节第 6 条）：
///   1. 词法级配平：对 .cjs 做字符串 / 注释 / 模板字面量感知的括号配平检查
///      （' " ` 三种引号、// 与 /* */ 注释、反斜杠转义、正则字面量用启发式跳过）。
///   2. 体积区间：替换后体积不得小于原文的 10%（也不得大于 10 倍）。
///   3. 附加校验：本机有 node.exe 时跑 <c>node --check</c>（30 秒超时）；找不到 node 就跳过并说明。
/// 任一检查失败 → 抛 SelfCheckFailed，且调用方不得写盘。
/// </summary>
public static class JsSyntaxGate
{
    private const double MinSizeRatio = 0.10;
    private const double MaxSizeRatio = 10.0;
    private const int NodeTimeoutMs = 30_000;

    private enum Mode { Code, Template }

    /// <summary>单文本的词法配平检查。扫描器是容错的：字符串里的裸换行按"字符串到此结束"处理并记软问题，
    /// 因为 minified bundle 里一个误判的引号会级联成假的不配平，那比漏报更危险（漏报由 node --check 兜底）。</summary>
    public static JsBalanceResult CheckBalance(string text)
    {
        var stack = new Stack<(char Ch, int Pos)>();
        var counts = new int[6];
        var maxDepth = 0;
        var soft = 0;
        var mode = Mode.Code;
        var lastSig = '\0';
        string? lastWord = null;
        var i = 0;
        var n = text.Length;

        int Idx(char c) => c switch { '(' => 0, ')' => 1, '[' => 2, ']' => 3, '{' => 4, _ => 5 };

        JsBalanceResult Fail(string msg, int at) => new()
        {
            Ok = false, Error = msg, ErrorOffset = at, ErrorLine = LineOf(text, at), SoftIssues = soft,
            Fingerprint = counts.Concat(new[] { maxDepth }).ToArray(),
        };

        while (i < n)
        {
            if (mode == Mode.Template)
            {
                var c = text[i];
                if (c == '\\') { i += 2; continue; }
                if (c == '`') { mode = Mode.Code; lastSig = '`'; lastWord = null; i++; continue; }
                if (c == '$' && i + 1 < n && text[i + 1] == '{')
                {
                    stack.Push(('T', i));
                    counts[Idx('{')]++;
                    maxDepth = Math.Max(maxDepth, stack.Count);
                    mode = Mode.Code; lastSig = '{'; lastWord = null; i += 2; continue;
                }
                i++; continue;
            }

            var ch = text[i];
            switch (ch)
            {
                case '\'':
                case '"':
                    {
                        var end = SkipQuoted(text, i, ch, out var softHit);
                        if (softHit) soft++;
                        i = end; lastSig = ch; lastWord = null;
                        continue;
                    }
                case '`':
                    mode = Mode.Template; i++; continue;
                case '/':
                    if (i + 1 < n && text[i + 1] == '/') { i = SkipLineComment(text, i); continue; }
                    if (i + 1 < n && text[i + 1] == '*')
                    {
                        var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (e < 0) { soft++; i = n; continue; }
                        i = e + 2; continue;
                    }
                    if (RegexAllowed(lastSig, lastWord) && TrySkipRegex(text, i, out var reEnd))
                    {
                        i = reEnd; lastSig = ')'; lastWord = null; continue;
                    }
                    i++; lastSig = '/'; lastWord = null; continue;
                case '(':
                case '[':
                case '{':
                    stack.Push((ch, i));
                    counts[Idx(ch)]++;
                    maxDepth = Math.Max(maxDepth, stack.Count);
                    i++; lastSig = ch; lastWord = null; continue;
                case ')':
                case ']':
                case '}':
                    {
                        if (stack.Count == 0) return Fail($"多余的 '{ch}'（没有与之配对的左括号）", i);
                        var top = stack.Pop();
                        if (top.Ch == 'T')
                        {
                            if (ch != '}') return Fail($"模板表达式未闭合：期望 '}}'，实际遇到 '{ch}'", i);
                            counts[Idx('}')]++;
                            mode = Mode.Template; i++; lastSig = '}'; lastWord = null; continue;
                        }
                        var want = ch switch { ')' => '(', ']' => '[', _ => '{' };
                        if (top.Ch != want)
                            return Fail($"括号不配平：'{top.Ch}'（偏移 {top.Pos}）与 '{ch}' 交叉", i);
                        counts[Idx(ch)]++;
                        i++; lastSig = ch; lastWord = null; continue;
                    }
                case ' ':
                case '\t':
                case '\r':
                case '\n':
                    i++; continue;
                default:
                    if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '$')
                    {
                        var s = i;
                        while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '$')) i++;
                        lastWord = text[s..i];
                        lastSig = text[i - 1];
                        continue;
                    }
                    i++; lastSig = ch; lastWord = null; continue;
            }
        }

        if (mode == Mode.Template) soft++;
        if (stack.Count > 0)
        {
            var unclosed = string.Join(", ", stack.Take(8).Select(x => $"'{x.Ch}'@{x.Pos}"));
            return Fail($"文件结束仍有未闭合的括号：{unclosed}", stack.Peek().Pos);
        }

        return new JsBalanceResult
        {
            Ok = true, SoftIssues = soft, Fingerprint = counts.Concat(new[] { maxDepth }).ToArray(),
        };
    }

    public static bool Balanced(string text) => CheckBalance(text).Ok;

    /// <summary>替换后自检。originalText = 改之前的内容（用于差分兜底），candidateText = 将要写盘的内容。</summary>
    public static JsGateOutcome VerifyAfterPatch(string originalText, string candidateText)
    {
        var notes = new List<Note>();
        var original = CheckBalance(originalText);
        var candidate = CheckBalance(candidateText);

        var structural = original.Fingerprint.Length == candidate.Fingerprint.Length &&
                         original.Fingerprint.SequenceEqual(candidate.Fingerprint);

        var balanced = candidate.Ok;
        if (!balanced && !original.Ok && structural)
        {
            // 原文自己就"不配平" → 判定为扫描器在这份 bundle 上误报（minified 里的正则/引号启发式），
            // 改用与原文的结构指纹差分：替换只动字符串内容，配料结构必须完全一致。
            balanced = true;
            notes.Add(Notes.Warn("词法扫描器在原文上即报不配平",
                $"已退化为与原文的结构指纹比对（六个括号计数 + 最大深度一致）通过。" +
                $"原文报错：{original.Error}；候选报错：{candidate.Error}。node --check 为最终判据。", "zcode/selfcheck"));
        }
        else if (balanced && candidate.SoftIssues > original.SoftIssues)
        {
            notes.Add(Notes.Warn("词法软问题增加",
                $"字符串裸换行/未闭合模板的计数由 {original.SoftIssues} 增至 {candidate.SoftIssues}，可能是替换引入了裸引号。", "zcode/selfcheck"));
        }

        var beforeBytes = FileText.Bytes(originalText);
        var afterBytes = FileText.Bytes(candidateText);
        var sizeOk = afterBytes >= Math.Max(1024, (long)(beforeBytes * MinSizeRatio)) &&
                     afterBytes <= Math.Max(1024, (long)(beforeBytes * MaxSizeRatio));

        string? nodePath = null;
        bool? nodeRan = null;
        bool? nodeOk = null;
        string nodeDetail = "";
        try
        {
            nodePath = FindNode();
            if (nodePath is null)
            {
                nodeRan = false;
                notes.Add(Notes.Info("跳过 node --check",
                    "本机（PATH / %ProgramFiles%\\nodejs / where 三处）都没有 node.exe，只做了词法配平与体积区间检查。",
                    "zcode/selfcheck"));
            }
            else
            {
                nodeRan = true;
                var (code, output) = RunNodeCheck(nodePath, candidateText);
                nodeOk = code == 0;
                nodeDetail = code == 0
                    ? $"node --check 通过（{nodePath}）"
                    : $"node --check 失败（exit {code}）：{Tail(output, 600)}";
                if (code != 0) notes.Add(Notes.Error("node --check 失败", nodeDetail, "zcode/selfcheck"));
                else notes.Add(Notes.Info("node --check 通过", nodeDetail, "zcode/selfcheck"));
            }
        }
        catch (Exception ex)
        {
            nodeRan = false;
            notes.Add(Notes.Warn("node --check 未能执行", $"{ex.GetType().Name}: {ex.Message}", "zcode/selfcheck"));
        }

        var ok = balanced && sizeOk && nodeOk != false;
        var detail = new StringBuilder();
        detail.Append(balanced ? "词法配平通过" : $"词法配平失败：{candidate.Error}（第 {candidate.ErrorLine} 行，偏移 {candidate.ErrorOffset}）");
        detail.Append("；");
        detail.Append(sizeOk
            ? $"体积 {FileText.Human(beforeBytes)} → {FileText.Human(afterBytes)}（在合理区间）"
            : $"体积异常：{FileText.Human(beforeBytes)} → {FileText.Human(afterBytes)}，拒绝写入");
        detail.Append("；");
        detail.Append(nodeRan == true ? nodeDetail : "node --check 已跳过");

        return new JsGateOutcome
        {
            Ok = ok,
            Balanced = balanced,
            SizeOk = sizeOk,
            NodeCheckRan = nodeRan,
            NodeCheckOk = nodeOk,
            NodePath = nodePath,
            Detail = detail.ToString(),
            Notes = notes,
        };
    }

    /// <summary>节点查找：PATH → %ProgramFiles%\nodejs → %LOCALAPPDATA%\Programs\nodejs → where node。</summary>
    public static string? FindNode()
    {
        var exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* 路径非法，跳过 */ }
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var p in new[]
                 {
                     string.IsNullOrEmpty(pf) ? null : Path.Combine(pf, "nodejs", exe),
                     string.IsNullOrEmpty(lad) ? null : Path.Combine(lad, "Programs", "nodejs", exe),
                 })
        {
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        }

        try
        {
            var where = OperatingSystem.IsWindows() ? "where.exe" : "which";
            var (code, output) = RunProcess(where, "node", 5000);
            if (code == 0)
            {
                foreach (var line in output.Split('\n'))
                {
                    var cand = line.Trim().TrimEnd('\r');
                    if (cand.Length > 0 && File.Exists(cand)) return cand;
                }
            }
        }
        catch { /* 尽力而为 */ }

        return null;
    }

    private static (int Code, string Output) RunNodeCheck(string nodePath, string candidateText)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"zcode-selfcheck-{Guid.NewGuid():N}.cjs");
        FileText.Write(tmp, candidateText);
        try
        {
            return RunProcess(nodePath, $"--check \"{tmp}\"", NodeTimeoutMs);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 尽力而为 */ }
        }
    }

    private static (int Code, string Output) RunProcess(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { /* 尽力而为 */ }
            return (-1, $"超时（>{timeoutMs / 1000} 秒）");
        }
        var text = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
        return (p.ExitCode, text);
    }

    // ---------------------------------------------------------------- 词法扫描内部件

    private static int SkipQuoted(string text, int i, char quote, out bool soft)
    {
        soft = false;
        i++;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c == quote) return i + 1;
            if (c == '\n') { soft = true; return i + 1; }
            i++;
        }
        soft = true;
        return text.Length;
    }

    private static int SkipLineComment(string text, int i)
    {
        var e = text.IndexOf('\n', i + 2);
        return e < 0 ? text.Length : e + 1;
    }

    /// <summary>正则字面量启发式：只有出现在"表达式位置"的 / 才当正则扫。</summary>
    private static bool RegexAllowed(char lastSig, string? lastWord)
    {
        if (lastSig == '\0') return true;
        if ("(,=:[!&|?{};+-*%^~<>".IndexOf(lastSig) >= 0) return true;
        return lastWord is "return" or "typeof" or "instanceof" or "case" or "in" or "of" or "new"
            or "delete" or "void" or "do" or "else" or "yield" or "await" or "throw";
    }

    private static bool TrySkipRegex(string text, int i, out int end)
    {
        end = i;
        var j = i + 1;
        var inClass = false;
        var n = text.Length;
        while (j < n)
        {
            var c = text[j];
            if (c == '\\') { j += 2; continue; }
            if (c == '\n') return false;
            if (inClass)
            {
                if (c == ']') inClass = false;
                j++; continue;
            }
            if (c == '[') { inClass = true; j++; continue; }
            if (c == '/')
            {
                j++;
                while (j < n && char.IsLetter(text[j])) j++;   // flags
                end = j;
                return true;
            }
            j++;
        }
        return false;
    }

    private static int LineOf(string text, int offset)
    {
        if (offset < 0) return 0;
        var line = 1;
        var max = Math.Min(offset, text.Length);
        for (var i = 0; i < max; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}
