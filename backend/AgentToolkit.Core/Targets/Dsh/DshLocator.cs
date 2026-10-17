using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Dsh;

/// <summary>
/// DeepSeek Harness 定位器。端口自 现有源码\breach-dsh\tools\dsh-locate.mjs。
///
/// 目标布局（实测 v44.0.0）：
///   &lt;DSH_ROOT&gt;\DeepSeek Harness.exe                Electron 主程序
///   &lt;DSH_ROOT&gt;\resources\app.asar                  应用包（121 MB）
///   &lt;DSH_ROOT&gt;\version                              版本号
///   $DSH_HOME\profiles\desktop\cordis.patch.yml     profile 补丁层（系统提示词注入点）
///   $DSH_HOME\AGENTS.md                             用户级指令文件（载荷层）
///   $DSH_HOME\sessions\&lt;ws&gt;\session-&lt;id&gt;\session.v4.jsonl.zstd   会话日志（多帧 zstd）
///
/// 解析顺序：CLI(--root) &gt; 环境变量(DSH_ROOT) &gt; 记忆文件 &gt; 运行中进程
///          &gt; 注册表卸载项 &gt; 快捷方式 &gt; 常见盘位嗅探。
/// 显式指定的根无效时 **fail-closed**：不回退、不猜，返回 NotFound 并把"试过哪些路径"填进 Attempts。
/// </summary>
public static class DshLocator
{
    public const string ExeName = "DeepSeek Harness.exe";
    public const string ProcessName = "DeepSeek Harness";
    public const string RootEnv = "DSH_ROOT";
    public const string HomeEnv = "DSH_HOME";
    public const string AsarEnv = "DSH_ASAR";
    public const string DefaultHomeDirName = ".dsh";
    public const string MemoryFileName = ".breach-dsh-path.json";
    public const string PatchFileName = "cordis.patch.yml";
    public const string AgentsFileName = "AGENTS.md";

    public static string AsarRel => Path.Combine("resources", "app.asar");

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string MemoryFile => Path.Combine(UserProfile, MemoryFileName);

    /// <summary>配置根：$DSH_HOME（去空白）→ ~/.dsh。</summary>
    public static string HomeDir()
    {
        var env = Environment.GetEnvironmentVariable(HomeEnv)?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;
        return Path.Combine(UserProfile, DefaultHomeDirName);
    }

    /// <summary>profile 目录：$DSH_HOME\profiles\desktop。</summary>
    public static string ProfileDir(string home) => Path.Combine(home, "profiles", "desktop");

    /// <summary>系统提示词层的补丁文件：$DSH_HOME\profiles\desktop\cordis.patch.yml。</summary>
    public static string PatchFile(string home) => Path.Combine(ProfileDir(home), PatchFileName);

    /// <summary>载荷层文件：$DSH_HOME\AGENTS.md。</summary>
    public static string AgentsFile(string home) => Path.Combine(home, AgentsFileName);

    /// <summary>会话日志根：$DSH_HOME\sessions。</summary>
    public static string SessionsRoot(string home) => Path.Combine(home, "sessions");

    // ---------------------------------------------------------------- 判定
    /// <summary>有效根的判据：存在 DeepSeek Harness.exe 或 resources\app.asar。</summary>
    public static bool IsRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return File.Exists(Path.Combine(path, ExeName)) || File.Exists(Path.Combine(path, AsarRel));
        }
        catch { return false; }
    }

    public static string? AsarOf(string root)
    {
        var a = Path.Combine(root, AsarRel);
        return File.Exists(a) ? a : null;
    }

    /// <summary>&lt;root&gt;\version（mjs versionOf）。</summary>
    public static string? VersionOf(string root)
    {
        try
        {
            var v = FileText.ReadOrNull(Path.Combine(root, "version"))?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch { return null; }
    }

    /// <summary>
    /// preset 原文所在的 app.asar。端口自 mjs asarPath()，并在末尾追加"已定位根的 asar"这一级：
    ///   1) $DSH_ASAR（设了就用，即便不存在——交给调用方 fail-closed）
    ///   2) $DSH_ROOT\resources\app.asar
    ///   3) 记忆文件里的 root\resources\app.asar
    ///   4) 本次已定位到的 root\resources\app.asar
    /// </summary>
    public static string? AsarForPresets(string? locatedRoot)
    {
        var env = Environment.GetEnvironmentVariable(AsarEnv)?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;

        var rootEnv = Environment.GetEnvironmentVariable(RootEnv)?.Trim();
        if (!string.IsNullOrEmpty(rootEnv))
        {
            var a = Path.Combine(rootEnv, AsarRel);
            if (File.Exists(a)) return a;
        }

        var mem = PathMemory.Load(MemoryFile, "root");
        if (!string.IsNullOrEmpty(mem))
        {
            var a = Path.Combine(mem, AsarRel);
            if (File.Exists(a)) return a;
        }

        if (!string.IsNullOrEmpty(locatedRoot))
        {
            var a = Path.Combine(locatedRoot, AsarRel);
            if (File.Exists(a)) return a;
        }
        return null;
    }

    // ---------------------------------------------------------------- 嗅探候选
    /// <summary>
    /// 全盘嗅探候选（端口自 mjs sniffCandidates）：各盘符 × 7 种常见布局，
    /// 加 LOCALAPPDATA / PROGRAMFILES / PROGRAMFILES(X86) / APPDATA 常见安装位，
    /// 加家目录下的 DSH / APP\DSH / Apps\DSH / scoop\apps\dsh\current / Downloads\DSH。
    /// </summary>
    public static IReadOnlyList<string> SniffCandidates()
    {
        var roots = new List<string>();

        var tails = new[]
        {
            Path.Combine("APP", "DSH"),
            "DSH",
            Path.Combine("Apps", "DSH"),
            Path.Combine("Program Files", "DeepSeek Harness"),
            Path.Combine("Program Files", "DSH"),
            Path.Combine("Program Files (x86)", "DeepSeek Harness"),
            Path.Combine("Program Files (x86)", "DSH"),
        };
        foreach (var drive in PathProbe.Drives())
            foreach (var t in tails) roots.Add(Path.Combine(drive, t));

        var appLocs = new[]
        {
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetEnvironmentVariable("PROGRAMFILES"),
            Environment.GetEnvironmentVariable("PROGRAMFILES(X86)"),
            Environment.GetEnvironmentVariable("APPDATA"),
        };
        foreach (var la in appLocs)
        {
            if (string.IsNullOrWhiteSpace(la)) continue;
            foreach (var t in new[] { "DSH", "DeepSeek Harness", Path.Combine("Programs", "DSH"), Path.Combine("Programs", "DeepSeek Harness") })
                roots.Add(Path.Combine(la, t));
        }

        var home = UserProfile;
        foreach (var t in new[]
                 {
                     "DSH", Path.Combine("APP", "DSH"), Path.Combine("Apps", "DSH"),
                     Path.Combine("scoop", "apps", "dsh", "current"), Path.Combine("Downloads", "DSH"),
                 })
            roots.Add(Path.Combine(home, t));

        return roots;
    }

    private static IReadOnlyList<string> ShortcutPaths()
    {
        var list = new List<string>();
        var lnk = ProcessName + ".lnk";
        var desktop = Path.Combine(UserProfile, "Desktop", lnk);
        list.Add(desktop);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
            list.Add(Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", lnk));
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
            list.Add(Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs", lnk));
        return list;
    }

    // ---------------------------------------------------------------- 主流程
    private sealed class Probe
    {
        public readonly List<LocateAttempt> Attempts = new();
        public readonly List<Note> Notes = new();
        public readonly List<string> Tried = new();
        private int _order;

        public LocateAttempt Add(string level, string candidate, bool hit, string? note = null)
        {
            var a = new LocateAttempt { Order = ++_order, Level = level, Candidate = candidate, Hit = hit, Note = note };
            Attempts.Add(a);
            return a;
        }

        public void Note(Note n) => Notes.Add(n);

        public void Miss(string level, string candidate, string note)
        {
            Add(level, candidate, false, note);
            Tried.Add($"{level}({candidate})");
        }
    }

    /// <summary>
    /// 逐级定位。remember=true 时成功后写记忆文件（与 mjs 一致：定位成功即记住）；
    /// 只读入口（状态页/Describe）用 remember=false，避免读操作产生写副作用。
    /// FullSniff=true 等价于 mjs 的 --diagnose：不在首个命中处停下，逐级记录全部探测结果。
    /// </summary>
    public static TargetLocation Resolve(LocateOptions? options, bool remember, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var p = new Probe();
        var home = HomeDir();
        var patch = PatchFile(home);
        var full = options?.FullSniff == true;

        string? sniffRoot = null;
        IReadOnlyList<string> sniffCandidates = Array.Empty<string>();
        var sniffHits = 0;

        string? hit = null;
        var source = "";

        // ---- 1. CLI --root（显式指定无效 → fail-closed） ----
        var explicitRoot = options?.ExplicitRoot;
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var normalized = PathProbe.NormalizeRoot(explicitRoot!, ExeName);
            var ok = IsRoot(normalized);
            p.Add("cli", $"{explicitRoot} -> {normalized}", ok, ok ? "命中" : "无效");
            if (ok) return Found(normalized, "cli", home, patch, p, remember, sniffCandidates, sniffHits);

            p.Note(Notes.Warn("显式路径无效（fail-closed）",
                $"显式指定的路径不是有效的 DSH 安装根：{explicitRoot}（判据：存在 {ExeName} 或 {AsarRel}）。" +
                "按纪律不回退、不猜其它安装位置。", "cli"));
            return TargetLocation.NotFound(p.Attempts, sniffCandidates, sniffHits, p.Notes);
        }

        void Accept(string level, string root)
        {
            if (hit is not null) return;
            hit = root;
            source = level;
        }

        // ---- 2. 环境变量 DSH_ROOT ----
        var envRoot = Environment.GetEnvironmentVariable(RootEnv)?.Trim();
        if (!string.IsNullOrEmpty(envRoot))
        {
            var ok = IsRoot(envRoot);
            p.Add("env", envRoot, ok, ok ? "命中" : "无效");
            if (ok)
            {
                Accept("env", envRoot!);
                if (!full) return Found(hit, source, home, patch, p, remember, sniffCandidates, sniffHits);
            }
        }
        else
        {
            p.Add("env", "(未设置)", false, null);
        }

        // ---- 3. 记忆文件 ~/.breach-dsh-path.json ----
        if (options?.IgnoreMemory != true)
        {
            var mem = PathMemory.Load(MemoryFile, "root");
            if (!string.IsNullOrEmpty(mem))
            {
                var ok = IsRoot(mem);
                p.Add("memory", mem, ok, ok ? "命中" : "失效");
                if (ok)
                {
                    Accept("memory", mem!);
                    if (!full) return Found(hit, source, home, patch, p, remember, sniffCandidates, sniffHits);
                }
                else
                {
                    p.Miss("memory", mem!, "记忆里的路径已失效");
                }
            }
            else
            {
                p.Add("memory", "(无)", false, null);
            }
        }
        else
        {
            p.Add("memory", "(IgnoreMemory，跳过)", false, null);
        }

        // ---- 4. 运行中进程 ----
        var procExe = PathProbe.FromProcess(ProcessName);
        var procRoot = string.IsNullOrEmpty(procExe) ? null : Path.GetDirectoryName(procExe);
        var procOk = procRoot is not null && IsRoot(procRoot);
        p.Add("process", procRoot ?? "(未命中)", procOk, procOk ? "命中" : null);
        if (procOk)
        {
            Accept("process", procRoot!);
            if (!full) return Found(hit, source, home, patch, p, remember, sniffCandidates, sniffHits);
        }
        else
        {
            p.Tried.Add($"process({procRoot ?? "未命中"})");
        }

        // ---- 5. 注册表卸载项 ----
        string? regRoot = null;
        string? regError = null;
        try
        {
            foreach (var candidate in PathProbe.FromRegistry("DeepSeek"))
                if (IsRoot(candidate)) { regRoot = candidate; break; }
        }
        catch (Exception ex) { regError = ex.Message; }
        p.Add("registry", regError is null ? regRoot ?? "(未命中)" : $"error: {regError}", regRoot is not null,
            regRoot is null ? null : "命中");
        if (regRoot is not null)
        {
            Accept("registry", regRoot);
            if (!full) return Found(hit, source, home, patch, p, remember, sniffCandidates, sniffHits);
        }
        else
        {
            p.Tried.Add($"registry({(regError is null ? "未命中" : "error: " + regError)})");
        }

        // ---- 6. 快捷方式 ----
        string? lnkRoot = null;
        string? lnkError = null;
        try
        {
            foreach (var target in PathProbe.FromShortcuts(ShortcutPaths()))
            {
                if (!File.Exists(target)) continue;
                var dir = Path.GetDirectoryName(target);
                if (dir is not null && IsRoot(dir)) { lnkRoot = dir; break; }
            }
        }
        catch (Exception ex) { lnkError = ex.Message; }
        p.Add("shortcut", lnkError is null ? lnkRoot ?? "(未命中)" : $"error: {lnkError}", lnkRoot is not null,
            lnkRoot is null ? null : "命中");
        if (lnkRoot is not null)
        {
            Accept("shortcut", lnkRoot);
            if (!full) return Found(hit, source, home, patch, p, remember, sniffCandidates, sniffHits);
        }
        else
        {
            p.Tried.Add($"shortcut({(lnkError is null ? "未命中" : "error: " + lnkError)})");
        }

        // ---- 7. 全盘嗅探 ----
        sniffCandidates = SniffCandidates();
        foreach (var c in sniffCandidates)
        {
            if (!IsRoot(c)) continue;
            sniffHits++;
            sniffRoot ??= c;
        }
        p.Add("sniff", sniffRoot ?? "(未命中)", sniffRoot is not null,
            sniffRoot is null ? null : $"命中，共 {sniffHits} 个有效候选");
        if (sniffRoot is not null) Accept("sniff", sniffRoot);
        else p.Tried.Add("sniff(未命中)");

        return Found(hit, source, home, patch, p, remember, sniffCandidates, sniffHits);
    }

    private static TargetLocation Found(string? root, string source, string home, string patch, Probe p,
        bool remember, IReadOnlyList<string> sniffCandidates, int sniffHits)
    {
        if (root is null)
        {
            p.Tried.Add("常见盘位嗅探");
            p.Note(Notes.Error("未找到安装", "没有定位到 DeepSeek Harness 安装根。可用 --root <DIR> 显式指定，例如 --root \"E:\\APP\\DSH\"。", "locate"));
            foreach (var t in p.Tried) p.Note(Notes.Info("试过", t, "locate"));
            return TargetLocation.NotFound(p.Attempts, sniffCandidates, sniffHits, p.Notes);
        }

        var asar = AsarOf(root);
        if (asar is null)
            p.Note(Notes.Warn("缺少 app.asar", $"找到安装根但缺少 {AsarRel}：preset 覆写无法生成，apply 会 fail-closed。", root));
        if (!File.Exists(patch))
            p.Note(Notes.Info("补丁层不存在", $"profile 补丁层不存在：{patch}（首次 apply 会创建目录与文件）。", "profile"));
        p.Note(Notes.Info("配置根", home, HomeEnv));

        if (remember) PathMemory.Save(MemoryFile, root, "root");

        return new TargetLocation(true, root, Path.Combine(root, ExeName), asar, home, VersionOf(root),
            source, p.Attempts, sniffCandidates, sniffHits, p.Notes,
            // 补丁层文件与承载系统提示词的 app.asar 不是同一个东西，分开报给界面
            PatchFile: patch,
            ProfileDir: Path.GetDirectoryName(patch),
            RunningProcessIds: ProcessControl.Pids(ProcessName));
    }
}
