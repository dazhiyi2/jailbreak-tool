using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using AgentToolkit.Core.Targets;

namespace AgentToolkit.SelfTest;

/// <summary>
/// 双注入方案（own / protocol）的共享断言。
/// 五个目标的自检都走这里，保证判据一致：原文 verbatim、方案选择器、空槽、遗留清理、标记集、真实客户端只读。
/// </summary>
public static class DualScheme
{
    /// <summary>权威副本的固定 sha256（7,575 字节）：它是「原文没被动过」的证明。</summary>
    public const string ProtocolSha256 = "37382fb7082652e5b18c99fa5086fbc6d414993eaeae2ec7852ba372b031acac";
    public const long ProtocolBytes = 7_575;

    public const string AssetPath = @"D:\破甲\toolkit\backend\AgentToolkit.Core\Assets\schemeB\protocol.md";
    public const string ProtocolResourceSuffix = "schemeB.protocol.md";
    public const string EmptyResourceSuffix = "schemeB.empty.md";

    public static string EmbeddedProtocol() => PolicyStore.ReadResource(ProtocolResourceSuffix) ?? "";

    /// <summary>B 套文本 verbatim：内嵌资源 == 落盘资产 == 固定 sha256（7,575 字节）。</summary>
    public static void Verbatim(SelfTestResult r, string targetId)
    {
        var embedded = EmbeddedProtocol();
        r.Check(embedded.Length > 0, $"[{targetId}] 内嵌资源 {ProtocolResourceSuffix} 读得到");
        var embeddedBytes = FileText.Bytes(embedded);
        r.Check(embeddedBytes == ProtocolBytes, $"[{targetId}] 内嵌资源 {embeddedBytes} 字节（期望 {ProtocolBytes}）");
        r.Check(FileText.Sha256Text(embedded) == ProtocolSha256,
            $"[{targetId}] 内嵌资源 sha256 == {ProtocolSha256[..16]}…（固定值）");

        r.Check(File.Exists(AssetPath), $"[{targetId}] 落盘资产存在：{AssetPath}");
        if (File.Exists(AssetPath))
        {
            var assetText = FileText.Read(AssetPath);
            r.Check(string.Equals(assetText, embedded, StringComparison.Ordinal),
                $"[{targetId}] 内嵌资源与落盘资产逐字节相同");
            r.Check(FileText.Sha256File(AssetPath) == ProtocolSha256,
                $"[{targetId}] 落盘资产 sha256 == {ProtocolSha256[..16]}…（固定值）");
            r.Check(new FileInfo(AssetPath).Length == ProtocolBytes,
                $"[{targetId}] 落盘资产大小 {new FileInfo(AssetPath).Length} 字节（期望 {ProtocolBytes}）");
        }

        r.Check(FileText.Count(embedded, "INJECTED REVISION ACTION") == 1,
            $"[{targetId}] 原文里有 B 套标记（INJECTED REVISION ACTION）");
        var empty = PolicyStore.ReadResource(EmptyResourceSuffix);
        r.Check(empty is not null && empty.Length == 0, $"[{targetId}] 空槽资源 {EmptyResourceSuffix} 是空字符串（0 字节）");
    }

    /// <summary>1) scheme.txt 不存在 → own；scheme 作为只读条目出现在 GetPolicyFiles 里。</summary>
    public static void SelectorMissingIsOwn(SelfTestResult r, TargetContext ctx, string targetId)
    {
        r.Check(!File.Exists(ctx.Policy.SchemePath), $"[{targetId}] 起点：scheme.txt 不存在");
        r.Check(ctx.ActiveScheme() == InjectionScheme.Own, $"[{targetId}] scheme.txt 不存在 → 当前方案 = own（向后兼容）");

        var files = StatusBuilder.PolicyFiles(ctx);
        var entry = files.FirstOrDefault(f => string.Equals(f.Name, "scheme", StringComparison.OrdinalIgnoreCase));
        r.Check(entry is not null, $"[{targetId}] GetPolicyFiles 返回 scheme 条目");
        r.Check(entry is { Text: "own", Editable: false }, $"[{targetId}] scheme 条目 = 只读 + Text=own");
        r.Check(entry?.Path == ctx.Policy.SchemePath, $"[{targetId}] scheme 条目路径 = <dataDir>\\policy\\scheme.txt");
        r.Check(entry is not null && entry.DisplayName.Contains("gpt-instruct", StringComparison.Ordinal),
            $"[{targetId}] scheme 条目显示名点明 protocol=gpt-instruct");
        r.Check(files.Count == SchemeResolve.ForScheme(ctx.PolicyDefs, InjectionScheme.Own).Count + 1,
            $"[{targetId}] own 方案只列 own 套定义 + scheme 条目（{files.Count} 条）");
    }

    /// <summary>2) 切到 protocol：方案可见，载体槽 def 解析到 B 套且文本逐字节等于原文。</summary>
    public static void SwitchToProtocol(SelfTestResult r, TargetContext ctx, string targetId, params string[] carrierSlots)
    {
        ctx.Schemes().Write(InjectionScheme.Protocol);
        r.Check(ctx.ActiveScheme() == InjectionScheme.Protocol, $"[{targetId}] 写入 scheme=protocol 后当前方案 = protocol");

        var files = StatusBuilder.PolicyFiles(ctx);
        var entry = files.First(f => string.Equals(f.Name, "scheme", StringComparison.OrdinalIgnoreCase));
        r.Check(entry.Text == "protocol", $"[{targetId}] GetPolicyFiles 报当前方案 = protocol");

        foreach (var slot in carrierSlots)
        {
            var def = ctx.DefFor(slot, InjectionScheme.Protocol);
            r.Check(def is not null && def.Scheme == InjectionScheme.Protocol, $"[{targetId}] 载体槽「{slot}」解析到 B 套定义");
            var text = ctx.TextFor(slot, InjectionScheme.Protocol);
            r.Check(string.Equals(text, EmbeddedProtocol(), StringComparison.Ordinal),
                $"[{targetId}] 载体槽「{slot}」文本逐字节等于 B 套原文（{FileText.Bytes(text)} 字节）");
            r.Check(files.First(f => f.Name == slot).Text == text, $"[{targetId}] GetPolicyFiles 的「{slot}」也是 B 套文本");
        }

        var empties = ctx.EmptySlotNames(InjectionScheme.Protocol);
        r.Step($"  protocol 方案的空槽：{string.Join("、", empties)}");
        foreach (var name in empties)
        {
            var text = ctx.TextFor(name, InjectionScheme.Protocol);
            r.Check(string.IsNullOrWhiteSpace(text), $"[{targetId}] 空槽「{name}」文本为空");
            var def = ctx.DefFor(name, InjectionScheme.Protocol);
            r.Check(def is { Required: false, Editable: false }, $"[{targetId}] 空槽「{name}」定义 = Required:false + 只读");
            var listed = files.First(f => f.Name == name);
            r.Check(!listed.Editable && listed.Notes.Any(n => n.Title == "该方案下此槽位不注入"),
                $"[{targetId}] 界面能逐条看到「{name} 该方案下此槽位不注入」");
        }
    }

    /// <summary>8) scheme.txt 内容非法 → 按 own 处理并留 Note（不猜、不抛）。</summary>
    public static void InvalidValueFallsBackToOwn(SelfTestResult r, TargetContext ctx, string targetId)
    {
        FileText.Write(ctx.Policy.SchemePath, "bogus-value\n");
        r.Check(ctx.ActiveScheme() == InjectionScheme.Own, $"[{targetId}] scheme.txt 内容非法 → 按 own 处理");

        var files = StatusBuilder.PolicyFiles(ctx);
        var entry = files.First(f => string.Equals(f.Name, "scheme", StringComparison.OrdinalIgnoreCase));
        r.Check(entry.Text == "own", $"[{targetId}] 非法内容下 scheme 条目报 own");
        r.Check(entry.Notes.Any(n => n.Title == "方案文件" && n.Severity == Severity.Warning),
            $"[{targetId}] 非法内容留了 Note（Warn: 方案文件）");

        FileText.Write(ctx.Policy.SchemePath, "  PROTOCOL  \n");
        r.Check(ctx.ActiveScheme() == InjectionScheme.Protocol, $"[{targetId}] 大小写与空白不敏感：\"  PROTOCOL  \" → protocol");
    }

    /// <summary>状态与层 Notes 必须报当前方案（载体槽 + 空槽）。</summary>
    public static void PropagatesToStatusNotes(SelfTestResult r, EngineStatus status, string targetId)
    {
        var engineNote = status.Notes.FirstOrDefault(n => n.Title == "注入方案");
        r.Check(engineNote is not null, $"[{targetId}] EngineStatus.Notes 有「注入方案」一条");
        r.Check(engineNote is not null && engineNote.Detail.Contains("protocol", StringComparison.Ordinal),
            $"[{targetId}] EngineStatus.Notes 写明当前是 protocol");
        r.Check(status.Layers.Any(l => l.Notes.Any(n => n.Title == "注入方案")),
            $"[{targetId}] 至少一层 LayerStatus.Notes 也带「注入方案」");
    }

    /// <summary>把一个目标上下文的 protocol 载体资源改成不存在的后缀（用于 fail-closed 用例）。</summary>
    public static TargetContext BreakProtocolCarrier(TargetContext ctx, string slot)
    {
        var defs = ctx.PolicyDefs.Select(d =>
            d.Scheme == InjectionScheme.Protocol && string.Equals(d.Name, slot, StringComparison.OrdinalIgnoreCase)
                ? new PolicyTextDef
                {
                    Name = d.Name,
                    DisplayName = d.DisplayName + "（自检：资源缺失）",
                    FileName = d.FileName + ".selftest-missing",
                    ResourceSuffix = "schemeB.__does_not_exist__.md",
                    CapBytes = d.CapBytes,
                    Editable = d.Editable,
                    Required = d.Required,
                    Scheme = d.Scheme,
                }
                : d).ToArray();

        return new TargetContext
        {
            Id = ctx.Id,
            DataDir = ctx.DataDir,
            Policy = ctx.Policy,
            PolicyDefs = defs,
            Ledger = ctx.Ledger,
        };
    }

    // ---------------------------------------------------------------- 真实客户端只读

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>该目标涉及的真实客户端路径（只读；自检期间一个字节都不许动）。</summary>
    public static IReadOnlyList<string> RealRootsFor(string targetId) => targetId switch
    {
        "dsh" => new[] { @"E:\APP", Path.Combine(Home, ".dsh") },
        "zcode" => new[] { Path.Combine(Home, ".zcode") },
        "opencode" => new[]
        {
            Path.Combine(Home, ".config", "opencode"),
            Path.Combine(Home, ".local", "share", "opencode"),
        },
        _ => AllRealRoots(),
    };

    public static IReadOnlyList<string> AllRealRoots() => new[]
    {
        @"E:\APP",
        Path.Combine(Home, ".dsh"),
        Path.Combine(Home, ".zcode"),
        Path.Combine(Home, ".config", "opencode"),
        Path.Combine(Home, ".local", "share", "opencode"),
    };

    public static IReadOnlyList<SnapshotEntry> CaptureRealEntries(string targetId) =>
        RealClientSnapshot.Capture(RealRootsFor(targetId));

    /// <summary>空闲窗口（1.5 秒什么都不做）里自己在变的文件键——用来把「客户端自己写的」与「本工具改的」分开。</summary>
    public static IReadOnlySet<string> IdleChurn(string targetId, int idleMs = 1500)
    {
        var roots = RealRootsFor(targetId);
        var a = RealClientSnapshot.Capture(roots);
        Thread.Sleep(idleMs);
        var b = RealClientSnapshot.Capture(roots);
        return RealClientSnapshot.ChangedKeys(a, b);
    }

    /// <summary>自检开始时的真实客户端基线：快照 + 空闲窗口里"自己在写"的键集合。</summary>
    public sealed record RealBaseline(IReadOnlyList<SnapshotEntry> Entries, IReadOnlySet<string> Churn);

    public static RealBaseline CaptureReal(string targetId, int idleMs = 1500) =>
        new(CaptureRealEntries(targetId), IdleChurn(targetId, idleMs));

    /// <summary>
    /// 自检结束时对比快照。判据与各目标适配器那一套一致：
    ///   1) 注入落点这类"内容口径"的关键文件必须逐字节不变；
    ///   2) 运行窗口里变化的文件键，必须全部落在**空闲窗口里自己在变**的集合里（客户端自己在写）；
    ///   3) 剩下的（解释不了的）变化项必须为 0。
    /// </summary>
    public static void CheckRealReadOnly(SelfTestResult r, string targetId, RealBaseline baseline)
    {
        var before = baseline.Entries;
        var after = RealClientSnapshot.Capture(before.Select(b => b.Path));
        var changed = RealClientSnapshot.ChangedKeys(before, after);
        // 两条判据合起来用，缺一不可：
        //   1) 空闲窗口里自己在变的键（周期性写入，如日志 / WAL）；
        //   2) 客户端自身的活状态路径（事件驱动的一次性写入，如 ~/.dsh 的会话 / 项目缓存——空闲窗口抓不到）。
        var clientSelf = changed.Where(k => baseline.Churn.Contains(k) || RealClientSnapshot.IsLiveStateKey(k)).ToArray();
        var unexplained = changed.Where(k => !baseline.Churn.Contains(k) && !RealClientSnapshot.IsLiveStateKey(k)).ToArray();

        var contentChanged = new List<string>();
        for (var i = 0; i < before.Count && i < after.Count; i++)
            if (before[i].Mode == "content" && after[i].Mode == "content" &&
                !string.Equals(before[i].Digest, after[i].Digest, StringComparison.Ordinal))
                contentChanged.Add(before[i].Path);

        foreach (var entry in after)
            r.Step($"  真实路径 {entry.Path}：{(entry.Exists ? $"{entry.Files} 个文件 / {entry.Bytes} 字节 / {entry.Mode} 摘要 {entry.Digest[..12]}…" : "不存在")}");
        r.Step($"  空闲 1.5 秒对照窗口：{baseline.Churn.Count} 个文件键是客户端自己在写；" +
               $"运行窗口里变化 {changed.Count} 项，其中客户端自身写入（空闲窗口命中 {clientSelf.Count(k => baseline.Churn.Contains(k))} 项 + " +
               $"活状态路径 {clientSelf.Length - clientSelf.Count(k => baseline.Churn.Contains(k))} 项）{clientSelf.Length} 项，" +
               $"解释不了的 {unexplained.Length} 项");
        if (clientSelf.Length > 0)
            r.Step("  客户端自身写入（不计入工具足迹）：" + string.Join("；", clientSelf.Take(4)) +
                   (clientSelf.Length > 4 ? " …" : ""));

        r.Check(contentChanged.Count == 0,
            $"[{targetId}] 内容口径的关键文件逐字节不变（{contentChanged.Count} 项变化）：{string.Join("；", contentChanged)}");
        r.Check(unexplained.Length == 0,
            $"[{targetId}] 真实客户端变化项 0（排除客户端自身写入：空闲窗口 {baseline.Churn.Count} 个键 + 活状态路径若干；" +
            $"实际解释不了的变化 {unexplained.Length} 项）" +
            (unexplained.Length == 0 ? "" : "：" + string.Join("；", unexplained.Take(6))));
    }
}
