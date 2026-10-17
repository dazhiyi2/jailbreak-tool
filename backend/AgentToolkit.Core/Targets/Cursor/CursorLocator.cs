using System.Diagnostics;
using System.Text.Json;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using Microsoft.Win32;

namespace AgentToolkit.Core.Targets.Cursor;

/// <summary>Cursor 的定位结果：安装根 / 配置根 / 数据根 / 会话库 / 工作区 + 「试过哪些路径」的完整诊断。</summary>
public sealed class CursorLocation
{
    public bool Found { get; init; }

    /// <summary>安装根（本机实测 E:\APP\cursor）。</summary>
    public string? Root { get; init; }

    /// <summary>Electron 主程序 Cursor.exe。</summary>
    public string? Exe { get; init; }

    /// <summary>补丁层所在的目录 &lt;安装根&gt;\resources\app（**没有 asar**，JS 明文）。</summary>
    public string? AppRoot { get; init; }

    /// <summary>承载本地提示词框架句的主文件（cursor-agent-host\dist\main.js）；只作报告与契约兜底。</summary>
    public string? Bundle { get; init; }

    /// <summary>配置根 %APPDATA%\Cursor（VS Code 风格）。</summary>
    public required string ConfigHome { get; init; }

    /// <summary>数据根 &lt;配置根&gt;\User（globalStorage 在这里）。</summary>
    public required string ProfileDir { get; init; }

    /// <summary>工作区根：解析不到就是 null（层 3 直接 TargetAbsent，**不猜**）。</summary>
    public string? WorkspaceDir { get; init; }

    /// <summary>工作区是从哪里解析出来的（人话，写进 Notes/诊断）。</summary>
    public string? WorkspaceSource { get; init; }

    public string? Version { get; init; }
    public string? LocatedBy { get; init; }

    /// <summary>Root 只代表配置根（没定位到安装根）时为真。</summary>
    public bool RootIsNotInstall { get; init; }

    public IReadOnlyList<LocateAttempt> Attempts { get; init; } = Array.Empty<LocateAttempt>();
    public IReadOnlyList<string> SniffCandidates { get; init; } = Array.Empty<string>();
    public int SniffHits { get; init; }
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();

    // ---------------- 由三个根推出来的实际路径 ----------------

    /// <summary>会话库 + 个人规则层的落点：%APPDATA%\Cursor\User\globalStorage\state.vscdb。</summary>
    public string DbPath => CursorLocator.DbPath(ConfigHome);

    /// <summary>层 2 的键。</summary>
    public string PersonalKey => CursorDb.PersonalContextKey;

    /// <summary>层 3 的落点：&lt;工作区&gt;\.cursor\rules\breach.mdc；工作区没解析出来时是空串。</summary>
    public string RulesPath => WorkspaceDir is null ? "" : CursorRules.PathFor(WorkspaceDir!);

    /// <summary>层 1 的四个文件（AppRoot 为 null 时给的是相对路径，调用方按缺失处理）。</summary>
    public IReadOnlyList<string> PatchFiles => CursorLocator.PatchFiles(AppRoot);
}

/// <summary>
/// Cursor 定位器。目标布局（本机实测 2026-10，见 docs/cursor.md）：
/// <code>
/// E:\APP\cursor\Cursor.exe                                                        Electron 外壳（235 MB）
/// E:\APP\cursor\resources\app\out\main.js                                         主进程（2.7 MB）
/// E:\APP\cursor\resources\app\extensions\cursor-agent-host\dist\main.js           规则框架句（4 份同源副本之一）
/// %APPDATA%\Cursor\User\globalStorage\state.vscdb                                 SQLite：个人规则层 + 会话/日志层
/// &lt;工作区&gt;\.cursor\rules\breach.mdc                                              项目规则层（alwaysApply: true 才进提示词）
/// </code>
///
/// 解析顺序：CLI --root（安装根 / Cursor.exe / 配置根 / 工作区）→ 本工具约定环境变量
///          → 记忆文件 ~/.breach-cursor-path.json → 运行进程 Cursor → 注册表卸载项（DisplayName ^Cursor）
///          → %LOCALAPPDATA%\Programs\cursor → %APPDATA%\Cursor。
///
/// 显式指定的路径无效时 **fail-closed**：不回退、不猜，返回 NotFound 并把「试过哪些路径」填进 Attempts。
/// 注意：CURSOR_ROOT / CURSOR_CONFIG / CURSOR_WORKSPACE / CURSOR_PATH_MEMORY_FILE 是本工具的约定变量，
/// **Cursor 官方没有这些变量**（本机实测也没有 CURSOR_USER_DATA_DIR，所以不编造它）——见 Notes。
/// </summary>
public static class CursorLocator
{
    public const string ProcessName = "Cursor";
    public const string ExeName = "Cursor.exe";

    /// <summary>补丁层所在目录（相对安装根）。**没有 asar**，这一层是明文目录。</summary>
    public const string AppRelative = @"resources\app";

    /// <summary>安装根的判据一：主进程 JS。</summary>
    public const string AppMainRelative = @"resources\app\out\main.js";

    /// <summary>安装根的判据二：agent host 扩展目录。</summary>
    public const string AgentHostRelative = @"resources\app\extensions\cursor-agent-host";

    /// <summary>本工具约定的安装根变量（Cursor 自己没有这个变量）。</summary>
    public const string RootEnv = "CURSOR_ROOT";
    /// <summary>本工具约定的配置根变量（Cursor 自己没有这个变量）。</summary>
    public const string ConfigEnv = "CURSOR_CONFIG";
    /// <summary>本工具约定的工作区变量（层 3 的落点；不设就走 recent paths 自动解析）。</summary>
    public const string WorkspaceEnv = "CURSOR_WORKSPACE";
    /// <summary>记忆文件重定向（自检用它把记忆钉在沙箱里，避免污染真实家目录）。</summary>
    public const string MemoryFileEnv = "CURSOR_PATH_MEMORY_FILE";

    public const string MemoryFileName = ".breach-cursor-path.json";
    public const string ConfigDirName = "Cursor";
    public const string UserDirName = "User";
    public const string GlobalStorageDirName = "globalStorage";
    public const string DbFileName = "state.vscdb";
    public const string StorageJsonName = "storage.json";

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
        // 注册表的 InstallLocation 本机实测是 "E:\APP\cursor\"（带尾分隔符）。
        return t.Length > 3 ? t.TrimEnd('\\', '/') : t;
    }

    /// <summary>配置根默认值：%APPDATA%\Cursor。</summary>
    public static string DefaultConfigHome()
    {
        var appData = Env("APPDATA")
                      ?? (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } p ? p : "");
        return string.IsNullOrEmpty(appData) ? "" : Path.Combine(appData, ConfigDirName);
    }

    /// <summary>安装根兜底：%LOCALAPPDATA%\Programs\cursor（本机实测不存在，是兜底项）。</summary>
    public static string DefaultInstallRoot()
    {
        var localAppData = Env("LOCALAPPDATA")
                           ?? (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } p ? p : "");
        return string.IsNullOrEmpty(localAppData) ? "" : Path.Combine(localAppData, "Programs", "cursor");
    }

    public static string DbPath(string configHome) =>
        Path.Combine(configHome, UserDirName, GlobalStorageDirName, DbFileName);

    public static string StorageJsonPath(string configHome) =>
        Path.Combine(configHome, UserDirName, GlobalStorageDirName, StorageJsonName);

    public static string ProfileDir(string configHome) => Path.Combine(configHome, UserDirName);

    public static string AppRoot(string installRoot) => Path.Combine(installRoot, AppRelative);

    /// <summary>层 1 的四个文件；installRoot 对应的 app 根为空时返回相对路径（调用方会报缺失）。</summary>
    public static IReadOnlyList<string> PatchFiles(string? appRoot) =>
        CursorPatchTable.Files
            .Select(rel => string.IsNullOrEmpty(appRoot) ? rel : Path.Combine(appRoot!, rel))
            .ToArray();

    /// <summary>记忆文件：~/.breach-cursor-path.json（可用 CURSOR_PATH_MEMORY_FILE 重定向）。</summary>
    public static string MemoryFile
    {
        get
        {
            var over = Env(MemoryFileEnv);
            return over ?? Path.Combine(UserProfile, MemoryFileName);
        }
    }

    // ---------------------------------------------------------------- 判据

    /// <summary>
    /// 安装根的判据：<c>resources\app\out\main.js</c> 与 <c>resources\app\extensions\cursor-agent-host</c>
    /// **两个都要在**（docs/cursor.md 第一节：没有 asar，out\ 是目录）。
    /// </summary>
    public static bool IsInstallRoot(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (!File.Exists(Path.Combine(dir, AppMainRelative))) return false;
            return Directory.Exists(Path.Combine(dir, AgentHostRelative));
        }
        catch { /* 权限 */ }
        return false;
    }

    /// <summary>配置根的判据：目录名是 Cursor 且含 User，或含 User\globalStorage。</summary>
    public static bool IsConfigRoot(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (Directory.Exists(Path.Combine(dir, UserDirName, GlobalStorageDirName))) return true;
            return Path.GetFileName(dir.TrimEnd('\\', '/')).Equals(ConfigDirName, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(Path.Combine(dir, UserDirName));
        }
        catch { /* 权限 */ }
        return false;
    }

    /// <summary>Electron 主程序 exe 的版本号（读 PE 的 FileVersion；读不到就 null，不猜）。</summary>
    public static string? VersionOf(string? exe)
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

    /// <summary>注册表卸载项：DisplayName 以 Cursor 开头，取 InstallLocation（本机实测 "Cursor (User)" → E:\APP\cursor\）。</summary>
    public static IReadOnlyList<string> FromRegistry()
    {
        var found = new List<string>();
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey(sub);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var app = key.OpenSubKey(name);
                        var display = app?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(display)) continue;
                        // 判据与阶段 0 侦察一致：DisplayName -match '^Cursor'
                        if (!display!.StartsWith("Cursor", StringComparison.OrdinalIgnoreCase)) continue;
                        var loc = Clean(app?.GetValue("InstallLocation") as string);
                        if (loc is not null) found.Add(loc);
                        var icon = Clean((app?.GetValue("DisplayIcon") as string)?.Split(',')[0]);
                        var iconDir = icon is null ? null : Path.GetDirectoryName(icon);
                        if (!string.IsNullOrEmpty(iconDir)) found.Add(iconDir!);
                    }
                    catch { /* 权限或值类型不对 */ }
                }
            }
            catch { /* hive 打不开 */ }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>嗅探候选（附加级，最低优先）。</summary>
    public static IReadOnlyList<string> SniffCandidates()
    {
        var list = new List<string>();
        var install = DefaultInstallRoot();
        if (install.Length > 0) list.Add(install);
        var localAppData = Env("LOCALAPPDATA");
        if (localAppData is not null)
        {
            list.Add(Path.Combine(localAppData, "Programs", "Cursor"));
            list.Add(Path.Combine(localAppData, "Cursor"));
        }
        var programFiles = Env("ProgramFiles");
        if (programFiles is not null) list.Add(Path.Combine(programFiles, "Cursor"));

        foreach (var drive in PathProbe.Drives())
            foreach (var tail in new[] { "Cursor", "cursor", Path.Combine("Program Files", "Cursor"), @"APP\cursor" })
                list.Add(Path.Combine(drive, tail));

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ---------------------------------------------------------------- 主流程

    public static CursorLocation Resolve(LocateOptions? options, bool remember, CancellationToken ct)
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

        var defaultConfig = DefaultConfigHome();
        var defaultInstall = DefaultInstallRoot();

        string? installRoot = null;
        string? exe = null;
        string? configHome = null;
        string? workspace = null;
        string? workspaceSource = null;
        string? locatedBy = null;

        // ---- 1. CLI 显式指定 ----
        // 一个 --root 只能声明一种东西：安装根 / Cursor.exe / 配置根 / 工作区。无效即 fail-closed。
        var explicitRoot = Clean(options?.ExplicitRoot);
        if (explicitRoot is not null)
        {
            if (File.Exists(explicitRoot) &&
                Path.GetFileName(explicitRoot).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            {
                installRoot = Path.GetDirectoryName(explicitRoot);
                exe = explicitRoot;
                locatedBy = "cli:--root(Cursor.exe)";
                Add("cli", $"--root {explicitRoot}", installRoot is not null && IsInstallRoot(installRoot!),
                    installRoot is null ? "取不到父目录" : $"安装根 {installRoot}");
            }
            else if (IsInstallRoot(explicitRoot!))
            {
                installRoot = explicitRoot;
                exe = Path.Combine(explicitRoot!, ExeName);
                locatedBy = "cli:--root(安装根)";
                Add("cli", $"--root {explicitRoot}", true, $"安装根 {installRoot}");
            }
            else if (IsConfigRoot(explicitRoot!))
            {
                configHome = explicitRoot;
                locatedBy = "cli:--root(配置根)";
                Add("cli", $"--root {explicitRoot}", true, $"配置根 {configHome}");
            }
            else if (Directory.Exists(explicitRoot!))
            {
                // 既不是安装根也不是配置根：当作工作区（层 3 的落点）——但只有真的能落 .cursor\rules 才算数。
                workspace = explicitRoot;
                workspaceSource = "cli:--root(工作区)";
                locatedBy = "cli:--root(工作区)";
                Add("cli", $"--root {explicitRoot}", true, $"工作区 {workspace}");
            }
            else
            {
                Add("cli", $"--root {explicitRoot}", false,
                    "无效：既不是安装根（要有 resources\\app\\out\\main.js 与 extensions\\cursor-agent-host），也不是 Cursor.exe / 配置根 / 已存在的目录");
                notes.Add(Notes.Warn("显式路径无效（fail-closed）",
                    $"显式指定的路径解释不出 Cursor 的安装根 / 配置根 / 工作区：{explicitRoot}。" +
                    $"安装根判据：{AppMainRelative} 与 {AgentHostRelative} 同时存在；配置根是 %APPDATA%\\Cursor；" +
                    "按纪律不回退、不猜其它位置。", "cli"));
                notes.Add(Notes.Info("怎么指定",
                    $"允许传：安装根（本机 {installRoot ?? @"E:\APP\cursor"}）、{ExeName}、配置根（{defaultConfig}）、或工作区目录。" +
                    $"也可以用环境变量 {RootEnv} / {ConfigEnv} / {WorkspaceEnv}。", "cli"));
                return Fail(attempts, notes, defaultConfig, sniffCandidates, sniffHits);
            }
        }

        // ---- 2. 环境变量（本工具约定）----
        var envRoot = Clean(Env(RootEnv));
        if (envRoot is not null)
        {
            var ok = IsInstallRoot(envRoot!);
            Add("env", $"{RootEnv}={envRoot}", ok, ok ? $"安装根 {envRoot}" : "不像安装根");
            if (ok) { installRoot ??= envRoot; locatedBy ??= $"{RootEnv}（本工具约定变量）"; }
            else if (IsConfigRoot(envRoot!)) { configHome ??= envRoot; locatedBy ??= $"{RootEnv}（本工具约定变量，配置根）"; }
            else notes.Add(Notes.Warn("环境变量无效",
                $"{RootEnv}={envRoot} 既不是安装根也不是配置根，已忽略（继续往下找）。" +
                $"注意：{RootEnv} / {ConfigEnv} / {WorkspaceEnv} 是本工具的约定变量，不是 Cursor 官方变量。", RootEnv));
        }
        else Add("env", $"({RootEnv} 未设置)", false, "本工具约定变量");

        var envCfg = Clean(Env(ConfigEnv));
        if (envCfg is not null)
        {
            Add("env", $"{ConfigEnv}={envCfg}", IsConfigRoot(envCfg!) || Directory.Exists(envCfg!),
                "本工具约定变量（配置根）");
            if (Directory.Exists(envCfg!)) configHome ??= envCfg;
        }
        else Add("env", $"({ConfigEnv} 未设置)", false, "本工具约定变量");

        var envWs = Clean(Env(WorkspaceEnv));
        if (envWs is not null)
        {
            Add("env", $"{WorkspaceEnv}={envWs}", Directory.Exists(envWs!), "本工具约定变量（工作区，层 3 落点）");
            if (Directory.Exists(envWs!)) { workspace ??= envWs; workspaceSource ??= $"{WorkspaceEnv}（本工具约定变量）"; }
        }
        else Add("env", $"({WorkspaceEnv} 未设置)", false, "本工具约定变量");

        // ---- 3. 记忆文件 ----
        if (options?.IgnoreMemory == true)
        {
            Add("memory", "(IgnoreMemory，跳过)", false, null);
        }
        else
        {
            var memRoot = Clean(PathMemory.Load(MemoryFile, "root"));
            if (memRoot is not null)
            {
                var ok = IsInstallRoot(memRoot!);
                Add("memory", memRoot!, ok, ok ? "安装根" : "失效");
                if (ok) { installRoot ??= memRoot; exe ??= Path.Combine(memRoot!, ExeName); locatedBy ??= $"memory（{MemoryFileName}）"; }
                else if (IsConfigRoot(memRoot!)) configHome ??= memRoot;
            }
            else Add("memory", "(无)", false, "(无记忆文件)");

            var memCfg = Clean(PathMemory.Load(MemoryFile, "configHome"));
            if (memCfg is not null && Directory.Exists(memCfg))
            {
                Add("memory", $"configHome={memCfg}", configHome is null, configHome is null ? "命中" : "已有更优先的来源，忽略");
                configHome ??= memCfg;
            }

            var memWs = Clean(PathMemory.Load(MemoryFile, "workspace"));
            if (memWs is not null && Directory.Exists(memWs))
            {
                Add("memory", $"workspace={memWs}", workspace is null, workspace is null ? "命中" : "已有更优先的来源，忽略");
                if (workspace is null) { workspace = memWs; workspaceSource = $"memory（{MemoryFileName}）"; }
            }
        }

        // ---- 4. 运行中进程 Cursor ----
        if (installRoot is null)
        {
            var procExe = PathProbe.FromProcess(ProcessName);
            if (!string.IsNullOrEmpty(procExe))
            {
                var dir = Path.GetDirectoryName(procExe!);
                var ok = dir is not null && IsInstallRoot(dir);
                Add("process", procExe!, ok, ok ? $"安装根 {dir}" : "推不出安装根");
                if (ok)
                {
                    installRoot = dir;
                    exe = procExe;
                    locatedBy ??= $"process（运行中的 {ProcessName}）";
                }
            }
            else Add("process", $"{ProcessName}（未运行）", false, null);
        }

        // ---- 5. 注册表卸载项（DisplayName ^Cursor → InstallLocation）----
        if (installRoot is null)
        {
            var reg = FromRegistry();
            string? hit = null;
            foreach (var candidate in reg)
            {
                ct.ThrowIfCancellationRequested();
                if (IsInstallRoot(candidate)) { hit ??= candidate; sniffHits.Add(candidate); }
            }
            Add("registry", hit ?? $"(DisplayName ^Cursor 的 InstallLocation，共 {reg.Count} 个候选)", hit is not null,
                hit is null ? null : $"命中 {hit}");
            if (hit is not null)
            {
                installRoot = hit;
                locatedBy ??= $"registry（{hit}）";
            }
        }

        // ---- 6. 安装目录兜底 %LOCALAPPDATA%\Programs\cursor ----
        if (installRoot is null && defaultInstall.Length > 0)
        {
            var ok = IsInstallRoot(defaultInstall);
            Add("install", defaultInstall, ok,
                ok ? "安装目录" : Directory.Exists(defaultInstall) ? "目录在，但不是 Cursor 安装根" : "目录不存在");
            if (ok)
            {
                installRoot = defaultInstall;
                locatedBy ??= $"install（{defaultInstall}）";
            }
        }

        // ---- 7. 嗅探（附加级）----
        if (installRoot is null)
        {
            string? sniffRoot = null;
            foreach (var c in sniffCandidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsInstallRoot(c)) continue;
                sniffHits.Add(c);
                sniffRoot ??= c;
            }
            Add("sniff", sniffRoot ?? "(未命中)", sniffRoot is not null,
                sniffRoot is null ? null : $"命中 {sniffHits.Count} 个候选");
            if (sniffRoot is not null)
            {
                installRoot = sniffRoot;
                locatedBy ??= $"sniff（{sniffRoot}）";
            }
        }

        // ---- 8. 配置根兜底 ----
        configHome ??= defaultConfig;
        if (Directory.Exists(configHome)) Add("config", configHome, true, "配置根（个人规则层与会话库所在）");

        if (exe is null && installRoot is not null)
        {
            var candidate = Path.Combine(installRoot, ExeName);
            exe = File.Exists(candidate) ? candidate : null;
        }

        var configExists = Directory.Exists(configHome);
        if (installRoot is null && !configExists)
        {
            notes.Add(Notes.Error("未找到 Cursor",
                $"多级探测（CLI --root → 环境变量 {RootEnv}/{ConfigEnv} → 记忆文件 → 运行进程 → 注册表 → {defaultInstall} → %APPDATA%\\Cursor）全部未命中。" +
                $"可用 --root <DIR> 显式指定安装根、{ExeName}、配置根（{defaultConfig}）或工作区目录。", "locate"));
            return Fail(attempts, notes, configHome, sniffCandidates, sniffHits);
        }

        if (installRoot is null)
            notes.Add(Notes.Warn("没找到安装根",
                $"只定位到配置根（{configHome}），没有定位到 {AppMainRelative}。" +
                "对本目标的后果比别的目标重：补丁层（层 1）在安装根里，缺它时 apply 会 fail-closed 拒绝写入。" +
                "层 2（state.vscdb）与层 3（工作区 .mdc）本身不需要安装根。", "locate"));

        // ---- 9. 工作区（层 3）----
        if (workspace is null)
        {
            var (ws, src) = CursorWorkspace.Resolve(configHome, ct, out var wsNote);
            workspace = ws;
            workspaceSource = src;
            Add("workspace", src ?? "(解析不到工作区)", ws is not null, wsNote);
        }

        return Found(installRoot, exe, configHome, workspace, workspaceSource, locatedBy ?? "unknown",
            attempts, sniffCandidates, sniffHits, notes, remember, ct);
    }

    private static CursorLocation Fail(
        IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<Note> notes,
        string configHome, IReadOnlyList<string> sniffCandidates, IReadOnlyList<string> sniffHits) =>
        new()
        {
            Found = false,
            ConfigHome = configHome,
            ProfileDir = ProfileDir(configHome),
            Attempts = attempts,
            SniffCandidates = sniffCandidates,
            SniffHits = sniffHits.Count,
            Notes = notes,
        };

    private static CursorLocation Found(
        string? installRoot, string? exe, string configHome, string? workspace, string? workspaceSource,
        string locatedBy, IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<string> sniffCandidates,
        IReadOnlyList<string> sniffHits, IReadOnlyList<Note> notes, bool remember, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var appRoot = installRoot is null ? null : AppRoot(installRoot);
        var bundle = appRoot is null ? null : Path.Combine(appRoot, CursorPatchTable.PrimaryFile);
        if (bundle is not null && !File.Exists(bundle)) bundle = null;

        var version = VersionOf(exe);
        var merged = new List<Note>(notes);
        if (installRoot is not null)
            merged.Add(Notes.Info("安装根",
                $"{installRoot}（{ExeName} {FileText.Human(LengthOf(exe))}，版本 {version ?? "读不到"}）；" +
                $"补丁层在 {appRoot}（**没有 asar**，4 个 JS 文件明文，逐字节等长替换）", "locate"));
        merged.Add(Notes.Info("配置根", $"{configHome}（数据根 {ProfileDir(configHome)}）", ConfigEnv));
        merged.Add(Notes.Info("会话库", DbPath(configHome), DbPath(configHome)));
        merged.Add(Notes.Info("契约缺口：SessionsDir",
            "契约里 LocateResult.SessionsDir 由引擎拼成 <ConfigHome>\\sessions，对 Cursor 不适用——" +
            $"会话与助手正文都在 {DbPath(configHome)} 的 cursorDiskKV 表里（SQLite）。本工具不改契约，写进 Notes。",
            DbPath(configHome)));
        merged.Add(Notes.Info("本工具约定变量",
            $"{RootEnv} / {ConfigEnv} / {WorkspaceEnv} / {MemoryFileEnv} 是本工具约定的变量，Cursor 官方没有它们；" +
            "本机也没有 CURSOR_USER_DATA_DIR 这个变量，所以不拿它当配置根来源。" +
            $"官方位置是 %APPDATA%\\Cursor（配置根，VS Code 风格）。", ConfigEnv));
        merged.Add(Notes.Info("工作区（层 3）",
            workspace is null
                ? $"没解析到工作区（来源：{workspaceSource ?? "无"}）：层 3 会报 TargetAbsent 并跳过，" +
                  $"**不往 C:\\ 或当前目录瞎写**（docs/cursor.md 的落点是 <workspace>\\.cursor\\rules\\*.mdc）。"
                : $"工作区 {workspace}（来源：{workspaceSource}），层 3 落点 {CursorRules.PathFor(workspace!)}。",
            workspace ?? configHome));
        if (!Directory.Exists(DbPath(configHome)))
            merged.Add(Notes.Info("会话库不存在",
                $"还没有 state.vscdb：{DbPath(configHome)}（Cursor 没在这台机器上落过会话）。" +
                "层 2 与日志层都会报 TargetAbsent；本工具**不创建**这个库。", DbPath(configHome)));

        if (remember)
        {
            var rootToSave = installRoot ?? configHome;
            if (!string.Equals(PathMemory.Load(MemoryFile, "root"), rootToSave, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(PathMemory.Load(MemoryFile, "configHome"), configHome, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(PathMemory.Load(MemoryFile, "workspace"), workspace, StringComparison.OrdinalIgnoreCase))
                Remember(rootToSave, configHome, workspace);
        }

        return new CursorLocation
        {
            Found = true,
            Root = installRoot ?? configHome,
            Exe = exe,
            AppRoot = appRoot,
            Bundle = bundle,
            ConfigHome = configHome,
            ProfileDir = ProfileDir(configHome),
            WorkspaceDir = workspace,
            WorkspaceSource = workspaceSource,
            Version = version,
            LocatedBy = locatedBy,
            RootIsNotInstall = installRoot is null,
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

    /// <summary>记忆文件同时记住三个根（PathMemory.Save 一次只写一个属性）。</summary>
    private static void Remember(string root, string configHome, string? workspace)
    {
        try
        {
            var map = new Dictionary<string, string>
            {
                ["root"] = root,
                ["configHome"] = configHome,
                ["savedAt"] = DateTimeOffset.Now.ToString("o"),
            };
            if (workspace is not null) map["workspace"] = workspace;
            FileText.Write(MemoryFile, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 记忆失败不致命 */ }
    }
}

/// <summary>
/// 工作区解析（层 3 的落点）。纪律：**不要猜**——只认「recent paths 里指向一个真实存在目录」的条目，
/// 解析不到就返回 null，由调用方报 TargetAbsent 并跳过本层。
///
/// 来源顺序：
///   1 <c>%APPDATA%\Cursor\User\globalStorage\storage.json</c> 的 <c>history.recentlyOpenedPathsList</c>
///   2 <c>state.vscdb</c> → <c>ItemTable</c> 键 <c>history.recentlyOpenedPathsList</c>（同一份数据的另一个落点）
///
/// 本机实测：该键只有一条 <c>fileUri</c>，指向一个**文件**（cordis.patch.yml），不是目录 →
/// 解析结果为空 → 层 3 报 TargetAbsent。这正是"不猜"该有的样子。
/// </summary>
public static class CursorWorkspace
{
    public static (string? Dir, string? Source) Resolve(string configHome, CancellationToken ct, out string? note)
    {
        note = null;
        var storageJson = CursorLocator.StorageJsonPath(configHome);
        var json = FileText.ReadOrNull(storageJson);
        if (json is not null)
        {
            var (dir, why) = ParseRecent(json);
            if (dir is not null) return (dir, $"storage.json（{storageJson}）");
            note = why;
        }

        var db = CursorLocator.DbPath(configHome);
        if (File.Exists(db))
        {
            ct.ThrowIfCancellationRequested();
            string? value;
            try
            {
                value = CursorDb.ReadItemValue(db, "history.recentlyOpenedPathsList");
            }
            catch (Exception ex)
            {
                // 定位阶段不因为库坏了就抛：工作区解析不出来就是"解析不到"，层 3 会 TargetAbsent 跳过。
                // 真正需要 fail-closed 的地方（层 2 / 日志层）照旧抛 IoError。
                note = $"会话库读不出来（{ex.GetType().Name}: {ex.Message}）：跳过工作区自动解析。";
                return (null, null);
            }
            if (value is not null)
            {
                var (dir, why) = ParseRecent(value);
                if (dir is not null) return (dir, $"state.vscdb（{db}）的 history.recentlyOpenedPathsList");
                note = why;
            }
            else if (note is null)
            {
                note = "ItemTable 里没有 history.recentlyOpenedPathsList（Cursor 还没打开过工作区目录）";
            }
        }
        else if (note is null)
        {
            note = $"会话库不存在：{db}";
        }

        return (null, null);
    }

    /// <summary>解析 recent paths：只接受能还原成「真实存在目录」的 folderUri / fileUri。</summary>
    private static (string? Dir, string Why) ParseRecent(string json)
    {
        string? firstReject = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                return (null, "recentlyOpenedPathsList 里没有 entries 数组");

            var seen = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                foreach (var prop in new[] { "folderUri", "fileUri" })
                {
                    if (!entry.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) continue;
                    var raw = v.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    seen++;
                    var local = ToLocalPath(raw!);
                    if (local is null)
                    {
                        firstReject ??= $"条目 {prop}={raw} 解析不出本地路径";
                        continue;
                    }
                    if (Directory.Exists(local)) return (local, "");
                    firstReject ??= $"条目 {prop}={raw} 指向的不是现存目录（{local}）";
                }
            }

            return (null, firstReject ?? (seen == 0
                ? "recentlyOpenedPathsList 里没有任何 folderUri / fileUri 条目"
                : "recentlyOpenedPathsList 里没有指向现存目录的条目"));
        }
        catch (JsonException ex)
        {
            return (null, $"recentlyOpenedPathsList 不是合法 JSON：{ex.Message}");
        }
    }

    private static string? ToLocalPath(string uri)
    {
        try
        {
            if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var local = new Uri(uri).LocalPath;
                return string.IsNullOrWhiteSpace(local) ? null : local;
            }
            // 少数版本直接存盘符路径
            return Path.IsPathRooted(uri) ? uri : null;
        }
        catch { return null; }
    }
}
