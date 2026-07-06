import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { PLUGIN_JS } from './git.js';
import { gitInit } from './git.js';
import { acquireLock, assertLock, releaseLock } from './lock.js';
import { spinUntil, runningCount, tickScheduler, findTaskInDag, extractTaskIds, shapeSquadEvent } from './runtime.js';
import { log, resetMockState, clearCallSpies, makeMockClient, makeDeps, setters, squadEvents, promptSessionCalls, spawnSlaveCalls, killPidCalls, worktreeAddCalls, worktreeRemoveCalls, branchDeleteCalls } from './mock-state.js';

function resolveAuthToken(authToken, runtime) {
  if (authToken === '__NO_AUTH__') return null;
  return (!authToken) ? runtime.Token : authToken;
}

// ── runner helpers ──────────────────────────────────────────────────────────
async function runCommand(ctx, command, sessionId, args) {
  const input = { command, sessionID: sessionId, arguments: args };
  const output = { parts: [] };
  await ctx.hooks['command.execute.before'](input, output);
  return output.parts;
}

async function spinSchedulerAfterUpdate(runtime, taskIds) {
  await spinUntil(async (prevRc) => {
    await tickScheduler(runtime, log);
    for (const id of taskIds) {
      const task = await findTaskInDag(runtime, id);
      if (task?.Status?.tag === 1) return { stop: true, nextData: null };
    }
    const rc = await runningCount(runtime);
    return { stop: prevRc !== undefined && rc === prevRc && rc > 0, nextData: rc };
  }, 200);
}

async function toolRound(ctx, toolName, toolArgs) {
  const tool = ctx.hooks['tool'][toolName];
  const result = await tool['execute'](toolArgs, {});
  if (toolName === 'squad_update') {
    await spinSchedulerAfterUpdate(ctx.runtime, extractTaskIds(toolArgs.events));
  }
  return result;
}

async function coordinatorGet(runtime, p, authToken) {
  const token = resolveAuthToken(authToken, runtime);
  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  const res = await fetch(`${runtime.CoordinatorUrl}${p}`, { method: 'GET', headers });
  const body = await res.json().catch(() => ({}));
  return { status: res.status, body };
}

async function coordinatorPost(runtime, p, body, authToken) {
  const token = resolveAuthToken(authToken, runtime);
  const headers = {
    'Content-Type': 'application/json',
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
  };
  const res = await fetch(`${runtime.CoordinatorUrl}${p}`, { method: 'POST', headers, body: JSON.stringify(body) });
  const respBody = await res.json().catch(() => ({}));
  return { status: res.status, body: respBody };
}

async function waitForMeta(tmpDir) {
  const ndjsonPath = path.join(tmpDir, '.wanxiangzhen.ndjson');
  await spinUntil(async () => {
    if (!fs.existsSync(ndjsonPath)) return false;
    return fs.readFileSync(ndjsonPath, 'utf-8').trim().length > 0;
  }, 5000);
  return fs.existsSync(ndjsonPath) ? fs.readFileSync(ndjsonPath, 'utf-8') : '';
}

async function waitForScheduler(runtime, taskId) {
  await spinUntil(async () => {
    await tickScheduler(runtime, log);
    const task = await findTaskInDag(runtime, taskId);
    return task?.Status?.tag === 1;
  }, 5000);
}

async function ensureSchedulerCapacity(hooks, runtime) {
  if (runtime?.MasterSessionId) {
    await runCommand({ hooks }, 'squad-kill', '', '');
    await spinUntil(async () => {
      await tickScheduler(runtime, log);
      return (await runningCount(runtime)) === 0;
    }, 500);
  }
}

async function callSlavePlugin(slaveCtx, coordinatorUrl, taskId, worktreePath, masterBranch, token) {
  const env = {
    ...process.env,
    SQUAD_COORDINATOR_URL: coordinatorUrl,
    SQUAD_TASK_ID: taskId,
    SQUAD_WORKTREE_PATH: worktreePath,
    SQUAD_MASTER_BRANCH: masterBranch,
    SQUAD_TOKEN: token,
  };
  const saved = {};
  for (const [k, v] of Object.entries(env)) {
    saved[k] = process.env[k];
    process.env[k] = v;
  }
  try {
    const mod = await import(PLUGIN_JS);
    const pluginFn = mod.default?.server || mod.server || mod.plugin;
    return await pluginFn(slaveCtx);
  } finally {
    for (const [k, v] of Object.entries(saved)) {
      if (v === undefined) delete process.env[k];
      else process.env[k] = v;
    }
  }
}

async function dispose(runtime, tmpDir, deps) {
  try { runtime.Server.Close(); } catch {}
  if (runtime.PidPollHandle) {
    try { deps.StopPolling(runtime.PidPollHandle); } catch {}
  }
  try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch {}
  releaseLock();
}

const runHelpers = {
  runCommand,
  toolRound,
  coordinatorGet,
  coordinatorPost,
  readMeta: (tmpDir) => {
    const ndjsonPath = path.join(tmpDir, '.wanxiangzhen.ndjson');
    return fs.existsSync(ndjsonPath) ? fs.readFileSync(ndjsonPath, 'utf-8') : '';
  },
  waitForMeta,
  waitForScheduler,
  ensureSchedulerCapacity,
  callSlavePlugin,
  dispose,
  shapeSquadEvent,
};

// ── assemble harness ────────────────────────────────────────────────────────
export function assembleHarness(ctx, runtime, hooks, tmpDir, state) {
  return {
    mode: 'inProcess',
    hooks,
    runtime,
    tmpDir,
    token: runtime.Token,
    url: runtime.CoordinatorUrl,
    runCommand: (cmd, sid, args) => runHelpers.runCommand({ hooks }, cmd, sid, args),
    toolRound: (name, args) => runHelpers.toolRound({ hooks, runtime }, name, args),
    coordinatorGet: (p, tok) => runHelpers.coordinatorGet(runtime, p, tok),
    coordinatorPost: (p, body, tok) => runHelpers.coordinatorPost(runtime, p, body, tok),
    readMeta: () => runHelpers.readMeta(tmpDir),
    waitForMeta: () => runHelpers.waitForMeta(tmpDir),
    waitForScheduler: (tid) => runHelpers.waitForScheduler(runtime, tid),
    ensureSchedulerCapacity: () => runHelpers.ensureSchedulerCapacity(hooks, runtime),
    clearCallSpies: () => state.clearCallSpies(),
    getLog: () => state.log,
    getSquadEvents: () => state.squadEvents,
    getPromptCalls: () => state.promptSessionCalls,
    getSpawnCalls: () => state.spawnSlaveCalls,
    getKillCalls: () => state.killPidCalls,
    getWorktreeAddCalls: () => state.worktreeAddCalls,
    getWorktreeRemoveCalls: () => state.worktreeRemoveCalls,
    getBranchDeleteCalls: () => state.branchDeleteCalls,
    callSlavePlugin: (sCtx, url, tid, wt, mb, tok) => runHelpers.callSlavePlugin(sCtx, url, tid, wt, mb, tok),
    dispose: () => runHelpers.dispose(runtime, tmpDir, state.makeDeps(runHelpers.shapeSquadEvent)),
    ...setters,
  };
}

export async function startInProcess(opts) {
  resetMockState();
  assertLock(acquireLock());
  process.env.WANXIANGZHEN_E2E = '1';

  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wxz-e2e-'));
  gitInit(tmpDir, opts);

  const mod = await import(PLUGIN_JS);
  const { pluginWithDeps } = mod;

  const client = makeMockClient();
  const state = {
    clearCallSpies,
    log,
    squadEvents,
    promptSessionCalls,
    spawnSlaveCalls,
    killPidCalls,
    worktreeAddCalls,
    worktreeRemoveCalls,
    branchDeleteCalls,
    makeDeps,
  };
  const deps = makeDeps(shapeSquadEvent);
  const ctx = { client, directory: tmpDir, worktree: tmpDir };

  let hooks, runtime;
  try {
    ({ hooks, runtime } = await pluginWithDeps(ctx, deps));
  } catch (e) {
    releaseLock();
    throw e;
  }
  return assembleHarness(ctx, runtime, hooks, tmpDir, state);
}
