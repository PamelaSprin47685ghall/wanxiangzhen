# DEV_TALK.md — 万象阵 设计决策历程

本文档记录 multi-agent opencode coordinator（万象阵）插件设计过程中所有用户做出的选择。按讨论轮次排列，每轮列出问题和用户的决策。

---

## 轮次 0：初始架构提案

用户提出完整架构蓝图，包含以下核心设计：

1. 正常启动（不带环境变量）的 opencode = coordinator（master），安装某插件，启动时监听随机 HTTP 端口
2. 过程中可启动若干 slave opencode，都安装同一插件，连接到 coordinator
3. coordinator 端口号用环境变量传入 slave
4. master 是唯一真理源（SSOT）
5. 事件驱动，拿不准的次次向 master 查询，需要串行的操作由 coordinator 代办
6. coordinator 启动 slave 时自动用 alacritty 开新终端启动
7. 主要目的：支持 DAG 式多任务，用户在 coordinator 中提需求，自动被拆解
8. DAG 式执行，每个小需求自动创建 worktree 和分支
9. 干完活以后 commit 并调用工具通知 coordinator
10. coordinator 在 master 分支上只允许 ff，不解决冲突
11. 如果无法 ff（基于旧版），原子地拒绝并要求 slave rebase 到最新 master 再次提交
12. 保证线性历史
13. 允许各 worktree 共享某些目录（如 node_modules），用户可在 AGENTS.md frontmatter 配置
14. 本插件和 万象术 插件相互独立配合
15. 本插件不提供 review 功能，利用同时安装后的 /loop 功能来 review
16. 基本流程：拆解 → worktree → /loop 制定计划 → /loop 开发 → while (不能 ff) rebase → 被合并后清理

---

## 轮次 1：12 个细化决策

用户对 12 个设计问题逐一做出选择：

### 决策 1.1：slave 提交机制

> **问题**：slave 的提交如何触发 ff 检查？slave 如何得知被拒绝？

**用户选择**：slave 的提交本质是一个 slave 端的工具调用（`submit_to_squad`）。工具调用触发 master 对 ff 的检查。如果拒绝，slave 的这次工具调用返回 "Please update..." 的结果。这样 slave 就会 rebase，其中有冲突 LLM 解决，再最终二次调用提交工具。

### 决策 1.2：DAG 拆解执行者

> **问题**：DAG 拆解由谁做？拆解失败怎么办？

**用户选择**：DAG 拆解由 coordinator 自己 LLM 拆。拆解产物结构之后再定。拆解的结果是 0-N 个小需求。拆解不会失败——顶多就是 LLM 没有调用工具返回拆解结果，我们可以 nudge 它。

### 决策 1.3：SSOT 存储方式

> **问题**：SSOT 存在哪里？slave 如何查询？

**用户选择**：SSOT 巧妙地编码进 master 的主 session 对话历史里面（yaml frontmatter 格式），重启重放即可。slave 查询的 API 之后再定。

### 决策 1.4：通信模型

> **问题**：coordinator 和 slave 如何通信？长连接还是短连接？谁主动？

**用户选择**：事件驱动，所有 HTTP 请求都是 slave 发起，coordinator 永远不主动，全都是短连接。slave 不持有 SSOT 因此这是可以的，每当需要状态就请求。

### 决策 1.5：coordinator 定位

> **问题**：coordinator 是纯后台进程还是也有对话？

**用户选择**：coordinator 是 opencode 插件，但后台逻辑和对话无关，只是借用宿主进程跑罢了。用户可以直接对话提 "/squad 需求"，这时需求会被拆解走流程。如果不加 /squad 就是个普通 opencode。

### 决策 1.6：slave 能力边界

> **问题**：slave 能否再 spawn 子 slave？slave 的 git 权限？

**用户选择**：slave 不能再 spawn。slave 只允许 worktree 内部的 git（提示词乐观约束），可以 commit + rebase，但不准动 master。

### 决策 1.7：并发控制

> **问题**：并发上限？

**用户选择**：并发上限可配置。

### 决策 1.8：共享目录语义

> **问题**：共享目录是只读还是读写？

**用户选择**：symlink 只读共享。

### 决策 1.9：环境变量注入方式

> **问题**：环境变量如何传入 slave？

**用户选择**：使用环境变量，由插件直接启动时 prompt() 注入。在有 万象术 时按 /loop 的输出来格式化，就会具有 /loop 效果。如果没有就降级成不格式化，不 review。

### 决策 1.10：slave 崩溃处理

> **问题**：slave 崩溃如何处理？master 崩溃如何处理？Esc 传播？

**用户选择**：slave 崩溃视为 DAG 对应节点正常完成。master 崩溃后导致 slave 请求失败，此时 slave 向用户报错后 idle。用户可以 /squad-kill 来杀树，一般的 Esc 不传播。

### 决策 1.11：终端配置

> **问题**：用什么终端？

**用户选择**：用什么终端可在 AGENTS.md frontmatter 配置。

### 决策 1.12：worktree 路径

> **问题**：worktree 创建在哪里？

**用户选择**：在 `项目/../worktree-hex4` 目录。

---

## 轮次 2：6 个深入决策

### 决策 2.1：事件消息格式与 LLM 交互

> **问题**：后台事件写入对话历史后，如何处理 LLM 的回复？prompt() 会触发 LLM 回复，这是否预期？

**用户选择**：直接作为用户消息 prompt() 进去可以触发 LLM 回复，只是在 frontmatter 后的正文里说 "LLM nothing need do" 就行。或者 LLM 可以顺便帮忙向用户解释当前状态。

**关键澄清**：
- DAG 后台自动推进，不依赖 LLM
- 事件以增量（事件流）方式写入对话历史
- LLM 收到事件后可以帮忙向用户解释状态，但不参与后台调度决策

### 决策 2.2：review 与 submit 的嵌套关系

> **问题**：/loop review 和 submit_to_squad 的关系？

**用户选择**：`do { /loop (开发 or rebase) } while (不能 ff)`。内层 review，外层 submit。即每次 submit 被 ff 拒绝后 rebase，rebase 后重新走 /loop review（因为 rebase 可能改变了代码），再 submit。

### 决策 2.3：done 的语义

> **问题**：task "done" 是什么含义？是否要求实际完成？

**用户选择**：done 只是阶段形式主义，不管内容。形式上总会把 DAG 跑完，哪怕内容上某些提交没有也没事。

### 决策 2.4：重试上限

> **问题**：rebase + submit 的循环有上限吗？

**用户选择**：无限。无限猴子的形式主义完成总是可以的。

### 决策 2.5：prompt() 注入的对象

> **问题**：prompt() 注入到 coordinator 自己的 LLM 还是别的地方？

**用户选择**：前者（coordinator 自己的 LLM）。

### 决策 2.6：DAG 依赖语义

> **问题**：DAG 的依赖关系具体含义是什么？是前一个 task 完成后就启动下一个，还是前一个 merged 进 master 后？

**用户选择**：前者——只有前一个进了 master（merged）后，后一个 worktree 才能基于新的 master fork 出来。这是 lazy worktree creation，确保后续 task 基于最新 master 代码。

---

## 轮次 3：3 个收尾决策

### 决策 3.1：并行竞争

> **问题**：两个无依赖的 task 并行运行，后到者 ff 失败怎么办？

**用户选择**：并行竞争是对的。后到者被拒绝 → rebase → 重新 submit。这是自然的并发控制机制。

### 决策 3.2：worktree/分支清理时机

> **问题**：task merged 后 worktree 和分支何时删除？done 后呢？

**用户选择**：merged 就删除，done 也就删除。

### 决策 3.3：/squad-kill 行为

> **问题**：/squad-kill 是否删除 worktree？

**用户选择**：此前已明确（决策 1.10），此处细化：/squad-kill 只杀进程，保留现场供调试。

---

## 轮次 4：最终确认

### 决策 4.1：实现细节延后

> **问题**：某些实现细节（如终端命令映射、worktree hex 生成算法）？

**用户选择**：此类细节写代码时候再说。

### 决策 4.2：确认并进入 PRD

用户确认所有决策点已锁定，要求撰写保姆式超级详细的 PRD.md。

---

## 轮次 5：源码核实与技术修正

撰写"事无巨细"PRD 前，对照 opencode 本体（`packages/opencode`、`packages/plugin`、`packages/sdk`）与 万象术（`src/Kernel`、`src/Shell`、`src/Opencode`）实际源码逐条核实 PRD 的技术假设。发现多处假设与真实 API 不符，逐一修正。本轮所有结论均有源码出处。

### 核实 5.1：opencode 插件入口与 PluginInput 真实能力

> **核实点**：插件能拿到什么？是否需要自起 HTTP server？

源码 `packages/plugin/src/index.ts` 的 `PluginInput`：

```
type PluginInput = {
  client: ReturnType<typeof createOpencodeClient>  // 含 session.* / event.subscribe
  project: Project
  directory: string
  worktree: string                                 // 当前 worktree 路径
  experimental_workspace: { register(type, adapter): void }
  serverUrl: URL                                   // opencode 自身 HTTP server
  $: BunShell                                      // Bun shell，可跑 git
}
type Plugin = (input: PluginInput, options?: PluginOptions) => Promise<Hooks>
```

万象术 入口 `src/Opencode/Plugin.fs` 证实：`pluginFor (host) (ctx) : Promise<obj>` 返回 hook 字典，`ctx.client` 即 SDK client。

**结论**：
- coordinator **仍需自起独立 HTTP server**（Node `http.createServer` + `listen(0)`）。opencode 的 `serverUrl` 是 Effect 架构的内部 server，无法挂自定义路由给 slave 调用。万象阵 的 server 与 opencode server 是两个独立 server，各司其职。
- 插件经 `$: BunShell` 或 Node `child_process` 跑 git，二者皆可。万象阵 用 `child_process.execSync`（与 万象术 `Shell.Executor` 一致，可控、同步、易测）。

### 核实 5.2：没有 child-exit / session-spawn hook（重大修正）

> **核实点**：PRD §10.1 假设有 "child exit" hook 监听 slave 退出。

源码 `packages/plugin/src/index.ts` 的 `Hooks` 接口穷举所有 hook：`dispose / event / config / tool / auth / provider / chat.message / chat.params / chat.headers / permission.ask / command.execute.before / tool.execute.before / shell.env / tool.execute.after / experimental.chat.messages.transform / experimental.chat.system.transform / experimental.provider.small_model / experimental.session.compacting / experimental.compaction.autocontinue / experimental.text.complete / tool.definition`。

**无任何进程生命周期 hook**。

**修正**：slave 退出探测改为用户选定的 **B + PID 退出**方案：
- coordinator spawn 终端后，slave 插件启动时 `POST /task/:id/register { pid }` 上报**自己的 opencode 进程 PID**（非终端 PID）。
- coordinator 起 PID 健康轮询（`process.kill(pid, 0)` 探测存活，ESRCH=已死），死亡即判定 task 退出。
- 这绕开了"守护进程型终端（gnome-terminal/konsole）下 spawn 子进程立即返回"的致命缺陷——不依赖终端子进程的 exit 事件。
- 兜底：slave 正常完成时主动 `POST /task/:id/done`（done beacon）加速感知；崩溃则靠 PID 轮询兜底。

### 核实 5.3：LLM 不能自己触发 slash command，但 `session.command` 可程序化触发

> **核实点**：PRD §5.4 假设 slave LLM "调用 /loop"。

源码 `packages/opencode/src/cli/cmd/run.ts:776` 证实 `client.session.command({ sessionID, command, arguments })` 可程序化触发 slash command。但 slash command 本质是用户输入路径（`command.execute.before` hook 拦截），**LLM 在对话中无法自己打 `/loop`**。

万象术 `src/Opencode/CommandHooks.fs` 证实 `/loop` 的实现：拦截命令 → `reviewStore.activateReview` → 注入 `buildLoopMessage` 文本。而 `src/Opencode/MessageTransform.fs` + `src/Kernel/LoopMessages.fs:inferReviewTaskFromTexts` 证实：**任何带 `task:` frontmatter 锚点的消息**都会在 `messages.transform` 重放时被识别为 With-Review 激活——不依赖 slash command 本身。

**修正**：slave 进入 /loop 的两条可行路径，PRD 采用前者为主：
- **路径主**：coordinator 构造 slave 初始 prompt 时，若检测到 万象术，直接把任务包成 万象术 的 `/loop` 输出格式（`task:` frontmatter + loopFooter），经 `opencode tui --prompt` 注入。slave LLM 自然进入 With-Review Mode，完成后调 `submit_review`。
- **路径备**：slave 插件在启动后用 `client.session.command({ command: "loop", arguments: task })` 程序化触发。
- 两条都只是 prompt 层/命令层协同，不 import 万象术，符合决策 1.14/1.15。

### 核实 5.4：SSOT 真相——compaction 不删存储，但 transform hook 只见切片（回应决策 1.3 的调查指令）

> **调查指令**（用户）：compaction 会压缩但历史消息可能不丢，只是不再发送。调查 transform 钩子触发时机，是否能拿到完整历史，否则 万象术 本身都有丢状态风险。

逐源核实：
- `packages/opencode/src/session/compaction.ts`：compaction **不删除**存储中的消息。它新建一条 summary assistant 消息 + 一个 `compaction` part 标记切点。`prune`（`compaction.ts:253`）只把**旧 tool part 的 output** 标记 `compacted` 截断，**从不动 user 消息文本**。
- `packages/opencode/src/session/message-v2.ts:532 filterCompacted`：构造发给模型的视图时，从最近 compaction 切点之后取消息（重排为 `[compaction-user, summary, ...tail..., continue]`）。
- `packages/opencode/src/session/prompt.ts:1145`：`msgs = filterCompactedEffect(sessionID)` —— **transform hook（`prompt.ts:1325`）拿到的 `msgs` 是 filterCompacted 后的切片，不是全量**。compaction 路径（`compaction.ts:360`）传的是 `selected.head`（被摘要的头部），更非全量。
- `packages/opencode/src/session/session.ts:857 messages()`：分页拉取**全部存储消息**（`MessageV2.page` 循环到 `!more`），返回全量历史。

**结论**（双重事实）：
1. **万象术 确实有丢状态风险**：它的 review 重放依赖 `messages.transform` 的切片（`IfStoreEmpty` 策略），若 `/loop` 激活消息落在 compaction 切点之前，重启后 transform 切片里看不到，review 状态丢失。但 万象术 的 review 是短生命周期（单任务内），切点跨越概率低，实践影响小。
2. **万象阵 的 DAG 是长生命周期**（跨多任务、贯穿整个 session），compaction 跨越概率高。**因此 万象阵 不能复用 transform-slice 路径重放 DAG**。

**修正方案**（决策 1.3 的落地，不引入 NDJSON）：
- coordinator 重放 DAG **主动调 `client.session.messages({ sessionID })` 拉全量存储历史**，而非依赖 transform 切片。compaction 不删存储，故全量历史里 DAG 事件消息永久存在。
- 内存 DAG 为工作态（live projection），每次状态变更经 `session.prompt` 注入事件消息（durable），重启从全量历史折叠重建。
- **git 作为合并事实的第二真理源**：`task_merged` 的最终判据是 master 是否含该 branch 的提交（`git merge-base --is-ancestor`），即使事件消息丢失也能从 git 反查。这是"信息完整性的最基本尊重"——合并是不可逆事实，落在 git refs 里。
- 残余风险：用户手动删除 session 或清空历史 → DAG 丢失。等价于 万象术 的同类降级，可接受。

**用户选择（已废止）**：曾选对话历史为 SSOT + `session.messages()` 全量重放。

**轮次 6 / with-review 任务（现行）**：SSOT 改为项目根 `.wanxiangzhen.ndjson`（见 `PRD/EventSourcing.md`）。意图不落盘，事件 append；内存 DAG 为积分；文件锁串行写；每行 `session` = 万象阵 session id。废弃 compaction/锚点/以 session 历史 fold DAG 的路径。git 仍为合并事实第二真理源。

### 核实 5.5：frontmatter 数组解析限制

> **核实点**：DAG 的 `dependsOn` 是数组，能否编码进 frontmatter？

万象术 `src/Kernel/PromptFrontMatter.fs:parseFrontMatterScalars` 只解析**标量** `key: value` 与**字面块** `key: |`，**不解析 YAML 序列**（`key: []` / `- item`）。`src/Kernel/LoopMessages.fs:inferReviewTaskFromTexts` 也只读标量字段。

但 `package.json` 证实 万象术 依赖 `"yaml": "^2.9.0"`。

**修正**：
- 万象阵 是独立插件，自带事件 codec，不复用 万象术 的标量解析器。
- 万象阵 的事件 frontmatter 解析放在 **Shell 层**（副作用层，可用 `yaml` 包全量解析数组），Kernel 层保持纯。
- `depends_on` 用标准 YAML 序列编码（`depends_on:\n  - squad-a1b2`），Shell 层用 `yaml` 包解析。架构分层（Kernel 纯 / Shell 副作用）与 万象术 一致。

### 核实 5.6：opencode tui --prompt 解决 slave 自动开工（决策选 B 的落地）

> **核实点**：交互式 TUI 不会自动开工，如何注入初始任务？

源码 `packages/opencode/src/cli/cmd/tui.ts:99` 证实 `opencode tui` 支持 `--prompt <text>`（还有 `--session/--agent/--model/--continue/--fork`）。TUI 启动即用该 prompt 开工，用户能看到完整 TUI 并随时介入。

**修正**：slave 启动命令 = `<terminal> -e opencode tui --prompt "<注入的任务 prompt>"`。完美匹配决策 B（交互式 TUI），且无需插件监听 `session.created` 再注入——prompt 在 CLI 层就位，消除 session 时序复杂度。

### 核实 5.7：git worktree 共享 .git，无 origin（重大修正）

> **核实点**：PRD §5 的 slave rebase 用 `git fetch origin && git rebase origin/master`。

git worktree 机制：所有 worktree 共享同一 `.git` 目录，`master` 是本地分支，对所有 worktree 可见。**没有 origin remote**（除非项目本身配了，但 万象阵 的合并完全在本地）。coordinator 在主仓库 ff 推进 master 后，所有 worktree 的 `.git/refs` 立即可见新 master。

**修正**：
- slave rebase：`git rebase <masterBranch>`（直接基于本地 master，**无 fetch、无 origin**）。
- coordinator ff：`git merge --ff-only <branch>`（本地分支，**无 fetch**）。
- ff 前置检查：`git merge-base --is-ancestor <masterBranch> <branch>`。
- PRD 全文 `origin/master` → `<masterBranch>`，删除所有 `git fetch`。

### 核实 5.8：opencode 原生 Worktree 服务（参考，不强依赖）

源码 `packages/opencode/src/worktree/index.ts` 证实 opencode 自带 `Worktree` 服务（`create/list/remove/reset`）+ `experimental_workspace.register` 适配器 API。

**结论**：万象阵 不依赖 opencode 的 Worktree 服务（Effect 架构，插件难直接调用，且语义偏"项目沙箱"非"任务隔离"）。万象阵 用 `child_process` 直接跑 `git worktree` 命令，全程可控。但 PRD 记录此事实供未来集成参考。

### 核实 5.9：ff 合并位置与 masterBranch 解析（决策选 A）

> **用户决策**：A + 主分支可能不是 master 而是 main 或别的，以主仓库启动时分支为准，假设用户不乱动。

**修正**：
- coordinator 在主仓库（用户当前 opencode 的 worktree）直接 ff，因 coordinator 不编辑文件，working tree 保持干净，ff 自然推进 working tree 到新 master。
- `masterBranch` 默认值**不再硬编码 "master"**：万象阵 启动时执行 `git rev-parse --abbrev-ref HEAD` 取当前分支作为 masterBranch（可被 AGENTS.md frontmatter 覆盖）。
- 启动前置校验：记录启动分支为 masterBranch；ff 前校验"仍在 masterBranch + working tree clean"，否则该次 ff 入队等待或报错（不静默）。
- 假设用户不在 万象阵 运行期切分支/改主仓库文件（决策已接受此约束）。

### 核实 5.10：配置来源（决策选 B）

> **用户决策**：B. 只用 AGENTS.md frontmatter（原 PRD）。

**结论**：万象阵 自行读 `<worktree>/AGENTS.md`，用 `yaml` 包解析其 frontmatter 的 `squad:` 节。opencode 不保证解析 AGENTS.md frontmatter，故由 万象阵 在 Shell 层自行读取解析。保持原 PRD 设计。

---

## 决策汇总表

| # | 决策点 | 用户选择 |
|---|--------|----------|
| 1.1 | slave 提交机制 | 工具调用触发 ff 检查，拒绝时返回提示让 slave rebase |
| 1.2 | DAG 拆解执行者 | coordinator LLM 拆，不会失败，顶多 nudge |
| 1.3 | SSOT 存储 | `.wanxiangzhen.ndjson`（`PRD/EventSourcing.md`）；旧：master session 历史（已废止） |
| 1.4 | 通信模型 | slave 发起短连接，coordinator 不主动 |
| 1.5 | coordinator 定位 | opencode 插件，借宿主进程跑，/squad 触发 |
| 1.6 | slave 能力边界 | 不能 spawn 子 slave，乐观 git 约束 |
| 1.7 | 并发控制 | 可配置上限 |
| 1.8 | 共享目录 | symlink 只读共享 |
| 1.9 | 环境变量注入 | prompt() 注入，有 万象术 按 /loop 格式化 |
| 1.10 | 崩溃处理 | slave 崩溃=task done，master 崩溃=slave idle，/squad-kill 杀树 |
| 1.11 | 终端配置 | AGENTS.md frontmatter 可配 |
| 1.12 | worktree 路径 | `项目/../worktree-hex4` |
| 2.1 | 事件与 LLM | prompt() 触发 LLM 回复是预期的，正文说 "nothing need do" 或顺便解释状态 |
| 2.2 | review/submit 嵌套 | `do { /loop } while (不能 ff)`，内层 review 外层 submit |
| 2.3 | done 语义 | 形式主义，不管内容 |
| 2.4 | 重试上限 | 无限（无限猴子） |
| 2.5 | prompt() 对象 | coordinator 自己的 LLM |
| 2.6 | DAG 依赖语义 | merged 后才 fork worktree（lazy creation） |
| 3.1 | 并行竞争 | 后到者 rebase |
| 3.2 | 清理时机 | merged 删，done 删 |
| 3.3 | /squad-kill | 只杀进程，保留现场 |
| 4.1 | 实现细节 | 写代码时再说 |
| 4.2 | PRD | 确认锁定，撰写 PRD.md |
| 5.1 | HTTP server | coordinator 自起独立 Node http server（opencode serverUrl 无法挂自定义路由）；git 经 child_process |
| 5.2 | slave 退出探测 | 无 child-exit hook → slave 上报自身 opencode PID，coordinator PID 轮询（process.kill(pid,0)）+ done beacon 兜底 |
| 5.3 | /loop 触发 | LLM 不能自打 slash command；coordinator 把任务包成 task: frontmatter 注入（主）/ session.command 程序化触发（备） |
| 5.4 | SSOT 重放路径 | NDJSON 重放 + git 校正；旧 session.messages 路径已废止 |
| 5.5 | frontmatter 数组 | 万象术 标量解析器不认数组；万象阵 自带 codec，Shell 层用 yaml 包全量解析 depends_on 序列 |
| 5.6 | slave 自动开工 | opencode tui --prompt 原生支持初始 prompt → slave 命令 = terminal -e opencode tui --prompt "<task>" |
| 5.7 | git rebase/ff | worktree 共享 .git 无 origin → slave `git rebase <masterBranch>`、coordinator `git merge --ff-only`，全部删 fetch |
| 5.8 | 原生 Worktree | opencode 自带 Worktree 服务，万象阵 不依赖（Effect 难调用），用 child_process 跑 git worktree |
| 5.9 | masterBranch 解析 | 不硬编码 master，启动时 git rev-parse --abbrev-ref HEAD 取当前分支；ff 前校验在分支且 clean |
| 5.10 | 配置来源 | 只用 AGENTS.md frontmatter，万象阵 Shell 层用 yaml 包自行解析 squad: 节 |
