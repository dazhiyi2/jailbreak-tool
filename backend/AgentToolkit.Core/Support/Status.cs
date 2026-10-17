using AgentToolkit.Contracts;
using AgentToolkit.Core.Support;
using AgentToolkit.Core.Targets;

namespace AgentToolkit.Core.Support;

/// <summary>状态页的层装配：把一个磁盘文件 + 标记表 + 备份，折成契约的 LayerStatus。</summary>
public static class StatusBuilder
{
    public static LayerStatus BuildLayer(
        LayerKind kind,
        string displayName,
        string? targetPath,
        long? capBytes,
        IReadOnlyList<string> markers,
        Func<string, bool>? parseOk,
        string absentDetail,
        string appliedDetail,
        string notAppliedDetail,
        string? installedEvidence = null)
    {
        var layer = new LayerStatus
        {
            Kind = kind,
            DisplayName = displayName,
            TargetPath = targetPath,
            CapBytes = capBytes,
        };

        if (!FileText.Exists(targetPath))
        {
            layer.State = LayerState.TargetAbsent;
            layer.StateDetail = absentDetail;
            return layer;
        }

        var text = FileText.Read(targetPath!);
        layer.SizeBytes = FileText.Bytes(text);
        layer.ParseOk = parseOk?.Invoke(text) ?? true;

        var hitMarkers = markers.Where(m => !string.IsNullOrEmpty(m) && FileText.Count(text, m) > 0).ToArray();
        layer.State = hitMarkers.Length > 0 ? LayerState.Applied : LayerState.NotApplied;
        layer.StateDetail = hitMarkers.Length > 0 ? appliedDetail : notAppliedDetail;
        layer.EffectivenessEvidence = installedEvidence;

        var backup = new BackupManager(targetPath!);
        var assessment = backup.Assess(markers);
        layer.Backup = backup.ToContract(assessment);

        if (assessment.UsesLegacyName)
            layer.Notes = layer.Notes.Concat(new[]
            {
                Notes.Info("备份口径",
                    $"这份备份用的是参考实现的旧命名：{assessment.Path}。本工具新建的备份叫 .breach.bak。", displayName),
            }).ToArray();

        if (layer.Backup.Health == BackupHealth.Contaminated)
            layer.Notes = new[]
            {
                Notes.Warn("备份污染",
                    "备份里含注入标记，它不是原始文件。用它还原会把注入「还原」回来，工具还会报告成功。" +
                    "请走逆向撤销路径，别用这份备份。", displayName),
            };
        else if (layer.State == LayerState.Applied && layer.Backup.Health == BackupHealth.Absent)
            layer.Notes = new[]
            {
                Notes.Warn("没有备份", "当前文件已带注入但没有备份，还原将只能靠逆向撤销。", displayName),
            };

        if (capBytes is long cap && layer.SizeBytes >= cap)
            layer.Notes = layer.Notes.Concat(new[]
            {
                Notes.Error("超出体积上限",
                    $"当前 {FileText.Human(layer.SizeBytes)}，上限 {FileText.Human(cap)}；客户端可能截断或拒绝加载。", displayName),
            }).ToArray();

        return layer;
    }

    /// <summary>锚点/替换目标的命中情况，用于把"升级覆盖 / 锚点不唯一"讲清楚。</summary>
    public static LayerStatus WithAnchorState(LayerStatus layer, int anchorHits, int markerHits)
    {
        if (markerHits > 0) { layer.State = LayerState.Applied; return layer; }
        layer.State = anchorHits switch
        {
            0 => LayerState.AnchorMissing,
            1 => LayerState.NotApplied,
            _ => LayerState.AnchorAmbiguous,
        };
        layer.StateDetail = layer.State switch
        {
            LayerState.AnchorMissing => "锚点失配：当前文件里找不到可打的位置，多半是被客户端升级覆盖了。",
            LayerState.AnchorAmbiguous => $"锚点不唯一（命中 {anchorHits} 次）：拒绝写入，先手工缩小范围。",
            _ => layer.StateDetail,
        };
        return layer;
    }

    public static IReadOnlyList<PolicyFileInfo> PolicyFiles(TargetContext ctx)
    {
        var list = new List<PolicyFileInfo>();
        foreach (var def in ctx.PolicyDefs)
        {
            var text = ctx.Policy.Read(def) ?? "";
            var bytes = FileText.Bytes(text);
            var notes = new List<Note>();
            if (def.CapBytes is long cap && bytes >= cap)
                notes.Add(Notes.Error("超出体积上限", $"当前 {FileText.Human(bytes)}，上限 {FileText.Human(cap)}。", def.DisplayName));
            if (!def.Editable) notes.Add(Notes.Info("只读", "该文本由补丁表固化，改它要改补丁表本身。", def.DisplayName));

            list.Add(new PolicyFileInfo
            {
                Name = def.Name,
                DisplayName = def.DisplayName,
                Path = ctx.Policy.PathOf(def),
                Text = text,
                SizeBytes = bytes,
                CapBytes = def.CapBytes,
                Editable = def.Editable,
                Notes = notes,
            });
        }
        return list;
    }

    public static PolicyFileInfo SavePolicy(TargetContext ctx, string name, string text)
    {
        var def = ctx.Def(name) ?? throw new AgentEngineException(EngineErrorCode.TargetFileMissing, $"没有名为「{name}」的注入文本。");
        if (def.CapBytes is long cap && FileText.Bytes(text) >= cap)
            throw new AgentEngineException(EngineErrorCode.VerifyProblems,
                $"「{def.DisplayName}」超出上限：{FileText.Human(FileText.Bytes(text))} / {FileText.Human(cap)}，拒绝保存。");
        if (def.Required && string.IsNullOrWhiteSpace(text))
            throw new AgentEngineException(EngineErrorCode.VerifyProblems, $"「{def.DisplayName}」不能为空。");
        ctx.Policy.Write(def, text);
        return PolicyFiles(ctx).First(p => p.Name == def.Name);
    }

    public static ExportResult ExportText(string sessionId, string? outputPath, string defaultName, string content)
    {
        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), defaultName)
            : outputPath!;
        FileText.Write(path, content);
        return new ExportResult
        {
            Exported = true,
            Path = path,
            SizeBytes = FileText.Bytes(content),
            Notes = new[] { Notes.Info("已导出", $"{sessionId} → {path}") },
        };
    }
}
