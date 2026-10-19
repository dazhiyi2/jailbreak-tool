# 目标档案：Codex（ChatGPT 桌面应用）

本机实测：**ChatGPT 桌面应用**（MSIX/Store 包 `OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0`），进程名 **`ChatGPT`**。
应用内版本号 `26.1013.40934`（`config.toml` 的 `BROWSER_USE_CODEX_APP_VERSION`）。
CLI 二进制：`C:\Users\Administrator\AppData\Local\OpenAI\Codex\bin\896a0166b774037a\codex.exe`（**不在 PATH**）。

## 一、客户端分类

| 项 | 实测 |
|---|---|
| 类型 | 打包 Electron/原生混合的桌面应用（MSIX）+ 独立 CLI |
| 进程名 | `ChatGPT`（本机实测 10 个进程） |
| 伴生进程 | `codex`、`codex-computer-use-swift`、`codex-windows-sandbox-service` |
| 主目录 | **`CODEX_HOME` = `C:\Users\Administrator\.codex`**（`config.toml` 的 `[mcp_servers.node_repl.env]` 里明确写着这个值） |
| 沙箱 | `config.toml` → `[windows] sandbox = "elevated"` |

## 二、注入通道：**官方配置键 `model_instructions_file`**（这就是 gpt-instruct 的机制）

`CODEX_HOME\config.toml`（本机 2,998 B，**当前没有 `model_instructions_file` 这一项** = 干净出厂态）：

```toml
model = "gpt-6.1-sol"
model_reasoning_effort = "high"
service_tier = "priority"
notify = [ "...codex-computer-use.exe", "turn-ended" ]
[desktop]
followUpQueueMode = "steer"
...
[windows]
sandbox = "elevated"
```

注入 = 两件事：

1. 把提示词文本写成 `CODEX_HOME\<文件名>.md`
2. 在 `config.toml` **顶层**写 `model_instructions_file = "<绝对路径>"`

**这是官方配置键，不打二进制、不劫持网络、不篡改进程。** 本机 `model = "gpt-6.1-sol"` —— 正是 gpt-instruct 的 RC 提示词对准的那条模型线。

### 2.1 上游工具怎么做的（`codex-instruct.py`，读原文得出）

- 每个可选提示词发成 ZIP，部署时解出 `.md` 到 `CODEX_HOME`
- 写 `model_instructions_file`（正则 `^\s*model_instructions_file\s*=\s*(['\"])(.*?)\1\s*(?:#.*)?$`）
- 操作前快照 `config.toml` → `<file>.gpt56-sol-instruct.bak`，状态文件 `.gpt56-sol-instruct-state.json`（`STATE_VERSION = 2`）
- **`reset` 只恢复自己那一个键**、只删自己拥有的提示词文件；**明确不整份覆盖 `config.toml`**（provider 管理器如 CCSwitch 仍是权威）
- 认 9 个旧文件名做迁移（`gpt-5.6-sol-unrestricted-v45.md` 等）
- 支持 `--file` 自定义、`--restore-snapshot`

**本工具的落点与它一致**，但要求：只增删自己那一个键、只动自己那个提示词文件、写前备份、哈希可证回滚、污染备份拒绝还原。

## 三、日志层（全部实测）

### 3.1 `CODEX_HOME` 下的数据文件

| 文件 | 大小 | 用途 |
|---|---|---|
| **`thread_history_1.sqlite`** | 40.8 MB | **会话正文**（下表） |
| **`logs_2.sqlite`** | 28.6 MB | 应用自身日志（`logs` 表 5,238 行） |
| `state_5.sqlite` | 288 KB | 线程索引（`threads` 18 行，带 **`rollout_path`**） |
| `session_index.jsonl` | 1.7 KB | `{id, thread_name, updated_at}` |
| `sessions\YYYY\MM\DD\rollout-*.jsonl`（部分 `.zst`） | 最大 10.3 MB | **原始转录**（`threads.rollout_path` 指向它） |
| `goals_1.sqlite` / `memories_1.sqlite` / `queue_1.sqlite` | 小 | 目标 / 记忆 / 队列 |
| `.codex-global-state.json` | 362 KB | 全局状态 |
| `models_cache.json` | 173.6 KB | 模型目录 |

### 3.2 ⚠️ 读库必须先连 `-wal` 一起拷出来

`logs_2.sqlite` 与 `thread_history_1.sqlite` 在**只读直连时只看得见 `_sqlx_migrations`**——真实表在 WAL 里（客户端在跑、没 checkpoint；`logs_2.sqlite-wal` 2.4 MB）。

**正确做法**：把 `<db>` + `<db>-wal` + `<db>-shm` 三个文件一起拷到临时目录，再打开副本（SQLite 会自己恢复 WAL）。**全程只读，不碰原文件。**

### 3.3 `thread_items` 表（`thread_history_1.sqlite`，3,237 行）

列：`thread_id` / `turn_id` / `item_id` / `rollout_ordinal` / `created_at_ms` / **`item_json`** / `item_type` / `updated_at_ordinal` / `started_at_ms` / `completed_at_ms`

`item_type` 分布（本机实测）：

| type | 条数 | `item_json` 关键字段 |
|---|---:|---|
| `reasoning` | **1,210** | `summary[]`、`content[]` —— **只有 33 条非空**（推理正文基本不落盘） |
| `commandExecution` | 954 | `command`、`cwd`、`status`、`aggregatedOutput`、`exitCode`、`durationMs` |
| **`agentMessage`** | **351** | **`text`**、`phase` ∈ {`commentary` 233, `final_answer` 117, null 1} |
| `fileChange` | 256 | `changes[].path`、`kind`、`diff` |
| `mcpToolCall` | 205 | `server`、`tool`、`arguments`、`result`、`status` |
| **`userMessage`** | **119** | **`content[].text`** |
| `imageView` | 57 | — |
| `subAgentActivity` | 51 | — |
| `webSearch` | 23 | `query`、`results` |
| `contextCompaction` | 7 | — |
| `sleep` | 3 | — |
| `plan` | 1 | — |

同库还有 `thread_turns`（98 行：`status`、`started_at`、`completed_at`、`duration_ms`、`first_user_item_id`、`final_agent_item_id`）与 `thread_history_projection_state`（18 行：`next_rollout_byte_offset`）。

### 3.4 `logs` 表（`logs_2.sqlite`，5,238 行）

列：`id` / `ts` / `ts_nanos` / `level` / `target` / `feedback_log_body` / `module_path` / `file` / `line` / `thread_id` / `process_uuid`

`target` 分布（前几名）：`codex_app_server::outgoing_message` 1758、`codex_app_server::message_processor` 1252、`codex_core::stream_events_utils` 475、`codex_http_client::custom_ca` 269、`feedback_tags` 264、`codex_core::session::world_state` 110、`codex_core::session::turn` 101。

## 四、⚠️ 生效判据：**本地没有系统提示词的记录**

实测搜索结果（`logs.feedback_log_body`，不区分大小写）：

| 特征串 | 命中 |
|---|---:|
| `You are` | **0** |
| `system prompt` | **0** |
| `AGENTS` | **0** |
| `base_instructions` | **0** |
| `model_instructions` | **0** |
| `instruction` | 161 |
| `developer` | 102 |

最大的 `feedback_log_body` 是 13,009 字符，内容是 `codex_core::session::handlers` 里的
`Submission sub=... op: TurnInput { request: TurnInputRequest { input: UserInput { con...`——**是用户回合的输入，不是系统提示词**。

**所以本目标不能用"日志里有没有指令"判生效**（这一点与 opencode 的 `instruction_blob`、Cursor 的 `promptTokenBreakdown` 都不同）。判据只能是两级：

1. **配置级（装上了）**：`config.toml` 顶层有 `model_instructions_file`、值指向本工具的提示词文件、文件存在且 sha256 == 本工具写入的内容。可回读校验，是硬证据。
2. **行为级（生效了）**：注入后**新建**的线程里，`thread_items.agentMessage.text` 是否带该提示词规定的协议特征。gpt-instruct 原文规定：
   - 后续回合必须以 `Current:` 或 `当前：` 开头（原文：`begin with \`Current:\` or \`当前：\``）
   - 绑定名要出现：`OUTPUT_CONTRACT` / `MODIFIED_FILE` / `DIFF_FILE` / `VERIFICATION.txt` / `ROLLBACK.sh`
   - `INJECTED REVISION ACTION` 段规定：提示词优化请求下必须**推导**出精确下一 beta 名（不是复制）

   这三类都是**可从 `agentMessage.text` 直接判读**的——这正是"反硬编码探针"的落地形态。

**注意**：`reasoning` 条目 1,210 条里只有 33 条有正文，所以"读推理原文"这条判据在 Codex 上很弱，不能当主判据。

### 4.1 已排除的两个"可能是提示词通道"的地方

| 疑点 | 实测结论 |
|---|---|
| `logs` 里 `instruction` 161 次 / `developer` 102 次命中 | **是 OpenTelemetry span 标签**（`target='feedback_tags'`），不是指令内容 |
| 最大的 `feedback_log_body`（8,523 字符） | 是**遥测标签转储**（`tags_json={...}` 一大串 feature flag），不是请求体 |

最大的那条日志的 span 头是：

```
run_sampling_request{turn_id=... model=gpt-6-luna cwd=E:\ctf测试}:try_run_sampling_request{...}:
  model="gpt-6-luna" approval_policy=Never sandbox_policy=ReadOnly { network_access: false }
  effort=Some(Medium) auth_mode=Some(Chatgpt) tags_json={...}
```

### 4.2 顺带挖到的可用事实（来自 `tags_json`）

| 键 | 值 | 意义 |
|---|---|---|
| `project_doc_max_bytes` | **32768** | Codex 读项目文档（`AGENTS.md`）的**上限 32 KB** —— 这是本目标潜在的第二条载荷通道 |
| `model_context_window_effective` | 258400 | 有效上下文窗口 |
| `model_reasoning_summary` | `none` | 推理摘要不落盘（解释了为什么 1,210 条 reasoning 只有 33 条有正文） |
| `sandbox_policy` / `approval_policy` | `ReadOnly { network_access: false }` / `Never` | 实测那一轮的沙箱与审批策略 |


## 五、本工具的设计（**只用 gpt-instruct 那一套，不做双方案**）

| 层 | 落点 |
|---|---|
| **系统提示词层** | `CODEX_HOME\config.toml` 顶层 `model_instructions_file` + `CODEX_HOME\<文件名>.md` |
| **日志层（只读）** | `thread_history_1.sqlite` → `thread_items`（`agentMessage` / `userMessage` / `reasoning`）；线程索引走 `state_5.sqlite.threads`；原始转录走 `sessions\**\rollout-*.jsonl` |

**读库纪律**：`<db>` + `-wal` + `-shm` 一起拷到临时目录再读；原文件只读。

**写 `config.toml` 纪律**：
- 只增删自己那一个键（顶层 `model_instructions_file`）
- 写前备份整份 `config.toml` + sha256
- 其它键（`model`、`model_reasoning_effort`、`service_tier`、`notify`、`[mcp_servers.*]`、`[plugins.*]`、`[desktop]`、`[windows]`）**一个字节都不能动**，写完必须重新只读打开校验：行数、其它键指纹不变
- 保留上游工具认得的旧文件名（`gpt-5.6-sol-unrestricted-v45.md` 等 9 个）作为"这是同类工具留下的"判据之一，但**不冒充**：本工具写自己的文件名，revert 时只删自己那个
- TOML 是文本格式 → 用等价的**纯文本行插入/删除**实现，不做 TOML 重序列化（重序列化会改格式、动到别人的键）

## 六、未验证

- **没有在真实客户端上注入过**（本次要求：只做，不测；由操作者自己测）。所以"写 `model_instructions_file` 后客户端真的加载它"未实测。
- `ChatGPT` 应用是否**每次启动重读** `config.toml`，还是需要完全退出（托盘也退）才重读：未实测。注入后必须让操作者确认客户端已重启。
- 注入后**是否需要新开线程**才生效：未实测（旧线程的提示词已经组装过）。
- `instruction`（161 次）与 `developer`（102 次）这两类日志的确切内容未逐条判读。
- `sessions\**\rollout-*.jsonl` 与 `thread_items` 的关系（是否等价、`.zst` 帧格式）未展开。
- 应用是 MSIX 打包，`config.toml` 在 `CODEX_HOME` 而不是包目录 → 升级应用不影响注入，但**升级后是否覆盖 `config.toml`** 未实测。
- `[windows] sandbox = "elevated"` 对注入与生效的影响未测。
