// === SPEC §4 外壳与用户八页清单；通过单个共享 IAgentEngine 给各页注入依赖。
// === 页面四态委托给 PageViewModel；导航取消旧页面加载，不在 code-behind 调用业务接口。
using AgentToolkit.Contracts;
using AgentToolkit.Ui.Services;
using AgentToolkit.Ui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace AgentToolkit.Ui.ViewModels;

public partial class ShellViewModel(IAgentEngine engine) : ObservableObject
{
    private PageViewModel? _active;
    private string _route = "Status";
    private long _navigationVersion;
    private readonly Dictionary<string, (Page Page, PageViewModel? Model)> _pages = new();
    public IAgentEngine Engine { get; } = engine;
    public bool IsMock => Engine is MockEngine;
    [ObservableProperty] public partial object? Content { get; set; }

    private bool _switching;

    [ObservableProperty] public partial IReadOnlyList<TargetItem> Targets { get; set; } = Array.Empty<TargetItem>();
    [ObservableProperty] public partial TargetItem? SelectedTarget { get; set; }
    [ObservableProperty] public partial string TargetSummary { get; set; } = "正在识别本机目标…";

    /// <summary>列出全部可识别目标，默认选第一个已安装的。切换目标会清空页面缓存并重新加载当前页。</summary>
    public async Task InitializeAsync()
    {
        _switching = true;
        try
        {
            var list = await Engine.ListTargetsAsync();
            Targets = list.Select(t => new TargetItem(t)).ToArray();
            SelectedTarget = Targets.FirstOrDefault(t => t.Installed) ?? Targets.FirstOrDefault();
            TargetSummary = SelectedTarget is null
                ? "没有可识别的目标客户端。"
                : Describe(SelectedTarget);
        }
        catch (AgentEngineException ex)
        {
            TargetSummary = $"目标识别失败：[{ex.Code}] {ex.Message}";
        }
        catch (Exception ex)
        {
            TargetSummary = "目标识别失败：" + ex.Message;
        }
        finally { _switching = false; }
    }

    partial void OnSelectedTargetChanged(TargetItem? value)
    {
        if (value is null || _switching) return;
        _ = SwitchTargetAsync(value);
    }

    private async Task SwitchTargetAsync(TargetItem item)
    {
        if (_switching) return;
        _switching = true;
        try
        {
            // 切换目标前先把上一页在跑的引擎调用取消掉，避免旧目标的加载结果落进新目标的页面
            _active?.Cancel();
            await Engine.SelectTargetAsync(item.Id);
            TargetSummary = Describe(item);
            _pages.Clear();
            _active = null;
            await LoadAsync(_route);
        }
        catch (Exception ex)
        {
            TargetSummary = "切换目标失败：" + ex.Message;
        }
        finally { _switching = false; }
    }

    private static string Describe(TargetItem t) =>
        $"{t.DisplayName} · {t.Kind} · {t.Version}" + (string.IsNullOrWhiteSpace(t.Root) || t.Root == "未定位" ? "" : "\n" + t.Root);

    public async Task LoadAsync(string route)
    {
        var version = ++_navigationVersion;
        var previous = _active;
        previous?.Cancel();
        if (previous != null) await previous.WhenIdle;
        if (version != _navigationVersion) return;
        _route = route;
        var created = !_pages.TryGetValue(route, out var selection);
        if (created) selection = route switch
        {
            "Locate" => (new LocatePage(), new LocatePageViewModel(Engine)),
            "Status" => (new StatusPage(), new StatusPageViewModel(Engine)),
            "Inject" => (new InjectPage(), new InjectPageViewModel(Engine)),
            "Revert" => (new RevertPage(), new RevertPageViewModel(Engine)),
            "Logs" => (new LogsPage(), new LogsPageViewModel(Engine)),
            "Diff" => (new DiffPage(), new DiffPageViewModel(Engine)),
            "Policy" => (new PolicyPage(), new PolicyPageViewModel(Engine)),
            "Probes" => (new ProbesPage(), new ProbesPageViewModel(Engine)),
            _ => (new AboutPage(), null),
        };
        _pages[route] = selection;
        var (page, model) = selection;
        _active = model;
        page.DataContext = model;
        Content = page;
        if (model != null && (created || !model.HasContent || route is "Status" or "Inject" or "Revert")) await model.LoadAsync();
    }

    public Task RefreshAsync()
    {
        _pages.Clear();
        return LoadAsync(_route);
    }
    public void Cancel() => _active?.Cancel();
}
