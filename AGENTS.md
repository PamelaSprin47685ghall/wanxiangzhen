---
import:
  - PRD.md
  - DEV_TALK.md
---

本项目同样适用 F# -> js。

严禁修改 ../wanxiangshu 目录内容。

万象阵 durable 状态 SSOT：`[项目]/.wanxiangzhen.ndjson`（规格 `PRD/EventSourcing.md`）。重放入口 `replayFromEventLog`（`Shell/CoordinatorReplay.fs`）；禁止用 master session 对话历史 fold DAG。写路径：`commitEvent` → 先 append NDJSON，成功后再改内存 DAG；`handleSquadKill` 成功路径用 `foldEvent(SquadCancelled)` 与重放同语义；append 失败写入 `CoordinatorRuntime.InjectError`。
