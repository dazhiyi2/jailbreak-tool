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
        // 方案选择器本身作为第一个只读条目返回（界面能显示、CLI 能切换）。
        var scheme = ctx.ActiveScheme(out var schemeNote);
        var list = new List<PolicyFileInfo> { SchemeEntry(ctx, scheme, schemeNote) };

        foreach (var def in SchemeResolve.ForScheme(ctx.PolicyDefs, scheme))
        {
            var text = ctx.Policy.Read(def) ?? "";
            var bytes = FileText.Bytes(text);
            var notes = new List<Note>();
            var emptySlot = !def.Required && string.IsNullOrWhiteSpace(text);
            if (def.CapBytes is long cap && bytes >= cap)
                notes.Add(Notes.Error("超出体积上限", $"当前 {FileText.Human(bytes)}，上限 {FileText.Human(cap)}。", def.DisplayName));
            if (emptySlot)
                notes.Add(Notes.Info("该方案下此槽位不注入",
                    "这是当前方案的空槽：不写文件、不产生计划变更；切换方案时会把本工具在另一套方案里遗留的文件移除。", def.DisplayName));
            else if (!def.Editable)
                notes.Add(Notes.Info("只读", "该文本由补丁表固化，改它要改补丁表本身。", def.DisplayName));

            list.Add(new PolicyFileInfo
            {
                Name = def.Name,
                DisplayName = def.DisplayName,
                Path = ctx.Policy.PathOf(def),
                Text = text,
                SizeBytes = bytes,
                CapBytes = def.CapBytes,
                Editable = def.Editable && !emptySlot,
                Notes = notes,
            });
        }
        return list;
    }

    /// <summary>方案选择器条目（保留名 scheme）。界面 / CLI / MultiTargetEngine 都靠它读当前方案。</summary>
    public static PolicyFileInfo SchemeFile(TargetContext ctx)
    {
        var scheme = ctx.ActiveScheme(out var note);
        return SchemeEntry(ctx, scheme, note);
    }

    private static PolicyFileInfo SchemeEntry(TargetContext ctx, InjectionScheme scheme, string? schemeNote)
    {
        var value = SchemeText.ValueOf(scheme);
        var notes = new List<Note>
        {
            Notes.Info("注入方案",
                $"当前 {SchemeText.Describe(scheme)}；切换用 SavePolicyFileAsync(\"scheme\", \"own\"|\"protocol\")。" +
                $"该方案下不注入的槽位：{DescribeEmptySlots(ctx, scheme)}。",
                ctx.Policy.SchemePath),
        };
        if (schemeNote is not null)
            notes.Add(Notes.Warn("方案文件", schemeNote, ctx.Policy.SchemePath));

        return new PolicyFileInfo
        {
            Name = SchemeText.EntryName,
            DisplayName = SchemeText.DisplayName,
            Path = ctx.Policy.SchemePath,
            Text = value,
            SizeBytes = FileText.Bytes(value),
            CapBytes = null,
            Editable = false,
            Notes = notes,
        };
    }

    public static string DescribeEmptySlots(TargetContext ctx, InjectionScheme scheme)
    {
        var names = SchemeResolve.EmptySlotNames(ctx, scheme);
        return names.Count == 0 ? "（无）" : string.Join("、", names);
    }

    public static PolicyFileInfo SavePolicy(TargetContext ctx, string name, string text)
    {
        if (string.Equals(name, SchemeText.EntryName, StringComparison.OrdinalIgnoreCase))
        {
            if (!SchemeText.TryParse(text, out var scheme))
                throw new AgentEngineException(EngineErrorCode.VerifyProblems,
                    $"「注入方案」只接受 own / protocol（收到「{(text ?? "").Trim()}」），拒绝保存。");
            ctx.Schemes().Write(scheme);
            return PolicyFiles(ctx).First(p => p.Name == SchemeText.EntryName);
        }

        var active = ctx.ActiveScheme();
        var def = SchemeResolve.Pick(ctx.PolicyDefs, name, active)
                  ?? throw new AgentEngineException(EngineErrorCode.TargetFileMissing, $"没有名为「{name}」的注入文本。");
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
