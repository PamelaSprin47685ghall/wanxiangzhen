# PRD: 万象阵 — Multi-Agent Opencode Coordinator

> 本文档为保姆级实现规格。所有 opencode/万象术 API 引用均经源码核实（见 DEV_TALK 轮次 5），出处以 `packages/...` 或 `src/...` 标注。凡与原始设想冲突处，已按真实 API + 最佳实践修正，并在正文标注「修正」。

## 0. 一句话定义

万象阵 是一个 opencode 插件。正常启动的 opencode 进程（coordinator）加载插件后自起一个本地 HTTP server，把用户需求经自身 LLM 拆解为 DAG 任务图；对每个就绪任务从最新 master 创建 git worktree、用终端模拟器启动一个独立 slave opencode 进程在隔离 worktree 中开发；slave 完成后经工具调用通知 coordinator，coordinator 以 ff-only 协议把分支线性合并回 master，永不产生 merge commit、永不解决冲突（冲突由 slave rebase 自行消化）。

---

## 1. 背景与动机

### 1.1 问题

单进程 opencode 串行处理任务：一个任务的编译/测试/审查未完成前，下一个无法开始。人工拆 worktree、管分支、排合并时序全靠人脑，极易出错。

### 1.2 解决方案

DAG 任务图 + git worktree 隔离 + ff-only 线性合并：

- 无依赖任务并行执行（各自 worktree、各自 opencode 进程）
- 有依赖任务串行等待（前驱 merged 进 master 后，后继才从最新 master fork —— lazy worktree creation）
- 线性历史（master 只允许 fast-forward，永不产生 merge commit）
- 崩溃容忍（slave 崩溃视为 task 形式完成，DAG 继续推进，不阻塞后续）

### 1.3 与 万象术 的关系

万象阵 与 万象术 是两个独立插件，必须同时安装（安装顺序：先 万象术，后 万象阵）：

- 万象术 提供 `/loop`（With-Review Mode）：slave 在 worktree 中完成开发后走 review。万象阵 不自实现 review，依赖 万象术 的 `/loop`。
- 两插件互不 import、互不感知，仅在 prompt 层 / slash-command 层协同（核实 5.3）。万象阵 把 slave 初始 prompt 包成 万象术 的 `task:` frontmatter 格式触发 With-Review，或经 `session.command` 程序化触发 `/loop`。
- **无降级路径**：未安装 万象术 时不应运行 万象阵。安装顺序为先 万象术 后 万象阵，不存在"万象术不存在也能半残运行"的主路径。

---

## 2. 第一性原理（新增）

新增本节把"为什么这么设计"钉死，后续所有实现细节都从这些公理推出。

### 2.1 稳定资产是协调规则，不是宿主进程

opencode 版本会变、TUI 会变、session 对象会漂移。真正稳定的是：

- DAG = 节点 + 依赖边 + 拓扑就绪判定
- task 生命周期 = 有限状态机
- ff 合并 = 串行化的原子前进
- slave 隔离 = 一进程一 worktree 一分支

把稳定规则抽到纯 Kernel，opencode/git/进程/HTTP 压到 Shell（核实 5.1）。判断标准：去掉 Node/opencode 对象后仍成立就进 Kernel。

### 2.2 事件流是事实，内存 DAG 是投影

DAG 当前状态不是进程内存能可靠承诺的——coordinator 会重启、hook 会打断。**SSOT** 是项目根 `.wanxiangzhen.ndjson`（见 `PRD/EventSourcing.md`）：意图不落盘，已发生事实 append；内存 DAG 是对 NDJSON 按 `session` fold 的投影，启动时重放。宿主 master session 对话 **不作** DAG 真相。

**git 是合并事实的第二真理源**：`task_merged` 落在 git refs；重放后可 `git merge-base --is-ancestor` 校正 `running`/`submitted`。追加式 NDJSON 保留从 A→B 的证据链。

### 2.3 副作用压到边界

opencode session API、git、子进程、HTTP server、文件系统、symlink 是现实接口，不是协调规则。全压到 Shell。Kernel 写成纯函数：给定 DAG + 命令 → 新 DAG + 事件列表 + 副作用意图（不直接执行副作用）。

### 2.4 命令可拒，事件不可驳

- 命令（用户/LLM 意图）：`squad_update` 创建任务、slave `submit_to_squad`。经校验（依赖存在性、ff 可行性、列转移合法性）后可拒绝返回错误。
- 事件（已发生事实）：`tasks_created` / `task_started` / `task_merged` / `task_done`。重放历史只能忠实应用，不能因今天规则升级否定昨天写入。

### 2.5 并发的本质是共享可变状态

JS/Node 无线程级并发，但有大量异步交错（多 slave 同时 submit、scheduler 被多源触发、后台事件注入）。策略不是到处加锁：

- 所有 master git 操作经单一 `SerialQueue` 串行化（`src/Shell/SerialQueue.fs`，同构 万象术）。
- NDJSON append 经文件锁 + 进程内队列串行化；可选 `session.prompt` 仅通知 LLM，非 SSOT。
- `Scheduler.tick()` 用 re-entrance guard 防重叠调用导致 DAG 快照不一致。

### 2.6 类型消灭不可能态

`TaskStatus` 用有限联合类型，状态转移用穷举匹配，新增状态编译器红线标红。可预见失败（ff 拒绝、依赖未满足）用返回类型分支，不伪装异常。

### 2.7 测试必须时间无关

测试在并发、重放、异步场景下的稳定性必须是 100% 确定性的。禁止依赖时钟（如 `Date.now`、睡眠特定毫秒数等）来等待状态收敛。所有等待、轮询和轮询检测必须基于确定的事件流、序列排队或断言收敛（如使用 `spinUntil` 判定状态变化，通过 `SerialQueue` 串行化主 git 操作）。测试数据和时空基准必须完全可重现，不能由于运行机器的 CPU 负载或执行速度产生随机失败（flaky tests）。

---

## 3. 核心概念定义

### 3.1 角色

| 角色 | 定义 |
|------|------|
| **Coordinator** | 用户直接交互的 opencode 进程。加载 万象阵 插件后在随机端口启动本地 HTTP server。运行时协调者与 NDJSON **唯一写入入口**；SSOT = `.wanxiangzhen.ndjson`；git refs 合并事实校正。负责 DAG 拆解、调度、ff 合并、worktree/slave 生命周期。 |
| **Slave** | coordinator spawn 的独立 opencode 进程（`opencode tui --prompt`），在隔离 worktree 工作。状态查询与提交都经 HTTP 短连接发给 coordinator。不能 spawn 子 slave。 |
| **masterBranch** | 集成分支。**不硬编码 `master`**（修正，核实 5.9）：万象阵 启动时 `git rev-parse --abbrev-ref HEAD` 取主仓库当前分支为默认值，可被 AGENTS.md frontmatter `squad.masterBranch` 覆盖。只有 coordinator 能 ff 推进它。 |

### 3.2 万象阵 Session

一次 `/squad` 命令创建的任务执行上下文，含一个 DAG。MVP 单 Session；多 Session 并行属第三阶段。

### 3.3 Task（DAG 节点）

```
Task {
  id: string                // "squad-" + 4 hex，如 "squad-a1b2"
  title: string             // 简短标题
  description: string       // 传给 slave 的完整任务描述
  dependsOn: string[]       // 依赖的 task id 列表（空 = 无依赖）
  status: TaskStatus
  worktreePath: string?     // pending 时为空
  branchName: string?       // pending 时为空（= task.id，碰撞时追加后缀，§5.8）
  slavePid: number?         // slave opencode 进程 PID（running 时有值，核实 5.2）
  lastHeartbeatAt: string?  // 最近一次 PID 探测/beacon 时间（ISO）
  mergedSha: string?        // merged 时记录的 master sha
  createdAt: string         // ISO
  updatedAt: string         // ISO
}
```

### 3.4 TaskStatus 状态机（扩展，新增完整转移表）

```
TaskStatus =
  | "pending"      // 已创建，等待依赖全部 merged
  | "running"      // worktree 已建，slave 已启动，PID 存活
  | "submitted"    // slave 调用 submit，coordinator 正在 SerialQueue 内检查 ff
  | "merged"       // 已 ff 合并进 masterBranch（终态之一，触发清理）
  | "done"         // slave 进程已退出（无论是否 merged，终态之一，触发清理）
  | "cancelled"    // /squad-kill 触发（终态，保留现场）
```

合法转移表（VALID_TRANSITIONS，类型驱动，非法转移编译/运行期拒绝）：

| from | 允许的 to | 触发 |
|------|-----------|------|
| pending | running | 依赖全 merged + 并发未满 → Scheduler 启动 |
| pending | cancelled | /squad-kill |
| running | submitted | slave `submit_to_squad` |
| running | done | slave PID 消失 / done beacon（崩溃或自然退出） |
| running | cancelled | /squad-kill |
| submitted | merged | ff 成功 |
| submitted | running | ff 非合并结果（rebase_needed / stale_commit / coordinator_not_ready）→ status 回 running，slave 重试 |
| submitted | done | slave 在 ff 检查期间 PID 消失 |
| submitted | cancelled | /squad-kill |
| merged | — | 终态（清理 worktree+branch） |
| done | — | 终态（清理 worktree+branch） |
| cancelled | — | 终态（保留 worktree+branch 供调试） |

关键不变式：
- `submitted → merged` 与 `submitted → running` 互斥，由 SerialQueue 内 ff 检查的单一结果决定。
- `merged`/`done` 都触发"清理 worktree + 删分支"；`cancelled` 都不清理（核实：决策 3.2/3.3）。
- `done` 不管内容（决策 2.3）：slave 退出即 done，哪怕没产出。DAG 形式上总会跑完。

### 3.5 DAG

```
DAG {
  sessionId: string
  tasks: Map<string, Task>  // taskId → Task
  rootRequirement: string   // 触发拆解的原始需求
}
```

**依赖语义（lazy worktree creation，决策 2.6）**：仅当 `task.dependsOn` 中所有 task 的 status = `merged` 时，本 task 才创建 worktree（从最新 masterBranch fork）。确保后继基于含前驱改动的最新 master。

**循环检测（新增）**：DAG 拆解后必须拓扑校验。`dependsOn` 形成环时 `squad_update` 拒绝该批事件并 nudge LLM 重新拆解（不静默吞掉）。拓扑排序复用 DFS + visiting/visited 双集合（同 kb `resolveDependencyOrder`）。

### 3.6 事件（增量事件流）

DAG 状态变更以增量事件 **append** 至 `.wanxiangzhen.ndjson`（决策 2.2/2.4）。可选经 `EventCodec` 生成 frontmatter 文案注入 session **仅作 LLM 展示**，重放只读 NDJSON（`PRD/EventSourcing.md`）。

```
事件类型（共 8 类，详见附录 D.1）:
  squad_created     // 万象阵 Session 创建（含原始需求）
  tasks_created     // DAG 拆解产物（multi-frontmatter，一个 tasks 数组）
  task_started      // worktree 创建 + slave 启动
  task_submitted    // slave 调用 submit（进入 ff 检查）
  task_merged       // ff 合并成功
  task_done         // slave 进程退出（merged 或崩溃）
  task_error        // git/worktree 操作失败（错误注入，不改变 task 状态）
  squad_cancelled   // /squad-kill 触发
```

> 无 `task_rebased` 事件：rebase 发生在 slave 本地 worktree（§6.4），coordinator 不感知 rebase 过程，只见 slave 再次 `task_submitted`（附录 D.1 论证）。

---

## 4. 系统架构

### 4.1 进程拓扑

```
┌──────────────────────────────────────────────────────┐
│  Coordinator (用户的 opencode 进程 + 万象阵 插件)        │
│                                                        │
│  ┌──────────┐ ┌────────────┐ ┌──────────────────────┐ │
│  │ 万象阵    │ │ Scheduler  │ │ Git Executor         │ │
│  │ HTTP     │ │ (事件驱动   │ │ (SerialQueue 串行化   │ │
│  │ Server   │ │  +re-entry │ │  所有 master git 操作)│ │
│  │ :random  │ │  guard)    │ │                      │ │
│  └────┬─────┘ └─────┬──────┘ └──────────┬───────────┘ │
│       │             │                   │             │
│       │   ┌─────────┴─────────────┐     │             │
│       │   │ 内存 DAG (live 投影)   │     │             │
│       │   │ + .wanxiangzhen.ndjson│◄────┘             │
│       │   │   (NDJSON = SSOT)     │                   │
│       │   └───────────────────────┘                   │
│       │                                                │
│       │   PID 健康轮询 (process.kill(pid,0))            │
│       │                                                │
│  ┌────┴───────────────────────────────────────────┐   │
│  │ 主 git 仓库 (masterBranch, working tree clean)   │   │
│  │ /home/user/project/                             │   │
│  └─────────────────────────────────────────────────┘   │
└───────┬───────────────┬────────────────────────────────┘
        │ spawn          │ spawn
        │ terminal -e    │ terminal -e
        │ opencode tui   │ opencode tui
        │ --prompt       │ --prompt
        ▼                ▼
┌───────────────┐  ┌───────────────┐
│ Slave #1      │  │ Slave #2      │  (各自独立 opencode 进程)
│ opencode tui  │  │ opencode tui  │
│ 万象阵 插件    │  │ 万象阵 插件    │  (同一插件，env 区分 slave 模式)
│ worktree /A/  │  │ worktree /B/  │  (各自 .git 共享主仓库 .git)
└───────┬───────┘  └───────┬───────┘
        │ HTTP 短连接       │ HTTP 短连接
        │ (slave 发起,      │
        │  coordinator     │
        │  永不主动)        │
        └──────► coordinator:random ◄──────┘
```

### 4.2 opencode 真实插件 API 映射（新增——实现基准）

所有实现必须对标这张表（出处经核实），不得臆造 API。

| 能力 | 真实 API | 出处 |
|------|----------|------|
| 插件入口 | `(input: PluginInput, options?) => Promise<Hooks>`；万象术 形式 `pluginFor host ctx : Promise<obj>` 返回 hook 字典 | `packages/plugin/src/index.ts`；`src/Opencode/Plugin.fs` |
| 拿 client | `input.client`（含 `session.*`、`event.subscribe`） | `packages/opencode/src/plugin/index.ts:142` |
| 拿 worktree/dir | `input.worktree`、`input.directory` | `packages/plugin/src/index.ts:60` |
| 拿 opencode server URL | `input.serverUrl: URL`（仅供参考，万象阵 自起独立 server） | 同上:62 |
| Bun shell | `input.$: BunShell`（可跑 git，万象阵 选 child_process） | `packages/plugin/src/shell.ts` |
| 注入消息到 session | `client.session.prompt({ path:{id}, body:{ agent, parts:[{type:"text",text}] } })` | `src/Opencode/SessionIo.fs:134`（插件内形式） |
| SDK v2 顶层 prompt | `client.session.prompt({ sessionID, agent, model, variant, parts })` | `packages/.../cli/cmd/run.ts:794`（实现时按所用 client 版本对齐） |
| 读全量历史 | `client.session.messages({ path:{id}, query:{directory} })` → 全量存储消息 | `src/Opencode/SessionIo.fs:105`；`session.ts:857` |
| 程序化触发 slash command | `client.session.command({ sessionID, command, arguments })` | `packages/.../cli/cmd/run.ts:776` |
| 创建子 session | `client.session.create({ query:{directory}, body:{ parentID, title } })` | `src/Opencode/SessionIo.fs:176` |
| 注册工具 | hook 返回对象的 `tool` 字段：`{ [name]: ToolDefinition }`，经 `@opencode-ai/plugin/tool` 的 `tool({description,args,execute})` | `packages/plugin/src/index.ts:226`；`src/Opencode/ToolSchema.fs:119` |
| 注册 slash command | `config` hook 回调里写 `cfg.command[name] = { template, description }` | `src/Shell/OpencodeHookInputCodec.fs:129`；`packages/.../command/index.ts:98` |
| 拦截 slash command | `command.execute.before` hook：`input.{command,sessionID,arguments}`，写 `output.parts` | `packages/plugin/src/index.ts:262`；`src/Opencode/CommandHooks.fs` |
| 订阅事件 | `event` hook：`input.event.{type,properties}`（**无 child-exit / session-spawn**，核实 5.2） | `packages/plugin/src/index.ts:224` |
| 消息变换 | `experimental.chat.messages.transform`：拿 **filterCompacted 切片非全量** | `packages/.../session/prompt.ts:1145,1325` |
| compaction 行为 | 不删存储消息，建 summary + 标 compaction 切点；prune 只截 tool output | `packages/.../session/compaction.ts` |
| slave 启动带初始 prompt | `opencode tui --prompt "<text>"`（+`--session/--agent/--model`） | `packages/.../cli/cmd/tui.ts:99` |
| 串行队列 | `Shell.PromiseQueue.SerialQueue.Enqueue(work)` | `src/Shell/PromiseQueue.fs` |
| 原生 worktree（不用） | `Worktree.{create,list,remove,reset}`、`experimental_workspace.register` | `packages/.../worktree/index.ts` |

### 4.3 Kernel / Shell 分层（新增）

与 万象术 同构（README 第一性原理）。F# + Fable 编译为 JS。

```
Kernel（纯规则，去掉 opencode/Node 仍成立）:
  Task            Task / TaskStatus / VALID_TRANSITIONS / 穷举转移（纯函数）
  Dag             DAG 数据结构、拓扑排序、就绪判定、循环检测
  SquadEvent      事件类型 DU、eventTypeName、foldEvent/foldEvents（重放）
  Scheduler       纯调度决策：给定 DAG+maxConcurrent → 应启动的 task 列表
  FfDecision      ff 可行性纯判定的输入/输出类型（执行在 Shell）
  SquadPrompts    slave 初始 prompt 构造（含 task: frontmatter 锚点）
  SquadConfig     万象阵Config 类型 + 默认值合并
  SquadUpdateIdAssign  taskId 自动分配 + 碰撞重试
  EventLog/Parse  NDJSON 行解析 + 损坏截断（纯函数）

Shell（副作用边界）:
  HttpServer      Node http.createServer + listen(0) + 路由 + bearer token 校验
  GitShell        SerialQueue + git worktree/merge/rebase/rev-parse（child_process spawnSync）
  SlaveSpawn      终端命令构造 + spawn + PID 登记
  PidMonitor      process.kill(pid,0) 健康轮询（setInterval/clearInterval）
  SessionIo       promptSession（可选 LLM 展示注入；DAG SSOT 非 session.messages）
  CoordinatorReplay  replayFromEventLog：读 NDJSON + foldEvent + git reconcile
  EventCodec      yaml frontmatter 编码/解码事件（仅 LLM 展示，非 SSOT）
  SquadEventLogCodec  SquadEvent ↔ NDJSON JSON 行（v/session/kind/at/payload）
  SquadEventLogFiles  文件锁（open wx）+ append + readAll + 损坏截断 + 进程内 SerialQueue
  SquadEventLogRuntime  coordinator 接线：readAllSquadEvents / appendSquadEvent
  ConfigReader    读 AGENTS.md + yaml 解析 squad: 节
  Yaml            yaml 包包装（parse/stringify）
  SymlinkShell    共享目录 symlink + .git/info/exclude
  Dyn             JS obj 动态访问辅助（get/str/setKey/isArray 等）
  SerialQueue     Promise 链串行队列（tail 锁队尾，catch 防断链）
  CoordinatorRuntime  内存 DAG 持有 + commitEvent + cleanupTask + 重放重建
  CoordinatorOps      routeHandler + handleSubmit + startTask + schedulerTick + handleSlaveExit + PID 轮询
  CoordinatorLifecycle  handleSquadKill + handleSquadUpdate + create/createWithDeps
  SlaveRuntime    slave 端：readSlaveConfig + registerPid + doneBeacon + submitToSquad + querySquad + slaveToolDefs

宿主接线:
  Plugin.fs      plugin：env 区分 coordinator/slave 模式，组装 hook 字典
```

架构边界纪律（架构测试守）：Kernel 不直接碰 opencode `obj` / Node API；所有宿主对象经 Shell codec；可变 DAG 状态只在 Shell `CoordinatorState` 内。

### 4.4 同一插件双模式

通过环境变量区分（决策 1.5）：

```
if process.env.SQUAD_COORDINATOR_URL 存在:  → Slave 模式
else:                                      → Coordinator 模式
```

#### Coordinator 模式（无 SQUAD_COORDINATOR_URL）

`plugin` 内：
1. 读主仓库当前分支 `git rev-parse --abbrev-ref HEAD` → masterBranch（核实 5.9）
2. 读 AGENTS.md frontmatter `squad:` 节（核实 5.10）
3. 起本地 HTTP server：`http.createServer(handler).listen(0, "127.0.0.1")`，记端口 + 生成随机 bearer token（核实 5.1，安全见 §5.2）
4. config hook 注册 `/squad`、`/squad-kill`、`/squad-status` slash command
5. 注册 `squad_update` 工具
6. 初始化空内存 DAG；首次拿到 master sessionID 后调 `replayFromEventLog` 重放重建（§5.4）
7. 起 PID 健康轮询定时器（§5.9）

#### Slave 模式（有 SQUAD_COORDINATOR_URL）

`plugin` 内：
1. 读 env：`SQUAD_COORDINATOR_URL` / `SQUAD_TASK_ID` / `SQUAD_WORKTREE_PATH` / `SQUAD_MASTER_BRANCH` / `SQUAD_TOKEN`
2. 注册 `submit_to_squad`、`query_squad` 工具
3. `POST /task/:id/register { pid: process.pid }` 上报自身 opencode PID（核实 5.2）
4. 初始任务 prompt 已由 coordinator 经 `opencode tui --prompt` 在 CLI 层注入（核实 5.6），slave 插件**无需**再注入第一条消息
5. 注册 `dispose` hook：进程退出前 `POST /task/:id/done`（done beacon，§5.9 兜底）

### 4.5 环境变量约定

coordinator spawn slave 时注入：

| 环境变量 | 说明 | 示例 |
|----------|------|------|
| `SQUAD_COORDINATOR_URL` | coordinator HTTP server 地址 | `http://127.0.0.1:54321` |
| `SQUAD_TASK_ID` | 分配给此 slave 的 task ID | `squad-a1b2` |
| `SQUAD_WORKTREE_PATH` | worktree 绝对路径（slave 的 cwd） | `/home/user/worktree-squad-a1b2` |
| `SQUAD_MASTER_BRANCH` | 集成分支名（= coordinator 启动时分支） | `main` |
| `SQUAD_TOKEN` | HTTP bearer token（防本机其他进程乱调，§5.2） | `<random hex>` |

---

## 5. Coordinator 详细设计

### 5.1 插件加载与启动序列

`plugin(ctx)` 在 coordinator 模式下的精确启动序列（全部在返回 hook 字典前完成，失败不可吞）：

```
1. masterBranch = git rev-parse --abbrev-ref HEAD   (cwd = input.worktree)
   ├─ 失败（非 git 仓库 / detached HEAD）→ 记录禁用标志，/squad 返回明确错误，不崩溃
   └─ 成功 → 记录 startupBranch = masterBranch
2. config = read万象阵Config(input.worktree/AGENTS.md)  (§7)
3. token = crypto.randomBytes(16).toString("hex")
4. server = http.createServer(handler)
   server.listen(0, "127.0.0.1")
   coordinatorPort = server.address().port
   coordinatorUrl = `http://127.0.0.1:${coordinatorPort}`
5. dag = empty DAG（masterSessionId 暂未知）
6. 起 PID 健康轮询 setInterval（pidPollMs，默认 2000）
7. 返回 hook 字典：{ tool:{squad_update}, config, "command.execute.before", event, dispose }
```

注意：master sessionID 在插件加载时**还拿不到**（用户尚未对话）。它在首个 `/squad` 或首条 `chat.message` 时从 hook input 捕获（§5.4）。

### 5.2 HTTP Server

#### 启动与绑定

- 只绑 `127.0.0.1`，端口 `listen(0)` 由 OS 分配，避免冲突。
- coordinatorUrl + token 存进程内变量，spawn slave 时注入 env。

#### 安全（新增——security_review）

opencode 无内置 API 认证，本机任何进程都能访问 `127.0.0.1:<port>`。**修正**：每请求校验 `Authorization: Bearer <SQUAD_TOKEN>`，token 在插件启动时随机生成、仅经 env 传给自己 spawn 的 slave。校验失败 → `401`。这阻止本机其他进程伪造 submit 触发未授权 ff。

> 安全标注：这是网络暴露面（即便仅 loopback）。token 校验是最低限度防护，不写日志、不回显 token 值。

#### API 端点

所有请求都是 slave 发起的短连接（决策 1.4：coordinator 永不主动）。请求体 JSON，响应同步 JSON。

```
鉴权：全端点要求 Authorization: Bearer <token>；缺/错 → 401 { result:"unauthorized" }。
判别：所有"领域结果"走 200 + 单一 result 标签（merged/rebase_needed/... 皆 200，slave 按 result 穷尽匹配，
      不读 HTTP 码做业务判断）；仅"传输/协议错误"用 HTTP 码（404 task 不存在、400 请求体非法）。
      不设 ok 字段——ok 可由 result 推导，双字段会造出 {ok:true,result:"rebase_needed"} 这类非法态（公理 6）。

POST /task/:id/register        (slave 启动时上报真实 PID)
  Body: { pid: number }
  → 200 { result: "registered" }
  作用：记录 task.slavePid，纳入 PID 健康轮询（核实 5.2）

GET  /task/:id
  → 200 { id, title, description, dependsOn, status }   (masterBranch 经 env 已知，不重复下发)
  → 404 { result: "task_not_found" }

POST /task/:id/submit          (slave 完成开发后提交)
  Body: { commitSha: string }  (branch 由 coordinator 从 task.id 映射，slave 不传)
  → 200 { result: "merged",                masterSha: string }
  → 200 { result: "rebase_needed",         masterSha: string }
  → 200 { result: "stale_commit" }
  → 200 { result: "coordinator_not_ready", reason: "not_on_master" | "dirty" }
  → 200 { result: "not_submittable",       currentStatus: string }   (task 非 running，如重复 submit)
  → 404 { result: "task_not_found" }
  handler 流程：① 鉴权 ② task 查找 ③ status≠running → not_submittable（不入队）
               ④ status=submitted + 注入 task_submitted ⑤ SerialQueue 内 tryFastForward（§5.7）
               ⑥ merged → status=merged + 清理 + 注入 task_merged；其余非合并 → status 回 running（slave 重试）

POST /task/:id/done            (slave done beacon，dispose hook 触发)
  Body: { }                    (退出意图，内容无关，决策 2.3)
  → 200 { result: "acknowledged" }
  作用：加速感知 slave 退出（PID 轮询的兜底加速，§5.9）

GET  /state
  → 200 { sessions: [{ sessionId, tasks: [{ id, title, status, dependsOn, slavePid }] }] }
  作用：slave query_squad 查全局 DAG（sessions 数组前向兼容多 Session，§14.3）

POST /task/:id/log             (可选，第三阶段；slave 报告进度)
  Body: { message: string }
  → 200 { result: "logged" }
```

说明：
- `submit` 不要 slave 传 branch name —— coordinator 建 worktree 时已记 `task.id → branchName(=task.id)` 映射。
- `commitSha` 用途（修正）：coordinator 校验 `git rev-parse <branch>` == commitSha，检测 slave 是否在报告后又改了东西。不匹配 → 返回 `200 { result:"stale_commit" }`（领域结果非传输错误），迫使 slave 重新提交。
- 全部短连接，不用 WebSocket/SSE（决策 1.4）。

### 5.3 `/squad` 命令

#### 触发

```
/squad 为登录加"记住我"，需改认证中间件、前端表单、数据库 schema
```

#### 处理（command.execute.before hook）

1. hook input 给出 `{ command:"squad", sessionID, arguments }`。
2. **捕获 master sessionID**：`masterSessionId ??= input.sessionID`（§5.4），触发 `replayFromEventLog`。
3. 若当前 DAG 有任务，先归档到 `sessions`。生成新的 squad-session-id，`commitEvent(SquadCreated)`。
4. `command.execute.before` 把原命令消息改写成 `squad_created` 事件正文（含拆解指令）：清除 `output.parts` 并替换为 `encodeEvent(evt)` 文本。这样对话历史里只留一条消息，既是用户输入的事实，也是给 LLM 的拆解指令。
5. coordinator 自己的 LLM 读取该事件后调用 `squad_update` 提交任务。

#### nudge（LLM 没调 squad_update）

复用 万象术 nudge 机制（决策 1.2）：LLM 回复了但没调 `squad_update` → 注入提示：

```
你还没提交拆解结果。请调用 squad_update 工具提交任务拆解（events 数组）。
```

nudge 上限 3 次（同 万象术 `maxNudges`），超限放弃并向用户说明。

> 注：当前代码通过 `squad_created` 事件正文中的指令文本（"Call the squad_update tool..."）作为初始提示，nudge 重试逻辑尚未实现。

### 5.4 master session 捕获与重放（NDJSON SSOT）

#### 为什么仍捕获 masterSessionId

向 coordinator LLM 注入进度/告警（可选 `session.prompt`）需要宿主 session id。插件加载时拿不到（§5.1）。

#### 捕获时机

`command.execute.before`（`/squad`）或 `chat.message`：首个非空 `sessionID` → `masterSessionId`。

#### 重放重建（`PRD/EventSourcing.md`）

coordinator 启动或首次需要 DAG 时：

```
replayFromEventLog(rt):
    events = rt.Deps.ReadAllSquadEvents(projectRoot)   // 读 .wanxiangzhen.ndjson
    // 按文件行序 fold，SquadCreated 边界归档旧 dag → sessions
    for ev in events:
        match ev:
          SquadCreated(sid, req): archive prior dag → sessions; dag = empty(sid, req)
          _ : dag = foldEvent(dag, ev)
    dag = gitReconcile(dag)   // §5.4 第二真理源
    return dag, sessions
```

**禁止**用 `client.session.messages()` fold DAG。

#### git 二次校正（决策 2.2 第二真理源）

重放后，对每个 `running`/`submitted` task 用 git 校正合并事实：

```
if git merge-base --is-ancestor <branch> <masterBranch> 为真:
    task.status = "merged"   ← 事件可能丢了，但 git 证明已合并
```

这把"合并是不可逆事实"钉在 git refs，事件流缺失也能恢复真相。

**重要契约与行为依赖**：
`gitReconcile` 校正仅适用于折叠历史后状态已经为 `Running` 或 `Submitted` 的任务。如果任务由于仅有 `tasks_created` 事件而处于 `Pending` 状态，即使在 Git 中其对应分支被检测到为祖先，也绝对**不进行**校正转换，必须保持为 `Pending` 状态。这是因为 `Pending` 状态下的任务尚未分配具体的分支（`BranchName` 尚未生成），物理上无从在 Git 中被追溯，只有通过 `task_started` 事件将任务激活为 `Running` 后，才能参与校正决策。

### 5.5 `squad_update` 工具

#### 定义（args schema）

```
工具名: squad_update
描述: Submit task decomposition for the current squad session.
     Send one tasks_created event carrying a tasks[] array (one entry per task).
     The handler aggregates all tasks, assigns missing taskIds, validates the DAG,
     and injects a single TasksCreated event into master session history.

参数 (JSON Schema):
{
  "events": {
    "type": "array", "minItems": 1,
    "items": {
      "type": "object",
      "properties": {
        "type":  { "type":"string", "enum":["tasks_created","squad_cancelled"] },
        "tasks": {
          "type":"array",
          "items": {
            "type":"object",
            "properties": {
              "taskId":      { "type":"string", "description":"Unique task ID; omit to auto-assign squad-<hex4>" },
              "title":       { "type":"string" },
              "description": { "type":"string", "description":"Full task description for the slave agent" },
              "dependsOn":   { "type":"array", "items":{"type":"string"}, "description":"Task IDs this depends on; [] = none" }
            },
            "required": ["title","description"]
          }
        }
      },
      "required": ["type"]
    }
  }
}
```

> tasks_created 事件携带 tasks[]：LLM 一次发完所有任务（multi-frontmatter 等效事实源的输入侧对应）。squad_cancelled 事件无载荷。

#### execute 行为

```
1. 校验 events：
   a. 每个 tasks_created 事件必须有非空 tasks[] 数组；每个 task 必须有非空 title + description
   b. dependsOn 引用的 taskId 必须存在（同批次内或已有）
   c. 合并后 DAG 拓扑校验（§3.5 循环检测）→ 有环则拒绝整批，返回错误让 LLM 重拆
2. 聚合所有 tasks_created 事件的 tasks[]：为缺 taskId 的 task 生成 squad-<hex4>（碰撞重试，§5.7），逐个 addTask 进内存 DAG
3. 构造 `TasksCreated` → `commitEvent` **append** `.wanxiangzhen.ndjson`（成功后再更新内存）；`squad_cancelled` → `handleSquadKill` + append
4. 触发 Scheduler.tick()
5. 返回确认给 LLM："N tasks recorded; scheduler notified."（仅有 squad_cancelled 且无 task → "Squad session <id> cancelled."）
```

校验失败返回类型分支（决策 2.4：可预见失败用返回值，不抛异常）：

```
拓扑有环 → "Dependency cycle detected: squad-x → squad-y → squad-x. Please re-decompose without cycles."
依赖悬空 → "Task squad-c3d4 dependsOn unknown task squad-zzzz. Fix dependencies."
```

### 5.6 Scheduler（后台调度引擎）

#### 触发时机（事件驱动，非定时轮询）

- `squad_update` execute 后
- `POST /task/:id/submit` ff 成功后
- slave 退出处理后（PID 消失 / done beacon）
- `squad_update` 创建后首次

#### tick() 逻辑（含 re-entrance guard，新增）

```
function tick():
    if scheduling: return        ← re-entrance guard（决策 2.5），防重叠 tick 致快照不一致
    scheduling = true
    try:
        decision = decide(dag, maxConcurrent)   ← Kernel.Scheduler 纯函数
        // occupied = count tasks where status in {Running, Submitted}
        // available = maxConcurrent - occupied
        // ready = pending tasks whose deps all Merged, sorted by Id
        // toStart = ready |> truncate available
        for taskId in decision.TasksToStart:
            createWorktree(task)          ← 从最新 masterBranch fork（§5.7）
            commitEvent(task_started)     ← append NDJSON + optional session.prompt
            createSymlinks(task)          ← 共享目录（§5.8）
            spawnSlave(task)              ← terminal -e opencode tui --prompt（§5.9）
            updateTask status=Running
        // decision.TasksWaiting 留 pending，下次 tick 再查
    finally:
        scheduling = false
```

#### 并发上限

AGENTS.md frontmatter `squad.maxConcurrent`（默认 3）。超限的就绪 task 留 pending，待 running task 转 merged/done 后下次 tick 启动。

### 5.7 Git Executor（SerialQueue 串行化）

所有对 masterBranch 的 git 操作经单一 SerialQueue 原子串行（决策 2.5，`src/Shell/SerialQueue.fs`）。

```
gitQueue = SerialQueue()
executeOnMaster(work) = gitQueue.Enqueue(work)
```

#### ff 检查与合并（修正：无 fetch、无 origin，核实 5.7）

```
tryFastForward(taskId, branchName, reportedSha):
  → executeOnMaster(() => {
      // 0. stale 校验（§5.2）
      headSha = git rev-parse <branchName>
      if headSha != reportedSha:
          return { result:"stale_commit" }
      // 1. 前置校验（决策 5.9）：coordinator 主仓库仍在 masterBranch 且 clean
      cur = git rev-parse --abbrev-ref HEAD
      if cur != masterBranch:          return { result:"coordinator_not_ready", reason:"not_on_master" }
      if git status --porcelain 非空:  return { result:"coordinator_not_ready", reason:"dirty" }
      // 2. ff 可行性：masterBranch 是否为 branch 的祖先
      if git merge-base --is-ancestor <masterBranch> <branchName> 成功:
          git merge --ff-only <branchName>        ← 本地分支，无 fetch
          return { result:"merged", masterSha: git rev-parse <masterBranch> }
      else:
          return { result:"rebase_needed", masterSha: git rev-parse <masterBranch> }
    })
```

关键不变式：步骤 1-2 在 SerialQueue 内原子执行，**不会有两个 slave 的 ff 交叉**。这是并行 ff 竞争（§6）正确性的根基。

`coordinator_not_ready`（reason: `not_on_master` / `dirty`）是诚实失败分支（决策 5.9 假设用户不乱动，但不静默吞掉违例）：返回给 slave 让其稍后重试，同时 coordinator 向用户报警。两个 reason 对 slave 处置相同（稍后重试），合一分支减少 slave 匹配臂；reason 仅供 coordinator 日志与用户报警区分。

### 5.8 Worktree 管理

#### 创建（修正：用 masterBranch 非硬编码 master）

```
createWorktree(task):
  1. worktreePath = join(projectRoot, "..", `worktree-${task.id}`)   // 决策 1.12
  2. branchName = task.id                                            // 如 squad-a1b2
  3. executeOnMaster(() =>
       git worktree add -b <branchName> <worktreePath> <masterBranch>) // 从最新集成分支
  4. createSymlinks(worktreePath, projectRoot, config.sharedDirs)    // §5.8 下
  5. 记 task.worktreePath / task.branchName
```

路径约定：`{projectRoot}/../worktree-{taskId}`（决策 1.12），不被项目 git 追踪。taskId = `squad-` + 4 hex（决策 1.12）。

碰撞避免（新增）：生成 taskId 后检查 `.worktrees` 目录 + `git show-ref` 分支是否已存在，冲突则重生成（4 hex = 65536 空间，碰撞罕见但不裸奔）。

#### 删除

```
removeWorktree(task):
  1. executeOnMaster(() => git worktree remove --force <task.worktreePath>)
  2. executeOnMaster(() => git branch -D <task.branchName>)
  3. 清 symlink（如有）
```

删除时机（决策 3.2）：
- status → `merged`：先确保 slave 已退出（§5.9 顺序），再删 worktree + 分支。
- status → `done`：删 worktree + 分支。
- status → `cancelled`：**不删**（保留现场，决策 3.3）。

#### merged 删除的时序安全（新增——root_cause_analysis）

若 merged 后立即删 worktree，但 slave 进程还活在该 worktree（cwd 失效崩溃）。**修正**：merged → 先发 done beacon 期望 slave 自然退出 / 或 coordinator 主动 `process.kill(slavePid, SIGTERM)` → 确认 PID 消失 → 再 removeWorktree。即"先停进程，后删目录"。

### 5.9 Slave 进程管理（修正：tui --prompt + PID 探测）

#### Spawn 流程（核实 5.6）

```
spawnSlave(task):
  1. env = { ...process.env,
             SQUAD_COORDINATOR_URL: coordinatorUrl,
             SQUAD_TASK_ID:        task.id,
             SQUAD_WORKTREE_PATH:  task.worktreePath,
             SQUAD_MASTER_BRANCH:  masterBranch,
             SQUAD_TOKEN:          token }
   2. initialPrompt = SquadPrompts.buildSlavePrompt(task)   // §6.2，固定按 /loop 可用构造
  3. cmd/args = terminalCommand(config.terminal, "opencode tui --prompt <initialPrompt>") // §5.10
  4. child = child_process.spawn(cmd, args, { cwd: task.worktreePath, env, detached:false })
  5. // 不依赖 child.on('exit')（守护进程型终端会立即返回，核实 5.2）
     // slave 启动后会 POST /task/:id/register { pid } 上报真实 opencode PID
```

#### PID 健康轮询（核实 5.2，替代不存在的 child-exit hook）

```
setInterval(pidPollMs):
  for task where status in {running, submitted} and task.slavePid != null:
      if not alive(task.slavePid):           // process.kill(pid, 0) 抛 ESRCH = 已死
          handleSlaveExit(task.id)
      else:
          task.lastHeartbeatAt = now
```

`alive(pid)`：`try { process.kill(pid, 0); return true } catch (e) { return e.code != "ESRCH" }`（EPERM 视为存活）。

#### done beacon（兜底加速）

slave 的 `dispose` hook 在进程退出前 `POST /task/:id/done` → coordinator 立即 `handleSlaveExit`，无需等下一次 PID 轮询。崩溃（无 dispose）则靠 PID 轮询兜底。

#### Slave Exit 处理

```
handleSlaveExit(taskId):
  if task.status in {merged, done, cancelled}: return   // 幂等，防 beacon+轮询重复
  task.status = "done"                                  // done 不管内容（决策 2.3）
  commitEvent(task_done, task)                           // append NDJSON + optional prompt
  removeWorktree(task)                                  // 决策 3.2
  Scheduler.tick()                                      // 推进后续
```

「done 不管内容」语义（决策 2.3）：
- slave 退出 = task done，无论是否真完成。
- 退出前已 merged → 该 task 已是 merged 终态，handleSlaveExit 因幂等 return，不覆盖。
- 退出前没合并 → done + 删 worktree，DAG 继续推进。
- 形式主义完成保证：DAG 总会跑完，哪怕某些 task 无产出。

#### 终端配置（§5.10 详述命令映射）

`squad.terminal`（AGENTS.md frontmatter，决策 1.11，默认 `alacritty`）。无终端时降级 headless（第二阶段）。

### 5.10 终端命令映射（实现细节，决策 4.1 写代码时定）

slave 启动 = 在终端模拟器里跑 `opencode tui --prompt "<task>"`（cwd=worktree）。各终端语法不同：

```
alacritty:        alacritty --working-directory <wt> -e opencode tui --prompt <p>
kitty:            kitty --directory <wt> opencode tui --prompt <p>
gnome-terminal:   gnome-terminal --working-directory=<wt> -- opencode tui --prompt <p>
konsole:          konsole --workdir <wt> -e opencode tui --prompt <p>
wezterm:          wezterm start --cwd <wt> -- opencode tui --prompt <p>
Windows Terminal: wt.exe -d <wt> opencode tui --prompt <p>
iterm2:           osascript -e 'tell application "iTerm" to create window...' -e '...write text "cd <wt> && opencode tui --prompt <p>"'
headless（降级）:  child_process spawn opencode tui --prompt <p> (cwd=<wt>, 无窗口)
```

注意（核实 5.2）：gnome-terminal/konsole 是守护进程型，spawn 的子进程立即返回，**必须靠 PID 探测而非 child.on('exit')**。`--prompt` 经 shell 传参需正确转义（防注入，用数组参数而非字符串拼接）。

### 5.11 事件落盘（NDJSON + 可选 LLM 通知）

#### 写入队列与文件锁

多源并发 append。所有 NDJSON 写在 **文件锁**（`open wx` 独占创建 `.wanxiangzhen.ndjson.lock`）内串行；进程内 `SquadEventLogStore` 的 `SerialQueue` 排队 append Promise。

```
commitEvent(rt, ev):
  InjectQueue.Enqueue(() =>
    AppendSquadEvent(projectRoot, now, ev)   ← 文件锁 + append NDJSON
    on Ok: tryPromptWithRetry(masterSessionId, encodeEvent(ev))  ← 可选 LLM 通知
    on Error: return Error
  )
  // 调用方在 commit Ok 后更新内存 DAG
```

#### 路径 A / B（语义不变，载体变更）

- **路径 A**：`squad_update` / `/squad` → append `tasks_created` / `squad_created`。
- **路径 B**：Scheduler / HTTP / PID → append `task_*` / `squad_cancelled`。

#### 先写盘后改内存（强制）

append 失败 → 内存 DAG **不得**反映该事实；调度不得依赖未落盘状态。git 校正仅补合并事实，不替代 append。

#### 可选 prompt()

`session.prompt` 可触发 LLM 回复（决策 2.1）；与 SSOT 解耦。DAG 推进 **不依赖** LLM。

### 5.12 Coordinator 崩溃恢复

```
1. 用户重启 opencode（coordinator 模式自动激活）
2. 插件加载，起新 HTTP server（新端口、新 token）
3. replayFromEventLog（§5.4：NDJSON + git 校正）
4. 但存量 slave 还连旧端口 → 其 HTTP 请求失败
5. slave submit_to_squad 返回错误 → slave LLM 向用户报错 → slave idle（决策 1.10）
6. 用户 /squad-kill 清残留 slave，或手动处理
```

这是预期降级（决策 1.10）：coordinator 崩溃后不自动重连 slave（不做的事，§12），用户手动介入。已 merged 的成果在 git 里安全，重放 + git 校正能恢复 DAG 合并事实。

### 5.13 `/squad-kill` 命令

```
/squad-kill [session_id]
```

- 无参：杀所有 万象阵 Session 的所有 running/submitted slave 进程。
- 带 session_id：只杀指定 Session 的 slave。

处理（决策 1.10/3.3）：
```
1. for task in running/submitted: process.kill(task.slavePid, SIGTERM)
2. 不删 worktree（保留现场供调试）
3. 不删分支（保留代码供检查）
4. commitEvent(squad_cancelled) → append NDJSON；失败则 DAG 不变，`InjectError` 记录
5. append 成功：`foldEvent(SquadCancelled)`（所有非终态 task → cancelled，与重放同语义）
```

与正常退出的区别：自然退出（done/merged）删 worktree + 分支；`/squad-kill` 保留所有现场（决策 3.3）。`cancelled` 状态使 PID 轮询/beacon 的 handleSlaveExit 因幂等 return，不误删现场。

---

## 6. Slave 详细设计

### 6.1 启动流程（修正：初始 prompt 由 CLI --prompt 注入）

```
1. coordinator 在终端模拟器里启动: opencode tui --prompt "<initialPrompt>"  (cwd = worktreePath)
2. opencode 加载 万象阵 插件
3. 插件检测 SQUAD_COORDINATOR_URL → 进入 Slave 模式
4. 插件注册 submit_to_squad + query_squad 工具
5. 插件 POST /task/:id/register { pid: process.pid }  ← 上报真实 opencode PID（核实 5.2）
6. 初始 prompt 已由 CLI 层 --prompt 注入为首条用户消息（核实 5.6），slave 插件无需再注入
7. slave LLM 开始工作
```

**修正说明**（核实 5.6）：原 PRD 设想"插件 HTTP GET /task 拿详情再 `prompt()` 注入第一条消息"。实测 `opencode tui --prompt "<text>"` 在 CLI 启动时即把文本作为首条 user 消息，更简洁可靠。故 coordinator spawn 前用 `SquadPrompts.buildSlavePrompt(task)` 构造好完整初始 prompt 直接经 `--prompt` 传入。slave 插件仍保留 `GET /task/:id` 能力供 LLM 中途 `query_squad` 复查任务详情。

### 6.2 buildSlavePrompt（初始 prompt 构造，Kernel.SquadPrompts 纯函数）

slave prompt 固定按 万象术 /loop（With-Review Mode）可用构造，含 `task:` frontmatter 锚点激活 With-Review：

```
---
task: {title}
---

You are executing squad task {taskId}: {title}
Task description:
{description}

Complete the task following the review workflow.
Activate With-Review Mode by calling /loop <task description>.
After development, call submit_review for review.
After review PASS, git commit, then call submit_to_squad.
If review REJECT, fix per feedback and re-review until PASS.
If asked to rebase, run: git rebase {masterBranch}, then resubmit.
```

`task:` frontmatter 是 万象术 `messages.transform` 识别 With-Review 激活的锚点（核实 5.3），经 `opencode tui --prompt` 注入后 slave LLM 自然进入 review 流程。

### 6.3 万象术 /loop 前提

slave prompt 固定按 万象术 /loop（With-Review Mode）可用构造。万象术 必须在 万象阵 之前安装；无运行时检测，无 `SQUAD_VIBEFS` 环境变量。

### 6.4 `submit_to_squad` 工具

```
工具名: submit_to_squad
 描述: |
  Submit your completed work to the squad coordinator for fast-forward merge into the integration branch.
  Prerequisites:
  - All required code changes are complete in this worktree.
  - You have passed 万象术 /loop review (PASS).
  - You have committed your changes (git commit).
  The coordinator atomically checks whether your branch can fast-forward the integration branch.
  Success → merged, task complete. Failure → you are asked to rebase onto the latest integration branch.
参数: (无 — coordinator 从 task_id 映射到 branch name)
```

execute 行为：

```
execute():
  1. commitSha = git rev-parse HEAD   (cwd = SQUAD_WORKTREE_PATH)
  2. POST {SQUAD_COORDINATOR_URL}/task/{SQUAD_TASK_ID}/submit
       headers: { Authorization: "Bearer " + SQUAD_TOKEN }
       body:    { commitSha }
  3. match response.result（按单一 result 标签穷尽匹配，不读 HTTP 码）:
       "merged":
         return "✅ Merged into {masterBranch} @ {masterSha}. Task complete. You may stop."
       "rebase_needed":
         return "❌ Cannot fast-forward. {masterBranch} moved to {masterSha}.
                 Rebase and resubmit:
                   git rebase {masterBranch}
                 (Resolve conflicts, re-run review if using /loop, then call submit_to_squad again.)"
       "stale_commit":
         return "❌ Your branch HEAD differs from the reported commit. Commit your latest work, then call submit_to_squad again."
       "coordinator_not_ready":
         return "❌ Coordinator's main repo is not ready (not on {masterBranch} / dirty). Wait and call submit_to_squad again shortly."
       "not_submittable":
         return "❌ This task is no longer accepting submissions (status: {currentStatus}). Report to user and stop."
       HTTP 失败（coordinator 崩溃 / 404）:
         return "❌ Coordinator unreachable. The coordinator may have crashed. Report to user and wait."
```

**修正**（核实 5.7）：rebase 目标是本地 `{masterBranch}`（worktree 共享主仓库 .git，能直接看到 coordinator 刚 ff 的最新 masterBranch），**不是 `origin/master`**。worktree 无 origin remote。

### 6.5 `query_squad` 工具

```
工具名: query_squad
描述: |
  Query the squad coordinator for current DAG state or a specific task.
  Use when you need status of other tasks, dependencies, or global context.
参数:
  query: { type:"string", description:"'state' for full DAG, or a task ID for that task's details." }
```

execute：

```
execute(query):
  headers = { Authorization: "Bearer " + SQUAD_TOKEN }
  if query == "state": GET /state                → return JSON
  else:                GET /task/{query}          → return JSON
  HTTP 失败 → return "Coordinator unreachable; proceeding without global context."
```

query_squad 失败不阻塞 slave（决策：HTTP 失败时 slave 不依赖查询结果继续，§9.5）。

### 6.6 Slave 完整工作循环（决策 2.2：do-while ff）

```
1. 接收任务（CLI --prompt 注入的首条消息）
2. 理解任务 → 开发代码（read/edit/write/grep/bash...）
 3. /loop review（submit_review → reviewer → PASS/REJECT）
    REJECT → 按反馈修改 → 重 review，直至 PASS
 4. review PASS
5. git add + git commit
6. 调用 submit_to_squad
7. result=="merged" → 任务完成，slave 可退出
8. result=="rebase_needed":
     a. git rebase {masterBranch}          ← 本地分支，非 origin（核实 5.7）
     b. 有冲突 → LLM 解决 → git add → git rebase --continue
     c. 无冲突 → 继续
     d. 回步骤 3（rebase 可能改了代码，重新 review）
     e. 回步骤 5（重新 commit + submit）
9. 循环 7-8 直到 merged（无限重试，决策 2.4：无限猴子形式主义完成）
```

时序本质（决策 2.2）：`do { /loop(开发 or rebase) } while (不能 ff)`。内层 /loop review，外层 submit_to_squad → ff 检查 → 可能 rebase → 重来。

### 6.7 Slave 退出与 done beacon

slave 注册 `dispose` hook（opencode 插件生命周期收尾，核实：plugin 返回的 hook 字典可含清理回调）：

```
dispose():
  POST /task/{SQUAD_TASK_ID}/done   (best-effort，失败忽略)
```

正常退出（任务 merged 后用户关窗 / opencode 退出）→ dispose 发 beacon → coordinator 立即 handleSlaveExit。崩溃（无 dispose 机会）→ coordinator PID 轮询兜底感知（§5.9）。

### 6.8 Slave 的 Git 约束（乐观，prompt 层）

slave 系统提示词约束（决策 1.6：乐观约束，非技术强制）：

```
你在一个 万象阵 worktree 中工作。

允许的 git 操作：
- git add / git commit          （在自己的分支提交）
- git rebase {masterBranch}     （rebase 到最新集成分支，本地分支）
- git fetch                     （仅当存在 remote 时；本地纯 worktree 通常无需）
- git log / status / diff       （只读查询）

禁止的 git 操作：
- git push                      （无需推送，coordinator 处理合并）
- git merge                     （无需合并，coordinator 做 ff-only）
- git checkout {masterBranch}   （不要切到集成分支；worktree 已锁定本任务分支）
- 任何修改集成分支的操作
- 任何写共享目录（node_modules 等 symlink 目标）的操作
```

**修正**（核实 5.7）：rebase 目标统一为本地 `{masterBranch}`（worktree 共享主仓库 .git，coordinator ff 后本地 masterBranch ref 即最新）。仅当项目本身有 origin remote 且需要时才 fetch；纯本地 万象阵 流程不需要。

> opencode worktree 与主仓库共享同一 `.git`（核实 5.8）：worktree 是 `git worktree add` 产物，`.git` 是指向主仓库的 gitdir 文件。故 slave 在 worktree 内 `git log {masterBranch}` 能直接看到 coordinator 刚 ff 的提交，无网络往返。

---

## 7. 并行 ff 竞争（并发正确性核心）

### 7.1 场景

两个无依赖 task A、B 同时从 `masterBranch` fork worktree（依赖均空，Scheduler 同 tick 并发启动）。A 先完成、commit、submit，ff 成功 → masterBranch 前进到 A 的提交。B 随后 submit，但 B 的分支基于 A 合并前的 `masterBranch`，B 不是当前 masterBranch 的后代 → 不能 ff。

### 7.2 处理（决策 3.1）

```
1. B 的 submit_to_squad 收到 { result:"rebase_needed", masterSha:<A 合并后> }
2. B 执行 git rebase {masterBranch}        ← 本地集成分支已含 A 的改动（共享 .git，核实 5.7/5.8）
3. 无冲突 → B 现基于含 A 的最新 masterBranch → 重新 /loop review → 重新 submit
4. 有冲突 → B 的 LLM 解决 → git add → git rebase --continue → 重新 review → 重新 submit
5. 循环直到 ff 成功（决策 2.4：无限重试）
```

### 7.3 正确性根基（invariance）

并发安全的唯一不变式：**ff 检查与 ff 合并在 SerialQueue 内作为单个原子单元执行（§5.7）**。展开：

```
executeOnMaster(() => {           // SerialQueue.Enqueue，全局串行
    assert coordinator on masterBranch ∧ worktree clean   // 前置校验
    if git merge-base --is-ancestor {masterBranch} {branch}:   // 检查
        git merge --ff-only {branch}                            // 合并
        return merged
    else:
        return rebase_needed
})
```

关键：检查（is-ancestor）与合并（merge --ff-only）之间 `masterBranch` ref **不可能**被另一个 slave 的 ff 改动——因为另一个 ff 必须经同一 SerialQueue，正排队等待。这消灭了 "A 检查通过 → B 抢先合并 → A 合并到错误基址" 的 TOCTOU 竞态。

反证：若两个 ff 可交叉，则存在 `is-ancestor(M, A)=true` 与 `merge(A)` 之间 M 被 B 推进的窗口，A 合并会基于陈旧判断。SerialQueue 把窗口压成零（同一队列内无并发），故安全。

### 7.4 为什么这是预期而非缺陷

- DAG 依赖由 LLM 拆解定义，可能不完美——两个"无依赖" task 仍可能改同一文件。
- ff 竞争是自然的乐观并发控制：无锁推进，冲突时后到者 rebase（决策 3.1）。
- 最坏情况 B 反复 rebase 失败 → 无限重试（决策 2.4 无限猴子形式主义完成），或用户关窗 / `/squad-kill` 中止（§5.13）。
- 对比悲观锁（全序串行执行所有 task）：ff 竞争允许真并行，仅在合并点串行，吞吐显著更高。代价是后到者 rebase 成本，由 slave LLM 自动承担。

### 7.5 stale_commit 边界（核实 5.7 衍生）

slave submit 上报 `commitSha`，但 coordinator 以 `task.id` 映射的 branch ref 为准做 ff。若 slave 上报的 `commitSha` ≠ 当前 branch HEAD（slave 报告后又改了代码却没重新 commit，或 commit 失败），coordinator 返回 `stale_commit`，要求 slave 重新 commit 后再 submit。这是诚实失败分支，不静默用 branch HEAD 兜底（避免合并 slave 未声明的提交）。

---

## 8. 配置

### 8.1 配置载体：AGENTS.md frontmatter（核实：配置走 AGENTS.md frontmatter）

coordinator 启动时读项目根 `AGENTS.md` 顶部 yaml frontmatter 的 `squad:` 段：

```yaml
---
squad:
  maxConcurrent: 3              # 同时运行 slave 上限，默认 3
  terminal: alacritty           # 终端模拟器，默认按平台探测
  masterBranch: master          # 集成分支名；缺省=coordinator 启动时所在分支（修正）
  sharedDirs:                   # 只读共享目录（symlink）
    - node_modules
    - .venv
---
```

### 8.2 配置项

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `maxConcurrent` | number | `3` | 同时运行的 slave 进程上限（决策 1.7） |
| `terminal` | string | 平台探测（§5.10） | 终端模拟器名；`headless` 为无 TUI 降级 |
| `masterBranch` | string | **coordinator 启动时所在分支**（修正） | 集成分支名。非硬编码 `master` |
| `sharedDirs` | string[] | `[]` | 只读共享目录列表（symlink，决策 1.8） |

### 8.3 masterBranch 解析（修正——非硬编码）

```
resolveMasterBranch(config, projectRoot):
  if config.masterBranch 显式配置: return config.masterBranch
  else:
    # 取 coordinator 启动时仓库当前分支
    branch = git -C {projectRoot} rev-parse --abbrev-ref HEAD
    if branch == "HEAD" (detached): 报错并要求用户显式配置 masterBranch
    return branch
```

原 PRD 硬编码 `master`。修正理由：现代仓库主分支可能是 `main`/`trunk`/自定义。以"coordinator 启动时所在分支"为集成目标更符合直觉（用户在哪个分支起 coordinator，成果就 ff 回哪个分支），且 frontmatter 可显式覆盖。`SQUAD_MASTER_BRANCH` env、worktree fork 基址、ff 目标、slave rebase 目标全部用此解析值（全文一致）。

### 8.4 配置解析（Shell 层全量 yaml，非仅标量）

核实 5.5：万象术 的轻量 frontmatter 解析器只认标量字段（用于 review 重放锚点），**不解析数组/嵌套**。但 `sharedDirs` 是数组、`squad:` 是嵌套对象。故 万象阵 配置解析走 Shell 层的完整 `yaml` 包（万象术 依赖已含 yaml），不复用那个轻量标量解析器。事件 frontmatter（§5.4/附录 D）才用得上轻量重放扫描，但其数组字段（如 `depends_on`）也需注意编码（见附录 D）。

### 8.5 配置缺失与降级

- 无 `AGENTS.md` 或无 `squad:` 段 → 全用默认值（`maxConcurrent=3`、terminal 平台探测、masterBranch=当前分支、sharedDirs=[]）。
- 单字段缺失 → 该字段取默认（合并语义，同 万象术 DEFAULT 合并）。
- `terminal` 配的模拟器不存在（spawn ENOENT）→ 降级 headless（§5.10），日志告警。
- 配置错误（yaml 语法错）→ coordinator 启动报错，要求用户修复（不静默吞，诚实失败）。

---

## 9. 完整执行流程示例（修正后 API）

### 9.1 用户操作

```
用户（coordinator 终端）: /squad 为登录功能加"记住我"，需改认证中间件、前端表单、数据库 schema
```

### 9.2 Coordinator 处理

```
[1] /squad 命令触发（§5.3）
    → command.execute.before hook 捕获 masterSessionId
    → 生成 squad-session-id，commitEvent(SquadCreated)
    → output.parts 替换为事件正文（含拆解指令）

[2] coordinator LLM 分析需求 → 调用 squad_update（§5.5）:
      events: [
        { type:"tasks_created", tasks: [
          { taskId:"squad-a1b2", title:"改数据库 schema",
            description:"在 users 表加 remember_me BOOLEAN 列 + migration...", dependsOn:[] },
          { taskId:"squad-c3d4", title:"改认证中间件",
            description:"中间件读 remember_me cookie 延长 session...", dependsOn:["squad-a1b2"] },
          { taskId:"squad-e5f6", title:"改前端表单",
            description:"登录表单加'记住我'复选框...", dependsOn:["squad-a1b2"] },
        ] }
      ]
     → squad_update：拓扑校验通过 → append NDJSON → 更新内存 DAG → Scheduler.tick()

[3] Scheduler.tick（§5.6）：squad-a1b2 依赖空 → ready
    → resolveMasterBranch → 设为 "main"（用户在 main 起的 coordinator，§8.3）
    → executeOnMaster(git worktree add -b squad-a1b2 ../worktree-squad-a1b2 main)
    → createSymlinks（node_modules/.venv → 主仓库，§5.8 只读）
     → spawnSlave：alacritty -e opencode tui --prompt "<buildSlavePrompt(squad-a1b2)>"
                   env: SQUAD_COORDINATOR_URL/TASK_ID/WORKTREE_PATH/MASTER_BRANCH=main/TOKEN
    → commitEvent(task_started squad-a1b2)
    squad-c3d4 / squad-e5f6 依赖 squad-a1b2 未 merged → 跳过
```

### 9.3 Slave #1（squad-a1b2：改数据库 schema）

```
[4] opencode tui 启动，--prompt 注入首条消息（§6.1）
    → 插件检测 SQUAD_COORDINATOR_URL → Slave 模式
    → 注册 submit_to_squad / query_squad
    → POST /task/squad-a1b2/register { pid }   ← 上报真实 PID（§5.9）

[5] slave LLM 开发：读 schema、写 migration、改 model
    → /loop review → reviewer PASS（§6.6）

[6] git add + git commit → 调用 submit_to_squad（§6.4）
    → POST /task/squad-a1b2/submit  Authorization: Bearer <token>  { commitSha:"abc123" }
    → coordinator executeOnMaster（SerialQueue，§5.7）:
        前置校验 on main ∧ clean ✓
        git merge-base --is-ancestor main squad-a1b2 → yes
        git merge --ff-only squad-a1b2 → main 前进
      → { result:"merged", masterSha:"abc123" }

[7] coordinator 处理 merged（§5.9 时序安全）:
    → task.status = merged
    → 先确保 slave 退出（done beacon / 或 SIGTERM 确认 PID 消失）→ removeWorktree(squad-a1b2)
    → commitEvent(task_merged squad-a1b2, masterSha=abc123)
    → session.prompt 注入正文 "Task merged into master @ abc123. Nothing needs to be done."
    → Scheduler.tick()

[8] Scheduler：squad-c3d4 / squad-e5f6 依赖 squad-a1b2 已 merged → 两者 ready（maxConcurrent=3，2 槽位可用）
     → worktree add -b squad-c3d4 ../worktree-squad-c3d4 main   ← main 已含 schema 变更
    → worktree add -b squad-e5f6 ../worktree-squad-e5f6 main
    → spawnSlave × 2 → commitEvent(task_started × 2)
```

### 9.4 Slave #2/#3 并行 + ff 竞争（§7）

```
[9]  Slave #2(squad-c3d4 中间件) 与 #3(squad-e5f6 前端) 并行；改不同文件，理论不冲突

[10] #2 先完成 → submit → ff 成功 → merged → main 前进 → removeWorktree → Scheduler.tick（无新 ready）

[11] #3 随后 submit
     → coordinator ff 检查：squad-e5f6 基于 #2 合并前的 main → 非后代 → 不能 ff
     → { result:"rebase_needed", masterSha:<#2 合并后> }

[12] #3 rebase（§6.6 / §7.2）:
     → git rebase main        ← 本地 main 已含 #2 改动（共享 .git，非 origin）
     → 无冲突 → 重新 /loop review PASS → 重新 submit → ff 成功 → merged
     → 有冲突 → LLM 解决 → rebase --continue → review → submit

[13] 全部 merged → DAG 完成
     → coordinator LLM 向用户报告："所有任务已完成并合并到 main"
```

### 9.5 Slave 崩溃场景（决策 1.10/2.3）

```
[14] 设 Slave #3 开发中途崩溃（用户关 alacritty 窗 / opencode 进程死）
     → 无 done beacon（崩溃无 dispose 机会）→ coordinator PID 轮询发现 squad-e5f6 的 PID 消失（§5.9）
     → handleSlaveExit(squad-e5f6):
         task.status = done（非 merged，决策 2.3 形式主义）
         removeWorktree(squad-e5f6)（决策 3.2）
         commitEvent(task_done squad-e5f6)
         Scheduler.tick()（无新 ready）
      → DAG 形式完成（所有 task 终态：2 merged + 1 done）
     → coordinator LLM 报告："任务'改前端表单'的 slave 已退出（未合并）。其余已完成。"
```

---

## 10. 错误处理与边界条件

错误处理哲学（铁律）：可预见失败 → 强类型返回分支，逼调用方匹配；不可继续的事故 → 异常。所有 HTTP 响应是 DU 的线序编码（附录 C），slave 工具按 `result` 字段穷尽匹配，不对文案做脆弱正则。

### 10.1 Coordinator 崩溃（决策 1.10）

| 场景 | 行为 | 依据 |
|------|------|------|
| coordinator opencode 进程崩溃 | HTTP server 随进程死 → slave 后续请求 ECONNREFUSED → slave 工具返回 `coordinator_unreachable` 分支 → slave LLM 向用户报错后 idle | 决策 1.10 |
| 用户重启 coordinator | 插件加载 → `replayFromEventLog` 读 `.wanxiangzhen.ndjson` fold + git 二次校正重建 DAG（§5.4）→ 启动新 HTTP server（新随机端口 + 新 token）| 决策 1.3 |
| 重启后旧 slave 仍连旧端口 | 旧 slave 的请求打到死端口 → 失败 → idle。coordinator 不自动重连（§13 明确排除）| 决策 1.10 |
| 用户清理残留 slave | `/squad-kill`（§5.13）杀进程，保留 worktree 供调试 | 决策 3.3 |

> 重启后 DAG 中 `running` 状态 task 的真实性：重放得到的 `running` 仅是事件投影，对应 slave 可能已死。coordinator 重启后对每个 `running` task 尝试 PID 探测（PID 来自 register，但 register 是内存态、重启丢失）→ 无 PID 记录 → 标记为"孤儿 running"，注入提示让用户决定 `/squad-kill` 或忽略。不自动杀（用户可能想保留现场）。

### 10.2 Slave 崩溃 / 退出（决策 1.10/2.3）

| 场景 | 行为 |
|------|------|
| slave opencode 进程崩溃 | coordinator PID 轮询发现 PID 消失（§5.9）→ handleSlaveExit → task=done → 删 worktree → tick |
| 用户关 alacritty 窗口 | 终端进程退出连带 opencode 退出 → PID 消失 → 同上 |
| slave 正常完成后 dispose | done beacon `POST /task/:id/done`（§6.7）→ coordinator 标记 → 不重复 PID 轮询处理 |
| slave git 操作失败（commit/rebase 报错）| slave LLM 看到 git stderr → 自行尝试修复或 idle 等用户。coordinator 不介入（决策 1.6 slave 自治）|
| done beacon 与 PID 轮询竞态 | 两路都可能触发 handleSlaveExit → 用 task.status 幂等保护：已 done/merged 则忽略二次触发（§5.9）|

### 10.3 Rebase 无限循环（决策 2.4）

| 场景 | 行为 |
|------|------|
| slave 反复 rebase 失败 | 无限重试（决策 2.4 无限猴子形式主义）。coordinator 无重试上限逻辑 |
| 用户想中止某 slave | 关该 slave 的 alacritty 窗 → exit → task=done |
| 用户想中止整个 session | `/squad-kill [session_id]` → 杀所有 slave，保留 worktree |

### 10.4 并发上限（决策 1.7）

| 场景 | 行为 |
|------|------|
| ready task 数 > maxConcurrent | 多出的留 `pending`；已 running 的 merged/done 后下次 tick 补位（§5.6）|
| maxConcurrent 运行时被改 | coordinator 重读 AGENTS.md 需重启；MVP 不支持热重载（§13）|

### 10.5 HTTP / 通信失败

| 场景 | 行为 |
|------|------|
| slave submit HTTP 失败（网络/超时）| 工具返回 `coordinator_unreachable` → slave LLM 重试或 idle（决策 1.10）|
| slave query HTTP 失败 | 工具返回错误 → slave 不依赖查询结果继续工作（query 是辅助非必需，§6.5）|
| 请求缺 / 错 bearer token | coordinator 返回 401 → 视为非法请求拒绝（§5.2 安全）|
| 请求体 JSON 解析失败 | coordinator 返回 400 + 错误分支，不崩溃 server |
| task_id 不存在 | `GET /task/:id` → 404 `task_not_found`；`submit` → 404 同 |

### 10.6 Git / Worktree 边界

| 场景 | 行为 |
|------|------|
| coordinator 启动时 detached HEAD | masterBranch 无法解析 → 启动报错要求显式配置（§8.3）|
| worktree 路径已存在（碰撞）| taskId hex4 碰撞极罕见；`git worktree add` 失败 → 重新生成 hex4 重试（§5.8）|
| 删 worktree 时 slave 仍持有文件锁（Windows）| `git worktree remove --force` 重试；失败则记录孤儿 worktree，`/squad-kill` 后用户手动清 |
| ff 前置校验失败（coordinator 不在 masterBranch / 工作区脏）| executeOnMaster 中止本次 ff，返回错误分支，不强行合并（§5.7 诚实失败）|
| sharedDir symlink 目标不存在 | 跳过该 symlink，日志告警，不阻塞 worktree 创建（§5.8）|

### 10.7 SSOT / 重放边界

| 场景 | 行为 |
|------|------|
| context compaction 删除 squad 事件消息 | DAG SSOT = `.wanxiangzhen.ndjson`，与 session 历史解耦（`EventSourcing.md`）。compaction 只影响 LLM 上下文窗口，不影响事件流重放。无需 `.squad/state.json` 备份兜底 |
| 事件 frontmatter 损坏（首行坏）| 重放时该条跳过 + git 二次校正补真相（§5.3）。截断语义同 万象术（坏处止）|
| NDJSON append 失败（锁/磁盘）| 内存 DAG **不**更新；`InjectError` 记录（如 squad_cancelled）；调度不得前进 |
| 可选 session.prompt 注入失败 | `InjectQueue` 重试；**不**影响已落盘 NDJSON 事实 |
| 重放投影与 git 真相冲突 | git 真相优先（§5.3）：branch 已合进 masterBranch 但事件停在 submitted → 校正为 merged |

---

## 11. 插件接口清单（修正：opencode 无 child-exit hook）

### 11.1 Coordinator 模式

| 类型 | 名称 | 说明 | 核实 |
|------|------|------|------|
| Slash Command | `/squad <requirement>` | 触发需求拆解（§5.5）| opencode command registration |
| Slash Command | `/squad-kill [session_id]` | 杀 slave 进程，保留 worktree（§5.13）| 同上 |
| Slash Command | `/squad-status` | 显示当前 DAG 状态文本（formatDag）| opencode command registration |
| Tool | `squad_update` | LLM 提交 DAG 拆解/状态更新（§5.6）| opencode tool definition |
| 插件 init | HTTP server + Scheduler 启动 | `plugin` 内 `listen(0)` + 后台 Scheduler（§5.1）| PluginInput |
| chat.message hook | 捕获 master sessionID | 首条非空 sessionID 存为 `masterSessionId`，触发 replayFromEventLog（§5.4）| PluginInput |
| **PID 轮询**（非 hook） | slave 退出探测 | **opencode 无 child-exit / session-spawn hook（核实 5.9）**。coordinator 用 `setInterval` 轮询 register 上报的 PID（`process.kill(pid,0)` 探活），消失即 handleSlaveExit | 核实 5.9 |
| Event 订阅（可选） | `client.event.subscribe` | 监听 opencode session 事件（如 idle）辅助判断；非 slave 生命周期来源 | PluginInput.client |

> 修正要点：原 PRD §10.1 列 "Hook: child exit 监听 slave 进程退出"。核实 opencode 插件 Hooks **无此 hook**。slave 是 coordinator 经 `child_process.spawn` 起的独立进程，其退出由 coordinator 进程内 `child.on('exit')`（若 spawn 句柄保留）+ PID 轮询双保险探测，不经 opencode hook 系统。done beacon（§6.7）是正常退出的主路径，PID 轮询是崩溃兜底。

### 11.2 Slave 模式

| 类型 | 名称 | 说明 |
|------|------|------|
| Tool | `submit_to_squad` | 提交 work 触发 ff 检查（§6.4）|
| Tool | `query_squad` | 查询 coordinator DAG 状态（§6.5）|
| 插件 init | env 读取 + `POST /register` 上报 PID + 不再注入 prompt（首条 prompt 由 coordinator 经 `tui --prompt` 注入，§6.1）| 修正：slave 插件不自己 prompt 注入 |
| dispose / shutdown hook | done beacon `POST /task/:id/done`（§6.7）| opencode 插件卸载钩子 |

> 修正要点：原 PRD §3.2 / §5.1 说 slave 插件加载时 `prompt()` 注入首条消息。核实 opencode tui 支持 `--prompt` 启动参数（核实 5.6），coordinator spawn 时直接 `opencode tui --prompt "<task prompt>"` 注入更可靠（避免插件 init 与 session 就绪的时序竞态）。slave 插件 init 只做 env 读取 + register + 工具注册。

### 11.3 HTTP API

| Method | Path | 说明 | 鉴权 |
|--------|------|------|------|
| GET | `/task/:id` | 获取 task 详情（§5.2）| Bearer |
| POST | `/task/:id/submit` | 提交 work（ff 检查 + merge，SerialQueue）| Bearer |
| POST | `/task/:id/register` | slave 上报真实 PID（§5.9）| Bearer |
| POST | `/task/:id/done` | done beacon，slave 正常退出通知（§6.7）| Bearer |
| GET | `/state` | DAG 全局状态（§6.5）| Bearer |
| POST | `/task/:id/log` | （可选）slave 进度日志（§13 第三阶段）| Bearer |

全端点绑 `127.0.0.1`，全程 Bearer token 校验（§5.2）。无 WebSocket/SSE——全短连接，slave 发起，coordinator 不主动（决策 1.4）。

---

## 12. 技术约束与实现注意事项

### 12.1 技术栈（核实 5.x）

- 插件 F# + Fable → JS，与 万象术 一致；可复用 万象术 Kernel/Shell 既有模块（§3.x 分层）。
- 宿主 opencode（Bun/Node 运行时）。插件入口 `plugin(ctx: PluginInput)`，`ctx` 提供 `client`（session.prompt/create/command/messages、event.subscribe）、`serverUrl`、`$`（BunShell）、`worktree`、`experimental_workspace`（核实：PluginInput 字段）。
- HTTP server 用宿主运行时内置 `http`（Node compat）或 Bun.serve，无外部依赖。
- Git 操作经 `$`（BunShell）或 `child_process.execSync`，与 万象术 Shell.Executor 一致同步串行。
- 异步原语：全程 `Promise`（Fable 编译 JS 环境开除 Async/Task，核实宝典）。SerialQueue 复用 万象术 `Shell.PromiseQueue.SerialQueue`（§5.7）。

### 12.2 opencode 插件系统集成（核实）

| 能力 | opencode API | 核实 |
|------|-------------|------|
| 注入消息到 coordinator 自己 session | `client.session.prompt({ sessionId, parts })` | 核实：session.prompt |
| 程序触发 slash command | `client.session.command` | 核实 5.6：可程序化触发 |
| 读全量历史重放 | `client.session.messages(sessionId)` 返回全量（含 compacted 标记）| 核实 5.4（仅 LLM 展示用，DAG 重放走 NDJSON）|
| 监听 session 事件 | `client.event.subscribe()` | 核实：PluginInput.client |
| slave 首条 prompt 注入 | `opencode tui --prompt "<text>"` 启动参数 | 核实 5.6 |
| 工具 / command 注册 | 插件 registration 返回 tools/commands | PluginInput |

> 无 child-exit / session-spawn hook（核实 5.9）：slave 生命周期不靠 hook，靠 spawn 句柄 + PID 轮询 + done beacon。

### 12.3 SerialQueue 复用（§5.7）

直接复用同构 `Shell.SerialQueue`（`src/Shell/SerialQueue.fs`）：局部可变 `tail` 锁队尾，内部 catch 防断链，异步变更强制排队 → 无锁保护 masterBranch git 操作原子性。ff 检查 + 合并作为单个 Enqueue 单元（§7.3 不变式）。NDJSON append 经 `SquadEventLogStore` 内独立 SerialQueue + 文件锁双重串行。

### 12.4 compaction 与 DAG 持久化

- DAG 事实只 append `.wanxiangzhen.ndjson`（`PRD/EventSourcing.md`）。
- 宿主 compaction **不影响** NDJSON；**禁止**为 DAG 做 compaction 补锚点或依赖 session 历史重放。
- 对话历史可膨胀，仅作 LLM 上下文；与协调状态解耦。

### 12.5 worktree 与共享 .git（核实 5.8）

- opencode worktree（`git worktree add`）与主仓库共享同一 `.git` gitdir。slave 在 worktree 内 `git log {masterBranch}` 直接见 coordinator 刚 ff 的提交，**无 origin、无网络往返**（§5.8/§6.6/§7）。
- 推论：slave rebase 目标是本地 `{masterBranch}` ref，非 `origin/master`（修正原 PRD §5.2/§5.6 的 `git rebase origin/master`）。
- 共享 .git 风险：slave 不得 `git checkout {masterBranch}`（会占用主仓库分支检出，worktree 模型禁止同分支双检出）；slave 不得动 masterBranch ref。乐观提示词约束（§6.8）。

### 12.6 跨平台

- Symlink（§5.8 sharedDirs）：Linux/macOS 原生；Windows 需 Developer Mode 或管理员权限，否则降级为跳过 symlink + 告警（slave 自行 install 依赖，慢但可用）。
- 终端模拟器：各平台命令行参数不同（§5.10 映射表）；探测失败降级 headless。
- Git worktree：全平台支持；Windows 路径长度限制（260）注意 `项目/../worktree-{hex4}` 不要过深。
- PID 探活：`process.kill(pid, 0)` POSIX/Windows 都支持（信号 0 仅探活不杀）。

---

## 13. 不做的事情（明确排除）

每条排除都对应一条第一性原理（§2），删一个功能就是省一份偶然复杂度。

| 不做 | 原因 | 对应原理 |
|------|------|----------|
| Slave 嵌套 spawn 子 slave | 避免 worktree 嵌套地狱、递归 PID 树、端口转发链。slave 只干活不调度（决策 1.6）| 公理 3 单层调度 |
| Coordinator 端自动解决合并冲突 | 冲突由持有上下文的 slave LLM 在自己 worktree 解决；coordinator 只做无脑 ff（决策 1.1）| 公理 4 ff-only |
| 非 ff 合并（产生 merge commit）| 违反线性历史；merge commit 会让 DAG 真相退化成图而非链（决策 1.1）| 公理 4 |
| Coordinator 主动推消息给 slave | 违反"slave 发起短连接、coordinator 不主动"；省掉 coordinator 持有 slave 连接的状态（决策 1.4）| 公理 5 单向通信 |
| Coordinator 崩溃后自动重连旧 slave | 旧 slave 连旧端口，重连需端口持久化 + 握手协议，复杂度高收益低。用户 `/squad-kill` 重来（决策 1.10）| 公理 6 诚实降级 |
| 内置 review | 依赖 万象术 `/loop`，两插件 prompt 层协同，互不 import（决策 1.9、§6.6）| 公理 1 关注点分离 |
| 以 session 历史 fold DAG | SSOT 已改为 `.wanxiangzhen.ndjson`（§2.2）| 公理 2 |
| DAG 可视化 TUI / Web dashboard | 后续可加，不阻塞 MVP；状态可经 `/state` 或 `query_squad` 文本查询 | MVP 聚焦 |
| 多 coordinator 协作 | 单 coordinator 足够；多 coordinator 需分布式锁协调 masterBranch，违反 SerialQueue 单点原子（决策隐含）| 公理 2 |
| maxConcurrent 热重载（MVP）| 改 AGENTS.md 后重启 coordinator 生效；省掉配置 watcher（§10.4）| MVP 聚焦 |
| rebase 重试上限 | 无限猴子形式主义完成（决策 2.4）；上限会引入"半完成"中间态破坏 done 形式保证 | 决策 2.3/2.4 |
| 技术强制 slave git 隔离 | slave git 约束是 prompt 层乐观约束，非 hook 拦截；技术强制需包裹 git 二进制，过重（决策 1.6、§6.8）| 公理 6 乐观约束 |

---

## 14. MVP 范围与分期

### 14.1 第一阶段（MVP — 最小可跑闭环）

目标：单 万象阵 Session，能拆解 → 起 slave → ff 合并 → 清理，崩溃不卡死 DAG。

- [x] Coordinator 模式探测（无 `SQUAD_COORDINATOR_URL`）+ `plugin` 内 `listen(0)` 起 HTTP server + 生成 bearer token（§5.1/§5.2）
- [x] master session 捕获（记录 coordinator 自身 sessionId 供注入与重放，§5.3）
- [x] `/squad <requirement>` command + `prompt()` 注入拆解指令（§5.5）
- [x] `squad_update` 工具（events 数组 + 拓扑校验防环，§5.6）
- [x] DAG Scheduler（`tick()` + re-entrance guard + 拓扑就绪判定 + lazy worktree creation，§5.6）
- [x] Git Executor（SerialQueue 包裹 + masterBranch 启动解析 + ff 前置校验 + `merge-base --is-ancestor` + `merge --ff-only`，§5.7）
- [x] Worktree 创建（`git worktree add -b {taskId} {项目/../worktree-hex4} {masterBranch}` + 碰撞重试，§5.8）+ merged/done 删除（§5.8）
- [x] sharedDirs symlink（§5.8）
- [x] Slave spawn（alacritty + `opencode tui --prompt "<task prompt>"` 注入首条，§5.9/§5.10）+ env 注入（§4.x）
- [x] Slave 退出探测：done beacon `POST /task/:id/done`（正常路径）+ PID 轮询（崩溃兜底）双保险，幂等（§5.9/§6.7）
- [x] Slave 模式探测 + `POST /register` 上报 PID + `submit_to_squad`（本地 masterBranch rebase 分支，§6.4）
- [x] `/squad-kill [session_id]`（杀进程保留现场，§5.13）
- [x] `query_squad` 工具（slave 查全局 DAG，§6.5）
- [x] 事件投影：8 类事件 NDJSON append（`commitEvent`）+ 可选 frontmatter `session.prompt` 注入（§5.11/附录 D）
- [x] 重启重放：`.wanxiangzhen.ndjson` fold + git 二次校正（§5.4 / `EventSourcing.md`）

### 14.2 第二阶段（健壮性与协同）

- [x] 多终端支持（kitty / gnome-terminal / iTerm2 / Windows Terminal / konsole / wezterm，§5.10）
- [x] Headless 降级（无终端时后台 spawn，§5.10）
- [x] slave prompt 已固定按 /loop 可用构造（§6.2），不再运行时检测
- [x] 孤儿 running 探测（重启后无 PID 记录的 running task 提示用户，§10.1）
- [x] bearer token 全端点校验加固 + 请求体 schema 校验（§5.2/§10.5）

### 14.3 第三阶段（可观测与扩展）

- [ ] 进度日志 `POST /task/:id/log`（§5.2 可选端点，端点已实现，语义待扩展）
- [ ] DAG 可视化（TUI 或 Web dashboard）
- [ ] 多 万象阵 Session 并行（多 DAG 隔离调度）
- [ ] maxConcurrent 热重载（AGENTS.md watcher）

---

## 附录 A：术语对照表

| 术语 | 定义 |
|------|------|
| Coordinator | 用户交互的 master opencode 进程；NDJSON 唯一写入入口；SSOT = `.wanxiangzhen.ndjson` |
| Slave | coordinator 经 `child_process.spawn` 起的独立 opencode 进程，有 `SQUAD_COORDINATOR_URL`，在隔离 worktree 中工作 |
| DAG | 有向无环图，描述 task 间依赖；`squad_update` 拆解产物，coordinator 校验无环 |
| Task | DAG 节点，一个可独立执行的工作单元；状态机 pending→running→submitted→merged→done（§2.2.2）|
| Worktree | `git worktree add` 隔离工作目录，与主仓库共享 `.git` gitdir（核实 5.8）|
| FF (Fast-Forward) | `git merge --ff-only`，masterBranch 仅线性前进，永不产生 merge commit |
| Rebase | slave 被 ff 拒绝后 `git rebase {masterBranch}`（本地 ref，非 origin）变基到最新 master |
| masterBranch | coordinator 启动时所在分支（非硬编码 "master"），AGENTS.md 可显式配置（§8.3）|
| 万象阵 Session | 一次 `/squad` 创建的执行上下文，含一个 DAG |
| SSOT | Single Source of Truth；此处 = `.wanxiangzhen.ndjson`；git refs 作第二真理源。|
| SerialQueue | 万象术 `Shell.PromiseQueue.SerialQueue`，串行锁队尾保 masterBranch git 操作原子（§5.7）|
| Lazy Worktree | 延迟创建 worktree——依赖全 merged 后才从最新 masterBranch fork（决策 2.6）|
| done beacon | slave 正常退出时经 dispose hook 发 `POST /task/:id/done`，coordinator 标记 task done 的主路径（§6.7）|
| PID 轮询 | coordinator 兜底探测 slave 崩溃：定时 `process.kill(pid,0)` 探活 register 上报的 PID（§5.9，因 opencode 无 child-exit hook）|
| 形式主义完成 | DAG 所有 task 终会到 done（merged 或 slave 退出），不保证内容真完成（决策 2.3）|

## 附录 B：与 万象术 的对照

| 维度 | 万象术 | 万象阵 |
|------|---------|-------|
| 架构 | Kernel(纯函数) + Shell(副作用) + 多 Host | Coordinator + Slave + HTTP，复用 万象术 Kernel/Shell |
| SSOT | master session 对话历史（frontmatter 锚点）| `.wanxiangzhen.ndjson`（`PRD/EventSourcing.md`）|
| 事件格式 | yaml frontmatter + prose | yaml frontmatter + prose（同构）|
| 重放 | `inferReviewTaskFromTexts` 扫 frontmatter 标量 | `replayFromEventLog` 读 `.wanxiangzhen.ndjson` fold + git 二次校正（§5.4 / `EventSourcing.md`）|
| frontmatter 解析 | Kernel 只认标量（核实）| Shell 层全量 yaml（含数组 dependsOn，需 yaml 包，§8.4）|
| 串行化 | `SerialQueue` 保护单进程关键路径 | `SerialQueue` 保护 masterBranch git 操作（复用）|
| Review | 内置 submit_review + reviewer loop | 不内置，依赖 万象术 `/loop`（决策 1.9）|
| Worktree | 无 | `git worktree` 隔离 + 共享 .git（核实 5.8）|
| 多进程 | 单进程内多 subagent | 多 opencode 进程 + HTTP 短连接 |
| 进程生命周期 | 进程内 Promise / abort | spawn 句柄 + PID 轮询 + done beacon（无 child-exit hook，核实 5.9）|
| 首条 prompt 注入 | 进程内 session.prompt | `opencode tui --prompt` 启动参数（核实 5.6）|
| 并发控制 | `AgentSemaphore` 优先级槽位 | `maxConcurrent` 配置 + Scheduler 就绪计数（§5.6）|
| 崩溃恢复 | 重启重放对话历史 | 重启重放 + git 真相校正（同构强化）|

## 附录 C：HTTP API 速查与 DU 编码

### C.1 端点总表

全端点：`127.0.0.1` 绑定，`Authorization: Bearer <token>` 必需，短连接，slave 发起。

```
GET  /task/:id
  200  { id, title, description, dependsOn[], status }
  401  { result: "unauthorized" }
  404  { result: "task_not_found" }

POST /task/:id/submit
  body { commitSha: string }            # branch 由 coordinator 从 taskId 映射，slave 不传
  # 领域结果全 200 + 单一 result 判别（§5.2）；仅传输/协议错误用 4xx
  200  { result: "merged",                masterSha: string }
  200  { result: "rebase_needed",         masterSha: string }
  200  { result: "stale_commit" }                                      # 上报 sha 非 worktree HEAD
  200  { result: "coordinator_not_ready", reason: "not_on_master"|"dirty" }
  200  { result: "not_submittable",       currentStatus: string } # task 非 running（重复 submit / 已终态）
  401  { result: "unauthorized" }
  404  { result: "task_not_found" }

POST /task/:id/register
  body { pid: number }
  200  { result: "registered" }
  401 / 404 同上

POST /task/:id/done                      # done beacon
  body { }                               # 退出意图，内容无关（决策 2.3）
  200  { result: "acknowledged" }
  401 / 404 同上

GET  /state
  200  { sessions: [{ sessionId, tasks: [{ id, title, status, dependsOn, slavePid }] }] }
  401  { result: "unauthorized" }

POST /task/:id/log                       # 可选，第三阶段
  body { message: string }
  200  { result: "logged" }
```

### C.2 submit 响应 DU（强类型边界，核实宝典）

submit 响应是判别联合的线序编码，slave `submit_to_squad` 按 `result` 字段穷尽匹配，**不对 message 文案做正则**（宝典：可见失败 → 返回类型具体分支，逼调用方匹配）：

```
SubmitResult =                                           # 全部 HTTP 200，单一 result 标签判别（无 ok 字段）
  | Merged           of masterSha: string                # ff 成功，task 完成
  | RebaseNeeded     of masterSha: string                # masterBranch 已前进，需本地 rebase
  | StaleCommit                                           # commitSha ≠ worktree HEAD，slave 先 commit
  | CoordinatorNotReady of reason: string                 # coordinator 不在 masterBranch / 工作区脏，稍后重试
  | NotSubmittable   of currentStatus: string             # task 状态非 running（重复 submit / 已 done/cancelled）
```

slave 匹配逻辑（§6.4）：

```
match result with
| Merged _            → "✅ Merged. Task complete. You can stop now."（停止工作循环）
| RebaseNeeded _      → git rebase {masterBranch} → 重 /loop → 重 submit（决策 2.2 do-while）
| StaleCommit         → git commit 后重 submit
| CoordinatorNotReady _→ 稍候重试 submit（不改代码，coordinator 主仓库瞬时不可合并）
| NotSubmittable _    → 向用户报告异常，idle
```

传输层轴（错误模型全封闭，§6.4）：上述 5 分支是 HTTP 200 body 的判别联合，前提是请求到达 coordinator 并拿到响应。请求本身可能失败，slave 工具须在 body 解析之前先处理传输层结果，否则错误会在深处嵌套爆炸：

```
SubmitOutcome =                              # slave 工具的完整结果空间
  | Response of SubmitResult                 # HTTP 200，进上面 5 分支匹配
  | TaskNotFound                             # HTTP 404：coordinator 在但不识此 task（多半 coordinator 重启丢状态）→ 报用户、idle
  | CoordinatorUnreachable                   # ECONNREFUSED / 超时：coordinator 崩溃或端口变更 → 报用户、idle（§10.1/§10.5）
  | LocalGitError of message: string         # 本地 git 操作失败（如 rev-parse 报错）→ 报用户、修复后重试
```

`CoordinatorUnreachable` 不是 coordinator 下发的 `result` 值，是 slave 对传输失败的本地分类——故不在 SubmitResult DU 内，单列一轴。slave 先判传输层（成功才有 body），再判 body `result`，两层穷尽匹配，无裸 try-catch 吞错。

### C.3 不变式

- submit 的 ff 检查 + 合并是 SerialQueue 内单个 Enqueue 单元，两 slave 的 submit 不交叉（§7.3）。
- 全响应同步短连接，无 WebSocket/SSE（决策 1.4）。
- coordinator 永不主动连 slave；slave 状态查询全靠主动发起（公理 5）。

## 附录 D：事件 Schema（NDJSON 见 `EventSourcing.md`；frontmatter 仅可选展示）

### D.1 八类事件（`task_error` 含）

| squad_event | 触发者 | 载荷字段 | 写入路径 |
|-------------|--------|----------|----------|
| `squad_created` | `/squad` command handler | session_id, requirement | 路径 A（LLM 前）|
| `tasks_created` | `squad_update` execute | session_id, tasks[{task_id,title,description,depends_on[]}] | 路径 A（LLM 驱动）|
| `task_started` | Scheduler.tick | session_id, task_id, worktree_path, branch_name | 路径 B（后台）|
| `task_submitted` | `POST /submit` 进入 ff 前 | session_id, task_id, commit_sha | 路径 B |
| `task_merged` | ff 成功后 | session_id, task_id, master_sha | 路径 B |
| `task_done` | done beacon / PID 轮询 | session_id, task_id, merged: bool | 路径 B |
| `task_error` | worktree/git 操作失败 | session_id, task_id, error | 路径 B（后台）|
| `squad_cancelled` | `/squad-kill` | session_id（可空=全部）| 路径 A/B |

> 注：原 PRD §2.2.4 列 `task_rebased`。修正：rebase 发生在 slave 本地（§6.4），coordinator 不感知 slave 的 rebase 过程，故无 `task_rebased` 事件。slave rebase 后重新 submit，coordinator 只见再次 `task_submitted`。

### D.2 frontmatter 编码规范

每个事件 = master session 一条 user message。结构：yaml frontmatter（程序精确识别）+ prose 正文（LLM 友好，告知是否需配合，决策 2.1）。

```yaml
---
squad_event: tasks_created
session_id: squad-session-001
tasks:
  - task_id: squad-a1b2
    title: "修改认证中间件"
    depends_on: ["squad-x9y8"]
  - task_id: squad-c3d4
    title: "改前端表单"
    depends_on: ["squad-a1b2"]
---

Task batch created in squad session squad-session-001.
Nothing needs to be done. The scheduler will start each task once dependencies are merged.
```

一条消息可含多块 `---` frontmatter，`EventCodec.decodeEvents` 一次解码全部事件块。

编码纪律：

- 标量字段（session_id / task_id / title / master_sha / status）平铺 frontmatter 顶层，重放只读 scalar，不做全文 NLP（同 万象术 front-matter 锚点）。
- 数组字段（depends_on）用 yaml 序列。**核实 5.x：万象术 Kernel frontmatter 解析器只认标量不认数组**——故 万象阵 重放须走 Shell 层全量 yaml 解析（依赖已含 yaml 包），不能复用 Kernel 轻量标量解析器（§8.4）。
- description 可能多行 → 用 yaml `|` 块标量或移出 frontmatter 放正文（重放时 description 非状态机必需字段，task 详情以 `GET /task/:id` 内存态为准，重放只需 id/title/depends_on/status 链）。
- 正文必含一句"是否需 LLM 配合"：后台事件写 `Nothing needs to be done.`（决策 2.1），让 LLM 看到自然不动作；需 LLM 解释进度时写 `You may summarize progress to the user.`

### D.3 重放投影规则（状态机折叠）

```
replayFromEventLog(rt):
  events = ReadAllSquadEvents(projectRoot)              # 读 .wanxiangzhen.ndjson（EventSourcing.md）
  for ev in events:                                     # 全局行序 fold
    match ev:
      SquadCreated(sid, req) → archive prior dag → sessions; dag = empty(sid, req)
      TasksCreated(_, tasks) → for t in tasks: dag = addTask(dag, create(t))
      TaskStarted(_, tid, wt, branch) → dag[tid].status = running; worktree/branch 记录
      TaskSubmitted(_, tid, sha)       → dag[tid].status = submitted
      TaskMerged(_, tid, sha)          → dag[tid].status = merged; mergedSha = sha
      TaskDone(_, tid, merged)         → dag[tid].status = done
      TaskError(_, tid, err)           → dag 不变（错误注入，不改状态）
      SquadCancelled(sid)              → session 内未终态 task = cancelled
  gitReconcile(dag)                                     # §5.4 git 二次校正：branch 已并入 masterBranch
                                                         #   但事件停在 submitted → 校正 merged；
                                                         #   cancelled task 跳过（见下）
  return dag, sessions
```

校正优先级：git 物理真相 > 事件投影（公理 2 历史是事实，但 git ref 是更硬的事实——事件 append 可能因文件锁等待，git 操作已落盘）。

例外：`cancelled` task 不受 gitReconcile 覆盖。`/squad-kill` 是用户刻意保留现场的显式意图（§5.13），其 worktree+branch 被有意保留。若 gitReconcile 因 branch 偶然是 masterBranch 祖先就把它校正为 `merged`、或因别的推断改成 `done` 触发清理，会摧毁用户要保的调试现场。故 cancelled 是不可降级的终态——用户显式中止的权威性高于 git 物理状态推断（公理 6 诚实降级）。这与 §3.4「`merged`/`done` 清理、`cancelled` 不清理」、§5.13 幂等 return 三处对齐。
