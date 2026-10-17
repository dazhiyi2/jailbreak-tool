# 给后端负责人的接入说明

UI 源码位于 `D:\破甲\toolkit\ui`。后端由另一位负责人实现；当前发布包为有明确模拟横幅的 Mock 版本，不是真实引擎。

## 先看这三份文件

1. `Contracts\EngineContracts.cs`：冻结的 v1 原文，不要在 UI 侧修改。
2. `Services\ServiceLocator.cs`：唯一引擎工厂；全窗口共享其创建的一个 IAgentEngine 实例。
3. `UI-NOTES.md`：页面映射、构建环境、交互约定和契约缺口。

## 最少改动的接入方式

当前契约源由 UI 项目直接编译。若后端按任务约定交付单文件 C# 实现，将该源码放到 `Services` 下，由现有项目一起编译；然后仅将工厂的返回表达式替换为真实实现的构造表达式。不要为了接线重新实现 UI 或改页面的接口调用。

若后端交付独立程序集，请先统一契约的程序集类型身份：在两个程序集各自编译同一份 `EngineContracts.cs`，仍会产生两个不同的 IAgentEngine 类型，不能直接赋值。独立程序集方案需要双方负责人统一公共契约项目/引用方式，保持冻结文件内容不变；不要用反射或随意转换掩盖类型不兼容。

UI 的三个直接 NuGet 引用已锁定，接入时不要在 UI 添加白名单外的包：

- Microsoft.WindowsAppSDK 2.5.1
- Microsoft.Windows.SDK.BuildTools 10.0.28000.2705
- CommunityToolkit.Mvvm 8.4.2

框架为 `net8.0-windows10.0.19041.0`，使用已安装的 .NET SDK 10.0.402 构建。

## 页面会怎样调用引擎

| 页面 | 约定 |
| --- | --- |
| 探测 | 传递手动安装根、配置根、IgnoreMemory 和 FullSniff；显示完整 Attempts、SniffCandidates 与 Notes |
| 状态 | 读取所有 Layers，分别展示安装状态、解析结果、备份健康度和生效证据；自检使用 LastMarkerReport.SessionId 调 CheckMarkersAsync |
| 注入 | 进入先 PreviewApplyAsync(DryRun=true)，用户确认后 ApplyAsync(DryRun=false)；执行使用最近预览的同一组选项 |
| 还原 | 先 RevertAsync(DryRun=true)，再 RevertAsync(DryRun=false)；预览阶段不会把回读字段解释为已完成的验证 |
| 日志 | ListSessionsAsync → LoadSessionAsync → CheckMarkersAsync；默认最多 60 轮、每段 20000 字符，截断须标记 Truncated |
| 差异 | GetSystemPromptDiffAsync；按引擎的 DiffKind 与行号显示，不自行重算业务差异 |
| 文本 | GetPolicyFilesAsync / SavePolicyFileAsync；UI 按 UTF-8 提示体积，后端仍须独立验证上限、只读和内容 |
| 台账 | 读取探针定义与历史；人工判定映射 Failed / HalfBroken / Broken，调用 SaveProbeVerdictAsync 后重新读历史 |

## 不可遗漏的后端语义

- DryRun 必须不修改真实目标、备份或台账。尤其不能把还原预览当成还原执行。
- Force 经 UI 二次确认后才传 true；它不代表 UI 授权跳过锚点唯一性、解析或备份保护。
- RemoveBackups 同样经过二次确认。取消确认时保持 false。
- 进度通过 IProgress<EngineProgress> 上报；Fraction=null 表示不确定，数值范围为 0–1。
- 支持 CancellationToken。取消发生后 UI 会要求刷新实际状态，不会把取消解释为已回滚。
- 失败抛 AgentEngineException 并保留 Code 与 Notes；UI 不解析异常文本来决定业务状态。
- LayerState 六个值、BackupHealth 五个值均有展示映射。InstalledButNotEffective 与 EffectivenessEvidence 必须按证据填写，不要由 Applied 推断生效。
- RevertResult.ReadBackClean 与 MatchesOriginal 是不同结论。原始备份无法信任时，MatchesOriginal 应为 null，不能伪造 true。
- UI 只保证本窗口内不主动并发切换两个引擎任务；后端仍负责文件锁、文件变动、幂等、备份与写入原子性。
- PreviewId 在 v1 中无法传回 ApplyAsync。后端必须在真正写入前重新验证文件与锚点，防止预览后目标被改动。

## 导出：冻结 v1 的现状

UI 已接好两处输出：`SessionDetail.ExportedJsonlPath` 与 `MarkerReport.ExportedSystemPromptPath`。当真实引擎返回本机绝对路径时，可用“打开已导出的会话文件 / 系统提示词”按钮交给记事本打开；不检查文件存在性、不自行写文件。

v1 **没有发起导出的方法，也没有导出目录参数**。后端需要明确已导出文件如何生成，并填入这两个字段：可以是后端已存在的导出结果，或经负责人明确约定的生成流程。若必须让用户点击“创建导出”并选择输出路径，则需要契约负责人另行设计接口；UI 不擅自扩展冻结契约。

## 接管后清理

将 ServiceLocator 切为真实实现后，顶部模拟横幅会自动关闭；导出按钮也会按返回路径可用性启用。删除 MainWindow.xaml.cs 中 `#if DEBUG` 内带 `// SEAM: mock-only` 的场景切换器即可。MockEngine 可作为开发样例保留。

最后按 UI-NOTES 的固定命令重新构建、发布，并用真实目标验证。当前 UI 的 Mock 检查不能代替真实引擎验收。
