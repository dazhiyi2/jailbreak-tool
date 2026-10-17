namespace AgentToolkit.SelfTest;

/// <summary>
/// 目标的回归自检。硬性纪律：**只在沙箱副本上操作，绝不碰真实客户端目录**。
/// 沙箱由测试自己创建，所有路径通过构造参数或环境变量注入适配器。
/// </summary>
public interface ITargetSelfTest
{
    string TargetId { get; }
    string Name { get; }
    SelfTestResult Run(SelfTestContext ctx);
}

public sealed class SelfTestContext
{
    public required string WorkDir { get; init; }
    public required Action<string> Log { get; init; }

    private int _seq;

    /// <summary>建一个全新沙箱目录。</summary>
    public string MakeSandbox(string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        var slug = $"{safe}-{++_seq}-{Guid.NewGuid():N}";
        if (slug.Length > 64) slug = slug[..64];
        var dir = Path.Combine(WorkDir, slug);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>复制真实文件进沙箱（只读源，绝不写回）。</summary>
    public string CopyInto(string sandbox, string sourceFile)
    {
        var dst = Path.Combine(sandbox, Path.GetFileName(sourceFile));
        File.Copy(sourceFile, dst, overwrite: true);
        return dst;
    }
}

public sealed class SelfTestResult
{
    public required string Name { get; init; }
    public List<string> Steps { get; } = new();
    public List<string> Failures { get; } = new();
    public bool Passed => Failures.Count == 0;

    public void Step(string text) => Steps.Add(text);

    public void Check(bool condition, string what)
    {
        if (condition) Steps.Add("  ✔ " + what);
        else Failures.Add("  ✘ " + what);
    }

    public void Fail(string why) => Failures.Add("  ✘ " + why);
}
