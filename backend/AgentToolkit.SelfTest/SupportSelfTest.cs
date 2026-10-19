using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.SelfTest;

/// <summary>
/// 共享支持层的回归自检。用**真实数据只读**验证多帧 zstd、jsonl、差异、原子写与进程探测，
/// 不写任何真实客户端文件。TargetId 用 "support"，可单独跑：--target support
/// </summary>
public sealed class SupportSelfTest : ITargetSelfTest
{
    public string TargetId => "support";
    public string Name => "共享支持层（多帧 zstd / jsonl / 差异 / 原子写 / 进程探测）";

    public SelfTestResult Run(SelfTestContext ctx)
    {
        var r = new SelfTestResult { Name = Name };
        var sandbox = ctx.MakeSandbox("support");
        // 真实客户端的只读快照（全部 7 个路径）：自检结束时变化项必须为 0。
        var realSnapshot = DualScheme.CaptureReal("support");

        // ---- 1. 纯文本纪律：原子写 + sha256 + UTF-8 无 BOM ----
        r.Step("文本与哈希");
        var file = Path.Combine(sandbox, "sample.txt");
        FileText.Write(file, "第一行\n第二行\n");
        r.Check(FileText.Exists(file), "原子写建出文件");
        var bytes = File.ReadAllBytes(file);
        r.Check(bytes.Length > 3 && bytes[0] != 0xEF, "UTF-8 未写 BOM");
        r.Check(FileText.Count(FileText.Read(file), "第二行") == 1, "UTF-8 中文往返一致");
        r.Check(FileText.Sha256File(file) == FileText.Sha256Text(FileText.Read(file)), "文件哈希与文本哈希一致");
        FileText.Write(file, "换了内容");
        r.Check(FileText.Read(file) == "换了内容", "覆盖写生效（File.Replace 路径）");

        // ---- 2. 差异构造 ----
        r.Step("差异");
        var (lines, added, removed) = DiffBuilder.Build("a\nb\nc\n", "a\nB\nc\nd\n");
        r.Check(lines.Count > 0, $"产出 {lines.Count} 行差异");
        r.Check(added == 2 && removed == 1, $"新增 {added} 行 / 删除 {removed} 行（期望 2 / 1）");
        r.Check(lines.Any(l => l.Kind == DiffKind.Added) && lines.Any(l => l.Kind == DiffKind.Removed), "同时含新增与删除");

        // ---- 3. 多帧 zstd：真实 DSH 会话日志（只读） ----
        r.Step("多帧 zstd（真实 DSH 会话日志，只读）");
        var sessions = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        var log = Directory.Exists(sessions)
            ? Directory.EnumerateFiles(sessions, "*.zstd", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
            : null;

        if (log is null)
        {
            r.Step("  （本机没有 DSH 会话日志，跳过真实数据部分）");
        }
        else
        {
            r.Step($"  用 {log.Name}（{FileText.Human(log.Length)}）");
            var raw = File.ReadAllBytes(log.FullName);
            r.Check(ZstdFrames.LooksLikeZstd(raw), "识别为 zstd 数据");
            var text = ZstdFrames.DecompressAll(raw);
            r.Check(text.Length > 1000, $"解出 {text.Length} 字符（多帧拼接）");
            r.Check(text.Contains("reasoningText"), "解出的内容含 reasoningText");
            var frameCount = 0;
            for (var i = 0; i + 4 <= raw.Length; i++)
                if (raw[i] == 0x28 && raw[i + 1] == 0xB5 && raw[i + 2] == 0x2F && raw[i + 3] == 0xFD) frameCount++;
            r.Check(frameCount > 1, $"文件里至少 {frameCount} 处帧魔数（证明确实是多帧，解一帧会丢内容）");

            var (docs, bad) = JsonlReader.Parse(text);
            r.Step($"  jsonl 解析：{docs.Count} 条记录，跳过 {bad} 行坏数据");
            r.Check(docs.Count > 0, "解析出记录");
            foreach (var d in docs) d.Dispose();
        }

        // ---- 4. 非 zstd 的明文 jsonl（ZCode 通道形状） ----
        r.Step("明文 jsonl");
        var jsonl = Path.Combine(sandbox, "model-io-sess_demo.jsonl");
        FileText.Write(jsonl,
            "{\"type\":\"model_io\",\"request\":{\"messages\":[{\"role\":\"system\",\"content\":\"系统段\"}]},\"response\":{\"reasoningText\":\"内部推理\"}}\n" +
            "坏行不是 JSON\n" +
            "{\"type\":\"model_io\",\"response\":{\"reasoningText\":\"第二轮推理\"}}\n");
        var plain = JsonlReader.ReadText(jsonl);
        r.Check(plain.Contains("内部推理"), "明文按 UTF-8 读出中文（不被 ANSI 弄成乱码）");
        var (docs2, bad2) = JsonlReader.Parse(plain);
        r.Check(docs2.Count == 2 && bad2 == 1, $"2 条好记录 + 1 行坏数据（实际 {docs2.Count} / {bad2}）");
        if (docs2.Count > 0)
        {
            var reasons = JsonlReader.StringsByKey(docs2[0].RootElement, "reasoningText").ToArray();
            r.Check(reasons.Length == 1 && reasons[0] == "内部推理", "按 key 取到嵌套的 reasoningText");
        }
        foreach (var d in docs2) d.Dispose();

        // ---- 5. 备份纪律：干净 / 污染 / 不可读 ----
        r.Step("备份健康度");
        var target = Path.Combine(sandbox, "target.cjs");
        FileText.Write(target, "原始内容");
        var backup = new BackupManager(target);
        r.Check(backup.Assess(new[] { "MARKER" }).Status == BackupStatus.Absent, "无备份 → Absent");
        backup.EnsureBackup("原始内容", out _);
        r.Check(backup.Assess(new[] { "MARKER" }).Status == BackupStatus.Clean, "备份不含标记 → Clean");
        FileText.Write(backup.BackupPath, "原始内容 + MARKER");
        r.Check(backup.Assess(new[] { "MARKER" }).Status == BackupStatus.Contaminated, "备份含标记 → Contaminated（拒绝用它还原）");
        FileText.Write(backup.MetaPath, "{ 这不是合法 JSON");
        r.Check(backup.Assess(new[] { "MARKER" }).Status == BackupStatus.Contaminated, "meta 损坏不影响污染判定");

        // ---- 6. 进程与注册表探测 ----
        r.Step("进程与注册表探测");
        var pids = PathProbe.ProcessIds("DeepSeek Harness");
        r.Check(pids.Count > 0, $"探测到运行中的 DeepSeek Harness（{pids.Count} 个进程）");
        r.Check(PathProbe.Drives().Count > 0, $"枚举到 {PathProbe.Drives().Count} 个可用盘符");
        var reg = PathProbe.FromRegistry("ZCode");
        r.Check(reg.Count > 0, $"注册表卸载项里认出 ZCode 候选 {reg.Count} 个");
        r.Step("  候选：" + string.Join(" | ", reg.Take(3)));

        // ---- 7. 方案选择器（scheme.txt）：缺省 own、规范化写入、非法值退回 own、B 套原文 verbatim ----
        r.Step("方案选择器与 B 套原文");
        var schemeDir = Path.Combine(sandbox, "policy");
        Directory.CreateDirectory(schemeDir);
        var store = new SchemeStore(schemeDir);
        r.Check(!File.Exists(store.Path), "scheme.txt 不存在");
        r.Check(store.Read(out var note0) == InjectionScheme.Own && note0 is null, "scheme.txt 不存在 → own（无告警）");
        store.Write(InjectionScheme.Protocol);
        r.Check(FileText.Read(store.Path) == "protocol", "写入后落盘内容是规范化值 protocol");
        r.Check(store.Read() == InjectionScheme.Protocol, "回读 = protocol");
        FileText.Write(store.Path, "  PROTOCOL  \n");
        r.Check(store.Read() == InjectionScheme.Protocol, "大小写与首尾空白不敏感");
        FileText.Write(store.Path, "not-a-scheme");
        r.Check(store.Read(out var note1) == InjectionScheme.Own && note1 is not null,
            $"非法值 → 按 own 并留 Note（「{note1}」）");
        FileText.Write(store.Path, "   ");
        r.Check(store.Read() == InjectionScheme.Own, "空白内容 → own");
        r.Check(SchemeText.TryParse("own", out var parsedOwn) && parsedOwn == InjectionScheme.Own &&
                SchemeText.TryParse("protocol", out var parsedProto) && parsedProto == InjectionScheme.Protocol &&
                !SchemeText.TryParse("PROTO", out _), "SchemeText.TryParse 只认 own / protocol");
        DualScheme.Verbatim(r, "support");

        DualScheme.CheckRealReadOnly(r, "support", realSnapshot);
        return r;
    }
}
