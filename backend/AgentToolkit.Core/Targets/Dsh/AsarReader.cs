using System.Text;
using System.Text.Json;

namespace AgentToolkit.Core.Targets.Dsh;

/// <summary>
/// app.asar 的**只读**访问。端口自 现有源码\breach-dsh\tools\dsh-patch.mjs 的
/// asarIndex / readAsarFile / listAsarDir。
///
/// asar 布局（Electron 标准）：
///   偏移 0..3   uint32 = 4（pickle 头长度字段）
///   偏移 4..7   uint32 = pickle 大小
///   偏移 8..11  uint32 = pickle 大小 + 4
///   偏移 12..15 uint32 = JSON 头长度 jsonSize
///   JSON 头     偏移 16 起，jsonSize 字节（尾部可能补 \0）
///   文件数据    偏移 16 + jsonSize 起，各条目按 node.offset（字符串）+ node.size 定位
///
/// 纪律：只 seek + read，绝不写。本工具不改 app.asar（不改二进制），
/// 这里读 preset 原文只是为了复刻成 profile 补丁层的覆写行。
/// </summary>
public sealed class AsarReader : IDisposable
{
    private const uint MaxHeaderBytes = 512u * 1024 * 1024;

    private readonly FileStream _fs;
    private readonly JsonDocument _header;
    private readonly long _dataOffset;

    /// <summary>asar 文件绝对路径。</summary>
    public string Path { get; }

    private AsarReader(string path, FileStream fs, JsonDocument header, long dataOffset)
    {
        Path = path;
        _fs = fs;
        _header = header;
        _dataOffset = dataOffset;
    }

    /// <summary>打开并解析 asar 头。文件不存在/头损坏 → 抛异常（调用方按 fail-closed 处理）。</summary>
    public static AsarReader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var head = new byte[16];
            ReadAt(fs, head, 0, 16, path);
            var jsonSize = BitConverter.ToUInt32(head, 12);
            if (jsonSize == 0 || jsonSize > MaxHeaderBytes)
                throw new InvalidDataException($"asar 头长度异常：{jsonSize} 字节。");

            var buf = new byte[jsonSize];
            ReadAt(fs, buf, 16, (int)jsonSize, path);
            var json = Encoding.UTF8.GetString(buf).TrimEnd('\0');
            var doc = JsonDocument.Parse(json);
            return new AsarReader(path, fs, doc, 16L + jsonSize);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>列出归档内某个目录的直接子项名（保持 JSON 头里的原始顺序）。</summary>
    public IReadOnlyList<string> ListDir(string innerPath)
    {
        if (!TryNavigate(innerPath, out var node)) return Array.Empty<string>();
        if (!node.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        var names = new List<string>();
        foreach (var p in files.EnumerateObject()) names.Add(p.Name);
        return names;
    }

    /// <summary>读归档内一个文件（UTF-8）。是目录 / unpacked / 不存在 → null。</summary>
    public string? ReadFile(string innerPath)
    {
        if (!TryNavigate(innerPath, out var node)) return null;
        if (node.TryGetProperty("files", out _)) return null;                                            // 目录
        if (node.TryGetProperty("unpacked", out var u) && u.ValueKind == JsonValueKind.True) return null; // 未打包
        if (!node.TryGetProperty("size", out var sizeEl)) return null;
        if (!node.TryGetProperty("offset", out var offsetEl)) return null;

        var size = sizeEl.ValueKind == JsonValueKind.Number ? sizeEl.GetInt64()
            : long.TryParse(sizeEl.GetString(), out var s2) ? s2 : -1;
        var offset = offsetEl.ValueKind == JsonValueKind.Number ? offsetEl.GetInt64()
            : long.TryParse(offsetEl.GetString(), out var o2) ? o2 : -1;
        if (size < 0 || offset < 0 || size > int.MaxValue) return null;

        var buf = new byte[size];
        ReadAt(_fs, buf, _dataOffset + offset, (int)size, Path);
        return Encoding.UTF8.GetString(buf);
    }

    /// <summary>按 "/a/b/c" 逐级进入 JSON 头。</summary>
    private bool TryNavigate(string innerPath, out JsonElement node)
    {
        node = _header.RootElement;
        foreach (var part in innerPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return false;
            if (!files.TryGetProperty(part, out node)) return false;
        }
        return true;
    }

    private static void ReadAt(FileStream fs, byte[] buf, long offset, int count, string path)
    {
        fs.Position = offset;
        var got = 0;
        while (got < count)
        {
            var n = fs.Read(buf, got, count - got);
            if (n <= 0) throw new EndOfStreamException($"asar 读取越界（{path} @ {offset}+{got}）。");
            got += n;
        }
    }

    public void Dispose()
    {
        _header.Dispose();
        _fs.Dispose();
    }
}
