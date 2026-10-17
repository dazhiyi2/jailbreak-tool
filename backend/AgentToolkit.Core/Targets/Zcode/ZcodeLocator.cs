using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using Microsoft.Win32;

namespace AgentToolkit.Core.Targets.Zcode;

/// <summary>定位结果：多级探测的命中路径 + "试过哪些路径"的完整诊断。</summary>
public sealed class ZcodeLocation
{
    public bool Found { get; init; }
    public string? Bundle { get; init; }
    public string? Exe { get; init; }
    public string? Root { get; init; }
    public required string Home { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    /// <summary>显式指定的路径无效时被置位（fail-closed：不回退到别的安装）。</summary>
    public string? ExplicitFailed { get; init; }
    public IReadOnlyList<LocateAttempt> Attempts { get; init; } = Array.Empty<LocateAttempt>();
    public IReadOnlyList<string> Tried { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SniffCandidates { get; init; } = Array.Empty<string>();
    public int SniffHits { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();
    public IReadOnlyList<string> ConflictBundles { get; init; } = Array.Empty<string>();

    public string Diagnose()
    {
        var lines = new List<string>();
        if (ExplicitFailed is not null)
        {
            lines.Add($"你显式指定的 {ExplicitFailed} 无法解析到 zcode.cjs，已按 fail-closed 原则中止。");
            lines.Add("（不会回退到其它安装，避免把补丁打到非目标副本上。）");
        }
        else
        {
            lines.Add("未能定位 ZCode 的 bundle 文件（resources/glm/zcode.cjs）。");
        }
        lines.Add("");
        lines.Add("已尝试的来源与路径：");
        foreach (var t in Tried.Distinct(StringComparer.Ordinal)) lines.Add($"  - {t}");
        lines.Add("");
        lines.Add("如何解决（任选其一）：");
        lines.Add("  1) 手动指定安装根：  --root \"X:\\你的ZCode目录\"");
        lines.Add("  2) 直接指定 bundle： --root \"X:\\...\\resources\\glm\\zcode.cjs\"");
        lines.Add("  3) 设置环境变量：    set ZCODE_ROOT=X:\\你的ZCode目录");
        lines.Add("  4) 若为便携版，把 ZCode.exe 所在目录设为 ZCODE_ROOT 即可。");
        lines.Add("");
        lines.Add($"配置根（AGENTS.md 目标）：{Home}");
        return string.Join("\n", lines);
    }
}

/// <summary>
/// ZCode 安装位置探测器。逐条移植 tools/zcode-locate.mjs：
///   CLI --root（可传安装根 / ZCode.exe / bundle 路径）
///   → 环境变量 ZCODE_BUNDLE &gt; ZCODE_ROOT &gt; ZCODE_EXE
///   → 记忆文件 ~/.breach-zcode-path.json
///   → 运行中进程 ZCode
///   → 注册表卸载项（DisplayName 含 ZCode；InstallLocation / DisplayIcon / UninstallString 三个字段推目录）
///   → 快捷方式（桌面与开始菜单 ZCode.lnk）+ 目录嗅探（合并成候选池后按 mtime 裁决）
/// 显式指定无效时 fail-closed，绝不回退到其它安装。
/// </summary>
public static class ZcodeLocator
{
    public const string ExeName = "ZCode.exe";

    /// <summary>照抄 BUNDLE_REL_CANDIDATES（顺序敏感：命中第一个就用它）。</summary>
    public static readonly string[][] BundleRelCandidates =
    {
        new[] { "resources", "glm", "zcode.cjs" },
        new[] { "resources", "app", "glm", "zcode.cjs" },
        new[] { "resources", "glm", "zcode.cjs" },
        new[] { "resources", "zcode.cjs" },
        new[] { "glm", "zcode.cjs" },
        new[] { "zcode.cjs" },
    };

    /// <summary>与源码一致的记忆文件名（放在用户主目录）。</summary>
    public const string MemoryFileName = ".breach-zcode-path.json";

    /// <summary>记忆文件绝对路径。可被环境变量 ZCODE_PATH_MEMORY_FILE 覆盖（自检用它把记忆重定向进沙箱，
    /// 避免回归用例污染真实 ~/.breach-zcode-path.json）。</summary>
    public static string MemoryFile
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("ZCODE_PATH_MEMORY_FILE");
            if (!string.IsNullOrWhiteSpace(over)) return over!;
            return Path.Combine(HomeDir(), MemoryFileName);
        }
    }

    public static string HomeDir() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } p
            ? p
            : Environment.GetEnvironmentVariable("USERPROFILE") ?? ".";

    /// <summary>默认的 ZCode 配置根：ZCODE_HOME 优先，否则 ~/.zcode。</summary>
    public static string DefaultZcodeHome()
    {
        var env = Environment.GetEnvironmentVariable("ZCODE_HOME");
        return string.IsNullOrWhiteSpace(env) ? Path.Combine(HomeDir(), ".zcode") : env!;
    }

    public static ZcodeLocation Locate(LocateOptions? options, CancellationToken ct)
    {
        var tried = new List<string>();
        var attempts = new List<LocateAttempt>();
        var warnings = new List<string>();
        var sniffHits = new List<string>();

        var home = !string.IsNullOrWhiteSpace(options?.ExplicitConfigHome)
            ? options!.ExplicitConfigHome!.Trim().Trim('"')
            : DefaultZcodeHome();
        tried.Add($"(home) {home}");

        void Tried(string level, string candidate, bool hit = false, string? note = null)
        {
            tried.Add(candidate);
            attempts.Add(new LocateAttempt { Order = attempts.Count + 1, Level = level, Candidate = candidate, Hit = hit, Note = note });
        }

        ZcodeLocation Fail(string? explicitFailed, IReadOnlyList<Note> notes) => new()
        {
            Found = false,
            Home = home,
            ExplicitFailed = explicitFailed,
            Attempts = attempts,
            Tried = tried,
            SniffCandidates = sniffHits,
            SniffHits = sniffHits.Count,
            Warnings = warnings,
            Notes = notes,
        };

        // ---------------------------------------------------------------- accept
        (string Bundle, string? Exe, string Source)? Accept((string? Bundle, string? Exe, string Src) entry, string? srcOverride = null)
        {
            if (entry.Bundle is not null)
            {
                if (!File.Exists(entry.Bundle)) return null;
                var exe = entry.Exe ?? Path.Combine(RootFromBundle(entry.Bundle), ExeName);
                return (entry.Bundle, File.Exists(exe) ? exe : null, srcOverride ?? entry.Src);
            }
            if (entry.Exe is not null)
            {
                if (!File.Exists(entry.Exe)) return null;
                var b = BundleFromExe(entry.Exe, tried);
                if (b is null) return null;
                return (b, entry.Exe, srcOverride ?? entry.Src);
            }
            return null;
        }

        ZcodeLocation Hit((string Bundle, string? Exe, string Source) a, IReadOnlyList<string>? conflicts = null)
        {
            var root = RootFromBundle(a.Bundle);
            // 命中的那条也要进 Attempts：状态页/探测页要能看到"最终选中的是哪一个、来自哪一级"。
            if (!attempts.Any(x => x.Hit && string.Equals(x.Candidate, a.Bundle, StringComparison.OrdinalIgnoreCase)))
            {
                attempts.Add(new LocateAttempt
                {
                    Order = attempts.Count + 1,
                    Level = LevelOf(a.Source),
                    Candidate = a.Bundle,
                    Hit = true,
                    Note = $"命中（{a.Source}）" + (a.Exe is null ? "" : $"；exe={a.Exe}"),
                });
            }
            var notes = new List<Note>();
            foreach (var w in warnings) notes.Add(Notes.Warn("定位警告", w, "zcode/locate"));
            if (conflicts is { Count: > 1 })
                notes.Add(Notes.Warn("多个安装共存",
                    $"检测到 {conflicts.Count} 个 ZCode 安装，已自动选用最近修改的一个：{a.Bundle}。全部候选：{string.Join("；", conflicts)}",
                    "zcode/locate"));
            return new ZcodeLocation
            {
                Found = true,
                Bundle = a.Bundle,
                Exe = a.Exe,
                Root = root,
                Home = home,
                Version = RegistryVersion(),
                Source = a.Source,
                Attempts = attempts,
                Tried = tried,
                SniffCandidates = sniffHits,
                SniffHits = sniffHits.Count,
                Warnings = warnings,
                Notes = notes,
                ConflictBundles = conflicts ?? Array.Empty<string>(),
            };
        }

        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- 1. 显式参数（fail-closed）
        var explicitRoot = options?.ExplicitRoot;
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var p = explicitRoot!.Trim().Trim('"');
            if (File.Exists(p))
            {
                if (Path.GetExtension(p).Equals(".cjs", StringComparison.OrdinalIgnoreCase))
                {
                    var r = Accept((p, null, "cli:--bundle"), "cli:--bundle");
                    if (r is not null) return Hit(r.Value);
                    Tried("cli", $"(cli:--bundle 无效) {p}");
                    return Fail("cli:--bundle", new[]
                    {
                        Notes.Error("显式指定的 bundle 无效", $"路径不存在或不是文件：{p}。已按 fail-closed 中止，不回退到其它安装。", "zcode/locate"),
                    });
                }

                var re = Accept((null, p, "cli:--path"), "cli:--path");
                if (re is not null) return Hit(re.Value);
                Tried("cli", $"(cli:--path 无效) {p}");
                return Fail("cli:--path", new[]
                {
                    Notes.Error("显式指定的程序无效", $"{p} 存在，但推不出 zcode.cjs。已按 fail-closed 中止。", "zcode/locate"),
                });
            }

            if (Directory.Exists(p))
            {
                foreach (var rel in BundleRelCandidates)
                {
                    var b = Combine(p, rel);
                    if (!File.Exists(b)) continue;
                    var r = Accept((b, null, "cli:--path(目录)"), "cli:--path(目录)");
                    if (r is not null) return Hit(r.Value);
                }
                var exeInDir = Path.Combine(p, ExeName);
                Tried("cli", $"(cli:--path 目录) {p}");
                if (File.Exists(exeInDir))
                {
                    var r = Accept((null, exeInDir, "cli:--path(目录)"), "cli:--path(目录)");
                    if (r is not null) return Hit(r.Value);
                }
                Tried("cli", $"{p} (recursive)");
                var hit = SearchBundleUnder(p, 3);
                if (hit is not null)
                {
                    var r = Accept((hit, null, "cli:--path(递归)"), "cli:--path(递归)");
                    if (r is not null) return Hit(r.Value);
                }
                Tried("cli", $"(cli:--path 无效) {p}");
                return Fail("cli:--path", new[]
                {
                    Notes.Error("显式指定的目录无效", $"{p} 里找不到 zcode.cjs（含浅递归）。已按 fail-closed 中止。", "zcode/locate"),
                });
            }

            Tried("cli", $"(cli:--path 无效) {p}");
            return Fail("cli:--path", new[]
            {
                Notes.Error("显式指定的路径不存在", $"{p} 既不是文件也不是目录。已按 fail-closed 中止。", "zcode/locate"),
            });
        }

        // ---------------------------------------------------------------- 2. 环境变量
        var envs = EnvCandidates();
        foreach (var c in envs)
        {
            var r = Accept(c);
            if (r is not null)
            {
                Tried("env", r.Value.Bundle, true, c.Src);
                return Hit(r.Value);
            }
            Tried("env", c.Bundle ?? c.Exe ?? "(空)", false, c.Src);
        }
        if (envs.Count > 0)
        {
            var set = new List<string>();
            foreach (var (name, val) in new[]
                     {
                         ("ZCODE_BUNDLE", Environment.GetEnvironmentVariable("ZCODE_BUNDLE")),
                         ("ZCODE_ROOT", Environment.GetEnvironmentVariable("ZCODE_ROOT")),
                         ("ZCODE_EXE", Environment.GetEnvironmentVariable("ZCODE_EXE")),
                     })
            {
                if (!string.IsNullOrWhiteSpace(val)) set.Add($"{name}={val}");
            }
            var msg = $"环境变量已设置但路径无效，已忽略：{string.Join("  ", set)}";
            tried.Add($"({msg})");
            warnings.Add(msg);
        }

        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- 3. 记忆文件
        if (options is null || !options.IgnoreMemory)
        {
            var mem = PathMemory.Load(MemoryFile, "bundle");
            if (!string.IsNullOrWhiteSpace(mem) && File.Exists(mem))
            {
                var r = Accept((mem, null, "memory"), "memory");
                if (r is not null)
                {
                    Tried("memory", r.Value.Bundle, true);
                    return Hit(r.Value);
                }
            }
            if (!string.IsNullOrWhiteSpace(mem))
            {
                var msg = $"记忆文件中的路径已失效，已忽略：{mem}";
                tried.Add($"({msg})");
                warnings.Add(msg);
                Tried("memory", mem!, false, "失效");
            }
        }

        // ---------------------------------------------------------------- 4. 运行中进程
        var procExe = PathProbe.FromProcess("ZCode");
        if (!string.IsNullOrEmpty(procExe))
        {
            Tried("process", $"process:{procExe}");
            var r = Accept((null, procExe, "process"), "process");
            if (r is not null)
            {
                Tried("process", r.Value.Bundle, true);
                return Hit(r.Value);
            }
        }

        // ---------------------------------------------------------------- 5. 注册表
        foreach (var dir in PathProbe.FromRegistry("ZCode"))
        {
            Tried("registry", $"registry:{dir}");
            var exe = Path.Combine(dir, ExeName);
            if (File.Exists(exe))
            {
                var r = Accept((null, exe, $"registry:{dir}"), $"registry:{dir}");
                if (r is not null)
                {
                    Tried("registry", r.Value.Bundle, true);
                    return Hit(r.Value);
                }
            }
            foreach (var rel in BundleRelCandidates)
            {
                var b = Combine(dir, rel);
                if (!File.Exists(b)) continue;
                var r = Accept((b, null, $"registry:{dir}"), $"registry:{dir}");
                if (r is not null)
                {
                    Tried("registry", r.Value.Bundle, true);
                    return Hit(r.Value);
                }
            }
        }

        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- 6 + 7. 快捷方式与嗅探合并成候选池
        var pooled = new List<(string Bundle, string? Exe, string Source)>();
        foreach (var c in ShortcutCandidates(Tried)) { var a = Accept(c); if (a is not null) pooled.Add(a.Value); }
        foreach (var c in SniffCandidates(options?.FullSniff == true, Tried, sniffHits)) { var a = Accept(c); if (a is not null) pooled.Add(a.Value); }

        if (pooled.Count > 0)
        {
            var uniq = new List<(string Bundle, string? Exe, string Source)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in pooled)
            {
                if (!seen.Add(c.Bundle.ToLowerInvariant())) continue;
                uniq.Add(c);
            }

            if (uniq.Count == 1)
            {
                var one = uniq[0];
                Tried("sniff", one.Bundle, true, one.Source);
                return Hit(one);
            }

            var withTime = uniq
                .Select(c => (c.Bundle, c.Exe, c.Source, Mtime: SafeMtime(c.Bundle)))
                .OrderByDescending(x => x.Mtime)
                .ToList();
            var chosen = withTime[0];
            var msg = $"检测到 {withTime.Count} 个 ZCode 安装，已自动选用最近修改的一个：{chosen.Bundle}";
            warnings.Add(msg);
            tried.Add($"(注意) 检测到 {withTime.Count} 个 ZCode 安装，已选用最近修改的一个：");
            foreach (var c in withTime) tried.Add($"  - {c.Bundle}  {c.Mtime:o}  [{c.Source}]");
            var conflicts = withTime.Select(c => $"{c.Bundle} [{c.Source}] {c.Mtime:o}").ToArray();
            return Hit((chosen.Bundle, chosen.Exe, $"{chosen.Source} (多安装，选最新)"), conflicts);
        }

        return Fail(null, new[]
        {
            Notes.Error("未能定位 ZCode 安装", "多级探测（CLI → 环境变量 → 记忆 → 进程 → 注册表 → 快捷方式 → 目录嗅探）全部未命中。", "zcode/locate"),
        }.Concat(warnings.Select(w => Notes.Warn("定位警告", w, "zcode/locate"))).ToArray());
    }

    /// <summary>命中的安装根记忆下来（源码在 find 子命令里做同样的事）。</summary>
    public static void Remember(string bundle) => PathMemory.Save(MemoryFile, bundle, "bundle");

    /// <summary>由某个可执行文件路径推导 bundle（含 resources 目录内的浅递归兜底）。</summary>
    public static string? BundleFromExe(string exe, List<string> tried)
    {
        var root = Path.GetDirectoryName(exe) ?? exe;
        foreach (var rel in BundleRelCandidates)
        {
            var p = Combine(root, rel);
            tried.Add(p);
            if (File.Exists(p)) return p;
        }
        var resDir = Path.Combine(root, "resources");
        if (Directory.Exists(resDir))
        {
            tried.Add($"{resDir} (recursive)");
            var hit = SearchBundleUnder(resDir, 3);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>从任意一个 bundle 路径推导安装根。</summary>
    public static string RootFromBundle(string bundle)
    {
        var parts = bundle.Split('\\', '/');
        var idx = Array.FindIndex(parts, s => s.Equals("resources", StringComparison.OrdinalIgnoreCase));
        if (idx > 0) return string.Join(Path.DirectorySeparatorChar, parts.Take(idx));
        return Path.GetDirectoryName(bundle) ?? bundle;
    }

    /// <summary>递归查找 bundle（限深度 + 预算，跳过明显无关的大目录）。</summary>
    public static string? SearchBundleUnder(string dir, int maxDepth = 3, int budget = 4000)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((dir, 0));
        while (stack.Count > 0)
        {
            if (budget-- <= 0) return null;
            var (d, depth) = stack.Pop();
            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(d);
                dirs = Directory.GetDirectories(d);
            }
            catch { continue; }

            foreach (var f in files)
            {
                if (Path.GetFileName(f).Equals("zcode.cjs", StringComparison.OrdinalIgnoreCase)) return f;
            }
            if (depth >= maxDepth) continue;
            foreach (var sub in dirs)
            {
                var name = Path.GetFileName(sub);
                if (name is "node_modules" or ".git" or "cache" or "Cache" or "logs" or "log") continue;
                stack.Push((sub, depth + 1));
            }
        }
        return null;
    }

    private static string Combine(string root, string[] rel) => Path.Combine(new[] { root }.Concat(rel).ToArray());

    /// <summary>把定位来源串映射到契约里的 level 取值（cli/env/memory/process/registry/shortcut/sniff）。</summary>
    private static string LevelOf(string source)
    {
        if (source.StartsWith("cli:", StringComparison.OrdinalIgnoreCase)) return "cli";
        if (source.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) return "env";
        if (source.StartsWith("memory", StringComparison.OrdinalIgnoreCase)) return "memory";
        if (source.StartsWith("process", StringComparison.OrdinalIgnoreCase)) return "process";
        if (source.StartsWith("registry:", StringComparison.OrdinalIgnoreCase)) return "registry";
        if (source.StartsWith("shortcut", StringComparison.OrdinalIgnoreCase)) return "shortcut";
        if (source.StartsWith("sniff:", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("user:", StringComparison.OrdinalIgnoreCase)) return "sniff";
        return "sniff";
    }

    private static DateTimeOffset SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTimeOffset.MinValue; }
    }

    private static List<(string? Bundle, string? Exe, string Src)> EnvCandidates()
    {
        var outList = new List<(string?, string?, string)>();
        var bundle = Environment.GetEnvironmentVariable("ZCODE_BUNDLE");
        if (!string.IsNullOrWhiteSpace(bundle)) outList.Add((bundle, null, "env:ZCODE_BUNDLE"));
        var root = Environment.GetEnvironmentVariable("ZCODE_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            foreach (var rel in BundleRelCandidates) outList.Add((Combine(root!, rel), null, "env:ZCODE_ROOT"));
            outList.Add((null, Path.Combine(root!, ExeName), "env:ZCODE_ROOT"));
        }
        var exe = Environment.GetEnvironmentVariable("ZCODE_EXE");
        if (!string.IsNullOrWhiteSpace(exe)) outList.Add((null, exe, "env:ZCODE_EXE"));
        return outList;
    }

    private static List<(string? Bundle, string? Exe, string Src)> ShortcutCandidates(
        Action<string, string, bool, string?> tried)
    {
        var outList = new List<(string?, string?, string)>();
        var up = HomeDir();
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var lnks = new List<string>
        {
            Path.Combine(up, "Desktop", "ZCode.lnk"),
        };
        if (!string.IsNullOrWhiteSpace(appData))
            lnks.Add(Path.Combine(appData!, "Microsoft", "Windows", "Start Menu", "Programs", "ZCode.lnk"));
        lnks.Add(Path.Combine(up, "Desktop", "ZCode.exe"));

        foreach (var lnk in lnks)
        {
            tried("shortcut", lnk, false, null);
            if (!File.Exists(lnk)) continue;
            if (lnk.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                outList.Add((null, lnk, "shortcut"));
                continue;
            }
            foreach (var target in PathProbe.FromShortcuts(new[] { lnk }))
            {
                if (File.Exists(target)) outList.Add((null, target, "shortcut"));
            }
        }
        return outList;
    }

    private static List<(string? Bundle, string? Exe, string Src)> SniffCandidates(
        bool fullSniff, Action<string, string, bool, string?> tried, List<string> sniffHits)
    {
        var outList = new List<(string?, string?, string)>();
        string[][] templates =
        {
            new[] { "ZCode" },
            new[] { "Program Files", "ZCode" },
            new[] { "Program Files (x86)", "ZCode" },
            new[] { "Program Files", "ZCode", "ZCode" },
            new[] { "Apps", "ZCode" },
            new[] { "App", "ZCode" },
            new[] { "Software", "ZCode" },
            new[] { "Tools", "ZCode" },
            new[] { "Programs", "ZCode" },
            new[] { "Applications", "ZCode" },
            new[] { "ZCode", "ZCode" },
        };

        void Dig(string dir, string src)
        {
            var exe = Path.Combine(dir, ExeName);
            if (File.Exists(exe))
            {
                outList.Add((null, exe, src));
                sniffHits.Add(exe);
            }
            foreach (var rel in BundleRelCandidates)
            {
                var b = Combine(dir, rel);
                if (!File.Exists(b)) continue;
                outList.Add((b, null, src));
                sniffHits.Add(b);
            }
            if (!fullSniff) return;
            // 扩展（源码没有）：显式要求全盘嗅探时，再在候选目录里浅递归找一次 zcode.cjs。
            var deep = SearchBundleUnder(dir, 3, 1200);
            if (deep is not null)
            {
                outList.Add((deep, null, src + "(deep)"));
                sniffHits.Add(deep);
            }
        }

        foreach (var drive in PathProbe.Drives())
        {
            foreach (var tpl in templates)
            {
                var dir = Path.Combine(new[] { drive }.Concat(tpl).ToArray());
                tried("sniff", dir, false, null);
                if (!Directory.Exists(dir)) continue;
                Dig(dir, $"sniff:{dir}");
            }
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var up = HomeDir();
        var userRoots = new[]
        {
            string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData!, "Programs", "ZCode"),
            string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData!, "ZCode"),
            string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData!, "ZCode"),
            string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData!, "Programs", "ZCode"),
            Path.Combine(up, "ZCode"),
            Path.Combine(up, "Desktop", "ZCode"),
            Path.Combine(up, "scoop", "apps", "zcode", "current"),
        }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray();

        foreach (var dir in userRoots)
        {
            tried("sniff", dir, false, null);
            if (!Directory.Exists(dir)) continue;
            Dig(dir, $"user:{dir}");
        }

        return outList;
    }

    /// <summary>注册表里的 DisplayVersion（源码没有报告版本，这是补契约 TargetLocation.Version 的附加读取，只读不写）。</summary>
    public static string? RegistryVersion()
    {
        var subKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var sub in subKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var key = baseKey.OpenSubKey(sub);
                    if (key is null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var app = key.OpenSubKey(name);
                        var display = app?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(display) ||
                            display.IndexOf("ZCode", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var ver = app?.GetValue("DisplayVersion") as string;
                        if (!string.IsNullOrWhiteSpace(ver)) return ver!.Trim();
                        var tail = display.Split(' ').LastOrDefault();
                        if (!string.IsNullOrWhiteSpace(tail) && char.IsDigit(tail![0])) return tail;
                    }
                }
                catch { /* 权限或键不存在 */ }
            }
        }
        return null;
    }
}
