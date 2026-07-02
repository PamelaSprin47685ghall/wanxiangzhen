# 万象阵 — Multi-Agent Opencode Coordinator

## 1. 一句话定义

万象阵 是一个 opencode 插件。它把用户需求经自身 LLM 拆解为 DAG 任务图，对每个就绪任务从最新主分支创建 git worktree，用终端模拟器启动独立 slave opencode 进程在隔离 worktree 中开发；slave 完成后经工具调用通知 coordinator，coordinator 以 ff-only 协议把分支线性合并回主分支，永不产生 merge commit、永不解决冲突。

## 2. 安装前提

万象阵 以 **万象术（wanxiangshu）** 为硬前提，必须先装后者。

```
# 必须先装 万象术
pnpm add -g wanxiangshu

# 再装 万象阵
pnpm add -g wanxiangzhen
```

安装顺序不可颠倒。万象阵 不保留"没有万象术也能半残运行"的降级主路径。若缺少依赖，插件尽早失败，不静默吞错。

`wanxiangshu` 提供 `/loop`（With-Review Mode）——万象阵 不自实现 review，依赖它完成开发后的审查环节。

## 3. 架构角色

| 角色 | 说明 |
|------|------|
| **Coordinator** | 用户的 opencode 进程。加载万象阵插件后自起本地 HTTP server，负责 DAG 拆解、调度、ff 合并、worktree 与 slave 生命周期。 |
| **Slave** | coordinator 经 `child_process` 起的独立 opencode 进程（`opencode tui --prompt`），在隔离 worktree 工作。状态查询与提交经 HTTP 短连接发给 coordinator。 |
| **`.wanxiangzhen.ndjson`** | **SSOT**（项目根 NDJSON 事件流，每行含 `session`）。意图不落盘，事实 append；内存 DAG 为 fold 投影。启动重放事件文件；文件锁保证串行追加。详见 `PRD/EventSourcing.md`。 |
| **git refs** | 合并事实的第二真理源。`task_merged` 落在 refs；重放后对 `running`/`submitted` 可 `git merge-base --is-ancestor` 校正。 |

## 4. 工作流简述

```
用户 /squad <需求>
    → coordinator LLM 拆解为 DAG（squad_update）
    → Scheduler.tick() 拓扑就绪判定
    → 对就绪 task：创建 worktree → 启动 slave（opencode tui --prompt）
    → slave 开发（可选 /loop review）→ commit → submit_to_squad
    → coordinator ff-only 检查
        → merged：推进主分支，清理 worktree，触发后续 task
        → rebase_needed：slave rebase 主分支，重新 review + submit（无限重试）
    → 全部终态 → DAG 完成
```

- **无依赖任务并行执行**，有依赖任务串行等待（前驱 merged 进主分支后，后继才从最新主分支 fork——lazy worktree creation）
- **线性历史**：主分支只允许 fast-forward，永不产生 merge commit
- **崩溃容忍**：slave 崩溃视为 task 形式完成，不阻塞后续；coordinator 崩溃后 slave 向用户报错并 idle，用户 `/squad-kill` 清理

## 5. 配置

项目根 `AGENTS.md` 顶部 yaml frontmatter 的 `squad:` 段：

```yaml
---
squad:
  maxConcurrent: 3          # 同时运行 slave 上限，默认 3
  terminal: alacritty       # 终端模拟器，默认按平台探测
  masterBranch: main        # 集成分支名；缺省 = coordinator 启动时所在分支
  sharedDirs:               # 只读共享目录（symlink）
    - node_modules
    - .venv
---
```

`masterBranch` 不硬编码为 `master`：默认取 coordinator 启动时 `git rev-parse --abbrev-ref HEAD` 的当前分支，可被上述 frontmatter 显式覆盖。所有 slave rebase 目标、ff 合并目标、worktree fork 基址统一用此值。

## 6. 与 万象术 的关系

两插件是独立插件，互不 import、互不感知，仅在 prompt 层协同：

- **万象术** 提供 review 引擎（`/loop`）、KG、nudge、历史投影等
- **万象阵** 提供 DAG 调度、worktree 隔离、slave 生命周期、ff 合并

万象阵 不自实现 review。安装万象阵 前必须先安装万象术——因为 slave 的工作流依赖 `/loop`。若缺少万象术，万象阵 插件在启动时尽早失败，给出明确提示，而非降级运行。
