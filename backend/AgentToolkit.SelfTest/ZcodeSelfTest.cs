using System.Text;
using System.Text.Json;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using AgentToolkit.Core.Targets;
using AgentToolkit.Core.Targets.Zcode;

namespace AgentToolkit.SelfTest;

/// <summary>
/// ZCode 适配器的回归自检。硬性纪律：**只在沙箱副本上操作**——合成一个几百 KB 的 bundle fixture，
/// 安装根、配置根、记忆文件、注入文本目录全部落在 ctx.MakeSandbox("zcode") 下，
/// 绝不写 E:\APP\zcode 或 ~/.zcode（用例 12 只读真实安装做锚点保真核对，缺失即跳过）。
/// </summary>
public sealed class ZcodeSelfTest : ITargetSelfTest
{
    public string TargetId => "zcode";
    public string Name => "ZCode 适配器回归（沙箱合成 bundle：五道补丁 / 备份纪律 / 载荷层 / 日志）";

    private sealed class Env
    {
        public required string Sandbox { get; init; }
        public required string InstallRoot { get; init; }
        public required string BundlePath { get; init; }
        public required string Home { get; init; }
        public required string DataDir { get; init; }
        public required ZcodeTarget Target { get; init; }
        public required string Fixture { get; init; }
        public required string FixtureSha { get; init; }
        public required string MemoryFile { get; init; }
    }

    public SelfTestResult Run(SelfTestContext ctx)
    {
        var r = new SelfTestResult { Name = Name };
        var saved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // 真实客户端的只读快照：自检全程只读（不写、不 touch），结束时变化项必须为 0。
        var realSnapshot = DualScheme.CaptureReal("zcode");

        void SetEnv(string key, string? value)
        {
            if (!saved.ContainsKey(key)) saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            var sandbox = ctx.MakeSandbox("zcode");
            var installRoot = Path.Combine(sandbox, "install");
            var bundlePath = Path.Combine(installRoot, "resources", "glm", "zcode.cjs");
            var home = Path.Combine(sandbox, "home");
            var dataDir = Path.Combine(sandbox, "data");
            var memoryFile = Path.Combine(sandbox, "breach-zcode-path.json");
            Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(dataDir);
            FileText.Write(Path.Combine(installRoot, "ZCode.exe"), "MZ-dummy");

            var fixture = BuildFixture();
            FileText.Write(bundlePath, fixture);
            var fixtureSha = FileText.Sha256File(bundlePath);
            r.Step($"沙箱：{sandbox}");
            r.Step($"合成 bundle：{FileText.Human(fixture.Length)}，sha256={fixtureSha[..16]}…");
            r.Check(fixture.Length > 200_000 && fixture.Length < 2_000_000, $"fixture 体积在几百 KB 量级（{FileText.Human(fixture.Length)}）");

            // 环境变量把适配器的自动探测钉在沙箱里；记忆文件也重定向，绝不污染真实 ~/.breach-zcode-path.json
            SetEnv("ZCODE_HOME", home);
            SetEnv("ZCODE_ROOT", installRoot);
            SetEnv("ZCODE_BUNDLE", null);
            SetEnv("ZCODE_EXE", null);
            SetEnv("ZCODE_PATH_MEMORY_FILE", memoryFile);

            var policy = new PolicyStore(dataDir);
            policy.Materialize(ZcodeTarget.PolicyDefs);
            var ctxT = new TargetContext
            {
                Id = "zcode",
                DataDir = dataDir,
                Policy = policy,
                PolicyDefs = ZcodeTarget.PolicyDefs,
                Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
            };
            var target = new ZcodeTarget(ctxT);
            var env = new Env
            {
                Sandbox = sandbox, InstallRoot = installRoot, BundlePath = bundlePath, Home = home,
                DataDir = dataDir, Target = target, Fixture = fixture, FixtureSha = fixtureSha,
                MemoryFile = memoryFile,
            };

            Case01Locate(ctx, r, env);
            Case02FactoryStatus(ctx, r, env);
            Case03PreviewReadOnly(ctx, r, env);
            Case04Apply(ctx, r, env);
            Case05Idempotent(ctx, r, env);
            Case06Revert(ctx, r, env);
            Case07ContaminatedBackup(ctx, r, env);
            Case08AnchorAmbiguous(ctx, r, env);
            Case09DeadlockRepair(ctx, r, env);
            Case10PayloadLayer(ctx, r, env);
            Case11LogsAndDiff(ctx, r, env);
            Case12RealInstallAnchorsReadOnly(ctx, r, env);
            Case13DualScheme(ctx, r, SetEnv);
        }
        finally
        {
            foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

        DualScheme.CheckRealReadOnly(r, "zcode", realSnapshot);
        return r;
    }

    // ================================================================ 1 定位

    private static void Case01Locate(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【1】定位：--root 三种形态 + fail-closed");

        var byDir = env.Target.Locate(new LocateOptions { ExplicitRoot = env.InstallRoot, ExplicitConfigHome = env.Home }, true, default);
        r.Check(byDir.Found && byDir.Bundle == env.BundlePath, "传安装根 → 命中 bundle");
        r.Check(byDir.Root == env.InstallRoot, $"由 bundle 反推安装根（{byDir.Root}）");
        r.Check(byDir.Attempts.Count > 0, $"Attempts 记录了试过的路径（{byDir.Attempts.Count} 条）");
        r.Step($"    定位来源：{byDir.LocatedBy}");

        var byBundle = env.Target.Locate(new LocateOptions { ExplicitRoot = env.BundlePath, ExplicitConfigHome = env.Home }, true, default);
        r.Check(byBundle.Found && byBundle.Bundle == env.BundlePath && byBundle.LocatedBy == "cli:--bundle", "直接传 bundle 路径 → 命中");

        var byExe = env.Target.Locate(new LocateOptions { ExplicitRoot = Path.Combine(env.InstallRoot, "ZCode.exe"), ExplicitConfigHome = env.Home }, true, default);
        r.Check(byExe.Found && byExe.Bundle == env.BundlePath, "传 ZCode.exe → 由 exe 推导 bundle");

        var bad = env.Target.Locate(new LocateOptions { ExplicitRoot = Path.Combine(env.Sandbox, "no-such-dir"), ExplicitConfigHome = env.Home }, true, default);
        r.Check(!bad.Found, "显式路径无效 → fail-closed（不回退到其它安装）");
        r.Check(bad.Attempts.Any(a => a.Candidate.Contains("cli:--path 无效")), "Attempts 记下「cli:--path 无效」");
        r.Check(bad.Notes.Any(n => n.Detail.Contains("fail-closed")), "Notes 说明 fail-closed 中止原因");

        // 自动探测（无显式参数）：走环境变量 ZCODE_ROOT
        var auto = env.Target.Locate(null, true, default);
        r.Check(auto.Found && auto.Bundle == env.BundlePath, "无显式参数 → 环境变量 ZCODE_ROOT 命中（自动探测链）");
        r.Check(File.Exists(env.MemoryFile), "定位成功后写记忆文件");
        r.Check(PathMemory.Load(env.MemoryFile, "bundle") == env.BundlePath, "记忆文件内容是本次命中的 bundle");
        ctx.Log($"locate ok: {auto.Bundle}");
    }

    // ================================================================ 2 出厂状态

    private static void Case02FactoryStatus(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【2】出厂状态：补丁层 NotApplied、备份 Absent");

        var status = env.Target.GetStatus(default);
        r.Check(status.Layers.Count == 2, $"两层 LayerStatus（{status.Layers.Count}）");
        var sys = status.Layers[0];
        var payload = status.Layers[1];
        r.Check(sys.Kind == LayerKind.SystemPrompt && sys.TargetPath == env.BundlePath, "层 1 指向 bundle");
        r.Check(sys.State == LayerState.NotApplied, $"层 1 状态 = NotApplied（实际 {sys.State}）");
        r.Check(sys.Backup.Health == BackupHealth.Absent, $"层 1 备份 = Absent（实际 {sys.Backup.Health}）");
        r.Check(sys.ParseOk, "层 1 解析检查（词法配平）通过");
        r.Check(payload.State == LayerState.TargetAbsent, $"层 2 目标不存在（实际 {payload.State}）");
        r.Check(payload.Backup.Path is not null && payload.Backup.Path.EndsWith("AGENTS.md.ops.bak", StringComparison.OrdinalIgnoreCase),
            $"层 2 备份路径按载荷层约定 .ops.bak 报告（实际 {payload.Backup.Path}）");
        r.Check(payload.Backup.Health == BackupHealth.Absent, $"层 2 备份 = Absent（实际 {payload.Backup.Health}）");
        r.Check(status.Target.Found && status.Target.TargetId == "zcode", "EngineStatus.Target 由 ToLocateResult 装配且 Found");
        ctx.Log($"status: {sys.State}/{sys.Backup.Health} {payload.State}");
    }

    // ================================================================ 3 预览

    private static void Case03PreviewReadOnly(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【3】预览只读：sha256 不变，Changes 列出 5 条补丁行");

        var before = FileText.Sha256File(env.BundlePath);
        var preview = env.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);
        var after = FileText.Sha256File(env.BundlePath);

        r.Check(before == after, "PreviewApply 前后 bundle sha256 不变");
        var patchRows = preview.Changes.Where(c => c.Layer == LayerKind.SystemPrompt).ToList();
        r.Check(patchRows.Count == 5, $"Changes 里 5 条补丁行（实际 {patchRows.Count}）");
        r.Check(patchRows.All(x => x.WillWrite), "出厂 fixture 上 5 条都标记为将写入");
        var ids = string.Join("\n", patchRows.SelectMany(x => x.Notes).Select(n => n.Title));
        foreach (var p in ZcodePatchTable.Patches) r.Check(ids.Contains(p.Id), $"预览列出 {p.Id}");
        r.Check(preview.Changes.Count == 6, $"另有 1 条载荷层变更行（合计 {preview.Changes.Count}）");
        r.Check(preview.CanApply, "CanApply=true");
        r.Check(!File.Exists(env.BundlePath + ".breach.bak"), "dry-run 不建立备份（绝不写盘）");
        ctx.Log($"preview: {patchRows.Count} patch rows, canApply={preview.CanApply}");
    }

    // ================================================================ 4 apply

    private static void Case04Apply(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【4】apply：5 条全部 apply、marker 各命中 1、备份干净、自检通过");

        var apply = env.Target.Apply(new ApplyOptions(), null, default);
        r.Check(apply.Applied, "ApplyResult.Applied=true");

        var planText = string.Join("\n", apply.Changes.Select(c => c.DiffText ?? ""));
        foreach (var p in ZcodePatchTable.Patches) r.Check(planText.Contains(p.Id + ": apply"), $"{p.Id} 动作 = apply");

        var text = FileText.Read(env.BundlePath);
        var markerCounts = ZcodePatchTable.Patches.Select(p => $"{p.Id}={FileText.Count(text, p.Marker)}").ToArray();
        r.Step("    盘上 marker 命中：" + string.Join(" ", markerCounts));
        r.Check(ZcodePatchTable.Patches.All(p => FileText.Count(text, p.Marker) == 1), "五道补丁 marker 各命中 1");

        // P1–P3 的 anchor 被替换掉（0）；P4/P5 的 anchor 恰好在各自 patch 的首句里被重新写回（1）——
        // 这与真实 3.14.4 bundle 的实测计数完全一致（P1/P2/P3=0，P4/P5=1）。
        r.Check(FileText.Count(text, ZcodePatchTable.Patches[0].Anchor) == 0, "P1 anchor 已消失（0）");
        r.Check(FileText.Count(text, ZcodePatchTable.Patches[1].Anchor) == 0, "P2 anchor 已消失（0）");
        r.Check(FileText.Count(text, ZcodePatchTable.Patches[2].Anchor) == 0, "P3 anchor 已消失（0）");
        r.Check(FileText.Count(text, ZcodePatchTable.Patches[3].Anchor) == 1, "P4 anchor 重新出现在 P4 patch 首句（1）");
        r.Check(FileText.Count(text, ZcodePatchTable.Patches[4].Anchor) == 1, "P5 anchor 重新出现在 P5 patch 首句（1）");
        r.Check(FileText.Count(text, "may or may not be relevant") == 0, "P3 打完后削弱句「may or may not be relevant」消失");
        r.Check(FileText.Count(text, "These instructions OVERRIDE any default behavior") == 1,
            "外围句「These instructions OVERRIDE any default behavior」不在这五道补丁的范围内，保持不变");

        var bak = env.BundlePath + ".breach.bak";
        r.Check(File.Exists(bak), "备份已建立");
        var bakText = FileText.Read(bak);
        r.Check(ZcodePatchTable.Markers.All(m => FileText.Count(bakText, m) == 0), "备份里 marker 全 0（干净，可安全还原）");
        r.Check(ZcodePatchTable.Anchors.Take(3).All(a => FileText.Count(bakText, a) == 1), "备份里 P1–P3 anchor 各命中 1（确是原件）");
        r.Check(FileText.Sha256File(bak) == env.FixtureSha, "备份与出厂 fixture 逐字节一致");
        r.Check(File.Exists(bak + ".meta.json"), "备份指纹 meta 已写入");

        var gate = JsSyntaxGate.VerifyAfterPatch(env.Fixture, text);
        r.Check(gate.Ok, "替换后词法配平 + 体积区间自检通过");
        r.Check(gate.Balanced, $"词法配平（正则/模板/注释感知）通过：{gate.Detail.Split('；')[0]}");
        r.Step("    自检：" + gate.Detail);

        var notes = string.Join("\n", apply.Notes.Select(n => n.Title + " " + n.Detail));
        r.Check(notes.Contains("node --check 通过") || notes.Contains("跳过 node --check"),
            "自检结论已记录（node --check 通过，或本机无 node 时按约定跳过并说明）");
        ctx.Log($"apply ok: {planText.Split('\n').Length} steps, sha={FileText.Sha256File(env.BundlePath)[..16]}…");
    }

    // ================================================================ 5 幂等

    private static void Case05Idempotent(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【5】幂等：第二次 apply 不改变 sha256");

        var first = FileText.Sha256File(env.BundlePath);
        var second = env.Target.Apply(new ApplyOptions(), null, default);
        var after = FileText.Sha256File(env.BundlePath);
        r.Check(first == after, "第二次 apply 后 sha256 与第一次相同");
        r.Check(!second.Applied, "第二次 ApplyResult.Applied=false（没有可打的补丁）");
        var planText = string.Join("\n", second.Changes.Select(c => c.DiffText ?? ""));
        r.Check(ZcodePatchTable.Patches.All(p => planText.Contains(p.Id + ": skip (already applied)")),
            "五条都记为 skip (already applied)");

        // 幂等预览：内容没变就不许报"将写入"（五个补丁行 + 载荷行都要 false）
        var prev = env.Target.PreviewApply(new ApplyOptions { DryRun = true }, default);
        r.Check(!prev.CanApply, "全部幂等时 CanApply=false");
        r.Check(prev.Changes.Count > 0 && prev.Changes.All(c => !c.WillWrite), "预览所有行 WillWrite=false（含 ContextPayload）");
        var payloadRow = prev.Changes.First(c => c.Layer == LayerKind.ContextPayload);
        r.Check(!payloadRow.WillWrite, "载荷层 WillWrite=false（磁盘内容与将要写入的内容逐字节相同）");
        r.Check(payloadRow.BeforeBytes == payloadRow.AfterBytes, "载荷行 Before==After，不虚报体积");
        r.Check(prev.TotalBeforeBytes == prev.TotalAfterBytes,
            $"总体积不虚报（{FileText.Human(prev.TotalBeforeBytes)} → {FileText.Human(prev.TotalAfterBytes)}）");
        r.Check(prev.Changes.SelectMany(c => c.Notes).Any(n => n.Title.Contains("无需写入")),
            "载荷层给出「已是当前文本，无需写入（幂等）」说明");

        // 只看载荷层（跳过补丁层）时同样不许报可执行
        var payloadOnly = env.Target.PreviewApply(new ApplyOptions { DryRun = true, SkipSystemPrompt = true }, default);
        r.Check(!payloadOnly.CanApply, "仅载荷层预览：内容一致时 CanApply=false");
        ctx.Log("idempotent ok");
    }

    // ================================================================ 6 revert

    private static void Case06Revert(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【6】revert：与出厂 fixture 逐字节一致，回读干净，指纹匹配");

        var rev = env.Target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "RevertResult.Reverted=true");
        r.Check(FileText.Sha256File(env.BundlePath) == env.FixtureSha, "还原结果与出厂 fixture 逐字节一致");
        r.Check(rev.ReadBackClean, "ReadBackClean=true（回读无 marker 残留）");
        r.Check(rev.MatchesOriginal == true, "MatchesOriginal=true（与 meta 的 originalSha256 一致）");
        r.Check(File.Exists(env.BundlePath + ".breach.bak"), "默认保留备份（RemoveBackups=false）");
        r.Check(ZcodePatchTable.Markers.All(m => FileText.Count(FileText.Read(env.BundlePath), m) == 0), "盘上 marker 全 0");
        ctx.Log("revert ok");
    }

    // ================================================================ 7 污染备份

    private static void Case07ContaminatedBackup(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【7】污染备份：状态报 Contaminated，revert 抛 AnchorConflict");

        var bak = env.BundlePath + ".breach.bak";
        FileText.Write(bak, $"/* {ZcodePatchTable.Patches[0].Marker} */\n" + env.Fixture);
        r.Check(ZcodePatchTable.Markers.Any(m => FileText.Count(FileText.Read(bak), m) > 0), "备份已被改写成含 marker 的内容");

        var status = env.Target.GetStatus(default);
        r.Check(status.Layers[0].Backup.Health == BackupHealth.Contaminated,
            $"状态页报备份 Contaminated（实际 {status.Layers[0].Backup.Health}）");
        r.Check(status.Layers[0].Notes.Any(n => n.Title.Contains("备份污染")), "状态页给出「备份污染」提示");

        var shaBefore = FileText.Sha256File(env.BundlePath);
        CheckThrows(r, () => env.Target.Revert(new RevertOptions(), null, default), EngineErrorCode.AnchorConflict,
            "污染备份 → revert 抛 AnchorConflict（拒绝用它还原）");
        r.Check(File.Exists(bak + ".contaminated"), "污染证据已另存 .breach.bak.contaminated");
        r.Check(FileText.Sha256File(env.BundlePath) == shaBefore, "抛错时没有改动 bundle");

        foreach (var p in new[] { bak, bak + ".meta.json", bak + ".contaminated", bak + ".superseded" })
            if (File.Exists(p)) File.Delete(p);
        ctx.Log("contaminated ok");
    }

    // ================================================================ 8 锚点不唯一

    private static void Case08AnchorAmbiguous(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【8】锚点不唯一：把 P2 的 anchor 复制成两份 → apply 拒绝写入");

        var idx = env.Fixture.IndexOf(ZcodePatchTable.Patches[1].Anchor, StringComparison.Ordinal);
        r.Check(idx > 0, "出厂 fixture 里 P2 anchor 命中 1 处");
        var dup = env.Fixture[..idx] + ZcodePatchTable.Patches[1].Anchor + " (dup) " + env.Fixture[idx..];
        FileText.Write(env.BundlePath, dup);
        var dupSha = FileText.Sha256File(env.BundlePath);
        r.Check(FileText.Count(dup, ZcodePatchTable.Patches[1].Anchor) == 2, "现在 P2 anchor 命中 2 处");

        var threw = false;
        try
        {
            env.Target.Apply(new ApplyOptions { SkipContextPayload = true }, null, default);
        }
        catch (AgentEngineException ex)
        {
            threw = true;
            r.Check(ex.Code == EngineErrorCode.AnchorConflict, $"整次 apply 抛 AnchorConflict（实际 {ex.Code}）");
            r.Check(ex.Message.Contains("retry-after-denial"), "异常信息点明是哪道补丁不唯一");
        }
        r.Check(threw, "apply 抛异常而不是猜位置");
        r.Check(FileText.Sha256File(env.BundlePath) == dupSha, "拒绝写入：文件未被修改");

        FileText.Write(env.BundlePath, env.Fixture);
        foreach (var p in new[] { env.BundlePath + ".breach.bak", env.BundlePath + ".breach.bak.meta.json" })
            if (File.Exists(p)) File.Delete(p);
        ctx.Log("ambiguous ok");
    }

    // ================================================================ 9 死锁修复

    private static void Case09DeadlockRepair(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【9】备份死锁：无备份 + 文件已含补丁 → 逆序还原出干净备份");

        // 9a：全打完 + 备份被删 → 再 apply。此时五道 marker 全在，没有可打的补丁，
        // 源实现（cmdApply 的 writes === 0 分支）只体检备份、不重建备份；端口保持同样语义。
        env.Target.Apply(new ApplyOptions { SkipContextPayload = true }, null, default);
        var bak = env.BundlePath + ".breach.bak";
        var patchedSha = FileText.Sha256File(env.BundlePath);
        foreach (var p in new[] { bak, bak + ".meta.json" }) if (File.Exists(p)) File.Delete(p);

        var again = env.Target.Apply(new ApplyOptions { SkipContextPayload = true }, null, default);
        r.Check(!again.Applied, "9a 无备份但已全打完 → 不报错、不重复写（幂等）");
        r.Check(FileText.Sha256File(env.BundlePath) == patchedSha, "9a bundle 未被改动");
        r.Check(!File.Exists(bak), "9a 如实记录：writes==0 的路径下源实现不重建备份（要靠 restore）");
        r.Step("    9a 结论：writes==0 时源实现只体检备份，不重建；重建发生在有补丁可打时（见 9b）");

        // 9b：真实死锁场景——文件里已经有补丁（P1 已打），但备份被删了，还剩补丁要打。
        // 直接复制当前文件当备份会把"半打补丁的版本"存成"原始文件"，端口必须逆序还原后再建备份。
        FileText.Write(env.BundlePath, env.Fixture);
        var p1 = ZcodePatchTable.Patches[0];
        var partially = ZcodePatchTable.ReplaceOnce(env.Fixture, p1.Anchor, p1.Patch);
        FileText.Write(env.BundlePath, partially);
        foreach (var p in new[] { bak, bak + ".meta.json" }) if (File.Exists(p)) File.Delete(p);
        r.Check(ZcodePatchTable.AnyMarker(partially) && !File.Exists(bak), "9b 前置：文件含补丁且没有任何备份");

        var repair = env.Target.Apply(new ApplyOptions { SkipContextPayload = true }, null, default);
        r.Check(repair.Applied, "9b apply 成功且没有抛错");
        r.Check(File.Exists(bak), "9b 已重建备份");
        var bakText = FileText.Read(bak);
        r.Check(ZcodePatchTable.Markers.All(m => FileText.Count(bakText, m) == 0), "9b 重建的备份里 marker 全 0（干净）");
        r.Check(ZcodePatchTable.Anchors.Take(3).All(a => FileText.Count(bakText, a) == 1), "9b 备份里的 P1–P3 anchor 各命中 1");
        r.Check(FileText.Sha256File(bak) == env.FixtureSha, "9b 重建的备份与出厂 fixture 逐字节一致");
        r.Check(FileText.Sha256File(env.BundlePath) != env.FixtureSha, "9b 盘上仍是打过补丁的版本");
        ctx.Log("deadlock repair ok");
    }

    // ================================================================ 10 载荷层

    private static void Case10PayloadLayer(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【10】载荷层 ~/.zcode/AGENTS.md：首行判归属、备份用户文件、还原、惰性目录");

        var def = ZcodeTarget.PolicyDefs.First(d => d.Name == "payload");
        var payloadText = env.Target.Ctx.Policy.Read(def) ?? "";
        r.Check(payloadText.Length > 0, $"载荷文本可用（{FileText.Human(FileText.Bytes(payloadText))}）");
        r.Check(ZcodePayload.FeatureOf(payloadText) == payloadText.Split('\n')[0].Trim(), "归属判定用载荷首行");

        var agents = Path.Combine(env.Home, "AGENTS.md");
        var opsBak = agents + ".ops.bak";
        foreach (var p in new[] { agents, opsBak }) if (File.Exists(p)) File.Delete(p);
        var userOwn = "# 我的约定\n\n这是用户自己的 AGENTS.md，工具不得把它当自家文件。\n";
        FileText.Write(agents, userOwn);
        var userSha = FileText.Sha256File(agents);

        var inert = Path.Combine(env.Home, "plugin-workspace", "ops-conventions");
        Directory.CreateDirectory(inert);
        FileText.Write(Path.Combine(inert, "selfcheck.txt"), "stale");

        var install = env.Target.Apply(new ApplyOptions { SkipSystemPrompt = true }, null, default);
        r.Check(install.Applied, "载荷 install 写入成功");
        r.Check(FileText.Read(agents) == payloadText, "AGENTS.md 内容 == 载荷文本");
        r.Check(File.Exists(opsBak), "用户自己的 AGENTS.md 已备份为 AGENTS.md.ops.bak");
        r.Check(FileText.Sha256File(opsBak) == userSha, "备份与用户原文件逐字节一致");
        r.Check(!Directory.Exists(inert), "惰性目录 plugin-workspace\\ops-conventions 已清除");
        r.Check(install.Notes.Any(n => n.Title.Contains("惰性")), "Notes 报告了惰性目录（plugin-workspace 不是 ZCode 的加载路径）");

        var status = env.Target.GetStatus(default);
        r.Check(status.Layers[1].State == LayerState.Applied, $"载荷层状态 = Applied（实际 {status.Layers[1].State}）");
        r.Check(status.Layers[1].Backup.Path == opsBak, $"载荷层备份路径 = {opsBak}（按 install.ps1 的 .ops.bak 约定）");
        r.Check(status.Layers[1].Backup.Health == BackupHealth.Clean, $"用户原文件备份报告为 Clean（实际 {status.Layers[1].Backup.Health}）");
        r.Check(status.Layers[1].Notes.Any(n => n.Title.Contains("用户原文件")), "状态页说明这份备份会被逐字节还原回去");

        // 幂等：内容逐字节相同时既不写盘、也不报 Applied（不虚报）
        var mtime = File.GetLastWriteTimeUtc(agents);
        var againInstall = env.Target.Apply(new ApplyOptions { SkipSystemPrompt = true }, null, default);
        r.Check(!againInstall.Applied, "载荷已一致 → ApplyResult.Applied=false（不虚报）");
        r.Check(File.GetLastWriteTimeUtc(agents) == mtime, "内容一致时不重写文件（修改时间不变）");
        r.Check(FileText.Sha256File(agents) == FileText.Sha256File(env.Target.Ctx.Policy.PathOf(def)),
            "盘上 AGENTS.md 与注入文本逐字节一致");

        var revert = env.Target.Revert(new RevertOptions { SkipSystemPrompt = true }, null, default);
        r.Check(revert.Reverted, "载荷 revert 执行");
        r.Check(File.Exists(agents) && FileText.Sha256File(agents) == userSha, "revert 后用户文件被还原且逐字节一致");
        r.Check(!File.Exists(opsBak), "用户文件备份已删除");

        // 分支：备份是自家旧载荷 → 删目标 + 删备份，而不是把旧载荷"还原"回来
        FileText.Write(agents, payloadText.Replace("本文件是本机的工作偏好", "本文件是本机的工作偏好（旧版）"));
        FileText.Write(opsBak, payloadText + "\n<!-- old payload -->\n");
        var staleStatus = env.Target.GetStatus(default);
        r.Check(staleStatus.Layers[1].Backup.Health == BackupHealth.Contaminated,
            $"备份是自家旧载荷时按污染报告（实际 {staleStatus.Layers[1].Backup.Health}）");
        r.Check(staleStatus.Layers[1].Notes.Any(n => n.Title.Contains("自家旧载荷")), "状态页说明 revert 会删目标而不是还原");
        env.Target.Revert(new RevertOptions { SkipSystemPrompt = true }, null, default);
        r.Check(!File.Exists(agents), "备份是自家旧载荷 → 删目标（不是还原旧载荷）");
        r.Check(!File.Exists(opsBak), "自家旧载荷备份已丢弃");

        // 分支：目标不是自家文件 → 不动
        FileText.Write(agents, "# 别人的文件\n");
        var foreignSha = FileText.Sha256File(agents);
        env.Target.Revert(new RevertOptions { SkipSystemPrompt = true }, null, default);
        r.Check(File.Exists(agents) && FileText.Sha256File(agents) == foreignSha, "不是自家文件 → 保持不动");
        ctx.Log("payload ok");
    }

    // ================================================================ 11 日志 / 差异 / 导出

    private static void Case11LogsAndDiff(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【11】日志：ListSessions / LoadSession / CheckMarkers / 导出 / 差异");

        var rollout = Path.Combine(env.Home, "cli", "rollout");
        Directory.CreateDirectory(rollout);
        const string sid = "11111111-2222-3333-4444-555555555555";
        var logPath = Path.Combine(rollout, $"model-io-sess_{sid}.jsonl");
        var payloadText = env.Target.Ctx.Policy.Read(ZcodeTarget.PolicyDefs.First(d => d.Name == "payload")) ?? "";
        var sysText = "You are ZCode, an interactive coding agent\n\n" +
                      " You are an interactive ZCode agent that helps users with software engineering tasks.\n\n" +
                      ZcodePatchTable.Patches[0].Patch + "\n" + ZcodePatchTable.Patches[1].Patch;
        var userText = "<system-reminder>\n# agentsMd\n\n" + payloadText[..Math.Min(300, payloadText.Length)] +
                       "\n</system-reminder>\n第一轮问题";
        var content = string.Join("\n",
            Record(sid, "turn_a", "2026-01-01T00:00:00.000Z", sysText, userText, "THINK-1", "ANSWER-1"),
            Record(sid, "turn_b", "2026-01-01T00:01:00.000Z", sysText, "第二轮问题", "THINK-2", "ANSWER-2")) + "\n";
        FileText.Write(logPath, content);
        ctx.Log($"session fixture: {FileText.Human(content.Length)}");

        var sessions = env.Target.ListSessions(default);
        r.Check(sessions.Count == 1, $"ListSessions 命中 1 个会话（实际 {sessions.Count}）");
        r.Check(sessions[0].Id == sid, $"Id 取文件名里 model-io-sess_ 之后的部分（{sessions[0].Id}）");

        // CLI 的 latest 约定：解析到最新会话，且报告里的 SessionId 是解析后的真会话号
        var latestReport = env.Target.CheckMarkers("latest", default);
        r.Check(latestReport.SessionId == sid, $"latest 解析到真会话号（{latestReport.SessionId}）");
        r.Check(latestReport.TotalChars > 0, $"latest 的 TotalChars > 0（实际 {latestReport.TotalChars}）");
        r.Check(env.Target.CheckMarkers("", default).TotalChars > 0, "空字符串同样按最新会话处理");
        r.Check(ZcodeLogs.ResolvePath(env.Home, latestReport.SessionId) == logPath, "按解析出的会话号能回查到同一文件");

        // 读不到会话时必须显式报错，绝不静默返回空文本（空文本会被误读成"注入未生效"）
        var threwMissing = false;
        try
        {
            env.Target.CheckMarkers("no-such-session", default);
        }
        catch (AgentEngineException ex)
        {
            threwMissing = true;
            r.Check(ex.Code == EngineErrorCode.TargetFileMissing, $"会话不存在 → TargetFileMissing（实际 {ex.Code}）");
            r.Check(ex.Message.Contains(sid), "报错里列出目录下现有的会话号");
        }
        r.Check(threwMissing, "会话不存在时抛异常，而不是给出 0 字符的空结论");

        var emptyDir = Path.Combine(env.Sandbox, "empty-home");
        Directory.CreateDirectory(ZcodeLogs.RolloutDir(emptyDir));
        FileText.Write(Path.Combine(ZcodeLogs.RolloutDir(emptyDir), "model-io-sess_broken.jsonl"), "{not json}\n{\"type\":\"model_io\"}\n");
        CheckThrows(r, () => ZcodeLogs.CheckMarkers(emptyDir, "latest", 8 * 1024 * 1024, default), EngineErrorCode.IoError,
            "记录全解析不出来 → 抛 IoError（拒绝拿空文本做判定）");

        var detail = env.Target.LoadSession(sid, new SessionLoadOptions(), default);
        r.Check(detail.Info.Id == sid, "LoadSession 解析到同一会话");
        r.Check(detail.Info.TurnCount == 2, $"按 turnId 归并出 2 轮（实际 {detail.Info.TurnCount}）");
        r.Check(detail.Turns.Count == 4, $"轮次记录 = user+assistant × 2（实际 {detail.Turns.Count}）");
        r.Check(detail.Turns[0].Role == "user" && detail.Turns[1].Role == "assistant", "轮次顺序：user → assistant");
        r.Check(detail.Turns[1].ReasoningText == "THINK-1", "助手轮的 response.reasoningText 进 ReasoningText（判读只看这里）");
        r.Check(detail.Turns[1].Text == "ANSWER-1", "助手轮的 response.text 进 Text");
        r.Check(detail.SystemPromptText is not null &&
                detail.SystemPromptText.Contains(ZcodePatchTable.Patches[0].Marker), "SystemPromptText 取 system 内容的串联");
        r.Check(detail.Turns.All(t => !t.Truncated), "未触发截断（MaxCharsPerText 默认 20000 未超）");

        var limited = env.Target.LoadSession(sid, new SessionLoadOptions { MaxTurns = 1 }, default);
        r.Check(limited.Info.TurnCount == 2, "MaxTurns 限制生效时 TurnCount 仍报总轮数");
        r.Check(limited.Notes.Any(n => n.Title.Contains("轮次已限制")), "Notes 说明只载入了最近 N 轮");
        var truncated = env.Target.LoadSession(sid, new SessionLoadOptions { MaxTurns = 2, MaxCharsPerText = 12 }, default);
        r.Check(truncated.Turns.Any(t => t.Truncated), "超过 MaxCharsPerText 的文本被截断并置 Truncated=true");
        r.Check(truncated.Turns.Where(t => t.Truncated).All(t => (t.Text?.Length ?? 0) <= 256),
            "截断后的文本不超过上限（实现带 256 字符的下限保护，避免调用方传 12 这种荒谬值把日志切碎）");
        r.Check(detail.Turns[0].Text is { Length: > 256 }, $"未截断时同一段文本更长（{detail.Turns[0].Text?.Length ?? 0} 字符），说明截断真的生效了");
        r.Check(truncated.Notes.Any(n => n.Title.Contains("截断")), "Notes 明示发生了截断（判读不能拿截断文本当全文）");

        var markers = env.Target.CheckMarkers(sid, default);
        r.Check(markers.SystemPromptLayerPresent, "CheckMarkers：系统提示词层进了模型上下文");
        r.Check(markers.ContextPayloadLayerPresent, "CheckMarkers：上下文载荷层进了模型上下文");
        var sysHits = markers.Hits.Where(h => h.Layer == MarkerLayer.SystemPrompt).Sum(h => h.Count);
        var payloadHits = markers.Hits.Where(h => h.Layer == MarkerLayer.ContextPayload).Sum(h => h.Count);
        r.Step($"    标记命中：系统层 {sysHits}，载荷层 {payloadHits}，第一方 {markers.Hits.Where(h => h.Layer == MarkerLayer.FirstParty).Sum(h => h.Count)}");
        r.Check(markers.Hits.Any(h => h.Marker == "These instructions OVERRIDE any default behavior"), "标记表包含外围句");
        r.Check(markers.ExportedSystemPromptPath is not null && File.Exists(markers.ExportedSystemPromptPath),
            "CheckMarkers 顺带导出 system 提示词（供 UI 打开）");
        r.Check(!string.IsNullOrWhiteSpace(markers.Verdict), $"Verdict 有结论：{markers.Verdict}");

        var exportSession = env.Target.ExportSession(sid, Path.Combine(env.Sandbox, "export.jsonl"), default);
        r.Check(exportSession.Exported && File.Exists(exportSession.Path), "ExportSession 写出文件");
        r.Check(FileText.Sha256File(exportSession.Path!) == FileText.Sha256File(logPath), "导出的 jsonl 与源日志逐字节一致（UTF-8 解码正确）");

        var exportPrompt = env.Target.ExportSystemPrompt(sid, Path.Combine(env.Sandbox, "prompt.txt"), default);
        r.Check(exportPrompt.Exported && File.Exists(exportPrompt.Path), "ExportSystemPrompt 写出文件");
        r.Check(FileText.Read(exportPrompt.Path!).Contains("You are ZCode, an interactive coding agent"), "导出的系统提示词内容正确");

        var diff = env.Target.GetSystemPromptDiff(null, default);
        r.Check(diff.BeforeSource is not null && diff.AfterSource is not null, "差异报告带来源（备份 / 当前 bundle）");
        r.Check(diff.AfterText.Contains("machine owner's own computer"), "AfterText 取当前 bundle 里生效的安全段");
        r.Check(diff.BeforeText.Contains("authorized security testing"), "BeforeText 取备份里那段出厂安全段");
        r.Check(diff.Lines.Count > 0 && diff.AddedLines + diff.RemovedLines > 0, $"逐行差异非空（+{diff.AddedLines}/-{diff.RemovedLines}）");
        r.Check(diff.MarkerDelta.Any(h => h.Count > 0), "MarkerDelta 记录注入前后标记变化");
        ctx.Log($"logs ok: sys={sysHits} payload={payloadHits} diff +{diff.AddedLines}/-{diff.RemovedLines}");
    }

    // ================================================================ 12 真实数据（只读）

    private static void Case12RealInstallAnchorsReadOnly(SelfTestContext ctx, SelfTestResult r, Env env)
    {
        r.Step("");
        r.Step("【12】真实安装与真实日志（只读核对，不写任何真实文件；缺失即跳过）");

        var realHome = Path.Combine(ZcodeLocator.HomeDir(), ".zcode");
        var rollout = ZcodeLogs.RolloutDir(realHome);
        if (Directory.Exists(rollout) && Directory.EnumerateFiles(rollout, "model-io-*.jsonl").Any())
        {
            var newest = ZcodeLogs.NewestPath(realHome);
            var size = newest is null ? 0 : new FileInfo(newest).Length;
            r.Step($"    真实日志：{newest}（{FileText.Human(size)}）");

            MarkerReport? report = null;
            try { report = ZcodeLogs.CheckMarkers(realHome, "latest", 8 * 1024 * 1024, default); }
            catch (AgentEngineException ex) { r.Check(false, $"真实日志 CheckMarkers 抛了 {ex.Code}: {ex.Message}"); }

            if (report is not null)
            {
                r.Check(report.TotalChars > 0, $"真实最新会话 TotalChars > 0（实际 {report.TotalChars} 字符）");
                r.Check(report.SessionId.Length > 0 && !report.SessionId.Equals("latest", StringComparison.OrdinalIgnoreCase),
                    $"报告里的 SessionId 是解析后的真会话号（{report.SessionId}）");
                var sysHits = report.Hits.Where(h => h.Layer == MarkerLayer.SystemPrompt).Sum(h => h.Count);
                r.Check(sysHits > 0, $"真实日志里系统层 marker 命中 > 0（实际 {sysHits}）");
                r.Check(report.SystemPromptLayerPresent, "系统提示词层在真实日志里被判定为已进上下文");
                r.Step($"    真实标记命中：系统层 {sysHits}，载荷层 {report.Hits.Where(h => h.Layer == MarkerLayer.ContextPayload).Sum(h => h.Count)}，" +
                       $"第一方 {report.Hits.Where(h => h.Layer == MarkerLayer.FirstParty).Sum(h => h.Count)}；{report.Verdict}");
            }

            var realLoad = ZcodeLogs.Load(realHome, "latest", new SessionLoadOptions { MaxTurns = 3, MaxCharsPerText = 4000 }, default);
            r.Check(realLoad.SystemPromptText is { Length: > 0 }, $"真实日志 LoadSession：SystemPromptText 非空（{realLoad.SystemPromptText?.Length ?? 0} 字符）");
            r.Check(realLoad.Info.TurnCount is > 0, $"真实日志按 turnId 归并出轮次（{realLoad.Info.TurnCount}）");

            // 「最新那个日志一定带推理」是错的假设：客户端会产生只有 request/usage、没有 reasoningText 的记录
            //（本机就有一个 3 条记录的会话）。所以这里改成在真实日志里扫一个带推理的，扫不到就如实说明，不伪造通过。
            var withReasoning = (string?)null;
            var scanned = 0;
            foreach (var file in Directory.EnumerateFiles(rollout, "model-io-*.jsonl")
                         .Select(p => new FileInfo(p))
                         .OrderByDescending(f => f.Length)
                         .Take(8))
            {
                scanned++;
                var name = file.Name;
                if (!name.StartsWith("model-io-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                var sid = name.Substring("model-io-".Length, name.Length - "model-io-".Length - ".jsonl".Length);
                try
                {
                    var probe = ZcodeLogs.Load(realHome, sid, new SessionLoadOptions { MaxTurns = 6, MaxCharsPerText = 2000 }, default);
                    if (probe.Turns.Any(t => !string.IsNullOrEmpty(t.ReasoningText))) { withReasoning = sid; break; }
                }
                catch (AgentEngineException) { /* 读不了就换下一个，这里只找样本 */ }
            }

            if (withReasoning is not null)
            {
                var sample = ZcodeLogs.Load(realHome, withReasoning, new SessionLoadOptions { MaxTurns = 6, MaxCharsPerText = 8000 }, default);
                var think = sample.Turns.First(t => !string.IsNullOrEmpty(t.ReasoningText)).ReasoningText!;
                r.Check(true, $"真实日志里读到了 response.reasoningText 推理原文（会话 {withReasoning}，{think.Length} 字符，" +
                              $"开头「{think.Substring(0, Math.Min(48, think.Length)).Replace('\n', ' ')}…」，扫了 {scanned} 个文件）");
            }
            else
            {
                r.Step($"    跳过推理原文断言：本机 {scanned} 个真实日志里没有带 reasoningText 的记录（客户端会产生只有 request/usage 的会话）");
            }
        }
        else
        {
            r.Step("    本机没有真实 model-io 日志，跳过真实日志用例");
        }

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ZCODE_REAL_ROOT"),
            @"E:\APP\zcode",
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();

        string? found = null;
        foreach (var root in roots)
        {
            var bundle = Path.Combine(root, "resources", "glm", "zcode.cjs");
            if (File.Exists(bundle) && File.Exists(bundle + ".breach.bak")) { found = bundle; break; }
        }
        if (found is null)
        {
            r.Step("    本机没有可核对的真实安装，跳过锚点保真比对");
            return;
        }

        var realBackup = found + ".breach.bak";
        var bakText = FileText.Read(realBackup);
        r.Step($"    只读比对：{found}");
        foreach (var p in ZcodePatchTable.Patches.Take(3))
            r.Check(FileText.Count(bakText, p.Anchor) == 1, $"真实备份里 {p.Id} 的 anchor 逐字命中 1 次（补丁表与 3.14.4 原文一致）");
        r.Check(ZcodePatchTable.Markers.All(m => FileText.Count(bakText, m) == 0), "真实备份里 marker 全 0（干净）");
        r.Check(FileText.Count(bakText, "require clear authorization context") == 1, "真实备份里保留了出厂条款原文");
        r.Check(!File.Exists(realBackup + ".contaminated"), "真实备份没有被标记为污染");

        // 真实 bundle 现在是「已注入」还是「出厂态」：判据用补丁表现成的 StateOf（marker 命中 → Applied，
        // 与状态页 RollupBundle / StatusBuilder 的口径一致），不另造一套。两种状态都只读，一个字节都不写。
        var realText = FileText.Read(found);
        var markerCounts = ZcodePatchTable.Patches.Select(p => FileText.Count(realText, p.Marker)).ToArray();
        var applied = ZcodePatchTable.Patches.Where((_, i) => markerCounts[i] > 0).Select(p => p.Id).ToArray();
        r.Step("    真实 bundle 的五道 marker 命中：" +
               string.Join(" ", ZcodePatchTable.Patches.Select((p, i) => $"{p.Id}={markerCounts[i]}")));

        if (applied.Length > 0)
        {
            // ---------- 已注入态：逐条验证五道 marker、P4/P5 anchor、真实 diff 有差异 ----------
            r.Check(ZcodePatchTable.Markers.All(m => FileText.Count(realText, m) == 1), "真实 bundle 里五道 marker 各命中 1");
            r.Check(FileText.Count(realText, ZcodePatchTable.Patches[3].Anchor) == 1 && FileText.Count(realText, ZcodePatchTable.Patches[4].Anchor) == 1,
                "真实 bundle 里 P4/P5 anchor 各命中 1（与合成 fixture 打完后的计数一致）");

            var realDiff = ZcodeLogs.Diff(found, realBackup, null, default);
            r.Check(realDiff.AfterText.Contains("machine owner's own computer"), "真实 diff：AfterText（当前 bundle）里是注入后的安全段");
            r.Check(!realDiff.BeforeText.Contains("machine owner's own computer"), "真实 diff：BeforeText（出厂备份）里没有注入后的安全段");
            r.Check(realDiff.BeforeText.Contains("authorized security testing"), "真实 diff：BeforeText 取出厂备份里的安全段原文");
            r.Check(realDiff.AddedLines + realDiff.RemovedLines > 0, $"真实 diff 有逐行差异（+{realDiff.AddedLines}/-{realDiff.RemovedLines}）");
        }
        else
        {
            // ---------- 出厂态：marker 0 命中不是失败，是当前状态；按出厂态核对（README/verify 的「出厂态跳过」同口径）----------
            r.Step("    真实 bundle 当前是出厂态（五道 marker 全部 0 命中），跳过已注入态断言；" +
                   "下面按出厂态核对：marker 命中 0 / P4·P5 anchor 命中 0 / 与出厂备份 diff 为 +0/-0。");
            r.Check(markerCounts.All(c => c == 0),
                $"出厂态：五道 marker 命中 0（实际 {string.Join("/", markerCounts)}）——这版没被注入过，不是自检失败");
            r.Check(ZcodePatchTable.Patches.Take(3).All(p => FileText.Count(realText, p.Anchor) == 1),
                "出厂态：P1–P3 anchor 各命中 1（盘上就是补丁表认的原文，不是升级换过的新版本）");
            r.Check(FileText.Count(realText, ZcodePatchTable.Patches[3].Anchor) == 0 &&
                    FileText.Count(realText, ZcodePatchTable.Patches[4].Anchor) == 0,
                "出厂态：P4/P5 anchor 命中 0（这两句是 P1/P4 打完才在现场生成的）");
            r.Check(FileText.Count(realText, "may or may not be relevant") == 1,
                "出厂态：削弱句「may or may not be relevant」仍在（确系原件）");

            var realDiff = ZcodeLogs.Diff(found, realBackup, null, default);
            r.Check(realDiff.AddedLines + realDiff.RemovedLines == 0,
                $"出厂态：与出厂备份的逐行差异为 +0/-0（实际 +{realDiff.AddedLines}/-{realDiff.RemovedLines}）");
            r.Check(realDiff.AfterText.Contains("authorized security testing"), "出厂态：当前 bundle 里是出厂安全段原文");
            r.Check(!realDiff.AfterText.Contains("machine owner's own computer"), "出厂态：当前 bundle 里没有注入后的安全段");
            r.Check(FileText.Sha256File(found) == FileText.Sha256File(realBackup),
                "出厂态：当前 bundle 与出厂备份逐字节一致（diff +0/-0 的字节级佐证）");
        }

        ctx.Log("real install + real log read-only cross-check ok");
    }

    // ================================================================ 13 双注入方案

    /// <summary>own / protocol 双方案：独立沙箱，不依赖前面用例留下的状态。</summary>
    private static void Case13DualScheme(SelfTestContext ctx, SelfTestResult r, Action<string, string?> setEnv)
    {
        r.Step("");
        r.Step("【13】双注入方案（own / protocol）：载体槽 payload、双向切换、幂等、revert、verbatim、fail-closed、标记");
        DualScheme.Verbatim(r, "zcode");

        var sandbox = ctx.MakeSandbox("zcode-scheme");
        var installRoot = Path.Combine(sandbox, "install");
        var bundlePath = Path.Combine(installRoot, "resources", "glm", "zcode.cjs");
        var home = Path.Combine(sandbox, "home");
        var dataDir = Path.Combine(sandbox, "data");
        var memoryFile = Path.Combine(sandbox, "breach-zcode-path.json");
        Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(dataDir);
        FileText.Write(Path.Combine(installRoot, "ZCode.exe"), "MZ-dummy");
        FileText.Write(bundlePath, BuildFixture());
        var factorySha = FileText.Sha256File(bundlePath);

        setEnv("ZCODE_HOME", home);
        setEnv("ZCODE_ROOT", installRoot);
        setEnv("ZCODE_BUNDLE", null);
        setEnv("ZCODE_EXE", null);
        setEnv("ZCODE_PATH_MEMORY_FILE", memoryFile);

        var policy = new PolicyStore(dataDir);
        policy.Materialize(ZcodeTarget.PolicyDefs);
        var targetCtx = new TargetContext
        {
            Id = "zcode",
            DataDir = dataDir,
            Policy = policy,
            PolicyDefs = ZcodeTarget.PolicyDefs,
            Ledger = new LedgerStore(Path.Combine(dataDir, "probe-ledger.json")),
        };
        var target = new ZcodeTarget(targetCtx);
        var agents = Path.Combine(home, "AGENTS.md");
        var ownText = targetCtx.TextFor("payload", InjectionScheme.Own);

        // 1 缺省 = own
        DualScheme.SelectorMissingIsOwn(r, targetCtx, "zcode");
        var ownApply = target.Apply(new ApplyOptions(), null, default);
        r.Check(ownApply.Applied, "own 方案 apply 写入（回归）");
        r.Check(FileText.Read(agents) == ownText, "own 方案把 A 套载荷写进 ~/.zcode/AGENTS.md");
        var ownAgain = target.Apply(new ApplyOptions(), null, default);
        r.Check(!ownAgain.Changes.Any(c => c.WillWrite), "own 方案连续两次 apply：WillWrite 全 false");

        // 2 切 protocol：载体槽 = payload
        DualScheme.SwitchToProtocol(r, targetCtx, "zcode", "payload");
        r.Check(targetCtx.EmptySlotNames(InjectionScheme.Protocol).Count == 0, "zcode 的 protocol 方案没有空槽");

        // 3 A → B：写入的唯一槽就是 B 套原文（逐字节）
        var bApply = target.Apply(new ApplyOptions(), null, default);
        r.Check(bApply.Applied, "A→B apply 报告已写入");
        r.Check(FileText.Read(agents) == DualScheme.EmbeddedProtocol(), "B 套：AGENTS.md 逐字节等于 gpt-instruct 原文");
        r.Check(FileText.Bytes(FileText.Read(agents)) == DualScheme.ProtocolBytes, $"B 套：AGENTS.md {DualScheme.ProtocolBytes} 字节");
        r.Check(!FileText.Read(agents).Contains("工作约定", StringComparison.Ordinal), "B 套：A 套文本没有残留");
        var bAgain = target.Apply(new ApplyOptions(), null, default);
        r.Check(!bAgain.Changes.Any(c => c.WillWrite) && !bAgain.Applied, "B 方案连续两次 apply：无字节变化、WillWrite 全 false");

        // 4 B → A：A 套全部恢复
        targetCtx.Schemes().Write(InjectionScheme.Own);
        var backA = target.Apply(new ApplyOptions(), null, default);
        r.Check(backA.Applied, "B→A apply 报告已写入");
        r.Check(FileText.Read(agents) == ownText, "切回 A：AGENTS.md 恢复为 A 套载荷");

        // 5 从 B 方案 revert 回出厂态
        targetCtx.Schemes().Write(InjectionScheme.Protocol);
        target.Apply(new ApplyOptions(), null, default);
        var rev = target.Revert(new RevertOptions(), null, default);
        r.Check(rev.Reverted, "从 B 方案 revert 报告已还原");
        r.Check(!FileText.Exists(agents), "revert 后 AGENTS.md 不存在（出厂态）");
        r.Check(FileText.Sha256File(bundlePath) == factorySha, "revert 后宿主补丁层与出厂 fixture 逐字节一致");
        r.Check(rev.ReadBackClean && rev.MatchesOriginal == true,
            $"revert 回读干净且与首次备份一致（ReadBackClean={rev.ReadBackClean} / MatchesOriginal={rev.MatchesOriginal}）");

        // 6 非法 scheme 值 → own + Note
        DualScheme.InvalidValueFallsBackToOwn(r, targetCtx, "zcode");
        FileText.Write(targetCtx.Policy.SchemePath, "bogus-value\n");
        var st = target.GetStatus(default);
        r.Check(st.Notes.Any(n => n.Title == "方案文件"), "状态页 Notes 说明方案文件非法（按 own 处理）");

        // 7 fail-closed：B 套资源缺失（载荷文本拿不到 → 拒绝写入且不动 AGENTS.md）
        var broken = new ZcodeTarget(DualScheme.BreakProtocolCarrier(targetCtx, "payload"));
        broken.SavePolicyFile("scheme", "protocol", default);
        FileText.Write(agents, ownText);              // 先放一份 A 套内容，确认 fail-closed 不会动它
        var agentsSha = FileText.Sha256File(agents);
        var threw = false;
        try { broken.Apply(new ApplyOptions(), null, default); }
        catch (AgentEngineException ex)
        {
            threw = true;
            r.Check(ex.Code is EngineErrorCode.TargetFileMissing or EngineErrorCode.SelfCheckFailed or EngineErrorCode.VerifyProblems,
                $"B 套资源缺失 → apply 抛 {ex.Code}");
        }
        r.Check(threw, "B 套资源缺失时 fail-closed（不是静默注空文本）");
        r.Check(FileText.Sha256File(agents) == agentsSha, "fail-closed 时 AGENTS.md 一个字节都没写");
        r.Check(FileText.Sha256File(bundlePath) == factorySha, "fail-closed 发生在写盘之前：宿主补丁层也没动");

        // 8 标记：B 方案用 B 套载荷层标记
        r.Check(ZcodeLogs.SpecsForScheme(InjectionScheme.Protocol)
                .Any(s => s.Marker == "INJECTED REVISION ACTION" && s.Layer == MarkerLayer.ContextPayload),
            "CheckMarkers 在 protocol 方案用 B 套载荷层标记集");
        r.Check(ZcodeTarget.PayloadMarkersFor(InjectionScheme.Own).Contains("工作约定"), "own 方案仍用原载荷标记集");

        // 9 状态 Notes 报当前方案
        var target2 = new ZcodeTarget(targetCtx);
        targetCtx.Schemes().Write(InjectionScheme.Protocol);
        target2.Apply(new ApplyOptions(), null, default);
        DualScheme.PropagatesToStatusNotes(r, target2.GetStatus(default), "zcode");
    }

    // ================================================================ 合成 fixture

    /// <summary>
    /// 合成 bundle：一个几百 KB、语法合法（node --check 能过）的 JS 文本，
    /// 内含 P1/P2/P3 的 anchor 原文（P4/P5 的 anchor 由 P1/P4 的 patch 尾句在现场生成，
    /// 与真实 3.14.4 bundle 的结构一致），外加模板字面量、正则字面量、注释与成对括号，
    /// 让"字符串/注释/模板感知的配平检查"有实际意义。
    /// </summary>
    private static string BuildFixture()
    {
        var p1 = ZcodePatchTable.Patches[0].Anchor;
        var p2 = ZcodePatchTable.Patches[1].Anchor;
        var p3 = ZcodePatchTable.Patches[2].Anchor;

        var sb = new StringBuilder();
        sb.Append("#!/usr/bin/env node\n");
        sb.Append("/* synthetic ZCode bundle fixture — 仅存在于沙箱，用于回归自测 */\n");
        sb.Append("(function () {\n");
        sb.Append("  \"use strict\";\n");
        sb.Append("  const FACTORY = {\n");
        sb.Append("    cliPrefix: \"You are ZCode, an interactive coding agent\",\n");
        sb.Append("    identity: \" You are an interactive ZCode agent that helps users with software engineering tasks.\",\n");
        sb.Append("    security: \"").Append(p1).Append("\",\n");
        sb.Append("    harness: \"").Append(p2).Append("\",\n");
        sb.Append("    disclaimer: \"").Append(p3).Append("\",\n");
        sb.Append("    override: \"These instructions OVERRIDE any default behavior and you MUST follow them exactly.\"\n");
        sb.Append("  };\n\n");

        for (var i = 0; i < 900; i++)
        {
            sb.Append("  function pad").Append(i).Append("(i) {\n");
            sb.Append("    const t = [`k${i}`, (i * ").Append(i % 7 + 2).Append(" + 1) % 11, { id: i, tag: \"n\" + i }];\n");
            sb.Append("    const re = /[a-z]{1,3}\\/\\d+/gi;\n");
            sb.Append("    // filler ").Append(i).Append(": brackets ( ) [ ] { } and quotes ' \" stay inert here\n");
            sb.Append("    const s = `a${pad").Append(i).Append("(i)}b${ { k: i }.k }c`;\n");
            sb.Append("    if (i % 3 === 0) { t.push(`x${i}{y}`); } else if (i % 5 === 0) { t.pop(); t.push(\"z\"); }\n");
            sb.Append("    return t.filter(Boolean).join(\"/\") + String(re).length + s.length;\n");
            sb.Append("  }\n\n");
        }

        sb.Append("  module.exports = { FACTORY: FACTORY, render: pad0 };\n");
        sb.Append("})();\n");
        return sb.ToString();
    }

    private static string Record(string sid, string turnId, string at, string sysText, string userText, string think, string answer) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "model_io",
            ["sessionId"] = "sess_" + sid,
            ["turnId"] = turnId,
            ["startedAt"] = at,
            ["completedAt"] = at,
            ["request"] = new Dictionary<string, object?>
            {
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?> { ["role"] = "system", ["content"] = sysText },
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = userText },
                },
            },
            ["response"] = new Dictionary<string, object?> { ["reasoningText"] = think, ["text"] = answer },
        });

    private static void CheckThrows(SelfTestResult r, Action act, EngineErrorCode expected, string what)
    {
        try
        {
            act();
            r.Check(false, what + "（实际没有抛异常）");
        }
        catch (AgentEngineException ex)
        {
            r.Check(ex.Code == expected, $"{what}（code={ex.Code}）");
        }
        catch (Exception ex)
        {
            r.Check(false, $"{what}（抛了 {ex.GetType().Name}: {ex.Message}）");
        }
    }
}
