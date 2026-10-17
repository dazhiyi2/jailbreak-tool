// === 各页展示投影；对应 SPEC §4.1、§5；不执行引擎调用。
// === 所有值预先格式化，XAML 只绑定展示属性；四态由 PageViewModel 统一提供。
using AgentToolkit.Contracts;
using AgentToolkit.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace AgentToolkit.Ui.ViewModels;

public static class Display
{
    public static string Bytes(long value) => value >= 1048576 ? (value / 1048576d).ToString("F1", CultureInfo.InvariantCulture) + " MB" : (value / 1024d).ToString("F1", CultureInfo.InvariantCulture) + " KB";
    public static string Size(long value, long? cap) => Bytes(value) + " / " + (cap.HasValue ? Bytes(cap.Value) : "未设上限");
    public static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "无时间记录";
    public static string Text(string? value, string fallback = "未提供") => string.IsNullOrWhiteSpace(value) ? fallback : value;
    public static string YesNo(bool value) => value ? "是" : "否";
    public static string MarkerLayerName(MarkerLayer layer) => layer switch
    { MarkerLayer.SystemPrompt => "系统提示词", MarkerLayer.ContextPayload => "上下文载荷", MarkerLayer.WrapperOrGuidance => "外围指令", MarkerLayer.FirstParty => "内置片段", MarkerLayer.PolicySignal => "策略信号", _ => "未知层" };
    public static string LayerKindName(LayerKind kind) => kind switch
    { LayerKind.SystemPrompt => "系统提示词层", LayerKind.PresetOverride => "preset 覆写层", LayerKind.ContextPayload => "上下文指令层", LayerKind.PolicyText => "注入文本层", _ => "未知层" };
    public static string TargetKindName(TargetKind kind) => kind switch
    {
        TargetKind.PlaintextBundle => "明文提示词的本地应用",
        TargetKind.PackagedElectron => "打包的 Electron（提示词在 app.asar）",
        TargetKind.CloudAssembled => "云端组装提示词（只能走载荷层）",
        TargetKind.CustomHarness => "自建 harness",
        _ => "未分类",
    };
}

public sealed class NoteItem(Note note)
{
    public string Title => note.Title;
    public string Detail => note.Detail + (string.IsNullOrWhiteSpace(note.Source) ? "" : "\n来源：" + note.Source);
    public InfoBarSeverity Severity => note.Severity switch
    { AgentToolkit.Contracts.Severity.Error => InfoBarSeverity.Error, AgentToolkit.Contracts.Severity.Warning => InfoBarSeverity.Warning, _ => InfoBarSeverity.Informational };
}

public sealed class AttemptItem(LocateAttempt item)
{
    public string Level => item.Level switch
    { "cli" => "手动路径", "env" => "环境变量", "memory" => "记忆路径", "process" => "运行进程", "registry" => "注册表", "shortcut" => "快捷方式", "sniff" => "全盘嗅探", _ => item.Level };
    public string Candidate => item.Candidate;
    public string Hit => item.Hit ? "✔ 命中" : "未命中";
    public BadgeTone Tone => item.Hit ? BadgeTone.Success : BadgeTone.Neutral;
    public string Detail => Display.Text(item.Note);
}

/// <summary>目标选择项：多目标引擎的一个可操作客户端。</summary>
public sealed class TargetItem(TargetInfo info)
{
    public string Id => info.Id;
    public string DisplayName => info.DisplayName;
    public string Label => info.Installed ? info.DisplayName : info.DisplayName + "（未安装）";
    public string Kind => Display.TargetKindName(info.Kind);
    public string Version => Display.Text(info.Version, "版本未知");
    public string Root => Display.Text(info.InstallRoot, "未定位");
    public string Bundle => Display.Text(info.BundlePath, "未定位");
    public string ConfigHome => Display.Text(info.ConfigHome, "未定位");
    public string LocatedBy => Display.Text(info.LocatedBy, "未定位");
    public bool Installed => info.Installed;
    public string Note => Display.Text(info.Note, "");
    public IReadOnlyList<string> Layers => info.Layers;
}

public sealed class LayerItem(LayerStatus layer)
{
    public string Name => layer.DisplayName;
    public string Path => Display.Text(layer.TargetPath);
    public string StateLabel => layer.State switch
    { LayerState.NotApplied => "未注入", LayerState.Applied => "已注入", LayerState.AnchorMissing => "锚点失配 · 升级覆盖", LayerState.AnchorAmbiguous => "锚点不唯一 · 拒绝写入", LayerState.TargetAbsent => "目标缺失", _ => "未知状态" };
    public BadgeTone StateTone => layer.State switch
    { LayerState.Applied => BadgeTone.Success, LayerState.AnchorMissing => BadgeTone.Warning, LayerState.AnchorAmbiguous => BadgeTone.Error, LayerState.TargetAbsent => BadgeTone.Error, _ => BadgeTone.Neutral };
    public string Detail => layer.StateDetail;
    public string Size => Display.Size(layer.SizeBytes, layer.CapBytes);
    public string ParseLabel => layer.State is LayerState.TargetAbsent or LayerState.Unknown ? "尚未解析" : layer.ParseOk ? "解析自检通过" : "解析自检失败";
    public BadgeTone ParseTone => layer.State is LayerState.TargetAbsent or LayerState.Unknown ? BadgeTone.Neutral : layer.ParseOk ? BadgeTone.Success : BadgeTone.Error;
    public string BackupLabel => layer.Backup.Health switch
    { BackupHealth.Absent => "备份缺失", BackupHealth.Clean => "备份干净", BackupHealth.Contaminated => "备份污染", BackupHealth.Unreadable => "备份不可读", _ => "备份未知" };
    public BadgeTone BackupTone => layer.Backup.Health switch
    { BackupHealth.Clean => BadgeTone.Success, BackupHealth.Contaminated => BadgeTone.Error, BackupHealth.Unreadable => BadgeTone.Error, BackupHealth.Absent => BadgeTone.Warning, _ => BadgeTone.Neutral };
    public string BackupPath => Display.Text(layer.Backup.Path);
    public string BackupSummary => Display.Bytes(layer.Backup.SizeBytes) + " · " + Display.Time(layer.Backup.SavedAt);
    public string BackupHash => Display.Text(layer.Backup.Sha256);
    public string SupersededPath => Display.Text(layer.Backup.SupersededPath);
    public string ContaminatedCopyPath => Display.Text(layer.Backup.ContaminatedCopyPath);
    public bool InstalledButNotEffective => layer.InstalledButNotEffective;
    public string Evidence => Display.Text(layer.EffectivenessEvidence, "没有生效证据。");
    public IReadOnlyList<NoteItem> Notes => layer.Notes.Select(n => new NoteItem(n)).ToArray();
}

public sealed class MarkerItem(MarkerHit item)
{
    public string Marker => item.Marker;
    public string Layer => Display.MarkerLayerName(item.Layer);
    public string Count => item.Count.ToString(CultureInfo.InvariantCulture);
    public BadgeTone Tone => item.Count > 0 ? BadgeTone.Success : BadgeTone.Neutral;
}

public sealed class ChangeItem(PlannedChange item)
{
    public string Path => item.Path;
    public string Layer => Display.LayerKindName(item.Layer);
    public string Action => item.WillWrite ? "将写入" : "无需写入";
    public string Size => Display.Bytes(item.BeforeBytes) + " → " + Display.Bytes(item.AfterBytes);
    public string Backup => Display.Text(item.BackupPath);
    public string Diff => Display.Text(item.DiffText, "引擎未提供统一差异文本。");
    public IReadOnlyList<NoteItem> Notes => item.Notes.Select(n => new NoteItem(n)).ToArray();
}

public sealed class SessionItem(SessionInfo item)
{
    public string Id => item.Id;
    public string Workspace => Display.Text(item.Workspace);
    public string Modified => Display.Time(item.ModifiedAt);
    public string Size => Display.Bytes(item.SizeBytes);
    public string Summary => Size + " · " + (item.TurnCount?.ToString() ?? "未知") + " 轮";
}

public sealed class TurnItem(TurnRecord item)
{
    public bool HasReasoning => !string.IsNullOrWhiteSpace(item.ReasoningText) && item.ReasoningText != "本轮没有推理原文。";
    public string Title => $"第 {item.Index} 轮 · " + (item.Role == "assistant" ? "助手" : item.Role == "user" ? "用户" : item.Role);
    public string At => Display.Time(item.At);
    public string Text => Display.Text(item.Text, "本轮没有回复文本。");
    public string Reasoning => Display.Text(item.ReasoningText, "本轮没有推理原文。");
    public bool Truncated => item.Truncated;
}

public sealed class DiffItem(DiffLine item)
{
    public string Left => Display.Text(item.LeftLineNo, "—");
    public string Right => Display.Text(item.RightLineNo, "—");
    public string Text => item.Text;
    public string Sign => item.Kind == DiffKind.Added ? "+" : item.Kind == DiffKind.Removed ? "−" : " ";
    public BadgeTone Tone => item.Kind == DiffKind.Added ? BadgeTone.Success : item.Kind == DiffKind.Removed ? BadgeTone.Error : BadgeTone.Neutral;
}

public sealed class ProbeItem(ProbeDefinition item)
{
    public string Id => item.Id;
    public string Title => item.Id + " · " + item.Title;
    public string HowTo => item.HowTo;
    public string Criteria => item.PassCriteria;
    public Visibility DecisiveVisibility => item.IsDecisive ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class VerdictItem(ProbeVerdict item)
{
    public string Probe => item.ProbeId;
    public string Level => item.Level switch { ProbeVerdictLevel.Failed => "没破", ProbeVerdictLevel.HalfBroken => "半破", ProbeVerdictLevel.Broken => "破了", _ => "未运行" };
    public string At => Display.Time(item.At);
    public string Excerpt => Display.Text(item.ReasoningExcerpt);
    public string Session => Display.Text(item.SessionId);
    public string Note => Display.Text(item.Note);
}
