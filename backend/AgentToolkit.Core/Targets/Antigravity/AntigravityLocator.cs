using System.Diagnostics;
using System.Text.Json;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Antigravity;

/// <summary>Antigravity 的定位结果：安装根 / 配置根 / 数据根 + 「试过哪些路径」的完整诊断。</summary>
public sealed class AntigravityLocation
{
    public bool Found { get; init; }

    /// <summary>安装根（%LOCALAPPDATA%\Programs\antigravity）；只给配置根/数据根时退化为那一层。</summary>
    public string? Root { get; init; }

    /// <summary>Electron 主程序 Antigravity.exe；没定位到就是 null。</summary>
    public string? Exe { get; init; }

    /// <summary>承载常驻提示词的原生二进制 resources\bin\language_server.exe（本轮只报告，不打补丁）。</summary>
    public string? Bundle { get; init; }

    /// <summary>配置根 ~/.gemini/config（规则层 / 技能层 / 权限层都在这里）。</summary>
    public required string ConfigHome { get; init; }

    /// <summary>数据根 ~/.gemini/antigravity（conversations / brain / builtin）。</summary>
    public required string DataHome { get; init; }

    public string? Version { get; init; }
    public string? LocatedBy { get; init; }

    /// <summary>Root 只代表配置根或数据根（没定位到安装根）时为真。</summary>
    public bool RootIsNotInstall { get; init; }

    public IReadOnlyList<LocateAttempt> Attempts { get; init; } = Array.Empty<LocateAttempt>();
    public IReadOnlyList<string> SniffCandidates { get; init; } = Array.Empty<string>();
    public int SniffHits { get; init; }
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();

    // ---------------- 由三个根推出来的实际路径 ----------------

    public string ConfigFile => AntigravityLocator.ConfigFile(ConfigHome);
    public string RulesDir => AntigravityLocator.RulesDir(ConfigHome);
    public string AgentsPath => AntigravityLocator.AgentsPath(ConfigHome);
    public string ExtraPath => AntigravityLocator.ExtraPath(ConfigHome);
    public string SkillsDir => AntigravityLocator.SkillsDir(ConfigHome);
    public string SkillPath => AntigravityLocator.SkillPath(ConfigHome);
    public string ConversationsDir => AntigravityLocator.ConversationsDir(DataHome);
    public string BrainDir => AntigravityLocator.BrainDir(DataHome);
    public string BuiltinSkillsDir => AntigravityLocator.BuiltinSkillsDir(DataHome);
}

/// <summary>
/// Antigravity 定位器。目标布局（本机实测，见 docs/gemini.md）：
/// <code>
/// %LOCALAPPDATA%\Programs\antigravity\Antigravity.exe                  Electron 外壳（VS Code 分支，246 MB）
/// …\resources\bin\language_server.exe                                 原生语言服务器（171 MB，智能体与常驻提示词在这里）
/// ~/.gemini/config/config.json                                        权限层（userSettings）
/// ~/.gemini/config/rules\AGENTS.md                                    规则层（本工具管理的全局规则文件）
/// ~/.gemini/config/skills\&lt;name&gt;\SKILL.md                             技能层（按需加载）
/// ~/.gemini/antigravity/conversations\&lt;cascade id&gt;.db                日志层（SQLite + protobuf，推理原文只在这里）
/// ~/.gemini/antigravity/brain\&lt;会话 id&gt;/…                              transcript_full.jsonl（没有推理，只作参考）
/// </code>
///
/// 解析顺序：CLI --root（允许传安装根 / exe / 配置根 / 数据根）→ 环境变量
///          → 记忆文件 ~/.breach-antigravity-path.json → 运行进程 Antigravity
///          → 安装目录 %LOCALAPPDATA%\Programs\antigravity → ~/.gemini/config、~/.gemini/antigravity。
///
/// 显式指定的路径无效时 **fail-closed**：不回退、不猜，返回 NotFound 并把「试过哪些路径」填进 Attempts。
/// 注意：ANTIGRAVITY_ROOT / ANTIGRAVITY_CONFIG / ANTIGRAVITY_HOME 是本工具的约定变量，
/// **不是** Antigravity 官方变量（官方没有提供指向这三层的环境变量）——见 Notes。
/// </summary>
public static class AntigravityLocator
{
    public const string ProcessName = "Antigravity";
    public const string ExeName = "Antigravity.exe";
    public const string InstallDirName = "antigravity";

    /// <summary>承载常驻提示词的原生二进制（相对安装根）。</summary>
    public const string BundleRelative = @"resources\bin\language_server.exe";

    /// <summary>本工具约定的安装根变量（Antigravity 自己没有这个变量）。</summary>
    public const string RootEnv = "ANTIGRAVITY_ROOT";
    /// <summary>本工具约定的配置根变量（Antigravity 自己没有这个变量）。</summary>
    public const string ConfigEnv = "ANTIGRAVITY_CONFIG";
    /// <summary>本工具约定的数据根变量（Antigravity 自己没有这个变量）。</summary>
    public const string DataEnv = "ANTIGRAVITY_HOME";
    /// <summary>记忆文件重定向（自检用它把记忆钉在沙箱里，避免污染真实家目录）。</summary>
    public const string MemoryFileEnv = "ANTIGRAVITY_PATH_MEMORY_FILE";

    public const string MemoryFileName = ".breach-antigravity-path.json";
    public const string GeminiDirName = ".gemini";
    public const string ConfigDirName = "config";
    public const string DataDirName = "antigravity";
    public const string ConfigFileName = "config.json";
    public const string RulesDirName = "rules";
    public const string SkillsDirName = "skills";
    public const string ConversationsDirName = "conversations";

    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } p
            ? p
            : Environment.GetEnvironmentVariable("USERPROFILE") ?? ".";

    public static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v!.Trim();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim().Trim('"');

    /// <summary>配置根默认值：~/.gemini/config。</summary>
    public static string DefaultConfigHome() => Path.Combine(UserProfile, GeminiDirName, ConfigDirName);

    /// <summary>数据根默认值：~/.gemini/antigravity。</summary>
    public static string DefaultDataHome() => Path.Combine(UserProfile, GeminiDirName, DataDirName);

    /// <summary>桌面安装根默认值：%LOCALAPPDATA%\Programs\antigravity。</summary>
    public static string DefaultInstallRoot()
    {
        var localAppData = Env("LOCALAPPDATA")
                           ?? (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } p
                               ? p
                               : "");
        return string.IsNullOrEmpty(localAppData)
            ? ""
            : Path.Combine(localAppData, "Programs", InstallDirName);
    }

    public static string ConfigFile(string configHome) => Path.Combine(configHome, ConfigFileName);
    public static string RulesDir(string configHome) => Path.Combine(configHome, RulesDirName);
    public static string AgentsPath(string configHome) => Path.Combine(RulesDir(configHome), AntigravityRules.AgentsFileName);
    public static string ExtraPath(string configHome) => Path.Combine(RulesDir(configHome), AntigravityRules.ExtraFileName);
    public static string SkillsDir(string configHome) => Path.Combine(configHome, SkillsDirName);

    /// <summary>本目标报告的技能层路径（本轮只报告是否已投放，不写入）。</summary>
    public static string SkillPath(string configHome) => Path.Combine(SkillsDir(configHome), AntigravityRules.SkillName, "SKILL.md");

    public static string ConversationsDir(string dataHome) => Path.Combine(dataHome, ConversationsDirName);
    public static string BrainDir(string dataHome) => Path.Combine(dataHome, "brain");
    public static string BuiltinSkillsDir(string dataHome) => Path.Combine(dataHome, "builtin", "skills");

    /// <summary>记忆文件：~/.breach-antigravity-path.json（可用 ANTIGRAVITY_PATH_MEMORY_FILE 重定向）。</summary>
    public static string MemoryFile
    {
        get
        {
            var over = Env(MemoryFileEnv);
            return over ?? Path.Combine(UserProfile, MemoryFileName);
        }
    }

    // ---------------------------------------------------------------- 判据

    /// <summary>安装根的判据：有 Antigravity.exe，或至少有 resources\bin\language_server.exe。</summary>
    public static bool IsInstallRoot(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, ExeName))) return true;
            if (File.Exists(Path.Combine(dir, BundleRelative))) return true;
        }
        catch { /* 权限 */ }
        return false;
    }

    /// <summary>配置根的判据：目录名是 config 且父目录是 .gemini，或含 config.json / rules / skills / plugins。</summary>
    public static bool IsConfigRoot(string dir)
    {
        try
        {
            var self = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var parent = Path.GetFileName(Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? "");
            if (self.Equals(ConfigDirName, StringComparison.OrdinalIgnoreCase) &&
                parent.Equals(GeminiDirName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var f in new[] { ConfigFileName, "mcp_config.json" })
                if (File.Exists(Path.Combine(dir, f))) return true;
            foreach (var d in new[] { RulesDirName, SkillsDirName, "plugins", "sidecars", "projects" })
                if (Directory.Exists(Path.Combine(dir, d))) return true;
        }
        catch { /* 权限 */ }
        return false;
    }

    /// <summary>数据根的判据：目录名是 antigravity 且父目录是 .gemini，或含 conversations / brain / builtin。</summary>
    public static bool IsDataRoot(string dir)
    {
        try
        {
            var self = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var parent = Path.GetFileName(Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? "");
            if (self.Equals(DataDirName, StringComparison.OrdinalIgnoreCase) &&
                parent.Equals(GeminiDirName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var d in new[] { ConversationsDirName, "brain", "builtin" })
                if (Directory.Exists(Path.Combine(dir, d))) return true;
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

    /// <summary>由 language_server.exe 反推安装根：&lt;root&gt;\resources\bin\ → &lt;root&gt;。</summary>
    private static string? InstallRootFromBundle(string bundlePath)
    {
        var bin = Path.GetDirectoryName(bundlePath);
        if (bin is null) return null;
        var resources = Path.GetDirectoryName(bin);
        if (resources is null) return null;
        return Path.GetDirectoryName(resources);
    }

    /// <summary>一次「把用户给的路径解释成什么」的结论。</summary>
    private sealed class Interpreted
    {
        public string? InstallRoot { get; init; }
        public string? Exe { get; init; }
        public string? Bundle { get; init; }
        public string? ConfigRoot { get; init; }
        public string? DataRoot { get; init; }
        public required string How { get; init; }

        public string Describe()
        {
            var parts = new List<string>();
            if (InstallRoot is not null) parts.Add($"安装根 {InstallRoot}");
            if (ConfigRoot is not null) parts.Add($"配置根 {ConfigRoot}");
            if (DataRoot is not null) parts.Add($"数据根 {DataRoot}");
            return $"{How}：" + string.Join("；", parts);
        }
    }

    /// <summary>把一个路径解释成安装根 / 配置根 / 数据根之一（或 exe）。解释不出返回 null。</summary>
    private static Interpreted? Interpret(string raw)
    {
        var p = Clean(raw);
        if (p is null) return null;

        if (File.Exists(p))
        {
            var name = Path.GetFileName(p);
            if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(p)!;
                return MakeInstall(dir, p, "cli:--root(Antigravity.exe)");
            }
            if (name.Equals("language_server.exe", StringComparison.OrdinalIgnoreCase))
            {
                var root = InstallRootFromBundle(p);
                if (root is not null) return MakeInstall(root, Path.Combine(root, ExeName), "cli:--root(language_server.exe)");
            }
            if (name.Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase))
                return new Interpreted { ConfigRoot = Path.GetDirectoryName(p), How = "cli:--root(config.json)" };
            return null;
        }

        if (!Directory.Exists(p)) return null;

        if (IsInstallRoot(p)) return MakeInstall(p, Path.Combine(p, ExeName), "cli:--root(安装根)");

        // resources 目录 → 退回安装根
        if (Path.GetFileName(p.TrimEnd('\\', '/')).Equals("resources", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetDirectoryName(p.TrimEnd('\\', '/'));
            if (root is not null && IsInstallRoot(root)) return MakeInstall(root, Path.Combine(root, ExeName), "cli:--root(resources)");
        }

        if (IsConfigRoot(p)) return new Interpreted { ConfigRoot = p, How = "cli:--root(配置根)" };
        if (IsDataRoot(p)) return new Interpreted { DataRoot = p, How = "cli:--root(数据根)" };

        // 配置根与数据根的共同父目录（~/.gemini）：同时解释出两层
        if (LooksLikeGeminiRoot(p))
            return new Interpreted
            {
                ConfigRoot = Path.Combine(p, ConfigDirName),
                DataRoot = Path.Combine(p, DataDirName),
                How = "cli:--root(.gemini)",
            };

        return null;
    }

    /// <summary>是不是配置根与数据根的共同父目录：名字叫 .gemini，或同时含 config 与 antigravity 两个子目录。</summary>
    public static bool LooksLikeGeminiRoot(string dir)
    {
        try
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (name.Equals(GeminiDirName, StringComparison.OrdinalIgnoreCase)) return true;
            return Directory.Exists(Path.Combine(dir, ConfigDirName)) &&
                   Directory.Exists(Path.Combine(dir, DataDirName));
        }
        catch { return false; }
    }

    private static Interpreted MakeInstall(string root, string exe, string how)
    {
        var bundle = Path.Combine(root, BundleRelative);
        return new Interpreted
        {
            InstallRoot = root,
            Exe = File.Exists(exe) ? exe : null,
            Bundle = File.Exists(bundle) ? bundle : null,
            How = how,
        };
    }

    /// <summary>嗅探候选（附加级，最低优先）：只在别处都落空时才走，且必须真含安装布局才算命中。</summary>
    public static IReadOnlyList<string> SniffCandidates()
    {
        var list = new List<string>();
        var localAppData = Env("LOCALAPPDATA");
        var programFiles = Env("ProgramFiles");
        var home = UserProfile;

        var install = DefaultInstallRoot();
        if (install.Length > 0) list.Add(install);
        if (localAppData is not null)
        {
            list.Add(Path.Combine(localAppData, "Programs", "Antigravity"));
            list.Add(Path.Combine(localAppData, "Antigravity"));
            list.Add(Path.Combine(localAppData, InstallDirName));
        }
        if (programFiles is not null)
        {
            list.Add(Path.Combine(programFiles, "Antigravity"));
            list.Add(Path.Combine(programFiles, InstallDirName));
        }
        list.Add(Path.Combine(home, InstallDirName));
        list.Add(Path.Combine(home, "AppData", "Local", "Programs", InstallDirName));

        foreach (var drive in PathProbe.Drives())
            foreach (var tail in new[] { InstallDirName, "Antigravity", Path.Combine("Program Files", "Antigravity") })
                list.Add(Path.Combine(drive, tail));

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ---------------------------------------------------------------- 主流程

    public static AntigravityLocation Resolve(LocateOptions? options, bool remember, CancellationToken ct)
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
        var defaultData = DefaultDataHome();
        var defaultInstall = DefaultInstallRoot();

        string? installRoot = null;
        string? exe = null;
        string? bundle = null;
        string? configHome = null;
        string? dataHome = null;
        string? locatedBy = null;

        // 三根是**三条独立的轴**：--root 只填它自己声明的那一层，剩下的层继续按
        // 环境变量 → 记忆文件 → 进程 → 安装目录 → 默认位置往下找。
        // （把 --root 当成"其它层一律不许探测"会把"指定安装根"变成"配置根悄悄换成默认位置"。）

        // ---- 1. CLI 显式指定（无效即 fail-closed） ----
        var explicitRoot = Clean(options?.ExplicitRoot);
        var explicitCfg = Clean(options?.ExplicitConfigHome);

        if (explicitRoot is not null)
        {
            var it = Interpret(explicitRoot!);
            Add("cli", $"--root {explicitRoot}", it is not null,
                it is null ? "无效：既不是安装根 / Antigravity.exe / language_server.exe，也不是配置根 / 数据根" : it.Describe());
            if (it is null)
            {
                notes.Add(Notes.Warn("显式路径无效（fail-closed）",
                    $"显式指定的路径解释不出 Antigravity 的安装根 / 配置根 / 数据根：{explicitRoot}。" +
                    $"判据：安装根要有 {ExeName} 或 {BundleRelative}；配置根是 ~/.gemini/config（含 config.json / rules / skills / plugins）；" +
                    "数据根是 ~/.gemini/antigravity（含 conversations / brain / builtin）。按纪律不回退、不猜其它位置。", "cli"));
                notes.Add(Notes.Info("怎么指定",
                    $"允许传：安装根（{defaultInstall}）、Antigravity.exe、resources\\bin\\language_server.exe、" +
                    $"配置根（{defaultConfig}）、数据根（{defaultData}）、或它们的共同父目录 ~/.gemini。", "cli"));
                return Fail(attempts, notes, defaultConfig, defaultData, sniffCandidates, sniffHits);
            }
            Apply(it, ref installRoot, ref exe, ref bundle, ref configHome, ref dataHome);
            locatedBy = it.How;
        }

        if (explicitCfg is not null)
        {
            var ok = Directory.Exists(explicitCfg) || IsConfigRoot(explicitCfg!);
            Add("cli", $"配置根 {explicitCfg}", ok, "来自 LocateOptions.ExplicitConfigHome");
            if (!ok)
            {
                notes.Add(Notes.Warn("显式配置根无效（fail-closed）",
                    $"显式指定的配置根不存在或不像 Antigravity 配置根：{explicitCfg}（应含 config.json / rules / skills / plugins）。" +
                    "按纪律不回退、不猜。", "cli"));
                return Fail(attempts, notes, defaultConfig, defaultData, sniffCandidates, sniffHits);
            }
            configHome = explicitCfg;
            locatedBy ??= "cli:--config";
        }

        // ---- 2. 环境变量（本工具约定）：补齐还空着的层 ----
        var envRoot = Clean(Env(RootEnv));
        if (envRoot is not null)
        {
            var it = Interpret(envRoot!);
            Add("env", $"{RootEnv}={envRoot}", it is not null, it is null ? "无效" : it.Describe());
            if (it is not null)
            {
                Apply(it, ref installRoot, ref exe, ref bundle, ref configHome, ref dataHome);
                locatedBy ??= $"{RootEnv}（本工具约定变量）";
            }
            else
            {
                notes.Add(Notes.Warn("环境变量无效",
                    $"{RootEnv}={envRoot} 解释不出 Antigravity 的安装根 / 配置根 / 数据根，已忽略（继续往下找）。" +
                    $"注意：{RootEnv} / {ConfigEnv} / {DataEnv} 是本工具的约定变量，不是 Antigravity 官方变量。", RootEnv));
            }
        }
        else Add("env", $"({RootEnv} 未设置)", false, "本工具约定变量");

        var envCfg = Clean(Env(ConfigEnv));
        if (envCfg is not null)
        {
            Add("env", $"{ConfigEnv}={envCfg}", Directory.Exists(envCfg), "本工具约定变量（配置根）");
            if (Directory.Exists(envCfg)) configHome ??= envCfg;
        }
        else Add("env", $"({ConfigEnv} 未设置)", false, "本工具约定变量");

        var envData = Clean(Env(DataEnv));
        if (envData is not null)
        {
            Add("env", $"{DataEnv}={envData}", Directory.Exists(envData), "本工具约定变量（数据根）");
            if (Directory.Exists(envData)) dataHome ??= envData;
        }
        else Add("env", $"({DataEnv} 未设置)", false, "本工具约定变量");

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
                var it = Interpret(memRoot!);
                Add("memory", memRoot!, it is not null, it is null ? "失效" : it.Describe());
                if (it is not null)
                {
                    Apply(it, ref installRoot, ref exe, ref bundle, ref configHome, ref dataHome);
                    locatedBy ??= "memory（~/.breach-antigravity-path.json）";
                }
            }
            else Add("memory", "(无)", false, "(无记忆文件)");

            var memCfg = Clean(PathMemory.Load(MemoryFile, "configHome"));
            if (memCfg is not null && Directory.Exists(memCfg))
            {
                Add("memory", $"configHome={memCfg}", configHome is null, configHome is null ? "命中" : "已有更优先的来源，忽略");
                configHome ??= memCfg;
            }
            var memData = Clean(PathMemory.Load(MemoryFile, "dataHome"));
            if (memData is not null && Directory.Exists(memData))
            {
                Add("memory", $"dataHome={memData}", dataHome is null, dataHome is null ? "命中" : "已有更优先的来源，忽略");
                dataHome ??= memData;
            }
        }

        // ---- 4. 运行中进程 Antigravity ----
        if (installRoot is null)
        {
            var procExe = PathProbe.FromProcess(ProcessName);
            if (!string.IsNullOrEmpty(procExe))
            {
                var it = Interpret(procExe!);
                Add("process", procExe!, it is not null, it is null ? "推不出安装根" : it.Describe());
                if (it is not null)
                {
                    Apply(it, ref installRoot, ref exe, ref bundle, ref configHome, ref dataHome);
                    exe ??= procExe;
                    locatedBy ??= $"process（运行中的 {ProcessName}）";
                }
            }
            else Add("process", $"{ProcessName}（未运行）", false, null);
        }

        // ---- 5. 安装目录 %LOCALAPPDATA%\Programs\antigravity ----
        if (installRoot is null && defaultInstall.Length > 0)
        {
            var ok = IsInstallRoot(defaultInstall);
            Add("install", defaultInstall, ok,
                ok ? "安装目录" : Directory.Exists(defaultInstall) ? "目录在，但没有 Antigravity.exe / language_server.exe" : "目录不存在");
            if (ok)
            {
                installRoot = defaultInstall;
                exe = File.Exists(Path.Combine(defaultInstall, ExeName)) ? Path.Combine(defaultInstall, ExeName) : null;
                var b = Path.Combine(defaultInstall, BundleRelative);
                bundle = File.Exists(b) ? b : null;
                locatedBy ??= $"install（{defaultInstall}）";
            }
        }

        // ---- 6. 配置根 / 数据根默认位置 ----
        if (configHome is null)
        {
            Add("default", defaultConfig, Directory.Exists(defaultConfig), "~/.gemini/config（配置根）");
            if (Directory.Exists(defaultConfig)) configHome = defaultConfig;
        }
        if (dataHome is null)
        {
            Add("default", defaultData, Directory.Exists(defaultData), "~/.gemini/antigravity（数据根）");
            if (Directory.Exists(defaultData)) dataHome = defaultData;
        }

        // ---- 7. 嗅探（附加级） ----
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
                exe = File.Exists(Path.Combine(sniffRoot, ExeName)) ? Path.Combine(sniffRoot, ExeName) : null;
                var b = Path.Combine(sniffRoot, BundleRelative);
                bundle = File.Exists(b) ? b : null;
                locatedBy ??= $"sniff（{sniffRoot}）";
            }
        }

        configHome ??= defaultConfig;
        dataHome ??= defaultData;

        var configExists = Directory.Exists(configHome);
        var dataExists = Directory.Exists(dataHome) || Directory.Exists(ConversationsDir(dataHome));

        if (installRoot is null)
        {
            if (configExists || dataExists)
            {
                Add("config", configHome, configExists, "配置根（三层注入点所在）");
                notes.Add(Notes.Warn("没找到安装根",
                    $"只定位到配置根 / 数据根，没有定位到 {ExeName}（{defaultInstall}）。" +
                    "对本目标不是阻塞：三层（规则 / 技能 / 权限）都在配置根里，语言服务器二进制只作报告。Exe / Bundle 留空。", "locate"));
                return Found(null, null, null, configExists ? "config（配置根）" : "data（数据根）",
                    configHome, dataHome, true, attempts, sniffCandidates, sniffHits, notes, remember, ct);
            }

            notes.Add(Notes.Error("未找到 Antigravity",
                $"多级探测（CLI --root → 环境变量 → 记忆文件 → 运行进程 → {defaultInstall} → ~/.gemini/config、~/.gemini/antigravity）全部未命中。" +
                $"可用 --root <DIR> 显式指定：安装根、{ExeName}、配置根（{defaultConfig}）或数据根（{defaultData}）。", "locate"));
            return Fail(attempts, notes, configHome, dataHome, sniffCandidates, sniffHits);
        }

        if (configExists) Add("config", configHome, true, "配置根（三层注入点所在）");
        if (dataExists) Add("data", ConversationsDir(dataHome), true, "会话库目录（日志层）");

        return Found(installRoot, exe, bundle, locatedBy ?? "unknown", configHome, dataHome, false,
            attempts, sniffCandidates, sniffHits, notes, remember, ct);
    }

    private static void Apply(Interpreted it, ref string? installRoot, ref string? exe, ref string? bundle,
        ref string? configHome, ref string? dataHome)
    {
        installRoot ??= it.InstallRoot;
        exe ??= it.Exe;
        bundle ??= it.Bundle;
        configHome ??= it.ConfigRoot;
        dataHome ??= it.DataRoot;
    }

    private static AntigravityLocation Fail(
        IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<Note> notes,
        string configHome, string dataHome,
        IReadOnlyList<string> sniffCandidates, IReadOnlyList<string> sniffHits) =>
        new()
        {
            Found = false,
            Root = null,
            Exe = null,
            Bundle = null,
            ConfigHome = configHome,
            DataHome = dataHome,
            LocatedBy = null,
            Attempts = attempts,
            SniffCandidates = sniffCandidates,
            SniffHits = sniffHits.Count,
            Notes = notes,
        };

    private static AntigravityLocation Found(
        string? installRoot, string? exe, string? bundle, string locatedBy,
        string configHome, string dataHome, bool rootIsNotInstall,
        IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<string> sniffCandidates, IReadOnlyList<string> sniffHits,
        IReadOnlyList<Note> notes, bool remember, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var version = VersionOf(exe);
        var merged = new List<Note>(notes);
        if (installRoot is not null)
            merged.Add(Notes.Info("安装根", $"{installRoot}（{ExeName} {FileText.Human(LengthOf(exe))}，版本 {version ?? "读不到"}）", "locate"));
        merged.Add(Notes.Info("配置根", configHome, ConfigEnv));
        merged.Add(Notes.Info("数据根", dataHome, DataEnv));
        merged.Add(Notes.Info("提示词载体", bundle is null
                ? $"{BundleRelative} 没找到（相对安装根）；本轮只报告、不打补丁，缺它不影响三层注入。"
                : $"{BundleRelative}（{FileText.Human(LengthOf(bundle))}，原生二进制）：常驻提示词在它里面，" +
                  "不在日志里；本轮**不做**等长字节替换（docs/gemini.md 第七节第 4 步还没开工）。", bundle));
        merged.Add(Notes.Info("本工具约定变量",
            $"{RootEnv} / {ConfigEnv} / {DataEnv} 是本工具约定的变量，Antigravity 官方没有它们；" +
            $"官方位置是 ~/.gemini/config 与 ~/.gemini/antigravity（MEMORY_FILE 同理：{MemoryFileEnv}）。", ConfigEnv));
        merged.Add(Notes.Info("会话目录",
            $"日志层在 {ConversationsDir(dataHome)}（不在配置根下）。契约里 LocateResult.SessionsDir 由引擎拼成 <ConfigHome>\\sessions，" +
            "对本目标不适用——以这里为准。", ConversationsDir(dataHome)));
        if (!Directory.Exists(ConversationsDir(dataHome)))
            merged.Add(Notes.Info("会话库不存在",
                $"还没有 conversations 目录：{ConversationsDir(dataHome)}（Antigravity 还没落过会话）。", ConversationsDir(dataHome)));

        if (remember)
        {
            var rootToSave = installRoot ?? configHome;
            var memRoot = PathMemory.Load(MemoryFile, "root");
            var memCfg = PathMemory.Load(MemoryFile, "configHome");
            var memData = PathMemory.Load(MemoryFile, "dataHome");
            if (!string.Equals(memRoot, rootToSave, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(memCfg, configHome, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(memData, dataHome, StringComparison.OrdinalIgnoreCase))
                Remember(rootToSave, configHome, dataHome);
        }

        return new AntigravityLocation
        {
            Found = true,
            Root = installRoot ?? configHome,
            Exe = exe,
            Bundle = bundle,
            ConfigHome = configHome,
            DataHome = dataHome,
            Version = version,
            LocatedBy = locatedBy,
            RootIsNotInstall = installRoot is null || rootIsNotInstall,
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
    private static void Remember(string root, string configHome, string dataHome)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["root"] = root,
                ["configHome"] = configHome,
                ["dataHome"] = dataHome,
                ["savedAt"] = DateTimeOffset.Now.ToString("o"),
            }, new JsonSerializerOptions { WriteIndented = true });
            FileText.Write(MemoryFile, json);
        }
        catch { /* 记忆失败不致命 */ }
    }
}
