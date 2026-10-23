using System.Text.Json;
using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;

namespace AgentToolkit.Core.Targets.Opencode;

/// <summary>opencode 的定位结果：安装根 / 配置根 / 数据根 + 「试过哪些路径」的完整诊断。</summary>
public sealed class OpencodeLocation
{
    public bool Found { get; init; }

    /// <summary>安装根（%APPDATA%\ai.opencode.desktop）；找不到 CLI 时退化为配置根。</summary>
    public string? Root { get; init; }

    /// <summary>CLI 单文件（…\cli\&lt;版本&gt;\opencode-cli.exe）；没找到就是 null。</summary>
    public string? Exe { get; init; }

    public required string ConfigHome { get; init; }
    public required string DataHome { get; init; }
    public string? Version { get; init; }
    public string? LocatedBy { get; init; }

    /// <summary>Root 只是配置根（没定位到 CLI 安装根）时为真。</summary>
    public bool RootIsConfigHome { get; init; }

    public IReadOnlyList<LocateAttempt> Attempts { get; init; } = Array.Empty<LocateAttempt>();
    /// <summary>cli 目录下的全部版本目录候选（人话形式：目录 + 版本 + mtime）。</summary>
    public IReadOnlyList<string> CliVersionCandidates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SniffCandidates { get; init; } = Array.Empty<string>();
    public int SniffHits { get; init; }
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();

    public string ConfigFile => OpencodeLocator.ConfigFile(ConfigHome);
    public string AgentsFile => OpencodeLocator.AgentsFile(ConfigHome);
    public string DbPath => OpencodeLocator.DbFile(DataHome);
}

/// <summary>
/// opencode 定位器。**两种安装布局都认**（issue #1）：
///
/// 桌面版（本机实测 2.0.32-x64）：
/// <code>
/// %APPDATA%\ai.opencode.desktop\cli\&lt;版本&gt;\opencode-cli.exe   Bun 编译的单文件 CLI（203.8 MB，不是注入点）
/// </code>
///
/// npm 全局安装：
/// <code>
/// &lt;npm prefix&gt;\node_modules\@opencode\cli\bin\opencode.exe     版本在包内 package.json 的 version
/// &lt;npm prefix&gt;\opencode.ps1 / opencode.cmd                   shim（prefix 通常在 PATH 上）
/// </code>
///
/// 注入点（两种布局相同）：
/// <code>
/// ~/.config/opencode/opencode.json                            官方配置通道（instructions 键 → 配置指令层）
/// ~/.config/opencode/AGENTS.md                                全局指令文件（上下文载荷层）
/// ~/.local/share/opencode/opencode.db                         SQLite + WAL 会话库（推理原文落点）
/// </code>
///
/// 解析顺序：CLI --root（允许传安装根 / exe / npm 包目录 / 配置根）→ 环境变量 OPENCODE_ROOT / OPENCODE_CONFIG
///          → 记忆文件 ~/.breach-opencode-path.json → 运行中进程 opencode-cli
///          → 桌面版安装目录 %APPDATA%\ai.opencode.desktop
///          → **npm 全局**（OPENCODE_NPM_PREFIX → %APPDATA%\npm → PATH 反查 node_modules\@opencode\cli）
///          → XDG_CONFIG_HOME / XDG_DATA_HOME → ~/.config/opencode、~/.local/share/opencode
///          → 浅嗅探（附加，最低优先）。
///
/// 版本来源优先级：**包内 package.json 的 version** → 目录名（桌面版 cli\&lt;版本&gt; 才有效）。
/// 显式指定的路径无效时 **fail-closed**：不回退、不猜，返回 NotFound 并把「试过哪些路径」填进 Attempts。
/// 注意：OPENCODE_ROOT / OPENCODE_CONFIG / OPENCODE_NPM_PREFIX 是本工具的约定变量，
///       **不是** opencode / npm 官方变量（见 Notes）。
/// </summary>
public static class OpencodeLocator
{
    public const string ProcessName = "opencode-cli";
    /// <summary>桌面版的 CLI 文件名（%APPDATA%\ai.opencode.desktop\cli\&lt;版本&gt;\opencode-cli.exe）。</summary>
    public const string CliExeName = "opencode-cli.exe";

    /// <summary>
    /// CLI 可执行文件名候选。桌面版是 opencode-cli.exe；**npm 全局安装**是
    /// &lt;npm prefix&gt;\node_modules\@opencode\cli\bin\opencode.exe。两套布局必须都认，
    /// 否则 npm 全局安装的用户会看到「版本 ?」+「找不到 opencode-cli.exe」（issue #1）。
    /// 无扩展名的两项是给非 Windows / 无 .exe 后缀的安装形态留的。
    /// </summary>
    public static readonly string[] CliExeNames = { "opencode-cli.exe", "opencode.exe", "opencode-cli", "opencode" };

    /// <summary>npm 全局安装的包相对路径（相对 npm prefix）。</summary>
    public const string NpmPackageRelPath = @"node_modules\@opencode\cli";

    /// <summary>npm prefix 重定向（本工具约定变量，npm 官方没有这个变量）。</summary>
    public const string NpmPrefixEnv = "OPENCODE_NPM_PREFIX";

    public const string AppDirName = "ai.opencode.desktop";
    public const string CliDirName = "cli";

    /// <summary>本工具约定的安装根变量（opencode 自己没有这个变量）。</summary>
    public const string RootEnv = "OPENCODE_ROOT";
    /// <summary>本工具约定的配置根变量（opencode 自己没有这个变量）。</summary>
    public const string ConfigEnv = "OPENCODE_CONFIG";
    /// <summary>记忆文件重定向（自检用它把记忆钉在沙箱里，避免污染真实家目录）。</summary>
    public const string MemoryFileEnv = "OPENCODE_PATH_MEMORY_FILE";

    public const string MemoryFileName = ".breach-opencode-path.json";
    public const string ConfigDirName = "opencode";
    public const string ConfigFileName = "opencode.json";
    public const string ConfigJsoncFileName = "opencode.jsonc";
    public const string AgentsFileName = "AGENTS.md";
    public const string DbFileName = "opencode.db";
    public const string ServiceFileName = "service.json";
    public const string CliPrefsFileName = "cli.json";

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

    /// <summary>XDG 配置基准目录：$XDG_CONFIG_HOME → ~/.config。</summary>
    public static string XdgConfigBase() => Env("XDG_CONFIG_HOME") ?? Path.Combine(UserProfile, ".config");

    /// <summary>XDG 数据基准目录：$XDG_DATA_HOME → ~/.local/share。</summary>
    public static string XdgDataBase() => Env("XDG_DATA_HOME") ?? Path.Combine(UserProfile, ".local", "share");

    /// <summary>配置根（&lt;config&gt;）：尊重 XDG_CONFIG_HOME，默认 ~/.config/opencode。</summary>
    public static string DefaultConfigHome() => Path.Combine(XdgConfigBase(), ConfigDirName);

    /// <summary>数据根：尊重 XDG_DATA_HOME，默认 ~/.local/share/opencode。</summary>
    public static string DefaultDataHome() => Path.Combine(XdgDataBase(), ConfigDirName);

    /// <summary>桌面版应用目录：%APPDATA%\ai.opencode.desktop。</summary>
    public static string AppRoot()
    {
        var appData = Env("APPDATA")
                      ?? (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } p ? p : "");
        return Path.Combine(appData, AppDirName);
    }

    public static string CliRoot(string appRoot) => Path.Combine(appRoot, CliDirName);

    public static string ConfigFile(string configHome) => Path.Combine(configHome, ConfigFileName);
    public static string JsoncFile(string configHome) => Path.Combine(configHome, ConfigJsoncFileName);
    public static string AgentsFile(string configHome) => Path.Combine(configHome, AgentsFileName);
    public static string DbFile(string dataHome) => Path.Combine(dataHome, DbFileName);

    /// <summary>记忆文件：~/.breach-opencode-path.json（可用 OPENCODE_PATH_MEMORY_FILE 重定向）。</summary>
    public static string MemoryFile
    {
        get
        {
            var over = Env(MemoryFileEnv);
            return over ?? Path.Combine(UserProfile, MemoryFileName);
        }
    }

    /// <summary>一个 CLI 候选（桌面版版本目录 / npm 包 / 安装根直下等）。</summary>
    public sealed class CliCandidate
    {
        public required string Dir { get; init; }
        public required string Version { get; init; }
        public string? Exe { get; init; }
        public DateTimeOffset Mtime { get; init; }
        /// <summary>命中的布局（写进诊断，便于判断是桌面版还是 npm 全局）。</summary>
        public string Layout { get; init; } = "";
        public override string ToString() =>
            $"{Version} ← {Dir}（{Mtime:yyyy-MM-dd HH:mm:ss}{(Exe is null ? "，缺 exe" : "")}{(Layout.Length > 0 ? "，" + Layout : "")}）";
    }

    /// <summary>目录里任意一个 CLI 可执行文件名命中就返回它的完整路径。</summary>
    public static string? ExeIn(string dir)
    {
        foreach (var n in CliExeNames)
        {
            try
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
            catch { /* 非法路径 */ }
        }
        return null;
    }

    /// <summary>从 &lt;dir&gt;\package.json 读 version；读不到返回 null。
    /// 容忍 UTF-8 BOM 与前导空白（Windows 上手工编辑过的 package.json 常带 BOM，
    /// JsonDocument 会把 BOM 当非法字符直接抛）。</summary>
    public static string? VersionFromPackageJson(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var pj = Path.Combine(dir!, "package.json");
            if (!File.Exists(pj)) return null;
            var raw = File.ReadAllText(pj);
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..];
            raw = raw.TrimStart();
            if (raw.Length == 0) return null;
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("version", out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
            }
        }
        catch { /* 坏 JSON / 权限 */ }
        return null;
    }

    /// <summary>
    /// npm 全局安装的 prefix 候选：显式变量 → %APPDATA%\npm（Windows 默认）→ PATH 里含
    /// node_modules\@opencode\cli 的目录。npm 把 shim 放在 prefix 上并把 prefix 加进 PATH，
    /// 所以 PATH 反查是唯一能覆盖「自定义 prefix」的办法（issue #1 的 D:\UsrCmd\nodejs 就是这种）。
    /// </summary>
    public static IReadOnlyList<string> NpmGlobalRoots()
    {
        var list = new List<string>();

        var over = Env(NpmPrefixEnv);
        if (over is not null) list.Add(over!);

        var appData = Env("APPDATA")
                      ?? (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } ap ? ap : null);
        if (appData is not null) list.Add(Path.Combine(appData, "npm"));

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var d = raw.Trim().Trim('"');
            if (d.Length == 0) continue;
            try
            {
                if (Directory.Exists(Path.Combine(d, NpmPackageRelPath))) list.Add(d);
            }
            catch { /* 非法路径 */ }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// 给定任意根，枚举其下全部 CLI 候选，按修改时间（exe 与目录取较新者）倒序。
    /// **布局无关**：同时认桌面版与 npm 全局两种形态（issue #1）。
    /// <code>
    /// 桌面版    &lt;app&gt;\cli\&lt;版本&gt;\opencode-cli.exe      版本 = package.json（若有）→ 目录名
    /// 桌面版    &lt;app&gt;\cli\opencode-cli.exe              版本 = package.json（若有）→ cli 直下
    /// 桌面版    &lt;app&gt;\opencode-cli.exe                  版本 = package.json（若有）→ 安装根直下
    /// npm 包    &lt;pkg&gt;\bin\opencode.exe                  版本 = &lt;pkg&gt;\package.json
    /// npm prefix &lt;prefix&gt;\node_modules\@opencode\cli     走上一行，版本来自包内 package.json
    /// </code>
    /// </summary>
    public static IReadOnlyList<CliCandidate> CliCandidates(string root)
    {
        var list = new List<CliCandidate>();

        // dir = exe 所在目录；versionDir = 读 package.json 的目录（npm 布局下是包目录，不是 bin\）
        void Add(string dir, string? versionDir, string fallbackVersion, string layout)
        {
            var exe = ExeIn(dir);
            var version = VersionFromPackageJson(versionDir) ?? fallbackVersion;
            var mtime = DateTimeOffset.MinValue;
            try
            {
                if (exe is not null) mtime = File.GetLastWriteTimeUtc(exe);
                var d = Directory.GetLastWriteTimeUtc(dir);
                if (d > mtime) mtime = d;
            }
            catch { /* 读不到时间不影响候选 */ }
            list.Add(new CliCandidate { Dir = dir, Version = version, Exe = exe, Mtime = mtime, Layout = layout });
        }

        try
        {
            // ① 自身就是 npm 包目录（…\node_modules\@opencode\cli）
            //    判据里必须带 package.json：光有 bin\*.exe 不足以证明是 npm 包
            //    （Electron 应用目录下也可能有 bin\，会误判）。
            var selfBin = Path.Combine(root, "bin");
            if (Directory.Exists(selfBin) && ExeIn(selfBin) is not null
                && File.Exists(Path.Combine(root, "package.json")))
                Add(selfBin, root, Path.GetFileName(root.TrimEnd('\\', '/')), "npm 包目录（bin\\ + package.json）");

            // ② 自身是 npm prefix
            var pkg = Path.Combine(root, NpmPackageRelPath);
            if (Directory.Exists(pkg) && File.Exists(Path.Combine(pkg, "package.json")))
            {
                var pkgBin = Path.Combine(pkg, "bin");
                if (ExeIn(pkgBin) is not null)
                    Add(pkgBin, pkg, "(package.json 无 version)", "npm 全局（node_modules\\@opencode\\cli\\bin）");
            }

            // ③ 桌面版三种位置
            if (ExeIn(root) is not null) Add(root, root, "(安装根直下)", "安装根直下");
            var cli = CliRoot(root);
            if (Directory.Exists(cli))
            {
                foreach (var d in Directory.EnumerateDirectories(cli))
                    Add(d, d, Path.GetFileName(d.TrimEnd('\\', '/')), "cli\\<版本>\\");
                if (ExeIn(cli) is not null) Add(cli, root, "(cli 直下)", "cli 直下");
            }
        }
        catch { /* 权限 */ }

        return list.OrderByDescending(c => c.Mtime).ToArray();
    }

    /// <summary>配置根的判据：含已知配置文件，或目录名就叫 opencode。</summary>
    public static bool IsConfigRoot(string dir)
    {
        try
        {
            if (Path.GetFileName(dir.TrimEnd('\\', '/')).Equals(ConfigDirName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var f in new[] { ConfigFileName, ConfigJsoncFileName, AgentsFileName, ServiceFileName, CliPrefsFileName })
                if (File.Exists(Path.Combine(dir, f))) return true;
        }
        catch { /* 权限 */ }
        return false;
    }

    /// <summary>一次「把用户给的路径解释成什么」的结论。</summary>
    private sealed class Interpreted
    {
        public string? InstallRoot { get; init; }
        public string? Exe { get; init; }
        public string? Version { get; init; }
        public string? ConfigRoot { get; init; }
        public required string How { get; init; }

        public string Describe()
        {
            var parts = new List<string>();
            if (InstallRoot is not null) parts.Add($"安装根 {InstallRoot}");
            if (Exe is not null) parts.Add($"exe {Exe}");
            if (Version is not null) parts.Add($"版本 {Version}");
            if (ConfigRoot is not null) parts.Add($"配置根 {ConfigRoot}");
            return $"{How}：" + string.Join("；", parts);
        }
    }

    /// <summary>由 exe 所在目录反推安装根：&lt;app&gt;\cli\&lt;版本&gt;\ → &lt;app&gt;。</summary>
    private static string? AppRootFromCliDir(string dir)
    {
        var parent = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
        if (parent is null) return null;
        return Path.GetFileName(parent).Equals(CliDirName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(parent)
            : null;
    }

    private static Interpreted? Interpret(string raw)
    {
        var p = Clean(raw);
        if (p is null) return null;

        if (File.Exists(p))
        {
            // 任意 CLI 名都接受：桌面版 opencode-cli.exe / npm 全局 opencode.exe / 无扩展名
            var name = Path.GetFileName(p);
            if (!CliExeNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;
            var dir = Path.GetDirectoryName(p);
            if (dir is null) return null;

            // 桌面版：<app>\cli\<版本>\<exe>
            var appRoot = AppRootFromCliDir(dir);
            if (appRoot is not null)
                return new Interpreted
                {
                    InstallRoot = appRoot,
                    Exe = p,
                    Version = VersionFromPackageJson(dir) ?? Path.GetFileName(dir.TrimEnd('\\', '/')),
                    How = "cli:--root(桌面版 exe)",
                };

            // npm 全局：<pkg>\bin\<exe> → 包目录就是 dir 的父目录
            if (Path.GetFileName(dir.TrimEnd('\\', '/')).Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                var pkgDir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
                if (pkgDir is not null)
                    return new Interpreted
                    {
                        InstallRoot = pkgDir,
                        Exe = p,
                        Version = VersionFromPackageJson(pkgDir),
                        How = "cli:--root(npm 包 exe)",
                    };
            }

            // 其它：exe 所在目录当安装根
            return new Interpreted
            {
                InstallRoot = dir,
                Exe = p,
                Version = VersionFromPackageJson(dir),
                How = "cli:--root(exe)",
            };
        }

        if (!Directory.Exists(p)) return null;

        // 1) 目录里直接就有 exe（桌面版版本目录 / npm 包目录 / 安装根直下）
        var directExe = ExeIn(p);
        if (directExe is not null)
            return new Interpreted
            {
                InstallRoot = AppRootFromCliDir(p) ?? p,
                Exe = directExe,
                Version = VersionFromPackageJson(p) ?? Path.GetFileName(p.TrimEnd('\\', '/')),
                How = "cli:--root(目录直下就有 exe)",
            };

        // 2) 安装根：<app>\cli\<版本>\
        var cands = CliCandidates(p);
        if (cands.Count > 0)
        {
            var best = cands[0];
            return new Interpreted
            {
                InstallRoot = p,
                Exe = best.Exe,
                Version = best.Exe is null ? null : best.Version,
                How = "cli:--root(安装根)",
            };
        }

        // 3) <app>\cli 目录本身
        if (Path.GetFileName(p.TrimEnd('\\', '/')).Equals(CliDirName, StringComparison.OrdinalIgnoreCase))
        {
            var appRoot = Path.GetDirectoryName(p.TrimEnd('\\', '/'));
            if (appRoot is not null)
            {
                var c2 = CliCandidates(appRoot);
                if (c2.Count > 0)
                    return new Interpreted
                    {
                        InstallRoot = appRoot,
                        Exe = c2[0].Exe,
                        Version = c2[0].Exe is null ? null : c2[0].Version,
                        How = "cli:--root(cli 目录)",
                    };
            }
        }

        // 4) 配置根
        if (IsConfigRoot(p))
            return new Interpreted { ConfigRoot = p, How = "cli:--root(配置根)" };

        return null;
    }

    /// <summary>嗅探候选（附加级，最低优先）：只在别处都落空时才走，且必须真含 CLI 才算命中。</summary>
    public static IReadOnlyList<string> SniffCandidates()
    {
        var list = new List<string>();
        var appData = Env("APPDATA");
        var localAppData = Env("LOCALAPPDATA");
        var home = UserProfile;

        if (appData is not null) list.Add(Path.Combine(appData, AppDirName));
        if (localAppData is not null)
        {
            list.Add(Path.Combine(localAppData, "Programs", ConfigDirName));
            list.Add(Path.Combine(localAppData, "Programs", AppDirName));
            list.Add(Path.Combine(localAppData, ConfigDirName));
            list.Add(Path.Combine(localAppData, AppDirName));
        }
        list.Add(Path.Combine(home, ConfigDirName));
        list.Add(Path.Combine(home, ".opencode"));
        list.Add(Path.Combine(home, "scoop", "apps", ConfigDirName, "current"));
        list.Add(Path.Combine(home, "scoop", "apps", AppDirName, "current"));

        foreach (var drive in PathProbe.Drives())
            foreach (var tail in new[] { ConfigDirName, Path.Combine("Apps", ConfigDirName), Path.Combine("App", ConfigDirName), Path.Combine("Program Files", ConfigDirName) })
                list.Add(Path.Combine(drive, tail));

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ---------------------------------------------------------------- 主流程

    public static OpencodeLocation Resolve(LocateOptions? options, bool remember, CancellationToken ct)
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
        var dataHome = DefaultDataHome();
        var xdgCfg = Env("XDG_CONFIG_HOME");

        // ---------- 配置根：显式 > OPENCODE_CONFIG > 记忆 > XDG/默认 ----------
        string configHome;
        var explicitCfg = Clean(options?.ExplicitConfigHome);
        var envCfg = Clean(Env(ConfigEnv));
        var memCfg = Clean(PathMemory.Load(MemoryFile, "configHome"));

        if (explicitCfg is not null)
        {
            configHome = explicitCfg;
            Add("cli", $"配置根 {configHome}", Directory.Exists(configHome), "来自 LocateOptions.ExplicitConfigHome");
        }
        else if (envCfg is not null)
        {
            configHome = envCfg;
            Add("env", $"{ConfigEnv}={configHome}", Directory.Exists(configHome),
                "本工具约定变量（opencode 官方没有这个变量）");
        }
        else if (options?.IgnoreMemory != true && memCfg is not null && Directory.Exists(memCfg))
        {
            configHome = memCfg;
            Add("memory", $"configHome={configHome}", true, "记忆文件 ~/.breach-opencode-path.json");
        }
        else
        {
            configHome = defaultConfig;
            Add("xdg", xdgCfg is null ? "(XDG_CONFIG_HOME 未设置)" : Path.Combine(xdgCfg, ConfigDirName),
                xdgCfg is not null && Directory.Exists(Path.Combine(xdgCfg, ConfigDirName)),
                "配置根尊重 XDG_CONFIG_HOME");
        }
        Add("default", defaultConfig, Directory.Exists(defaultConfig),
            xdgCfg is null ? "~/.config/opencode（XDG 未设时的默认值）" : "XDG_CONFIG_HOME 下的 opencode（本次实际生效的配置根）");

        // ---------- 数据根：XDG_DATA_HOME → ~/.local/share ----------
        var xdgData = Env("XDG_DATA_HOME");
        Add("xdg", xdgData is null ? "(XDG_DATA_HOME 未设置)" : Path.Combine(xdgData, ConfigDirName),
            xdgData is not null && Directory.Exists(Path.Combine(xdgData, ConfigDirName)),
            "数据根尊重 XDG_DATA_HOME");
        Add("default", Path.Combine(UserProfile, ".local", "share", ConfigDirName),
            Directory.Exists(Path.Combine(UserProfile, ".local", "share", ConfigDirName)), "~/.local/share/opencode");
        Add("default", DbFile(dataHome), File.Exists(DbFile(dataHome)), "会话库 opencode.db");

        // ---------- 安装根：CLI --root > env > 记忆 > 进程 > 安装目录 > 嗅探 ----------
        string? installRoot = null;
        string? exe = null;
        string? version = null;
        string? locatedBy = null;
        string? explicitFail = null;
        IReadOnlyList<string> versionCandidates = Array.Empty<string>();
        var rootIsConfigHome = false;

        // ---- 1. CLI --root（显式指定无效 → fail-closed） ----
        var explicitRoot = Clean(options?.ExplicitRoot);
        if (explicitRoot is not null)
        {
            var it = Interpret(explicitRoot!);
            Add("cli", $"--root {explicitRoot}", it is not null, it is null ? "无效：既不是安装根 / exe，也不是配置根" : it.Describe());
            if (it is null)
            {
                explicitFail = explicitRoot;
                notes.Add(Notes.Warn("显式路径无效（fail-closed）",
                    $"显式指定的路径解释不出 opencode 的安装根 / exe / 配置根：{explicitRoot}。" +
                    "判据：安装根要有 cli\\<版本>\\opencode-cli.exe（桌面版）或 bin\\opencode.exe（npm 全局），" +
                    "配置根要有 opencode.json / AGENTS.md / service.json。" +
                    "按纪律不回退、不猜其它位置。", "cli"));
                notes.Add(Notes.Info("怎么指定",
                    "允许传：桌面版安装根（%APPDATA%\\ai.opencode.desktop）、桌面版 exe（opencode-cli.exe）、" +
                    $"npm 全局包目录（<npm prefix>\\{NpmPackageRelPath}）、npm 全局 exe（bin\\opencode.exe）、" +
                    "或配置根（~/.config/opencode）。npm 全局安装请指到 …\\node_modules\\@opencode\\cli。", "cli"));
                return Fail(attempts, notes, configHome, dataHome, sniffCandidates, sniffHits, versionCandidates);
            }
            installRoot = it.InstallRoot;
            exe = it.Exe;
            version = it.Version;
            locatedBy = it.How;
            if (it.InstallRoot is null && it.ConfigRoot is not null)
            {
                configHome = it.ConfigRoot;
                rootIsConfigHome = true;
            }
        }

        // ---- 2. 环境变量（本工具约定） ----
        if (installRoot is null && explicitRoot is null)
        {
            var envRoot = Clean(Env(RootEnv));
            if (envRoot is not null)
            {
                var it = Interpret(envRoot!);
                Add("env", $"{RootEnv}={envRoot}", it is not null, it is null ? "无效" : it.Describe());
                if (it is not null)
                {
                    installRoot = it.InstallRoot ?? it.ConfigRoot;
                    exe = it.Exe;
                    version = it.Version;
                    locatedBy = $"{RootEnv}（本工具约定变量）";
                    if (it.InstallRoot is null) rootIsConfigHome = true;
                }
                else
                {
                    notes.Add(Notes.Warn("环境变量无效",
                        $"{RootEnv}={envRoot} 解释不出 opencode 的安装根 / exe / 配置根，已忽略（继续往下找）。" +
                        $"注意：{RootEnv} / {ConfigEnv} 是本工具的约定变量，不是 opencode 官方变量。", RootEnv));
                }
            }
            else
            {
                Add("env", $"({RootEnv} 未设置)", false, "本工具约定变量");
            }
        }

        // ---- 2b. 显式 npm prefix（OPENCODE_NPM_PREFIX）：显式指定优先于自动发现 ----
        if (installRoot is null && explicitRoot is null)
        {
            var envNpm = Clean(Env(NpmPrefixEnv));
            if (envNpm is not null)
            {
                var cands = CliCandidates(envNpm!);
                var best = cands.FirstOrDefault(c => c.Exe is not null);
                Add("env", $"{NpmPrefixEnv}={envNpm}", best is not null,
                    best is null ? "该 prefix 下没有 node_modules\\@opencode\\cli\\bin\\<exe>" : $"{best.Layout}，版本 {best.Version}");
                if (best is not null)
                {
                    installRoot = envNpm;
                    exe = best.Exe;
                    version = best.Version;
                    locatedBy = $"{NpmPrefixEnv}（本工具约定变量）";
                    versionCandidates = cands.Select(c => c.ToString()).ToArray();
                    notes.Add(Notes.Info("npm prefix 显式指定",
                        $"按 {NpmPrefixEnv} 使用 npm 全局布局：{best.Dir}（{best.Layout}），版本 {best.Version}。" +
                        "显式变量优先于自动发现，所以本机若同时装了桌面版，桌面版本次不采用。", NpmPrefixEnv));
                }
                else
                {
                    notes.Add(Notes.Warn("npm prefix 无效",
                        $"{NpmPrefixEnv}={envNpm} 下没有 {NpmPackageRelPath}\\bin\\<exe>，已忽略（继续往下找）。" +
                        $"注意：{NpmPrefixEnv} 是本工具的约定变量，不是 npm 官方变量。", NpmPrefixEnv));
                }
            }
        }

        // ---- 3. 记忆文件 ----
        if (installRoot is null && explicitRoot is null && options?.IgnoreMemory != true)
        {
            var memRoot = Clean(PathMemory.Load(MemoryFile, "root"));
            if (memRoot is not null)
            {
                var it = Interpret(memRoot!);
                Add("memory", memRoot!, it is not null, it is null ? "失效" : it.Describe());
                if (it is not null)
                {
                    installRoot = it.InstallRoot ?? it.ConfigRoot;
                    exe = it.Exe;
                    version = it.Version;
                    locatedBy = "memory（~/.breach-opencode-path.json）";
                    if (it.InstallRoot is null) rootIsConfigHome = true;
                }
            }
            else
            {
                Add("memory", "(无)", false, MemoExplain(options));
            }
        }

        // ---- 4. 运行中进程 opencode-cli ----
        if (installRoot is null && explicitRoot is null)
        {
            var procExe = PathProbe.FromProcess(ProcessName);
            if (!string.IsNullOrEmpty(procExe))
            {
                var it = Interpret(procExe!);
                Add("process", procExe!, it is not null, it is null ? "推不出安装根" : it.Describe());
                if (it is not null)
                {
                    installRoot = it.InstallRoot ?? it.ConfigRoot;
                    exe = it.Exe ?? procExe;
                    version = it.Version;
                    locatedBy = "process（运行中的 opencode-cli）";
                }
            }
            else
            {
                Add("process", "opencode-cli（未运行）", false, null);
            }
        }

        // ---- 5. 安装目录 %APPDATA%\ai.opencode.desktop ----
        if (installRoot is null && explicitRoot is null)
        {
            var appRoot = AppRoot();
            var cands = CliCandidates(appRoot);
            versionCandidates = cands.Select(c => c.ToString()).ToArray();
            if (cands.Count == 0)
            {
                Add("install", appRoot, false, Directory.Exists(appRoot) ? "目录在，但 cli\\ 下没有 CLI 可执行文件（opencode-cli.exe / opencode.exe）" : "目录不存在（当前是 npm 全局安装或未安装桌面版）");
            }
            else
            {
                // 多个版本目录 → 取 mtime 最新的那个；全部候选都写进 Attempts/Notes
                foreach (var c in cands)
                    Add("install", c.Dir, ReferenceEquals(c, cands[0]),
                        $"版本 {c.Version}，mtime {c.Mtime:yyyy-MM-dd HH:mm:ss}");
                var best = cands[0];
                installRoot = appRoot;
                exe = best.Exe;
                version = best.Version;
                locatedBy = $"install（{appRoot}）";
                if (cands.Count > 1)
                    notes.Add(Notes.Info("cli 下有多个版本目录",
                        $"共 {cands.Count} 个候选，按修改时间取最新：{best.Version}（{best.Dir}）。全部候选：{string.Join("；", cands.Select(c => c.ToString()))}",
                        CliRoot(appRoot)));
                else
                    notes.Add(Notes.Info("CLI 版本", $"{best.Version} ← {best.Dir}", CliRoot(appRoot)));
            }
        }

        // ---- 5b. npm 全局安装（<prefix>\node_modules\@opencode\cli；issue #1） ----
        if (installRoot is null && explicitRoot is null)
        {
            var npmRoots = NpmGlobalRoots();
            Add("npm", npmRoots.Count == 0
                    ? $"(无候选：{NpmPrefixEnv} 未设、%APPDATA%\\npm 不在、PATH 里也没有含 {NpmPackageRelPath} 的目录)"
                    : string.Join("；", npmRoots),
                false, "npm 全局布局：<prefix>\\node_modules\\@opencode\\cli\\bin\\opencode.exe");

            foreach (var r in npmRoots)
            {
                var cands = CliCandidates(r);
                var bestNpm = cands.FirstOrDefault(c => c.Exe is not null);
                if (bestNpm is null) continue;
                Add("npm", bestNpm.Dir, true, $"{bestNpm.Layout}，版本 {bestNpm.Version}");
                installRoot = r;
                exe = bestNpm.Exe;
                version = bestNpm.Version;
                locatedBy = $"npm（{r}）";
                if (versionCandidates.Count == 0) versionCandidates = cands.Select(c => c.ToString()).ToArray();
                notes.Add(Notes.Info("npm 全局安装",
                    $"CLI 在 {bestNpm.Dir}（{bestNpm.Layout}），版本 {bestNpm.Version} 读自包内 package.json。" +
                    "该布局下注入点仍是配置与指令文件，CLI 只用于版本显示与 Exe 探针。", r));
                break;
            }
        }

        // ---- 6. 嗅探（附加级） ----
        if (installRoot is null && explicitRoot is null)
        {
            string? sniffRoot = null;
            foreach (var c in sniffCandidates)
            {
                ct.ThrowIfCancellationRequested();
                var cands = CliCandidates(c);
                if (cands.Count == 0) continue;
                sniffHits.Add(c);
                sniffRoot ??= c;
            }
            Add("sniff", sniffRoot ?? "(未命中)", sniffRoot is not null,
                sniffRoot is null ? null : $"命中 {sniffHits.Count} 个候选");
            if (sniffRoot is not null)
            {
                var cands = CliCandidates(sniffRoot);
                installRoot = sniffRoot;
                exe = cands[0].Exe;
                version = cands[0].Version;
                if (versionCandidates.Count == 0) versionCandidates = cands.Select(c => c.ToString()).ToArray();
                locatedBy = $"sniff（{sniffRoot}）";
            }
        }

        // ---------- 汇总 ----------
        var configExists = Directory.Exists(configHome);
        var dataExists = Directory.Exists(dataHome) || File.Exists(DbFile(dataHome));

        if (installRoot is null)
        {
            if (configExists || dataExists)
            {
                // 配置/数据根在，CLI 不在：注入点本来就不依赖 CLI，仍算定位成功。
                Add("config", configHome, configExists, "配置根（注入点所在）");

                // 按 issue #1 的反馈拆成两条，别让它们在同一句里相互放大：
                //   ① 「本目标设计上不做二进制注入」是本目标的固有设计，跟有没有找到 CLI 无关
                //   ② 「CLI 未定位」只影响版本显示与 Exe 探针，不影响注入
                notes.Add(Notes.Info("本目标设计上不做二进制注入",
                    "opencode 的注入点是配置与指令文件（opencode.json 的 instructions、AGENTS.md），" +
                    "不碰 203.8 MB 的 Bun 单文件 CLI。所以「Exe 留空」对本目标是正常状态，不是版本不支持。", "locate"));
                notes.Add(Notes.Info("CLI 未定位（不影响注入）",
                    "两种布局都没命中：桌面版 %APPDATA%\\ai.opencode.desktop\\cli\\<版本>\\opencode-cli.exe，" +
                    "npm 全局 <prefix>\\node_modules\\@opencode\\cli\\bin\\opencode.exe。" +
                    "版本号因此显示为 “?”。要补齐版本显示：" +
                    $"① 用 --root 指到 npm 包目录（<prefix>\\node_modules\\@opencode\\cli）；或 ② 设 {NpmPrefixEnv}=<npm prefix>；" +
                    "③ 把 <npm prefix> 放进 PATH（本工具会按 PATH 反查）。", "locate"));
                return Found(installRoot: null, exe: null, version: null,
                    locatedBy: configExists ? "config（配置根）" : "data（数据根）",
                    configHome, dataHome, rootIsConfigHome: true, attempts, sniffCandidates, sniffHits.ToArray(),
                    versionCandidates, notes, remember, ct);
            }

            notes.Add(Notes.Error("未找到 opencode",
                "多级探测（CLI --root → 环境变量 → 记忆文件 → 运行进程 → %APPDATA%\\ai.opencode.desktop → npm 全局 → XDG → ~/.config/opencode）全部未命中。" +
                $"可用 --root <DIR> 显式指定：安装根（{AppRoot()}）、桌面版 exe（{CliExeName}）、" +
                $"npm 包目录（<npm prefix>\\{NpmPackageRelPath}）、或配置根（{defaultConfig}）。", "locate"));
            foreach (var t in attempts.Where(a => !a.Hit).Select(a => $"{a.Level}({a.Candidate})").Distinct())
                notes.Add(Notes.Info("试过", t, "locate"));
            return Fail(attempts, notes, configHome, dataHome, sniffCandidates, sniffHits, versionCandidates);
        }

        if (configExists)
            Add("config", configHome, true, "配置根（注入点所在）");
        if (File.Exists(DbFile(dataHome)))
            Add("data", DbFile(dataHome), true, "会话库");

        if (versionCandidates.Count == 0)
        {
            var cands = CliCandidates(installRoot!);
            versionCandidates = cands.Select(c => c.ToString()).ToArray();
        }

        return Found(installRoot, exe, version, locatedBy ?? "unknown",
            configHome, dataHome, rootIsConfigHome, attempts, sniffCandidates, sniffHits.ToArray(),
            versionCandidates, notes, remember, ct);
    }

    private static string MemoExplain(LocateOptions? options) =>
        options?.IgnoreMemory == true ? "(IgnoreMemory，跳过)" : "(无记忆文件)";

    private static OpencodeLocation Fail(
        IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<Note> notes,
        string configHome, string dataHome,
        IReadOnlyList<string> sniffCandidates, IReadOnlyList<string> sniffHits,
        IReadOnlyList<string> versionCandidates) =>
        new()
        {
            Found = false,
            Root = null,
            Exe = null,
            ConfigHome = configHome,
            DataHome = dataHome,
            LocatedBy = null,
            Attempts = attempts,
            SniffCandidates = sniffCandidates,
            SniffHits = sniffHits.Count,
            CliVersionCandidates = versionCandidates,
            Notes = notes,
        };

    private static OpencodeLocation Found(
        string? installRoot, string? exe, string? version, string locatedBy,
        string configHome, string dataHome, bool rootIsConfigHome,
        IReadOnlyList<LocateAttempt> attempts, IReadOnlyList<string> sniffCandidates, IReadOnlyList<string> sniffHits,
        IReadOnlyList<string> versionCandidates, IReadOnlyList<Note> notes, bool remember, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var merged = new List<Note>(notes);
        if (installRoot is not null && version is not null)
            merged.Add(Notes.Info("CLI", $"{CliExeName} 版本 {version}：{exe ?? "(缺 exe)"}", CliRoot(installRoot)));
        merged.Add(Notes.Info("配置根", configHome, ConfigEnv));
        merged.Add(Notes.Info("数据根", dataHome, "XDG_DATA_HOME"));
        merged.Add(Notes.Info("注入点不是 bundle",
            "opencode 的提示词在 203.8 MB 的 Bun 单文件里，但那里没有可删的安全条款（docs/opencode.md 实测：20 个策略特征串 0 命中）——" +
            "该目标的注入点是配置与指令文件（opencode.json 的 instructions + AGENTS.md），不是 bundle。Bundle 字段留空。", "locate"));
        merged.Add(Notes.Info("本工具约定变量",
            $"{RootEnv} / {ConfigEnv} 是本工具约定的变量，opencode 官方没有它们；官方位置是 ~/.config/opencode（尊重 XDG_CONFIG_HOME）。",
            ConfigEnv));
        if (!File.Exists(DbFile(dataHome)))
            merged.Add(Notes.Info("会话库不存在", $"还没有 opencode.db：{DbFile(dataHome)}（opencode 还没落过会话）。", DbFile(dataHome)));

        if (remember)
        {
            var rootToSave = installRoot ?? configHome;
            var mem = PathMemory.Load(MemoryFile, "root");
            var memCfg = PathMemory.Load(MemoryFile, "configHome");
            if (!string.Equals(mem, rootToSave, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(memCfg, configHome, StringComparison.OrdinalIgnoreCase))
                RememberMemory(rootToSave, configHome, dataHome);
        }

        var resolvedRoot = installRoot ?? configHome;
        return new OpencodeLocation
        {
            Found = true,
            Root = resolvedRoot,
            Exe = exe,
            ConfigHome = configHome,
            DataHome = dataHome,
            Version = version,
            LocatedBy = locatedBy,
            RootIsConfigHome = installRoot is null || rootIsConfigHome,
            Attempts = attempts,
            CliVersionCandidates = versionCandidates,
            SniffCandidates = sniffCandidates,
            SniffHits = sniffHits.Count,
            Notes = merged,
        };
    }

    /// <summary>记忆文件同时记住 root 与 configHome（PathMemory.Save 一次只写一个属性）。</summary>
    private static void RememberMemory(string root, string configHome, string dataHome)
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
