# 目标档案：Cursor

本机实测：**Cursor 3.25.6**，安装在 `E:\APP\cursor\`（用户级安装，注册表项名 `Cursor (User)`）。

## 一、客户端分类

| 项 | 实测 |
|---|---|
| 类型 | VS Code 分支 + Electron（`TargetKind.PackagedElectron`） |
| 主进程 | `resources\app\out\main.js`（2.73 MB） |
| 工作台 | `resources\app\out\vs\workbench\workbench.desktop.main.js`（含 `workbench.glass.main.js`） |
| 内置扩展 | 117 个，其中 21 个 `cursor-*` |
| **关键：没有 asar** | `resources\app\out\` 是**目录**，JS 明文可直接读改（比 ZCode 的 bundle 好处理） |
| 进程名 | `Cursor` |

`cursor-*` 内置扩展（体积）：

```
cursor-agent-host            47.87 MB     ← 规则渲染 + agent 主逻辑（含 daemon.cjs）
cursor-retrieval             44.35 MB
cursor-agent-exec            30.15 MB     ← 规则渲染（同源副本）
cursor-local-agent-runtime   11.67 MB     ← 规则渲染（同源副本）
cursor-resolver               5.35 MB
cursor-always-local           3.88 MB
cursor-computer-use           3.06 MB
cursor-mcp                    1.21 MB
```

**注意**：规则渲染逻辑在 **4 个文件里各有一份同源副本**（`cursor-agent-host\dist\main.js`、`cursor-agent-host\dist\agent-host-daemon\dist\bin\daemon.cjs`、`cursor-agent-exec\dist\main.js`、`cursor-local-agent-runtime\dist\main.js`）——**打补丁必须四处全打**，否则哪条路径被走到就漏哪条。

## 二、提示词层：**`system_prompt` 在服务端，但规则框架句在本地**

### 2.1 提示词的分档（权威证据）

`cursorDiskKV` → `composerData:<id>` 里有 **`promptTokenBreakdown`**，把进模型的内容按类别拆解。本机实测一份真实会话：

| id | label | 估算 tokens | 字符数 |
|---|---|---|---|
| `system_prompt` | System prompt | 1057 | **4,099** |
| `tools` | Tool definitions | 3435 | 13,324 |
| **`rules`** | **Rules** | **2705** | **10,497** |
| `skills` | Skills | 1815 | 7,041 |
| `mcp` | MCP & dynamic tools | 2692 | 10,442 |
| `subagents` | Subagent definitions | 1103 | 4,276 |
| `summarized_conversation` | Summarized conversation | 0 | 0 |
| `conversation` | Conversation | 5963 | 23,312 |

**`rules` 是独立的一档**，和 `system_prompt` 并列。这既是注入通道，也是**定量生效证据**——注入后该档的 `characterCount` 会精确增长我们写入的字符数。

### 2.2 `system_prompt` 是服务端下发的（本地无提示词字符串）

在 `out\main.js`、`out\vs\workbench\*`、`cursor-always-local` 里搜 `You are (a|an|the) …` 与 `systemPrompt` / `SYSTEM_PROMPT`：**0 命中**。所以那 4,099 字符的系统提示词由 Cursor 后端下发，本地改不到（与 Antigravity 同型）。

### 2.3 规则框架句在本地，且**明写"如果合适的话"**（可打补丁）

规则的渲染代码（4 个文件同源），原文：

```js
function UserRulesSection({userRules, composer2CustomUserRules=[]}) {
  return section({
    title: "user_rules",
    description: "These are rules set by the user that you should follow if appropriate.",
    children: [
      composer2CustomUserRules.map((rule,i) => x({tag:"user_rule", children:rule}, i)),
      userRules.map(rule => x({tag:"user_rule", children:rule.content}))
    ]
  })
}

function RulesSection({globalRules, agentRequestableRules, userRules, readToolName, composer2CustomUserRules, alwaysAppliedRulesDescription}) {
  return section({ title: "rules", children: [
    p({children: "The rules section has a number of possible rules/memories/context that you should consider. In each subsection, we provide instructions about what information the subsection contains and how you should consider/follow the contents of the subsection."}),
    br(),
    globalRules.length>0 && GlobalRulesSection(...),
    agentRequestableRules.length>0 && AgentRequestableRulesSection(...),
    hasUserRules && UserRulesSection(...),
    br()
  ]})
}
```

**`if appropriate`（"如果合适的话"）给了模型裁量权**——等于在框架句里明写"你可以判断合不合适"，也就是可以拒绝。这与 opencode 那轮的教训同源：**框架句决定注入内容被当硬规定还是当建议**（见 `docs/opencode.md` 第八节与 README「注入文本的两条硬规则」）。

### 2.4 补丁锚点（唯一性已实测）

| id | 锚点原文 | 长度 | 命中 |
|---|---|---|---|
| **A1** | `These are rules set by the user that you should follow if appropriate.` | 70 字符 / 70 字节 | **4 个文件各 1 次** |
| **A2** | `The rules section has a number of possible rules/memories/context that you should consider.` | 91 字符 / 91 字节 | **4 个文件各 1 次** |

命中位置（字符偏移）：

| 文件 | 大小 | A1 | A2 |
|---|---|---|---|
| `extensions\cursor-agent-exec\dist\main.js` | 9.0 MB | 3262397 | 3262827 |
| `extensions\cursor-agent-host\dist\agent-host-daemon\dist\bin\daemon.cjs` | 17.7 MB | 12662828 | 12663350 |
| `extensions\cursor-agent-host\dist\main.js` | 10.2 MB | 3460205 | 3460635 |
| `extensions\cursor-local-agent-runtime\dist\main.js` | 6.0 MB | 3553503 | 3553934 |

全部 ASCII → 等长替换可行，不必动文件长度。

## 三、载荷层：规则从哪来

`workbench.desktop.main.js` @31089650 的 `getExplicitContext` 原文：

```js
async getExplicitContext(e) {
  const [t, n] = await Promise.all([
    this._cursorRulesService.getExplicitRules(),
    this._mcpService.getInstructionsForComposer(e)
  ]);
  return new sF({
    context: this.getPersonalContext(),            // ← 个人规则
    repoContext: await this.getCursorRulesFile(),  // ← 仓库级规则文件
    rules: t.map(i => {
      const r = this._cursorRulesService.getRuleUriByFilename(i.filename);
      return new sDe({
        name: i.filename.replace(fbm, ""),
        description: i.description,
        body: i.alwaysApply ? i.body : void 0,     // ← 只有 alwaysApply 才带正文
        alwaysApply: i.alwaysApply,
        fullPath: s,
        environments: i.environments ?? [],
        disabledEnvironments: i.disabledEnvironments ?? [],
        plugin: i.plugin, marketplace: i.marketplace
      });
    }),
    mcpInstructions: n
  })
}
```

### 3.1 个人规则（全局，DB 通道）

@31085813 原文：

```js
updatePersonalContext(e){ this._storageService.store("aicontext.personalContext", e, -1, 1) }
getPersonalContext(){ const e = this._storageService.get("aicontext.personalContext", -1, ""); return e.trim()==="Always respond in" ? "" : e }
```

- 存储位置：`%APPDATA%\Cursor\User\globalStorage\state.vscdb` → `ItemTable` 键 **`aicontext.personalContext`**
- **本机现状：该键不存在**（`ItemTable` 126 个键里没有，`persistentStorage.applicationUser` 的 57 个字段里也没有）→ 通道是干净的处女地
- 另有迁移路径 `maybeAddOldUserRules()`：把 personalContext 迁进知识库（`knowledgeBaseService.items`）当 user rules

### 3.2 项目规则（工作区，文件通道）

| 落点 | 语义 |
|---|---|
| `.cursor/rules/*.mdc` | 现代格式，**YAML frontmatter**，只有 `alwaysApply: true` 的才把正文进提示词 |
| `.cursorrules` | 旧格式单文件，由 `getCursorRulesFile()` 读取（`repoContext`） |
| `AGENTS.md` / `CLAUDE.md` / `CLAUDE.local.md` | 被 `computePathsFromFullPath` 当规则文件识别 |

`.mdc` 的 frontmatter 由本地代码写（`updateRule`）：

```js
async updateRule(e, t, n) {
  const i = this.createUriFromFullPath(e);
  if (!await this.fileService.exists(i)) throw new Error(`Rule file not found: ${e}`);
  const r = n?.alwaysApply ?? !0, s = [];
  n?.description !== void 0 && s.push(`description: ${n.description}`);
  n?.globs && s.push(`globs: ${n.globs}`);
  s.push(`alwaysApply: ${r ? "true" : "false"}`);
  const o = `---\n${s.join("\n")}\n---\n...`;
  await this.fileService.writeFile(i, Ai.fromString(o));
  await this.refresh()
}
```

字段：`description` / `globs` / `alwaysApply`。**`alwaysApply: true` 是正文进提示词的开关。**

### 3.3 还有一条本地注入路径

`injectLocalModeNonFileRules(e)`（@19596838）：local 模式下把知识库条目 + 团队规则注入 `e.rules`：

```js
injectLocalModeNonFileRules(e) {
  if (!Dc.localMode) return;
  const t = D3_({ userRules: this.knowledgeBaseService.items, teamRules: this.cursorRulesService.getTeamRules({includeDisabled:!0}) });
  t.length > 0 && (e.rules = ...)
}
```

## 四、日志层：结构与落点已坐实

`%APPDATA%\Cursor\User\globalStorage\state.vscdb`（SQLite，本机 1.71 MB）：

| 表 | 行数 | 用途 |
|---|---|---|
| `ItemTable` | 126 | 键值存储（含 `aicontext.personalContext`、`cursorai/serverConfig`、`persistentStorage.applicationUser`） |
| `cursorDiskKV` | 39 | **会话正文**：`composerData:<id>` / `bubbleId:<composerId>:<bubbleId>` / `agentKv:blob:*` / `checkpointId:*` |
| `composerHeaders` | 3 | 会话索引：`composerId` / `workspaceId` / `createdAt` / `isArchived` / `isSubagent` |

`composerData:<id>` 的关键字段（实测一份真实会话）：

```
composerId, name, status, unifiedMode("agent"), forceMode("edit"),
modelConfig: { modelName:"grok-4.6", maxMode:false, selectedModels:[{modelId, parameters:[{id:"effort",value:"medium"}]}] }
promptTokenBreakdown: { totalUsedTokens:18770, maxTokens:256000, categories:[…] }   ← 生效证据在这里
contextTokensUsed: 18770, contextTokenLimit: 256000, contextUsagePercent: 7.33
fullConversationHeadersOnly: [{bubbleId, type, grouping:{…}}, …]
context: { composers:[], selectedCommits:[], … }
```

## 五、注入设计（三层）

| 层 | 落点 | 说明 |
|---|---|---|
| **补丁层** | 4 个 JS 里的 A1/A2 框架句 | 等长替换，把"如果合适的话"改成硬性要求；4 处全打 |
| **个人规则层** | `state.vscdb` → `ItemTable` 键 `aicontext.personalContext` | 全局，对所有工作区生效 |
| **项目规则层** | `<workspace>\.cursor\rules\*.mdc`（`alwaysApply: true`） | 工作区级；另可选 `.cursorrules` / `AGENTS.md` |

## 六、生效证据的判据

**主判据（定量）**：新会话的 `composerData.promptTokenBreakdown.categories` 里 `id=="rules"` 那一档的 `characterCount` —— 注入后应精确增长「我们写入的字符数」。

**次判据**：`cursorDiskKV` 里 `bubbleId:*` 的助手文本，看它是否引用我们写入的规则原文。

**注意**：`system_prompt` 那一档不会变（服务端下发），所以**不能拿它判生效**。

## 七、未验证

- **没有端到端跑过真实客户端**：本档案全部来自静态阅读 + 只读打开 DB。真实注入与真实会话未做。
- `system_prompt` 的 4,099 字符内容未取得（服务端下发，本地无明文）。
- `aicontext.personalContext` 写入后，是否**立即**进 `rules` 档、还是要重启客户端：未实测（`getPersonalContext` 是读时取，但 UI 侧可能有缓存）。
- 知识库迁移路径（`maybeAddOldUserRules` → `knowledgeBaseService.items`）的存储落点未定位。
- `.cursor/rules/*.mdc` 的 `globs` 触发（非 `alwaysApply`）在 agent 模式下的实际行为未实测。
- 团队规则（`getTeamRules`）与企业策略下发的影响未测。
- `rules` 档本机实测 10,497 字符，但当时工作区是 `empty-window`、无个人规则——**这 10.5 KB 来自哪里未定位**（疑似服务端下发的 global rules + 内置 skills 之外的内容）。定位它才能算准"我们注入的部分占多少"。
