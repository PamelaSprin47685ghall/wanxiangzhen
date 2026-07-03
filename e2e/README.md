# wanxiangzhen e2e

## Run

In-process (default):

```
WANXIANGZHEN_E2E_INPROCESS=1 npm run e2e
```

Serve mode (requires `opencode` CLI):

```
OPENCODE_E2E=1 npm run e2e
```

Harness spawns `opencode serve` which writes `.wanxiangzhen-e2e-meta.json` (`token` + `coordinatorUrl`); tests poll this file then drive the coordinator over HTTP.

## Environment

| Variable | Effect |
|----------|--------|
| `WANXIANGZHEN_E2E_INPROCESS=1` | In-process mode — `pluginWithDeps()` called directly, no external process |
| `OPENCODE_E2E=1` | Serve mode — spawns `opencode serve`, waits for `.wanxiangzhen-e2e-meta.json` |
| `WANXIANGZHEN_E2E=1` | Set automatically by `npm run e2e`; enables E2E stubs inside the plugin |

## Components

| File | Role |
|------|------|
| `harness.js` | Test harness — spawns coordinator, mock client, git stubs, call spies (`spawnSlave`, `killPid`, `worktreeAdd/Remove`, `branchDelete`), leak-proof temp dir cleanup |
| `mock-llm.js` | Fake LLM — SSE server with `/_expect` queue; injects `warn_tdd: i-am-sure-i-have-followed-tdd-and-kolmolgorov-principles` into every tool call |
| `Tests.fs` | 14 E2E cases — plugin config, squad lifecycle, HTTP auth, full flow, DAG cycle/dangling rejection, slave `query_squad` via `SQUAD_*` env |

## Event-driven waits

Harness and kernel never sleep on wall-clock:

- `spinUntil(pred, maxSteps)` (SpinWait.fs / harness.js) polls `pred` on **every microtask boundary** — `await Promise.resolve()` between checks. No `Date.now`, no `setTimeout`, no polling interval. Predicates that depend on pure state transitions (task `Running`, file existence, spy counts) converge in microseconds without timing races.
- `waitForScheduler taskId` — spins until `rt.Dag[taskId].Status = Running` via the above mechanism. A `½`-style initial capacity drain (`ensureSchedulerCapacity`) is executed **once** before enqueue to drain prior squads.
- `waitForMeta ()` — spins until `.wanxiangzhen.ndjson` is non-empty on disk.
- `regsub` — the merged-register+submit flow (`testHttpRegisterAndSubmitMerged`) is isolated via `runWithFreshHarness` to prevent state leaking into the shared harness. Asserts: register 200 → Running → merge → worktree+branch cleanup delta each exactly 1.
- CI — `npm run ci` runs build + unit + e2e in-process. No external process, no serve mode. See e2e/README.md ## CI.

## Lock

`/tmp/wanxiangzhen-e2e.lock` serializes concurrent runs via `O_EXCL` creation + PID liveness check. Stale locks from dead processes are reclaimed automatically.

## AGENTS.md

Harness auto-generates a repo-local `AGENTS.md` with `squad.maxConcurrent: 100`. Lower on memory-constrained or worktree-limited machines.

## Stress

```
npm run e2e:stress     # 20 consecutive runs, abort on first failure
```

## CI

GitHub Actions uses `npm run ci` (build + unit + e2e in-process). See `.github/workflows/ci.yml`.
