# 目标档案：opencode

面向 opencode 桌面版 **2.0.32**（`ai.opencode.desktop`，2026-10-17 安装）。以下为本机实测。

## 一、客户端分类

**打包的 Electron 桌面版 + 一个 Bun 编译的单文件 CLI。**

```
%APPDATA%\ai.opencode.desktop\cli\2.0.32-x64\opencode-cli.exe   203.8 MB，sha256 D0FFF437AC139CFB…
~/.config/opencode/          配置根（<config>）
~/.local/share/opencode/     数据根（opencode.db = SQLite + WAL）
```

二进制是 PE（`4d 5a`），但 JS 是**明文内嵌、未压缩**——`"You are opencode"` 在偏移 213,141,290 处一次命中，周围就是完整 system prompt。所以字符串级替换在技术上可行。

## 二、系统提示词层：**里面没有安全条款**

实测扫过 20 个策略类特征串，结论如下：

| 特征串 | 命中 | 落点 |
|---|---|---|
| `authorized security` / `clear authorization` / `dual-use` / `penetration test` / `security research` | **0** | — |
| `refuse to` (2) / `malicious` (2) | 是 | **npm 文档**（`engine-strict`、`--force` 说明） |
| `safety` (32) | 是 | Node 头文件注释、**Gemini SDK 类型定义**（`safetySettings` / `blockReason`） |
| `illegal` (22) / `policy` (140) | 是 | 压缩数据字典里的英文词、TLS cipher policy、Trusted Types 报错 |
| `guardrail` (2) | 是 | 无关字符串 |

也就是说：**opencode 客户端里没有"授权语境"这类可删的条款**，和 DSH 同型。它的 system prompt 由这几段构成（实测读到的原文）：

- 身份段：`You are opencode, an interactive CLI tool that helps users with software engineering tasks.`
- `# Tone and style`：极简输出、不要前言后语、CLI 里 4 行以内
- `# Code Style`：注释不得用来写长链思考（"Long thinking texts must be generated as private reasoning"）
- `# Delegation`：除非用户或 AGENTS.md/skill 明确要求，不要起子代理
- `# Destructive actions`：不要回滚你没做的改动、不要跑破坏性命令
- git 规则：除非明确要求，绝不提交

注意其中一条是**风格层与边界层混在一起**的：*"If you cannot or will not help the user with something, please do not say why or what it could lead to, since this comes across as preachy…"* —— 它管的是"拒绝时别说教"，不是"不许拒绝"。

**结论：opencode 的策略约束不在客户端。** 补丁层没有"删条款"这一路，可用杠杆只剩载荷层与权限配置。

## 三、载荷层：`<config>/AGENTS.md`

**源码级证据**（从二进制里读出的内嵌实现，插件名 `ConfigInstructionPlugin`）：

```js
R = yield* r.resolve(H5(l.config, "AGENTS.md"))          // 全局：<config>/AGENTS.md
te = [ ...(a.global ? [R] : []),
       ...(Q ? X5(T, C).map(Ne => H5(Ne, "AGENTS.md"))    // 项目级：从项目目录向上搜到项目根
              .filter(Ne => a.global || Ne !== R) : []) ]
```

即：

| 层 | 路径 |
|---|---|
| 全局（主注入点） | `~/.config/opencode/AGENTS.md` |
| 项目级 | 从工作目录**向上**搜到项目根，逐级 `AGENTS.md`（用户级与项目级同时加载，同路径不重复） |
| 配置 | `~/.config/opencode/opencode.json(c)`；项目级 `opencode.json(c)` 或 `.opencode/opencode.json(c)` |
| CLI 偏好 | `~/.config/opencode/cli.json`（与项目配置分离） |

本机现状：配置根下**只有 `service.json`**（64 B，一个 password 字段），`AGENTS.md` / `opencode.json` / `cli.json` 都不存在。也就是说**载荷层是干净的处女地**——写进去就是从零建立。

另有一个官方命令 `init`（描述 `guided AGENTS.md setup`）会引导生成 AGENTS.md，可以做对照基线。

## 四、日志层：结构与落点都已坐实

`~/.local/share/opencode/opencode.db`（6.7 MB SQLite + 6.8 MB WAL，50 条 migration 已跑完）。相关表与**实测行数**（跑过一次真实会话之后）：

| 表 | 列（节选） | 行数 | 作用 |
|---|---|---|---|
| `session_v2` | `id, title, model, agent, permission, tokens_reasoning, tokens_output, cost, directory, time_*` | 1 | 会话索引；`tokens_reasoning` 说明推理 token 单独统计 |
| `session_message` | `id, session_id, type, seq, data` | 5 | `type ∈ {user, assistant, idle}`，`data` 是 JSON |
| `instruction_blob` | `hash, value` | 4 | **真正进了模型上下文的常驻指令载荷**（按 hash 索引） |
| `instruction_state` | `session_id, epoch_start, through_seq, initial_values, current_values` | 1 | `key → blob hash` 的映射：这次会话模型被喂了什么 |

### 推理原文的落点（实测确认）

`session_message.data.content[]` 里 `type == "reasoning"` 的块，其 `text` 就是推理原文；`state.reasoningField == "reasoning_content"`。真实样例（`ses_eb8ef608fffeDDKiwgOntuAsAr`）：

> *"Let me run a command to list the 5 largest files in the current directory. Platform is win32, PowerShell. Let me use Get-ChildItem. Note: working directory is D:\破甲\.toolchain\oc-probe. Let me list largest 5 files recursively? …"*

`type == "text"` 的块是给用户看的回复。`tokens: {input, output, reasoning, cache}` 也在同一条 `data` 里。

### 生效证据的落点（这条比前两个目标更硬）

`instruction_blob` + `instruction_state` 是 opencode 独有的好东西：**它记录了"模型实际收到了哪些常驻指令"**，而不是只记聊天。实测的 4 条 `core/*`：

| key | blob 内容（节选） |
|---|---|
| `core/codemode` | 工具命名空间清单（`tools.opencode.list_…`） |
| `core/skill-guidance` | 技能清单 |
| `core/date` | `"Sat Oct 17 2026"` |
| `core/environment` | `<env> Working directory: … Workspace root folder: … Platform: win32 …` |

所以本工具对 opencode 的"命中数自检"可以直接查 **blob 里有没有我们注入的文本**——这比在聊天记录里数标记更接近"模型真的被告知了什么"。反过来说：**文件在盘上但 blob 里没有 = 没生效**，这正是状态页要区分的两件事。

## 五、阻塞项：已解决（记录踩坑过程）

最初的失败是加了 `--standalone`：

```
opencode run --standalone --auto "列出当前目录下最大的 5 个文件"
Error: Transport: Unable to connect. Is the computer able to access the url?
```

`--standalone` 会起一个**私有服务**，那个服务连不上模型端点。而本机本来就有后台服务在跑：

```powershell
& "$env:APPDATA\ai.opencode.desktop\cli\2.0.32-x64\opencode-cli.exe" service status   # → http://127.0.0.1:45278
& ... auth list                                                                        # → OpenCode Console / Default / stored（凭据是有的）
& ... models                                                                           # → opencode-go/* 一长串可用模型
```

去掉 `--standalone`，走已跑的后台服务即可：

```powershell
Set-Location <工作目录>
& "$env:APPDATA\ai.opencode.desktop\cli\2.0.32-x64\opencode-cli.exe" run --auto --model "opencode-go/deepseek-v4.1-flash" "<请求>"
```

实测会话已落库（`ses_eb8ef608fffeDDKiwgOntuAsAr`）。注意 `run` 的请求是**位置参数**，没有 `--prompt` 这个 flag。

**教训**：判定"客户端跑不通"之前，先分清是**客户端本身**跑不通，还是我给它加的**启动方式**跑不通。

## 六、可以现在就确定的部分

- **定位**：安装根 `%APPDATA%\ai.opencode.desktop`（CLI 在 `cli\<版本>\`，版本目录会累积，取最新）、配置根 `~/.config/opencode`、数据根 `~/.local/share/opencode`；进程名 `opencode-cli`。
- **注入通道一（官方配置）**：`opencode.json` 的 `instructions` 键 —— *"Additional paths or URLs supplying ambient instructions"*。
- **注入通道二（载荷）**：`~/.config/opencode/AGENTS.md`（路径来自内嵌源码，非猜测）。
- **权限层**：CLI 有 `--auto`（"Auto-approve permissions that are not explicitly denied"）；agent 配置里有逐工具的 `permission`（`allow`/`deny`）。**这是 opencode 真正的本地杠杆**——比改提示词更接近"改判定分支"。
- **不需要做的**：不必去二进制里删安全条款——那里没有。

## 七、真实端到端（已实测通过）

适配器实现完成后，在**真实客户端**上做了一次完整的 apply → 开新会话 → 判读 → revert。全程有备份，做完已还原回出厂态。

| 步 | 结果 |
|---|---|
| `apply` | `opencode.json` 建出（186 B，`instructions` 含本工具路径，原有键一个不少，写完重新解析校验通过）；`AGENTS.md` 3.5 KB（sha `D9D294B5…`，与已验证载荷一致） |
| 开新会话 | `opencode run --auto --model opencode-go/deepseek-v4.1-flash` → 新会话 `ses_eb8d3c7daffe…` |
| **命中数自检** | `instruction_blob` **从 4 条涨到 6 条**（多出来的两条就是我们的指令文件与 AGENTS.md），标记命中 **7 次**；判定"两层都进了模型上下文" |
| `revert` | 与基线**逐字节一致**：`service.json` 64 B / `7513CA6F63F0DD84` 不变，另两个文件已删除，两层回到 `TargetAbsent` |

**不需要重启客户端**：opencode 的 `run` 走的是常驻后台服务，但只要**开新会话**就会重新装配常驻指令。已把这条写进 `VerificationHint`，CLI 的通用文案（"需要完全退出并重启客户端"）在拿到目标给的提示时不再覆盖它。

### 一个反直觉的实测细节

`FirstParty` 标记（`You are opencode, an interactive CLI tool`）在 `instruction_blob` 里命中 **0** —— 也就是说 **opencode 的基础 system prompt 不走 blob 通道**，blob 只记录"常驻指令"（AGENTS.md、instructions、环境、技能清单等）。所以：

- 判"我们注入的文本进没进上下文" → 看 blob，**这是权威视图**；
- 判"客户端自己的基础提示词是什么" → blob 里看不到，得去 203.8 MB 的二进制里读。

## 八、未验证

- `instructions` 与项目级 AGENTS.md 同时存在时的**优先级与合并顺序**（本机只验证了全局通道）。
- 桌面版（GUI）与 CLI 是否读同一份 `<config>/AGENTS.md`（端到端是用 CLI 验的；GUI 未测）。
- `instruction_blob.value` 里**中文的非 ASCII 转义风格**：真实库里我们的文本已命中 7 次，说明当前读取路径（含 `\uXXXX` 兼容还原）对中文有效，但没构造过转义与非转义两种写法的对照。
- `SQLITE_BUSY` 锁死场景无法稳定构造，只做了代码层审查（并发只读已实测可用）。
- 探针 A–F 尚未在 opencode 上跑（其余两个目标已跑完）；要跑得先把注入留在盘上并开新会话。
