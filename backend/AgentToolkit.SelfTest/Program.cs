using System.Reflection;
using AgentToolkit.SelfTest;

// selftest.exe —— 回归自检入口。所有用例都在沙箱副本上跑，不碰真实客户端。
//   selftest [--target dsh|zcode] [--work <dir>] [--keep] [--json]

var rest = new List<string>(args);
string? TakeFlag(string name)
{
    var i = rest.IndexOf(name);
    if (i < 0) return null;
    var v = i + 1 < rest.Count ? rest[i + 1] : null;
    rest.RemoveAt(i);
    if (v is not null) rest.RemoveAt(i);
    return v;
}
bool HasFlag(string name)
{
    var i = rest.IndexOf(name);
    if (i < 0) return false;
    rest.RemoveAt(i);
    return true;
}

var targetFilter = TakeFlag("--target");
var workRoot = TakeFlag("--work") ?? Path.Combine(Path.GetTempPath(), "agent-toolkit-selftest");
var keep = HasFlag("--keep");
var json = HasFlag("--json");

Directory.CreateDirectory(workRoot);

var tests = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => typeof(ITargetSelfTest).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
    .Select(t => (ITargetSelfTest)Activator.CreateInstance(t)!)
    .Where(t => targetFilter is null || t.TargetId.Equals(targetFilter, StringComparison.OrdinalIgnoreCase))
    .OrderBy(t => t.TargetId)
    .ToArray();

if (tests.Length == 0)
{
    Console.Error.WriteLine($"没有匹配的自检用例（--target {targetFilter ?? "any"}）。");
    return 2;
}

var results = new List<(string target, SelfTestResult r)>();
foreach (var test in tests)
{
    var ctx = new SelfTestContext
    {
        WorkDir = workRoot,
        Log = m => { if (!json) Console.WriteLine("    " + m); },
    };
    SelfTestResult r;
    try { r = test.Run(ctx); }
    catch (Exception ex)
    {
        r = new SelfTestResult { Name = test.Name };
        r.Fail($"用例抛出异常：{ex.GetType().Name}: {ex.Message}");
    }
    results.Add((test.TargetId, r));

    if (!json)
    {
        Console.WriteLine($"\n=== [{test.TargetId}] {r.Name} — {(r.Passed ? "通过" : "失败")} ===");
        foreach (var s in r.Steps) Console.WriteLine(s);
        foreach (var f in r.Failures) Console.WriteLine(f);
    }
}

var failed = results.Count(x => !x.r.Passed);
if (json)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
        results.Select(x => new { target = x.target, name = x.r.Name, passed = x.r.Passed, steps = x.r.Steps, failures = x.r.Failures }),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
else
{
    Console.WriteLine($"\n共 {results.Count} 个用例，失败 {failed} 个。沙箱：{workRoot}");
}

if (!keep)
{
    try { Directory.Delete(workRoot, true); } catch { /* 尽力而为 */ }
}

return failed == 0 ? 0 : 1;
