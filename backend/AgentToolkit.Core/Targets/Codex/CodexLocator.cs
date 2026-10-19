using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using Microsoft.Win32;

namespace AgentToolkit.Core.Targets.Codex;

/// <summary>Codex（ChatGPT 桌面应用）的定位结果：配置主目录 + 提示词落点 + 日志层三个库 + 版本 + 诊断。</summary>
public sealed class CodexLocation
{
    public bool Found { get; init; }

    /// <summary>安装根：MSIX 包目录 <c>C:\Program Files\WindowsApps\OpenAI.Codex_&lt;版本&gt;_x64__&lt;hash&gt;</c>。
    /// 普通权限通常**枚举/读取被拒**，所以经常是 null（不是"没装"）。</summary>
    public string? Root { get; init; }

    /// <summary>独立 CLI：<c>%LOCALAPPDATA%\OpenAI\Codex\bin\&lt;hash&gt;\codex.exe</c>（不在 PATH）。</summary>
    public string? Exe { get; init; }

    /// <summary>承载注入的配置文件 <c>config.toml</c>（契约里 Bundle / PatchFile 都指它）。</summary>
    public string? Bundle { get; init; }

    /// <summary>主目录 CODEX_HOME（本机实测 <c>C:\Users\Administrator\.codex</c>）。</summary>
    public required string ConfigHome { get; init; }

    public string? Version { get; init; }

    /// <summary>MSIX 包全名 <c>OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0</c>（目录名 / 注册表里的那一份）。</summary>
    public string? PackageFullName { get; init; }

    public string? LocatedBy { get; init; }

    public IReadOnlyList<LocateAttempt> Attempts { get; init; } = Array.Empty<LocateAttempt>();
    public IReadOnlyList<string> SniffCandidates { get; init; } = Array.Empty<string>();
    public int SniffHits { get; init; }
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();

    // ---------------- 由主目录推出来的实际路径 ----------------

    /// <summary>层 1 的键所在文件。</summary>
    public string ConfigToml => CodexConfig.PathOf(ConfigHome);

    /// <summary>层 1 的提示词文件（本工具自己的文件名，不冒充上游的 9 个旧名）。</summary>
    public string PromptPath => CodexRules.PromptPath(ConfigHome);

    public string ThreadDbPath => CodexDb.ThreadDbPath(ConfigHome);
    public string StateDbPath => CodexDb.StateDbPath(ConfigHome);
    public string LogsDbPath => CodexDb.LogsDbPath(ConfigHome);

    /// <summary>原始转录目录 <c>CODEX_HOME\sessions\YYYY\MM\DD\rollout-*.jsonl</c>。</summary>
    public string SessionsDir => Path.Combine(ConfigHome, "sessions");
}

/// <summary>
/// Codex 定位器。目标布局（本机实测 2026-10，见 docs/codex.md）：
/// <code>
/// C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0    MSIX 包目录（普通权限读不到）
/// C:\Users\Administrator\.codex\config.toml                                      层 1 的键在这（2,998 B）
/// C:\Users\Administrator\.codex\thread_history_1.sqlite                          日志层：thread_items（3,237 行）
/// C:\Users\Administrator\.codex\state_5.sqlite                                   日志层：threads（18 行，带 rollout_path）
/// C:\Users\Administrator\.codex\logs_2.sqlite                                    应用日志（本地没有系统提示词记录）
/// C:\Users\Administrator\AppData\Local\OpenAI\Codex\bin\896a0166b774037a\codex.exe  独立 CLI（不在 PATH）
/// </code>
///
/// 解析顺序：CLI --root → 环境变量 <c>CODEX_HOME</c>（**这是官方变量**，config.toml 的
/// <c>[mcp_servers.node_repl.env]</c> 里写的值就是它）→ 本工具记忆文件 → 运行进程 <c>ChatGPT</c>
/// → 注册表 Appx 包（拿包全名/版本）→ config.toml 的 <c>CODEX_CLI_PATH</c> / 目录通配 → <c>%USERPROFILE%\.codex</c> → 嗅探。
///
/// 显式指定的路径无效时 fail-closed：不回退、不猜，返回 NotFound 并把「试过哪些路径」填进 Attempts。
/// </summary>
public static class CodexLocator
{
    /// <summary>应用进程名（本机实测 10 个进程；伴生 codex / codex-computer-use-swift 不算）。</summary>
    public const string ProcessName = "ChatGPT";

    public const string CliExeName = "codex.exe";

    /// <summary>官方主目录变量。**与 Cursor 的 CURSOR_ROOT 不同：这是 Codex 自己认的变量。**</summary>
    public const string HomeEnv = "CODEX_HOME";

    /// <summary>本工具约定的主目录覆盖变量（Codex 官方没有这个；自检用它把路径钉在沙箱里）。</summary>
    public const string HomeEnvOverride = "CODEX_HOME_OVERRIDE";

    /// <summary>记忆文件重定向（自检用它把记忆钉在沙箱里，避免污染真实家目录）。</summary>
    public const string MemoryFileEnv = "CODEX_PATH_MEMORY_FILE";

    public const string MemoryFileName = ".breach-codex-path.json";
    public const string ConfigFileName = "config.toml";
    public const string DefaultDirName = ".codex";
    public const string PackagePrefix = "OpenAI.Codex_";

    /// <summary>config.toml 里的应用版本键（本机实测 <c>[mcp_servers.node_repl.env]</c> 里写着）。</summary>
    public const string AppVersionKey = "BROWSER_USE_CODEX_APP_VERSION";

    /// <summary>config.toml 里的 CLI 绝对路径键。</summary>
    public const string CliPathKey = "CODEX_CLI_PATH";

    private static readonly Regex PackageRe = new(
        @"^OpenAI\.Codex_(?<ver>[0-9][0-9A-Za-z.\-]*)_(?<arch>[^_]+)__[a-z0-9]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } p
            ? p
            : Environment.GetEnvironmentVariable("USERPROFILE") ?? ".";

    public static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v!.Trim();
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s!.Trim().Trim('"');
        return t.Length > 3 ? t.TrimEnd('\\', '/') : t;
    }

    /// <summary>默认主目录 <c>%USERPROFILE%\.codex</c>。</summary>
    public static string DefaultConfigHome() => Path.Combine(UserProfile, DefaultDirName);

    /// <summary>层 1 的落点判据：目录里必须有 config.toml（没有就 fail-closed，本工具不创建它）。</summary>
    public static bool IsConfigRoot(string? dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) &&
                   Directory.Exists(dir) &&
                   File.Exists(Path.Combine(dir!, ConfigFileName));
        }
        catch { /* 权限 */ }
        return false;
    }

    /// <summary>记忆文件：~/.breach-codex-path.json（可用 CODEX_PATH_MEMORY_FILE 重定向）。</summary>
    public static string MemoryFile
    {
        get
        {
            var over = Env(MemoryFileEnv);
            return over ?? Path.Combine(UserProfile, MemoryFileName);
        }
    }

    /// <summary>从包全名 <c>OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0</c> 里解析版本。</summary>
    public static (string? Version, string? Arch) ParsePackageName(string fullName)
    {
        var m = PackageRe.Match(fullName.Trim());
        return m.Success ? (m.Groups["ver"].Value, m.Groups["arch"].Value) : (null, null);
    }

    /// <summary>注册表 Appx 仓库里的包全名（这是 WindowsApps 目录名，普通权限读不到目录但读得到注册表）。</summary>
    public static IReadOnlyList<string> PackageFullNames()
    {
        var found = new List<string>();
        var subs = new[]
        {
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
            @"Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
        };
        foreach (var sub in subs)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                using var key = baseKey.OpenSubKey(sub);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                    if (name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)) found.Add(name);
            }
            catch { /* 权限或键不存在 */ }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>CLI 候选项：config.toml 的 CODEX_CLI_PATH 优先，其后 <c>bin\*\codex.exe</c>（取目录最新的那个）。</summary>
    public static IReadOnlyList<string> CliCandidates(string configHome)
    {
        var list = new List<string>();

        var cfgText = FileText.ReadOrNull(Path.Combine(configHome, ConfigFileName));
        if (cfgText is not null)
        {
            var fromCfg = CodexConfig.ReadScalar(cfgText, CliPathKey);
            if (!string.IsNullOrWhiteSpace(fromCfg)) list.Add(fromCfg!);
        }

        var localAppData = Env("LOCALAPPDATA")
                           ?? (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } p ? p : "");
        if (!string.IsNullOrEmpty(localAppData))
        {
            var bin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(bin))
                    foreach (var dir in Directory.EnumerateDirectories(bin)
                                 .OrderByDescending(d => SafeMtime(d)))
                    {
                        var exe = Path.Combine(dir, CliExeName);
                        if (File.Exists(exe)) list.Add(exe);
                    }
            }
            catch { /* 权限 */ }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static DateTime SafeMtime(string dir)
    {
        try { return Directory.GetLastWriteTimeUtc(dir); } catch { return DateTime.MinValue; }
    }

    /// <summary>嗅探候选（附加级，最低优先）。</summary>
    public static IReadOnlyList<string> SniffCandidates()
    {
        var list = new List<string> { DefaultConfigHome() };
        var localAppData = Env("LOCALAPPDATA");
        if (localAppData is not null)
        {
            list.Add(Path.Combine(localAppData, "OpenAI", "Codex"));
            list.Add(Path.Combine(localAppData, "Codex"));
        }
        var appData = Env("APPDATA");
        if (appData is not null) list.Add(Path.Combine(appData, "Codex"));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ---------------------------------------------------------------- 主流程

    public static CodexLocation Resolve(LocateOptions? options, bool remember, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var attempts = new List<LocateAttempt>();
        var notes = new List<Note>();
        var sniffCandidates = SniffCandidates();
        var sniffHits = new List<string>();

        void Add(string level, string candidate, bool hit, string? note = null) =>
            attempts.Add(new LocateAttempt
            {
                Order = attempts.Count + 1,
                Level = level,
                Candidate = candidate,
                Hit = hit,
                Note = note,
            });

        var defaultHome = DefaultConfigHome();
        string? configHome = null;
        string? exe = null;
        string? packageName = null;
        string? version = null;
        string? locatedBy = null;

        // ---- 1. CLI 显式指定 ----
        var explicitRoot = Clean(options?.ExplicitRoot);
        if (explicitRoot is not null)
        {
            if (File.Exists(explicitRoot) &&
                Path.GetFileName(explicitRoot).Equals(CliExeName, StringComparison.OrdinalIgnoreCase))
            {
                exe = explicitRoot;
                var dir = Path.GetDirectoryName(explicitRoot);
                // codex.exe 与 CODEX_HOME 本来就不在一棵树下（bin\<hash>\ 与 ~/.codex），
                // 所以只把它当 CLI，主目录继续按后面的级别解析。
                locatedBy = "cli:--root(codex.exe)";
                Add("cli", $"--root {explicitRoot}", true, $"CLI（主目录另按 CODEX_HOME 解析；bin 父目录 {dir}）");
            }
            else if (IsConfigRoot(explicitRoot))
            {
                configHome = explicitRoot;
                locatedBy = "cli:--root(主目录)";
                Add("cli", $"--root {explicitRoot}", true, $"主目录 {configHome}");
            }
            else
            {
                Add("cli", $"--root {explicitRoot}", false,
                    Directory.Exists(explicitRoot)
                        ? $"目录在，但里面没有 {ConfigFileName}（主目录判据）"
                        : "路径不存在");
                notes.Add(Notes.Warn("显式路径无效（fail-closed）",
                    $"显式指定的路径解释不出 Codex 的主目录：{explicitRoot}。" +
                    $"主目录判据是「目录里存在 {ConfigFileName}」（本机实测 {defaultHome} 满足）。" +
                    "按纪律不回退、不猜其它位置。", "cli"));
                notes.Add(Notes.Info("怎么指定",
                    $"允许传：主目录（含 {ConfigFileName} 的目录）或 {CliExeName}。" +
                    $"也可以用环境变量 {HomeEnv}（Codex 官方变量）或 {HomeEnvOverride}（本工具约定）。", "cli"));
                return Fail(attempts, notes, configHome ?? defaultHome, sniffCandidates, sniffHits);
            }
        }

        var explicitCfg = Clean(options?.ExplicitConfigHome);
        if (explicitCfg is not null)
        {
            var ok = IsConfigRoot(explicitCfg);
            Add("cli", $"--config {explicitCfg}", ok, ok ? $"主目录 {explicitCfg}" : $"目录里没有 {ConfigFileName}");
            if (ok) { configHome ??= explicitCfg; locatedBy ??= "cli:--config(主目录)"; }
            else
                notes.Add(Notes.Warn("--config 无效",
                    $"{explicitCfg} 里没有 {ConfigFileName}：该目录不是 Codex 主目录（本工具不创建 config.toml）。", "cli"));
        }

        // ---- 2. 环境变量：CODEX_HOME 是 Codex 官方变量 ----
        // 它一旦被显式设置，就是权威来源：**不回退**到 %USERPROFILE%\.codex。
        // 理由：这个变量通常由客户端/启动器注入，指到哪儿就只该看哪儿；
        // 回退会让「沙箱里没有 config.toml」这种情形静默落到真实主目录上（那等于往真实客户端写）。
        var envHome = Clean(Env(HomeEnv)) ?? Clean(Env(HomeEnvOverride));
        if (envHome is not null)
        {
            var envName = Clean(Env(HomeEnv)) is not null ? HomeEnv : HomeEnvOverride;
            var ok = IsConfigRoot(envHome!);
            Add("env", $"{envName}={envHome}", ok, ok ? $"主目录 {envHome}" : $"目录里没有 {ConfigFileName}（fail-closed，不回退）");
            if (!ok)
            {
                notes.Add(Notes.Error($"{envName} 指向的目录不是主目录（fail-closed）",
                    $"{envName}={envHome}：目录{(Directory.Exists(envHome!) ? "存在" : "不存在")}，但里面没有 {ConfigFileName}。" +
                    $"本工具**不回退**到 {defaultHome}（否则会静默落到真实客户端上），也不创建 config.toml。" +
                    "改环境变量、或删掉 config.toml、或让 Codex 自己先把主目录跑起来。", envName));
                return Fail(attempts, notes, envHome!, sniffCandidates, sniffHits);
            }
            configHome = envHome;
            locatedBy = $"{envName}（官方变量）";
        }
        else Add("env", $"({HomeEnv} 未设置)", false, "官方变量（本机实测 shell 里也没设，config.toml 里写的是同一路径）");

        // ---- 3. 记忆文件 ----
        if (options?.IgnoreMemory == true)
        {
            Add("memory", "(IgnoreMemory，跳过)", false, null);
        }
        else
        {
            var memHome = Clean(PathMemory.Load(MemoryFile, "configHome"));
            if (memHome is not null)
            {
                var ok = IsConfigRoot(memHome!);
                Add("memory", memHome!, ok, ok ? "主目录" : "失效（缺 config.toml）");
                if (ok) { configHome ??= memHome; locatedBy ??= $"memory（{MemoryFileName}）"; }
            }
            else Add("memory", "(无)", false, "(无记忆文件)");
        }

        // ---- 4. 运行中的 ChatGPT ----
        var procExe = PathProbe.FromProcess(ProcessName);
        if (procExe is not null)
        {
            var dir = Path.GetDirectoryName(procExe);
            var pkgName = dir is null ? null : Path.GetFileName(dir);
            var parsed = pkgName is null ? (null, null) : ParsePackageName(pkgName);
            var ok = pkgName is not null && pkgName.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase);
            Add("process", procExe!, ok, ok ? $"MSIX 包 {pkgName}（版本 {parsed.Item1}）" : "推不出 MSIX 包名");
            if (ok)
            {
                packageName ??= pkgName;
                version ??= parsed.Item1;
                locatedBy ??= $"process（运行中的 {ProcessName}）";
            }
        }
        else Add("process", $"{ProcessName}（未运行 / 拿不到 MainModule）", false, null);

        // ---- 5. 注册表 Appx 仓库：包全名与版本（目录读不到时的替代来源）----
        if (version is null || packageName is null)
        {
            var pkgs = PackageFullNames();
            string? bestName = null;
            string? bestVer = null;
            foreach (var p in pkgs)
            {
                ct.ThrowIfCancellationRequested();
                var (ver, _) = ParsePackageName(p);
                if (ver is null) continue;
                if (bestVer is null || CompareVersions(ver, bestVer) > 0) { bestName = p; bestVer = ver; }
            }
            Add("registry", bestName ?? $"(Appx 仓库里没有 {PackagePrefix}* 包，共 {pkgs.Count} 个)", bestName is not null,
                bestName is null ? null : $"包全名 {bestName}（版本 {bestVer}）");
            if (bestName is not null)
            {
                packageName ??= bestName;
                version ??= bestVer;
                locatedBy ??= "registry（Appx 包仓库）";
            }
        }

        // ---- 6. 主目录兜底 %USERPROFILE%\.codex / 嗅探 ----
        if (configHome is null)
        {
            var ok = IsConfigRoot(defaultHome);
            Add("default", defaultHome, ok, ok ? "主目录" : "目录不存在或缺 config.toml");
            if (ok) { configHome = defaultHome; locatedBy ??= $"default（{defaultHome}）"; }
        }

        if (configHome is null)
        {
            string? sniffRoot = null;
            foreach (var c in sniffCandidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsConfigRoot(c)) continue;
                sniffHits.Add(c);
                sniffRoot ??= c;
            }
            Add("sniff", sniffRoot ?? "(未命中)", sniffRoot is not null,
                sniffRoot is null ? null : $"命中 {sniffHits.Count} 个候选");
            if (sniffRoot is not null) { configHome = sniffRoot; locatedBy ??= $"sniff（{sniffRoot}）"; }
        }

        // ---- 7. WindowsApps 包目录（仅供报告；普通权限通常 UnauthorizedAccessException）----
        string? installRoot = null;
        if (packageName is not null)
        {
            var candidate = Path.Combine(
                Env("ProgramFiles") ?? @"C:\Program Files", "WindowsApps", packageName);
            bool exists;
            string? why = null;
            try { exists = Directory.Exists(candidate); }
            catch (Exception ex) { exists = false; why = $"{ex.GetType().Name}: {ex.Message}"; }
            Add("package", candidate, exists, exists ? "MSIX 包目录" : why ?? "不可读（普通权限下 WindowsApps 拒绝列出）");
            if (exists) installRoot = candidate;
        }

        configHome ??= defaultHome;

        // ---- 8. CLI ----
        var cliCandidates = CliCandidates(configHome);
        foreach (var c in cliCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var ok = File.Exists(c);
            Add("install", c, ok, ok ? "CLI（不在 PATH）" : "不存在");
            if (ok) { exe ??= c; locatedBy ??= $"install（{Path.GetFileName(Path.GetDirectoryName(c))}）"; }
        }
        if (cliCandidates.Count == 0) Add("install", @"%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe", false, "没有候选");

        // ---- 9. 版本兜底：config.toml 的 BROWSER_USE_CODEX_APP_VERSION → CLI 的 FileVersion ----
        var cfgPath = Path.Combine(configHome, ConfigFileName);
        var cfgText = FileText.ReadOrNull(cfgPath);
        if (version is null && cfgText is not null)
        {
            var fromCfg = CodexConfig.ReadScalar(cfgText, AppVersionKey);
            Add("config", $"{AppVersionKey} = {fromCfg ?? "(没有这个键)"}", fromCfg is not null,
                fromCfg is null ? null : $"应用版本 {fromCfg}");
            if (fromCfg is not null) { version = fromCfg; locatedBy ??= $"config（{AppVersionKey}）"; }
        }
        if (version is null && exe is not null)
        {
            var v = VersionOf(exe);
            Add("cli-version", exe, v is not null, v is null ? "PE 没有版本资源（本机实测 codex.exe 的 FileVersion 为空）" : $"FileVersion {v}");
            if (v is not null) { version = v; locatedBy ??= "cli（FileVersion）"; }
        }

        if (!IsConfigRoot(configHome))
        {
            notes.Add(Notes.Error("未找到 Codex 主目录",
                $"多级探测（CLI --root → {HomeEnv}/{HomeEnvOverride} → 记忆文件 → 运行进程 → Appx 注册表 → " +
                $"{defaultHome} → 嗅探）全部未命中：{configHome} 里没有 {ConfigFileName}。" +
                "本工具**不创建** config.toml：没有它就没有层 1 的落点，apply 一律 fail-closed。",
                "locate"));
            return Fail(attempts, notes, configHome, sniffCandidates, sniffHits);
        }

        return Found(installRoot, exe, configHome, packageName, version, locatedBy ?? "unknown",
            attempts, sniffCandidates, sniffHits, notes, remember, ct);
    }

    /// <summary>比较 26.1013.4894.0 这种点分版本；解析不了的段按 0 处理，不做字符串排序。</summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length && int.TryParse(pa[i], out var xi) ? xi : 0;
            var y = i < pb.Length && int.TryParse(pb[i], out var yi) ? yi : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static string? VersionOf(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe!);
            var s = v.FileVersion ?? v.ProductVersion;
            return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        }
        catch { return null; }
    }

    private static CodexLocation Fail(
        IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<Note> notes,
        string configHome, IReadOnlyList<string> sniffCandidates, IReadOnlyList<string> sniffHits) =>
        new()
        {
            Found = false,
            ConfigHome = configHome,
            Attempts = attempts,
            SniffCandidates = sniffCandidates,
            SniffHits = sniffHits.Count,
            Notes = notes,
        };

    private static CodexLocation Found(
        string? installRoot, string? exe, string configHome, string? packageName, string? version, string locatedBy,
        IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<string> sniffCandidates, IReadOnlyList<string> sniffHits,
        IReadOnlyList<Note> notes, bool remember, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cfgPath = Path.Combine(configHome, ConfigFileName);

        var merged = new List<Note>(notes);
        merged.Add(Notes.Info("主目录（CODEX_HOME）",
            $"{configHome}（{ConfigFileName} {FileText.Human(LengthOf(cfgPath))}）" +
            (packageName is null ? "" : $"；MSIX 包 {packageName}；应用版本 {version ?? "读不到"}"), HomeEnv));
        merged.Add(Notes.Info("本工具约定的变量",
            $"{HomeEnv} 是 **Codex 官方**变量（config.toml 的 [mcp_servers.node_repl.env] 里写的就是它）；" +
            $"{HomeEnvOverride} / {MemoryFileEnv} 是本工具约定的，Codex 不认识它们。", HomeEnv));
        merged.Add(Notes.Info("层 1 的落点",
            $"键 {CodexConfig.KeyName} 写在 {cfgPath} 的**顶层**；提示词文件是本工具自己的 " +
            $"{CodexRules.PromptFileName}（{CodexRules.PromptPath(configHome)}）。" +
            "上游工具认的 9 个旧文件名一个都不用（不冒充）。", cfgPath));
        merged.Add(Notes.Info("日志层落点（契约缺口：SessionsDir）",
            $"契约里 LocateResult.SessionsDir 由引擎固定拼成 <ConfigHome>\\sessions —— 对 Codex 只对了一半：" +
            $"会话索引在 {CodexDb.StateDbPath(configHome)}（threads 表）、正文在 {CodexDb.ThreadDbPath(configHome)}" +
            $"（thread_items 表），sessions\\YYYY\\MM\\DD\\rollout-*.jsonl 只是原始转录。本工具不改契约，写进 Notes。",
            CodexDb.ThreadDbPath(configHome)));
        merged.Add(Notes.Info("CLI",
            exe is null
                ? $@"没找到 CLI（候选：{CliPathKey} 与 %LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe）。CLI 不影响注入。"
                : $"{exe}（不在 PATH；CLI 与本工具的注入互不依赖）", exe ?? configHome));
        merged.Add(Notes.Info("包目录权限",
            installRoot is null
                ? @"MSIX 包目录 C:\Program Files\WindowsApps\OpenAI.Codex_* 在普通权限下**枚举被拒**（本机实测 UnauthorizedAccessException）：" +
                  "所以版本从注册表 Appx 仓库的包全名解析，安装根可能报 null（那不是「没装」）。"
                : $"包目录 {installRoot}", installRoot ?? configHome));

        if (!File.Exists(CodexDb.ThreadDbPath(configHome)) && !File.Exists(CodexDb.StateDbPath(configHome)))
            merged.Add(Notes.Info("还没有日志库",
                $"{CodexDb.StateDbPath(configHome)} / {CodexDb.ThreadDbPath(configHome)} 都不存在：日志层与行为级判据会报「无法判定」" +
                "（不是「没生效」）。本工具**不创建**这两个库。", configHome));

        if (remember)
        {
            var save = installRoot ?? configHome;
            if (!string.Equals(PathMemory.Load(MemoryFile, "root"), save, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(PathMemory.Load(MemoryFile, "configHome"), configHome, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var map = new Dictionary<string, string>
                    {
                        ["root"] = save,
                        ["configHome"] = configHome,
                        ["savedAt"] = DateTimeOffset.Now.ToString("o"),
                    };
                    FileText.Write(MemoryFile,
                        System.Text.Json.JsonSerializer.Serialize(map,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                catch { /* 记忆失败不致命 */ }
            }
        }

        return new CodexLocation
        {
            Found = true,
            Root = installRoot,
            Exe = exe,
            Bundle = cfgPath,
            ConfigHome = configHome,
            Version = version,
            PackageFullName = packageName,
            LocatedBy = locatedBy ?? "unknown",
            Attempts = attempts,
            SniffCandidates = sniffCandidates,
            SniffHits = sniffHits.Count,
            Notes = merged,
        };
    }

    private static long LengthOf(string? path)
    {
        try { return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
