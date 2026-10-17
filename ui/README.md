# 破甲 · Windows 界面（WinUI 3）

八页工作台，已接入**真实多目标引擎**：左栏可切换目标（DeepSeek Harness / ZCode），所有页面都作用于当前选中的目标。

## 运行

打开 `dist\JailbreakTool.exe`。**分发时必须复制整个 `dist` 文件夹**（含 DLL、PRI、XBF 与子目录），不能只复制 EXE——Windows App SDK 不支持单文件发布。

发布版没有调试场景下拉（`#if DEBUG` 才编译）。开发时用 Debug 构建可切换八种模拟场景（MockEngine 仍保留在代码里作界面开发样例）。

## 引擎从哪来

界面只通过冻结的 C# 接口 `IAgentEngine` 与引擎对话，唯一替换入口是 `Services\ServiceLocator.cs`（现在返回 `MultiTargetEngine`）。引擎是同一仓库里的 `backend\`：

- 无界面也能用：`backend\dist\engine.exe` 是自包含单文件，提供 status / preview / apply / revert / sessions / check / diff / probes 等命令，支持 `--json`。
- 文件读写、注册表/进程探测、asar 解析、多帧 zstd 解码、补丁与备份纪律全在引擎里，**界面不做任何 I/O**。

契约文件的唯一来源是 `Contracts\EngineContracts.cs`，但它由 `backend\AgentToolkit.Core` 链接编译后引用（UI 侧 `Compile Remove` + `ProjectReference`）——**不要再复制成两份**，那会产出两个 `IAgentEngine` 类型身份。

## 构建

```powershell
Set-Location -LiteralPath 'D:\破甲\toolkit'
$dn = 'C:\Program Files\dotnet\dotnet.exe'
& $dn build   AgentToolkit.slnx -c Release
& $dn publish ui\AgentToolkit.Ui.csproj -c Release -r win-x64 -o ui\dist
```

发布的完整验收（构建 + 沙箱自检 + 两个目标只读验收）用仓库根的 `verify.ps1`。

## 文档

| 文件 | 内容 |
|---|---|
| [UI-NOTES.md](UI-NOTES.md) | 文件职责、页面→引擎方法映射、契约缺口、本轮**后端接管记录** |
| [BACKEND-HANDOFF.md](BACKEND-HANDOFF.md) | 当初给后端负责人的接入说明（历史文件，接缝已被接管） |
| `..\docs\` | 两个目标的档案、探针流程、验收记录 |
| `docs\ui\final\` | 最新界面截图（注意：这些文件的扩展名是 `.png`，实际编码是 JPEG） |
