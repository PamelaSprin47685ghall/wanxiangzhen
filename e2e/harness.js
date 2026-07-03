// E2E harness for wanxiangzhen — dual mode:
//   WANXIANGZHEN_E2E_INPROCESS=1 (or opts.inProcess) → in-process via pluginWithDeps
//   default → spawn opencode serve

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, execSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const E2E_LOCK = '/tmp/wanxiangzhen-e2e.lock';
const E2E_META = '.wanxiangzhen-e2e-meta.json';
const NDJSON = '.wanxiangzhen.ndjson';

// Spin until predicate converges or max steps reached — no Date.now / setTimeout pollution.
// predicate accepts previousData for stability tracking (e.g. runningCount deltas).
async function spinUntil(predicate, maxSteps = 200) {
  let data;
  for (let i = 0; i < maxSteps; i++) {
    const stop = await predicate(data);
    const shouldStop = typeof stop === 'object' && stop !== null ? stop.stop === true : !!stop;
    if (shouldStop) return;
    await Promise.resolve();
    data = typeof stop === 'object' && stop !== null && 'nextData' in stop ? stop.nextData : data;
  }
}

// Running-count from runtime.Dag nodes whose Status.tag === 1 (Running).
async function runningCount(runtime) {
  const url = pathToFileURL(path.join(BUILD_SRC, 'Kernel', 'Dag.js')).href;
  const dag = await import(url);
  return dag.runningCount(runtime.Dag);
}

// ── plugin path resolution ─────────────────────────────────────────────────
// Resolves build/src/Plugin.js from both repo-root runs (__dirname=e2e)
// and build-dir runs (__dirname=build/e2e).  Falls back to parent.
// PLUGIN resolve fallback ../.. for contract (path.join('..', '..', 'build', ...)).
function resolvePluginJs() {
  const candidates = [
    path.join(__dirname, '..', 'build', 'src', 'Plugin.js'),
    path.join(__dirname, 'build', 'src', 'Plugin.js'),
    path.join(__dirname, '..', '..', 'build', 'src', 'Plugin.js'),
  ];
  for (const c of candidates) {
    if (fs.existsSync(c)) return c;
  }
  return candidates[0];
}

const PLUGIN_JS = resolvePluginJs();

// ── dynamic build/src helpers ───────────────────────────────────────────────
// PLUGIN_JS = .../build/src/Plugin.js → buildSrc = .../build/src/
const BUILD_SRC = path.dirname(PLUGIN_JS);

async function tickScheduler(runtime) {
  try {
    const url = pathToFileURL(path.join(BUILD_SRC, 'Shell', 'CoordinatorOps.js')).href;
    const ops = await import(url);
    runtime.Scheduling = false;
    await ops.schedulerTick(runtime);
  } catch (e) {
    log.push(['tickSchedulerError', e.message]);
  }
}

async function findTaskInDag(runtime, taskId) {
  const url = pathToFileURL(path.join(BUILD_SRC, 'Kernel', 'Dag.js')).href;
  const dag = await import(url);
  return dag.findTask(taskId, runtime.Dag);
}

// Extract taskIds from toolArgs.events (tasks_created → tasks[].taskId).
function extractTaskIds(events) {
  const ids = [];
  for (const evt of events || []) {
    if (evt?.type === 'tasks_created' && Array.isArray(evt.tasks)) {
      for (const t of evt.tasks) {
        const id = t?.taskId;
        if (id) ids.push(id);
      }
    }
  }
  return ids;
}

// ── squad event DU → NDJSON line ───────────────────────────────────────────
// evt is a Fable DU object: { tag: "CaseName", fields: [...] }
function shapeSquadEvent(evt, at) {
  const tag = evt.tag || evt.constructor?.name;
  const fields = evt.fields || [];
  switch (tag) {
    case 'SquadCreated':
      return { v: 1, session: fields[0], kind: 'squad_created', at, payload: { requirement: fields[1] } };
    case 'TasksCreated':
      return { v: 1, session: fields[0], kind: 'tasks_created', at, payload: { tasks: fields[1] } };
    case 'TaskStarted':
      return { v: 1, session: fields[0], kind: 'task_started', at, payload: { taskId: fields[1], worktreePath: fields[2], branchName: fields[3] } };
    case 'TaskSubmitted':
      return { v: 1, session: fields[0], kind: 'task_submitted', at, payload: { taskId: fields[1], commitSha: fields[2] } };
    case 'TaskMerged':
      return { v: 1, session: fields[0], kind: 'task_merged', at, payload: { taskId: fields[1], masterSha: fields[2] } };
    case 'TaskDone':
      return { v: 1, session: fields[0], kind: 'task_done', at, payload: { taskId: fields[1], merged: fields[2] } };
    case 'TaskError':
      return { v: 1, session: fields[0], kind: 'task_error', at, payload: { taskId: fields[1], error: fields[2] } };
    case 'SquadCancelled':
      return { v: 1, session: fields[0], kind: 'squad_cancelled', at, payload: {} };
    default:
      return { v: 1, session: '', kind: String(tag), at, payload: {} };
  }
}

// ── mock state ─────────────────────────────────────────────────────────────
let log = [];
let squadEvents = [];
let revParseRefResult = 'deadbeef';
let revParseRefOverrides = {};
let mergeBaseResult = true;
let mergeFfResult = 'merged-sha';
let statusClean = true;
let hasCommitsResult = true;
let showRefExistsResult = false;
let spawnSlaveCalls = [];
let killPidCalls = [];
let worktreeAddCalls = [];
let worktreeRemoveCalls = [];
let branchDeleteCalls = [];
let isPidAliveResult = true;
let promptSessionCalls = [];
let nowResult = '2025-01-01T00:00:00.000Z';

function resetMockState() {
  log = [];
  squadEvents = [];
  revParseRefResult = 'deadbeef';
  revParseRefOverrides = {};
  mergeBaseResult = true;
  mergeFfResult = 'merged-sha';
  statusClean = true;
  hasCommitsResult = true;
  showRefExistsResult = false;
  spawnSlaveCalls = [];
  killPidCalls = [];
  worktreeAddCalls = [];
  worktreeRemoveCalls = [];
  branchDeleteCalls = [];
  isPidAliveResult = true;
  promptSessionCalls = [];
  nowResult = '2025-01-01T00:00:00.000Z';
}

function clearCallSpies() {
  spawnSlaveCalls = [];
  killPidCalls = [];
  worktreeRemoveCalls = [];
  branchDeleteCalls = [];
}

// ── mock client ─────────────────────────────────────────────────────────────
function makeMockClient() {
  return {
    session: {
      prompt: () => Promise.resolve(null),
      messages: () => Promise.resolve({ data: [] }),
      command: () => Promise.resolve(null),
    },
  };
}

// ── mock deps ───────────────────────────────────────────────────────────────
function makeDeps() {
  return {
    PromptSession: (client, sessionId, msg) => {
      promptSessionCalls.push({ sessionId, msg });
      log.push(['prompt', sessionId, msg]);
      if (client?.session?.prompt) {
        const part = { type: 'text', text: msg };
        const arg = { path: { id: sessionId }, body: { parts: [part] } };
        return client.session.prompt(arg);
      }
      return Promise.resolve(null);
    },
    ReadAllSquadEvents: (_root) => {
      return Promise.resolve([...squadEvents]);
    },
    AppendSquadEvent: (root, at, evt) => {
      const filePath = path.join(root, NDJSON);
      const line = JSON.stringify(shapeSquadEvent(evt, at)) + '\n';
      fs.appendFileSync(filePath, line);
      squadEvents.push(evt);
      return Promise.resolve({ tag: 0, fields: [undefined] });
    },
    TryWorktreeAdd: (cwd, branch, wtPath, base) => {
      worktreeAddCalls.push({ cwd, branch, wtPath, base });
      log.push(['tryWorktreeAdd', branch]);
      return { tag: 0, fields: [''] };
    },
    TryWorktreeRemoveForce: (cwd, wtPath) => {
      worktreeRemoveCalls.push({ cwd, wtPath });
      log.push(['tryWorktreeRemoveForce', wtPath]);
      return { tag: 0, fields: [''] };
    },
    TryBranchDeleteForce: (cwd, branch) => {
      branchDeleteCalls.push({ cwd, branch });
      log.push(['tryBranchDeleteForce', branch]);
      return { tag: 0, fields: [''] };
    },
    ShowRefExists: (_cwd, _ref) => showRefExistsResult,
    RevParseHead: (_cwd) => revParseRefResult,
    RevParseRef: (_cwd, ref) => {
      if (revParseRefOverrides[ref]) return revParseRefOverrides[ref];
      return revParseRefResult;
    },
    RevParseBranch: (_cwd) => 'main',
    IsDetached: (_cwd) => false,
    StatusIsClean: (_cwd) => statusClean,
    MergeBaseIsAncestor: (_cwd, _a, _d) => mergeBaseResult,
    MergeFfOnly: (_cwd, _branch) => mergeFfResult,
    HasCommits: (_cwd) => hasCommitsResult,
    CreateSymlinks: (_wt, _root, _dirs) => {},
    SpawnSlave: (terminal, wtPath, env, prompt) => {
      spawnSlaveCalls.push({ terminal, wtPath, env, prompt });
      log.push(['spawnSlave', wtPath]);
    },
    IsPidAlive: (_pid) => isPidAliveResult,
    KillPid: (pid, signal) => {
      killPidCalls.push({ pid, signal });
      log.push(['killPid', pid]);
    },
    WaitForPidDeath: (_pid, _remaining) => Promise.resolve(),
    StartPolling: (_ms, _cb) => ({}),
    StopPolling: (_handle) => {},
    Now: () => nowResult,
  };
}

// ── git init ────────────────────────────────────────────────────────────────
function gitInit(tmpDir) {
  execSync('git init', { cwd: tmpDir, stdio: 'ignore' });
  const agentsMd = `---
squad:
  terminal: headless
  masterBranch: main
  maxConcurrent: 100
  sharedDirs: []
---
`;
  fs.writeFileSync(path.join(tmpDir, 'AGENTS.md'), agentsMd);
  execSync('git add AGENTS.md', { cwd: tmpDir, stdio: 'ignore' });
  execSync('git config user.email e2e@test', { cwd: tmpDir, stdio: 'ignore' });
  execSync('git config user.name e2e', { cwd: tmpDir, stdio: 'ignore' });
  execSync('git commit -m init', { cwd: tmpDir, stdio: 'ignore' });
  execSync('git branch -M main', { cwd: tmpDir, stdio: 'ignore' });
}

// ── lock ────────────────────────────────────────────────────────────────────
function isPidAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return e.code !== 'ESRCH';
  }
}

function acquireLock() {
  try {
    fs.writeFileSync(E2E_LOCK, String(process.pid), { flag: 'wx' });
    return true;
  } catch (e) {
    if (e.code !== 'EEXIST') throw e;
    let stalePid;
    try {
      stalePid = parseInt(fs.readFileSync(E2E_LOCK, 'utf-8').trim(), 10);
    } catch {
      return false;
    }
    if (isNaN(stalePid) || isPidAlive(stalePid)) return false;
    try { fs.unlinkSync(E2E_LOCK); } catch { return false; }
    try {
      fs.writeFileSync(E2E_LOCK, String(process.pid), { flag: 'wx' });
      return true;
    } catch (e2) {
      if (e2.code === 'EEXIST') return false;
      throw e2;
    }
  }
}

function assertLock(locked) {
  if (!locked) throw new Error('E2E lock failed — another E2E run is active');
}

function releaseLock() {
  try { fs.unlinkSync(E2E_LOCK); } catch {}
}

// ── auth token convention ──────────────────────────────────────────────────
// null/undefined/"" → use runtime.Token (authorized)
// "__NO_AUTH__"     → no Authorization header (unauthorized)
function resolveAuthToken(authToken, runtime) {
  if (authToken === '__NO_AUTH__') return null;
  if (authToken === undefined || authToken === null || authToken === '') return runtime.Token;
  return authToken;
}

// ── start in-process ────────────────────────────────────────────────────────
async function startInProcess(opts) {
  resetMockState();
  assertLock(acquireLock());

  process.env.WANXIANGZHEN_E2E = '1';

  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wxz-e2e-'));
  gitInit(tmpDir);

  const mod = await import(PLUGIN_JS);
  const { pluginWithDeps } = mod;

  const client = makeMockClient();
  const deps = makeDeps();

  const ctx = {
    client,
    directory: tmpDir,
    worktree: tmpDir,
  };

  let hooks, runtime;
  try {
    ({ hooks, runtime } = await pluginWithDeps(ctx, deps));
  } catch (e) {
    releaseLock();
    throw e;
  }

  const harness = {
    mode: 'inProcess',
    hooks,
    runtime,
    tmpDir,
    token: runtime.Token,
    url: runtime.CoordinatorUrl,

    runCommand: async (command, sessionId, args) => {
      const input = { command, sessionID: sessionId, arguments: args };
      const output = { parts: [] };
      const hook = hooks['command.execute.before'];
      await hook(input, output);
      return output.parts;
    },

    toolRound: async (toolName, toolArgs) => {
      const tool = hooks['tool'][toolName];
      const execute = tool['execute'];
      const result = await execute(toolArgs, {});
      if (toolName === 'squad_update') {
        const taskIds = extractTaskIds(toolArgs.events);
        // Spin until any listed task transitions to Running via findTaskInDag;
        // fallback: tick until scheduler idle (runningCount stable).
        await spinUntil(async (prevRc) => {
          await tickScheduler(runtime);
          for (const id of taskIds) {
            const task = await findTaskInDag(runtime, id);
            if (task?.Status?.tag === 1) return { stop: true, nextData: null };
          }
          const rc = await runningCount(runtime);
          const stop = prevRc !== undefined && rc === prevRc && rc > 0;
          return { stop, nextData: rc };
        }, 200);
      }
      return result;
    },

    coordinatorGet: async (p, authToken) => {
      const token = resolveAuthToken(authToken, runtime);
      const headers = token ? { Authorization: `Bearer ${token}` } : {};
      const res = await fetch(`${runtime.CoordinatorUrl}${p}`, { method: 'GET', headers });
      const body = await res.json().catch(() => ({}));
      return { status: res.status, body };
    },

    coordinatorPost: async (p, body, authToken) => {
      const token = resolveAuthToken(authToken, runtime);
      const headers = {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      };
      const res = await fetch(`${runtime.CoordinatorUrl}${p}`, {
        method: 'POST',
        headers,
        body: JSON.stringify(body),
      });
      const respBody = await res.json().catch(() => ({}));
      return { status: res.status, body: respBody };
    },

    readMeta: () => {
      const ndjsonPath = path.join(tmpDir, NDJSON);
      if (!fs.existsSync(ndjsonPath)) return '';
      return fs.readFileSync(ndjsonPath, 'utf-8');
    },

    waitForMeta: async () => {
      const ndjsonPath = path.join(tmpDir, NDJSON);
      await spinUntil(async () => {
        if (!fs.existsSync(ndjsonPath)) return false;
        const content = fs.readFileSync(ndjsonPath, 'utf-8');
        return content.trim().length > 0;
      }, 5000);
      return fs.existsSync(ndjsonPath) ? fs.readFileSync(ndjsonPath, 'utf-8') : '';
    },

    waitForScheduler: async (taskId) => {
      await spinUntil(async () => {
        await tickScheduler(runtime);
        const task = await findTaskInDag(runtime, taskId);
        return task?.Status?.tag === 1;
      }, 5000);
    },

    clearCallSpies: () => clearCallSpies(),

    ensureSchedulerCapacity: async () => {
      if (runtime?.MasterSessionId) {
        const input = { command: "squad-kill", sessionID: "", arguments: "" };
        const output = { parts: [] };
        const hook = hooks['command.execute.before'];
        await hook(input, output);
        await spinUntil(async () => {
          await tickScheduler(runtime);
          return (await runningCount(runtime)) === 0;
        }, 500);
      }
    },

    getLog: () => log,
    getSquadEvents: () => squadEvents,
    getPromptCalls: () => promptSessionCalls,
    getSpawnCalls: () => spawnSlaveCalls,
    getKillCalls: () => killPidCalls,
    getWorktreeAddCalls: () => worktreeAddCalls,
    getWorktreeRemoveCalls: () => worktreeRemoveCalls,
    getBranchDeleteCalls: () => branchDeleteCalls,

    setRevParseRef: (ref, val) => { revParseRefOverrides[ref] = val; },
    setMergeBaseResult: (v) => { mergeBaseResult = v; },
    setMergeFfResult: (v) => { mergeFfResult = v; },
    setStatusClean: (v) => { statusClean = v; },
    setHasCommits: (v) => { hasCommitsResult = v; },
    setShowRefExists: (v) => { showRefExistsResult = v; },
    setIsPidAlive: (v) => { isPidAliveResult = v; },

    callSlavePlugin: async (ctx, coordinatorUrl, taskId, worktreePath, masterBranch, token) => {
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
        return await pluginFn(ctx);
      } finally {
        for (const [k, v] of Object.entries(saved)) {
          if (v === undefined) delete process.env[k];
          else process.env[k] = v;
        }
      }
    },

    dispose: async () => {
      try { runtime.Server.Close(); } catch {}
      try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch {}
      releaseLock();
    },
  };

  return harness;
}

// ── start opencode serve ────────────────────────────────────────────────────
async function startOpencode(opts) {
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wxz-e2e-'));
  gitInit(tmpDir);

  const env = {
    ...process.env,
    WANXIANGZHEN_E2E: '1',
    OPENCODE_PLUGIN: PLUGIN_JS,
  };

  const child = spawn('opencode', ['serve'], {
    cwd: tmpDir,
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  const metaPath = path.join(tmpDir, E2E_META);

  const meta = await new Promise((resolve, reject) => {
    const to = setTimeout(() => reject(new Error('opencode serve timeout')), 30000);
    const iv = setInterval(() => {
      if (fs.existsSync(metaPath)) {
        clearTimeout(to);
        clearInterval(iv);
        try {
          resolve(JSON.parse(fs.readFileSync(metaPath, 'utf-8')));
        } catch (e) {
          reject(e);
        }
      }
    }, 100);
    child.on('exit', (code) => {
      clearTimeout(to);
      clearInterval(iv);
      reject(new Error(`opencode serve exited with code ${code}`));
    });
  });

  return {
    mode: 'opencode',
    tmpDir,
    child,
    token: meta.token,
    url: meta.coordinatorUrl,

    coordinatorGet: async (p, authToken) => {
      const token = resolveAuthToken(authToken, { Token: meta.token });
      const headers = token ? { Authorization: `Bearer ${token}` } : {};
      const res = await fetch(`${meta.coordinatorUrl}${p}`, { method: 'GET', headers });
      const body = await res.json().catch(() => ({}));
      return { status: res.status, body };
    },

    coordinatorPost: async (p, body, authToken) => {
      const token = resolveAuthToken(authToken, { Token: meta.token });
      const headers = {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      };
      const res = await fetch(`${meta.coordinatorUrl}${p}`, {
        method: 'POST',
        headers,
        body: JSON.stringify(body),
      });
      const respBody = await res.json().catch(() => ({}));
      return { status: res.status, body: respBody };
    },

    readMeta: () => {
      const ndjsonPath = path.join(tmpDir, NDJSON);
      if (!fs.existsSync(ndjsonPath)) return '';
      return fs.readFileSync(ndjsonPath, 'utf-8');
    },

    waitForMeta: async () => {
      const ndjsonPath = path.join(tmpDir, NDJSON);
      await spinUntil(async () => {
        if (!fs.existsSync(ndjsonPath)) return false;
        const content = fs.readFileSync(ndjsonPath, 'utf-8');
        return content.trim().length > 0;
      }, 5000);
      return fs.existsSync(ndjsonPath) ? fs.readFileSync(ndjsonPath, 'utf-8') : '';
    },

    dispose: async () => {
      try { child.kill('SIGTERM'); } catch {}
      try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch {}
    },
  };
}

// ── start ───────────────────────────────────────────────────────────────────
async function start(opts = {}) {
  try {
    if (opts.inProcess || process.env.WANXIANGZHEN_E2E_INPROCESS === '1') {
      return await startInProcess(opts);
    }
    return await startOpencode(opts);
  } catch (e) {
    return { error: e.message, stack: e.stack };
  }
}

export { start };
