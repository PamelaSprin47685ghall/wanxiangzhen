# 万象阵：工作区事件溯源（`.wanxiangzhen.ndjson`）

> **规格 SSOT**。实现与测试须与本文件一致；与旧「master session 对话历史 + yaml frontmatter 重放」叙事冲突时，以本文件为准。

## 0. 动机

OpenCode 会对 **context 做 compaction**。`session.messages()` 虽可拉存储全量，但对话历史仍是宿主消息模型：膨胀、与 LLM 上下文纠缠、需 frontmatter 锚点与注入队列兜底，且长期 DAG 协调事实不应绑在单一 session 消息流上。

**旧方案（废弃）**：DAG 事件经 `session.prompt` 写入 master session；重启 `client.session.messages()` 扫文本 → `EventCodec.decodeEvents` 折叠；git refs 二次校正合并事实。

**新方案**：项目根 `[workspace]/.wanxiangzhen.ndjson` 为万象阵 durable 语义的**唯一真相**；宿主对话仅作 LLM 交互与可选展示，**不作** DAG 状态机 SSOT。无需 compaction 后补锚点、无需为 SSOT 依赖 `injectQueue` + `session.prompt` 重试链。

## 1. 公理

| 公理 | 含义 |
|------|------|
| 意图不落盘 | 用户/LLM 自然语言、未校验的 `squad_update` 参数、内存中的调度打算 **不** 写入 NDJSON |
| 事件才落盘 | 命令经校验并产生 **不可抵赖事实** 后，追加一行（例：`squad_created`、`tasks_created`、`task_merged`） |
| 当前状态 = 积分 | 内存 `Dag` / `Sessions` = 对 NDJSON（按 `session` 过滤后，保留文件行序）**纯 fold** 的结果 |
| 先写盘后改内存 | append 成功 → 再更新内存 DAG；append 失败 → 等同该事实未发生（调度不得前进） |
| 一行一事件 | NDJSON：每行一个自包含 JSON 对象；**禁止** JSON 数组文件追加 |
| 按 session 分区 | 每行 **必须** 含 `session` 字段（**万象阵 session id**，如 `squad-session-2025-01-01-120000`）；fold 时消费匹配 session 的行；文件全局行序仍单调 |

**git 仍为合并事实的第二真理源**：`task_merged` 的硬事实落在 refs；重放后对 `running`/`submitted` 做 `merge-base --is-ancestor` 校正（与旧 PRD §5.4 一致），**不**用 git 替代事件流。

## 2. 物理文件

```
join(workspaceRoot, ".wanxiangzhen.ndjson")
```

- **追加**：只写文件末尾。
- **损坏行**：恢复时遇无法解析的非空行 → **截断**（该行及之后丢弃），不跳过坏行继续 fold。
- **锁**：旁路 **`.wanxiangzhen.ndjson.lock`**（`open(wx)` 独占创建 → `appendFile` 写 NDJSON → 删 lock）。进程内 `SerialQueue` 与 lock 文件双重串行。
- **启动**：coordinator 激活且需 DAG 投影时，**重放** NDJSON → 填充内存 DAG；**禁止**用「仅读 master session 历史」替代重放作为真相。

## 3. 行格式（契约）

每行 JSON 对象最低字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `v` | int | schema 版本，从 `1` 起 |
| `session` | string | 万象阵 session id（`Dag.SessionId`） |
| `kind` | string | 事件类型（见 §4，与 `SquadEvent` 名称一致） |
| `at` | string | ISO-8601 审计时间；**逻辑 fold 依文件行序，不依赖 `at` 排序** |
| `payload` | object | 由 `kind` 决定；仅存 Kernel 已定义字段，禁止塞宿主 `obj` |

编码纪律：复杂结构（如 `tasks[]`）放在 `payload` 内嵌 JSON 数组/对象，或 `payload.tasks` 等明确键；Shell 负责 JSON，Kernel `foldEvent` 仍消费 `SquadEvent` DU。

## 4. 事件类型（与 `SquadEvent` 对齐）

| `kind` | 何时 append | payload 要点 |
|--------|-------------|--------------|
| `squad_created` | `/squad` 创建新万象阵 session | `requirement` |
| `tasks_created` | `squad_update` 校验通过 | `tasks`: `[{ task_id, title, description, depends_on? }]` |
| `task_started` | Scheduler 启动 task | `task_id`, `worktree_path`, `branch_name` |
| `task_submitted` | slave `submit_to_squad` 进入 ff 前 | `task_id`, `commit_sha` |
| `task_merged` | ff 成功 | `task_id`, `master_sha` |
| `task_done` | done beacon / PID 退出 | `task_id`, `merged` (bool) |
| `task_error` | worktree/git 启动失败等 | `task_id`, `error` |
| `squad_cancelled` | `/squad-kill` | —（可空 payload） |

增 kind 须先改 `Kernel/SquadEvent.fs` + 本表 + 测试。

## 5. 分层职责

```
HTTP / hook / squad_update
    → Kernel: validate → SquadEvent（意图）
    → Shell: withFileLock → append NDJSON → on OK foldEvent into Dag
    → Shell: optional session.prompt（展示文案，非 SSOT）
```

| 层 | 职责 |
|----|------|
| `Kernel/SquadEvent.fs` | `SquadEvent` DU、`foldEvent`/`foldEvents`（已有） |
| `Kernel/EventLog/*`（可选） | 行 payload ↔ DU 的纯映射、按 session 过滤列表 |
| `Shell/SquadEventLogCodec.fs` | 路径、`SquadEvent` ↔ JSON 行 |
| `Shell/SquadEventLogFiles.fs` | 锁、append、读全文件、损坏截断 |
| `Shell/SquadEventLogRuntime.fs` | coordinator 接线：`replayFromEventLog`、`appendSquadEvent` |
| `Shell/EventCodec.fs` | **仅**可选 LLM 展示用 frontmatter；**不得**作为重放 SSOT |

## 6. 删除或废弃的行为

> 下列「旧行为」**已从代码库移除**，仅作迁移对照；实现与文档均以「新行为」为准。

| 旧行为（已删除） | 新行为 |
|--------|--------|
| `replayFromHistory` ← `ReadAllTexts` + `decodeEvents` | `replayFromEventLog` ← 读 `.wanxiangzhen.ndjson` + `foldEvents` |
| `injectEvent` = SSOT 写入 `session.prompt` | `appendSquadEvent` = SSOT；prompt 可选且失败不丢事实 |
| master session 历史 = SSOT（PRD §2.2） | `.wanxiangzhen.ndjson` = SSOT |
| §12.4「session.messages 全量即可」作 DAG 依据 | 仅 NDJSON；session 历史不参与 fold |
| 附录 D frontmatter 重放投影 | 附录 D 降级为「可选展示编码」；重放规则见 `foldEvent` |
| `.squad/state.json` MVP 兜底 | MVP 不需要；NDJSON 已足够 |
| compaction 补锚点 / 为 SSOT 担心 transform 切片 | **删除**；compaction 只影响 LLM 窗口 |

**保留**：`masterSessionId` 捕获（向 coordinator LLM `prompt` 注入进度/告警）；`SerialQueue` 用于 git；`InjectQueue` 可缩为仅 LLM 通知队列或移除。

## 7. 实施顺序（任务强制）

1. **文档**：`README.md`、`PRD/PRD.md`、`PRD/DEV_TALK.md`、`PRD/EventSourcing.md`（本文件）、`AGENTS.md`。
2. **测试**：`tests/EventLog*` — 行 round-trip、`foldEvents` 与现 `EventReplayTests` 等价、损坏截断、replay + git reconcile（改 stub 为事件列表）。
3. **开发**：Shell EventLog → `replayFromEventLog` / `appendSquadEvent` 接线；调用点 **先 append 后改 Dag**；删除以 session 历史为 SSOT 的路径。

## 8. 验收标准

- 重启 coordinator 后，**仅**依赖 `.wanxiangzhen.ndjson` 可恢复当前万象阵 session 的 DAG（含多 session 归档到 `Sessions` 的 `squad_created` 边界语义）。
- compaction 后无 anchor 注入，DAG 仍正确。
- `npm run build-and-test` 全绿。
- Kernel 无 `Dyn`；fold 在 Kernel；append/fs 仅在 Shell。

## 9. 与 PRD 对照

- 替代 PRD §2.2「历史是事实」中 **对话历史** 段落 → 本文件 §1。
- 替代 PRD §5.4 重放伪代码 → `replayFromEventLog`。
- 替代 PRD §5.11「注入队列 = SSOT 投影」→ §5 本文件；注入队列仅通知。
- 替代 PRD §12.4、§13 `.squad/state.json` 行 → §6。

---

*版本：与 with-review 任务「万象阵事件溯源」同步。*