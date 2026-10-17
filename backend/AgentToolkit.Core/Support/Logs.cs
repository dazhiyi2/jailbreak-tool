using System.Text;
using ZstdSharp;

namespace AgentToolkit.Core.Support;

/// <summary>
/// 多帧 zstd 解码。客户端每追加一条记录写一帧，所以整个文件是**多帧拼接**的；
/// 只解第一帧会静默丢内容。做法：扫帧魔数逐帧解压，边界不确定时向后取到下一帧或文件尾。
/// </summary>
public static class ZstdFrames
{
    private static readonly byte[] Magic = { 0x28, 0xB5, 0x2F, 0xFD };
    private const long MaxOutputBytes = 512L * 1024 * 1024;

    public static string DecompressAll(byte[] buf)
    {
        var parts = new List<string>();
        long total = 0;

        var searchFrom = 0;
        var first = IndexOf(buf, Magic, searchFrom);
        if (first < 0) throw new InvalidDataException("不是 zstd 数据：未找到帧魔数 0x28B52FFD。");

        var off = first;
        while (off >= 0 && off < buf.Length)
        {
            var nextMagic = IndexOf(buf, Magic, off + Magic.Length);
            var ends = nextMagic < 0 ? new[] { buf.Length } : new[] { nextMagic, buf.Length };

            byte[]? decoded = null;
            foreach (var end in ends)
            {
                var slice = new byte[end - off];
                Buffer.BlockCopy(buf, off, slice, 0, slice.Length);
                try { decoded = Unwrap(slice); break; }
                catch { /* 换下一个候选边界（魔数可能出现在压缩数据内部） */ }
            }
            if (decoded is null) break;

            total += decoded.Length;
            if (total > MaxOutputBytes) throw new InvalidDataException("解压结果超出上限，疑似帧边界判断错误。");
            parts.Add(Encoding.UTF8.GetString(decoded));

            if (nextMagic < 0) break;
            off = nextMagic;
        }

        if (parts.Count == 0) throw new InvalidDataException("zstd 多帧解码失败：没有任何一帧解压成功。");
        return string.Join("\n", parts);
    }

    private static byte[] Unwrap(byte[] frame)
    {
        using var d = new Decompressor();
        var decoded = d.Unwrap(frame);
        return decoded.ToArray();
    }

    public static bool LooksLikeZstd(byte[] buf) => IndexOf(buf, Magic, 0) == 0;

    private static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        if (start < 0) start = 0;
        for (var i = start; i <= hay.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}

/// <summary>jsonl 读取：统一按 UTF-8 解码（本机默认 ANSI 会把中文读成乱码）。</summary>
public static class JsonlReader
{
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 3 && ZstdFrames.LooksLikeZstd(bytes)) return ZstdFrames.DecompressAll(bytes);
        return new UTF8Encoding(false).GetString(bytes);
    }

    public static IEnumerable<(int LineNo, string Text)> Lines(string text)
    {
        var no = 0;
        foreach (var raw in text.Split('\n'))
        {
            no++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            yield return (no, line);
        }
    }

    /// <summary>逐行 JSON 解析；解析失败的行跳过并计数，不因为一行坏数据丢掉整个会话。</summary>
    public static (List<System.Text.Json.JsonDocument> docs, int badLines) Parse(string text)
    {
        var docs = new List<System.Text.Json.JsonDocument>();
        var bad = 0;
        foreach (var (_, line) in Lines(text))
        {
            try { docs.Add(System.Text.Json.JsonDocument.Parse(line)); }
            catch { bad++; }
        }
        return (docs, bad);
    }

    /// <summary>递归取出一个 JSON 节点下所有名为 key 的字符串值。</summary>
    public static IEnumerable<string> StringsByKey(System.Text.Json.JsonElement el, string key)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (p.NameEquals(key) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    yield return p.Value.GetString() ?? "";
                foreach (var nested in StringsByKey(p.Value, key)) yield return nested;
            }
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                foreach (var nested in StringsByKey(item, key)) yield return nested;
        }
    }

    public static System.Text.Json.JsonElement? FindFirst(System.Text.Json.JsonElement el, string key)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (p.NameEquals(key)) return p.Value;
                var nested = FindFirst(p.Value, key);
                if (nested is not null) return nested;
            }
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var nested = FindFirst(item, key);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
}
