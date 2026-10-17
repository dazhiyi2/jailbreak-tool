using System.Globalization;
using System.Text;

namespace AgentToolkit.Core.Targets.Antigravity;

/// <summary>从 protobuf payload 里抽出来的一段文本，带字段路径（<c>20.3</c> 这种）。</summary>
public sealed record ProtoTextField(string Path, string Text);

/// <summary>
/// Antigravity 的 <c>steps.step_payload</c> 解码器：**只做 wire format 逐字段走，不解释 varint 语义**。
///
/// 口径（与 docs/gemini.md 第六节里已验证过的 Python 实现一致）：
///   · 读 tag（varint）→ 取 wire type 与字段号；路径按 <c>父.子</c> 拼（如 <c>20.3</c>）；
///   · <c>wt=0</c> 读 varint 后跳过、<c>wt=1</c> 跳 8 字节、<c>wt=5</c> 跳 4 字节；
///   · <c>wt=2</c> 读长度前缀取子串：先尝试严格 UTF-8 解码，长度 ≥ 阈值且可打印字符占比 &gt; 0.9 就当作文本；
///     否则**递归下钻**（深度上限 <see cref="MaxDepth"/>，防病态数据炸栈）；
///   · 任何越界 / 非法 varint / 非法 wire type → 立刻停止这一层，**不抛异常**：
///     抽不出来是数据的问题，不是崩溃的理由（负例见自检用例 11）。
///
/// 为什么必须保留字段路径：推理原文（<c>20.3</c>）与答复正文（<c>20.1</c> / <c>20.8</c>）在同一个
/// protobuf 消息里、结构一模一样，只能靠路径区分。不记路径就等于把"模型在想什么"和"模型说了什么"混成一锅。
/// </summary>
public static class AntigravityProto
{
    /// <summary>当作文本的最小字节数（比这短的长度分隔字段一律当嵌套消息继续下钻）。</summary>
    public const int MinTextBytes = 24;

    /// <summary>递归深度上限。</summary>
    public const int MaxDepth = 12;

    /// <summary>单步最多收集多少段文本（病态数据保护）。</summary>
    public const int MaxFields = 200_000;

    /// <summary>可打印字符占比阈值。</summary>
    public const double PrintableRatio = 0.9;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>把一个 step_payload 里的全部文本抽出来（顺序 = wire 里的出现顺序）。</summary>
    public static IReadOnlyList<ProtoTextField> Extract(byte[]? payload,
        int maxDepth = MaxDepth, int minTextBytes = MinTextBytes)
    {
        var list = new List<ProtoTextField>();
        if (payload is null || payload.Length == 0) return list;
        try { Walk(payload, 0, payload.Length, "", 0, maxDepth, minTextBytes, list); }
        catch { /* 深挖过程中的任何意外都不该让整个会话读不出来 */ }
        return list;
    }

    private static void Walk(byte[] buf, int start, int end, string path, int depth,
        int maxDepth, int minTextBytes, List<ProtoTextField> outFields)
    {
        var i = start;
        while (i < end)
        {
            if (outFields.Count >= MaxFields) return;

            if (!TryReadVarint(buf, ref i, end, out var key)) return;
            if (key == 0) return;

            var wire = (int)(key & 7);
            var fieldNo = key >> 3;
            if (fieldNo == 0) return;

            var childPath = path.Length == 0
                ? fieldNo.ToString(CultureInfo.InvariantCulture)
                : path + "." + fieldNo.ToString(CultureInfo.InvariantCulture);

            switch (wire)
            {
                case 0:                                   // varint：读掉就跳过（不解释语义）
                    if (!TryReadVarint(buf, ref i, end, out _)) return;
                    break;

                case 1:                                   // 64 位定长
                    if (i + 8 > end) return;
                    i += 8;
                    break;

                case 5:                                   // 32 位定长
                    if (i + 4 > end) return;
                    i += 4;
                    break;

                case 2:                                   // 长度分隔：文本 or 嵌套消息
                {
                    if (!TryReadVarint(buf, ref i, end, out var rawLen)) return;
                    if (rawLen > (ulong)(end - i)) return;     // 长度越界 = 数据坏了，停止这一层
                    var len = (int)rawLen;
                    var childStart = i;
                    var childEnd = i + len;
                    i = childEnd;

                    if (len >= minTextBytes && TryDecodeText(buf, childStart, childEnd, out var text))
                    {
                        outFields.Add(new ProtoTextField(childPath, text));
                    }
                    else if (depth + 1 <= maxDepth && len > 0)
                    {
                        Walk(buf, childStart, childEnd, childPath, depth + 1, maxDepth, minTextBytes, outFields);
                    }
                    break;
                }

                default:                                  // wt=3/4（group）与未知类型：停止这一层
                    return;
            }
        }
    }

    private static bool TryReadVarint(byte[] buf, ref int i, int end, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (i < end)
        {
            var b = buf[i++];
            if (shift < 64) value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    /// <summary>严格 UTF-8 解码 + 可打印占比判据；两者任一不满足就不是文本（是嵌套消息）。</summary>
    private static bool TryDecodeText(byte[] buf, int start, int end, out string text)
    {
        text = "";
        var len = end - start;
        if (len <= 0) return false;

        string s;
        try { s = StrictUtf8.GetString(buf, start, len); }
        catch { return false; }
        if (s.Length == 0) return false;

        var printable = 0;
        foreach (var ch in s)
            if (ch is '\t' or '\n' or '\r' || (ch >= ' ' && ch != '\u007f')) printable++;

        if ((double)printable / s.Length <= PrintableRatio) return false;
        text = s;
        return true;
    }

    // ---------------------------------------------------------------- 取字段

    /// <summary>某个路径上的全部文本（同一步里同一字段可能出现多次）。</summary>
    public static IReadOnlyList<string> All(IReadOnlyList<ProtoTextField> fields, string path) =>
        fields.Where(f => string.Equals(f.Path, path, StringComparison.Ordinal)).Select(f => f.Text).ToArray();

    /// <summary>某个路径上的第一段文本，没有则 null。</summary>
    public static string? First(IReadOnlyList<ProtoTextField> fields, string path) =>
        fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal))?.Text;

    /// <summary>
    /// 按路径优先级拼接：先取 priority[0] 的全部文本，再补 priority[1]…；
    /// 逐字节相同的段只保留一次（Antigravity 会把同一份答复同时放进 <c>20.1</c> 与 <c>20.8</c>，
    /// 不去重就会把答复原文重复计数一遍）。
    /// </summary>
    public static string Join(IReadOnlyList<ProtoTextField> fields, params string[] priority)
    {
        var seen = new List<string>();
        var sb = new StringBuilder();
        foreach (var p in priority)
        {
            foreach (var piece in All(fields, p))
            {
                if (piece.Length == 0) continue;
                if (seen.Any(x => string.Equals(x, piece, StringComparison.Ordinal))) continue;
                seen.Add(piece);
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(piece);
            }
        }
        return sb.ToString();
    }

    /// <summary>把一次抽取的所有文本按出现顺序拼起来（标记命中统计用）。</summary>
    public static string Flatten(IReadOnlyList<ProtoTextField> fields)
    {
        var sb = new StringBuilder();
        foreach (var f in fields)
        {
            if (f.Text.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(f.Text);
        }
        return sb.ToString();
    }
}
