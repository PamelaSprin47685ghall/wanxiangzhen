import path from 'node:path';
import fs from 'node:fs';

const NDJSON = '.wanxiangzhen.ndjson';

export let log = [];
export let squadEvents = [];
export let revParseRefResult = 'deadbeef';
export let revParseRefOverrides = {};
export let mergeBaseResult = true;
export let mergeFfResult = 'merged-sha';
export let statusClean = true;
export let hasCommitsResult = true;
export let showRefExistsResult = false;
export let spawnSlaveCalls = [];
export let killPidCalls = [];
export let worktreeAddCalls = [];
export let worktreeRemoveCalls = [];
export let branchDeleteCalls = [];
export let isPidAliveResult = true;
export let promptSessionCalls = [];
export let nowResult = '2025-01-01T00:00:00.000Z';

export function resetMockState() {
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

export function clearCallSpies() {
  spawnSlaveCalls.length = 0;
  killPidCalls.length = 0;
  worktreeRemoveCalls.length = 0;
  branchDeleteCalls.length = 0;
}

export function makeMockClient() {
  return {
    session: {
      prompt: () => Promise.resolve(null),
      messages: () => Promise.resolve({ data: [] }),
      command: () => Promise.resolve(null),
    },
  };
}

// ── mock actions ──────────────────────────────────────────────────────────
export function mockPromptSession(client, sessionId, msg) {
  promptSessionCalls.push({ sessionId, msg });
  log.push(['prompt', sessionId, msg]);
  if (client?.session?.prompt) {
    const part = { type: 'text', text: msg };
    const arg = { path: { id: sessionId }, body: { parts: [part] } };
    return client.session.prompt(arg);
  }
  return Promise.resolve(null);
}

// shapeSquadEvent DU mapper is defined in runtime.js but imported here or resolved dynamically
export function mockAppendSquadEvent(root, at, evt, shapeFn) {
  const filePath = path.join(root, NDJSON);
  const line = JSON.stringify(shapeFn(evt, at)) + '\n';
  fs.appendFileSync(filePath, line);
  squadEvents.push(evt);
  return Promise.resolve({ tag: 0, fields: [undefined] });
}

export function mockTryWorktreeAdd(cwd, branch, wtPath, base) {
  worktreeAddCalls.push({ cwd, branch, wtPath, base });
  log.push(['tryWorktreeAdd', branch]);
  return { tag: 0, fields: [''] };
}

export function mockTryWorktreeRemoveForce(cwd, wtPath) {
  worktreeRemoveCalls.push({ cwd, wtPath });
  log.push(['tryWorktreeRemoveForce', wtPath]);
  return { tag: 0, fields: [''] };
}

export function mockTryBranchDeleteForce(cwd, branch) {
  branchDeleteCalls.push({ cwd, branch });
  log.push(['tryBranchDeleteForce', branch]);
  return { tag: 0, fields: [''] };
}

export function makeDeps(shapeFn) {
  return {
    PromptSession: (client, sessionId, msg) => mockPromptSession(client, sessionId, msg),
    ReadAllSquadEvents: (_root) => Promise.resolve([...squadEvents]),
    AppendSquadEvent: (root, at, evt) => mockAppendSquadEvent(root, at, evt, shapeFn),
    TryWorktreeAdd: (cwd, branch, wtPath, base) => mockTryWorktreeAdd(cwd, branch, wtPath, base),
    TryWorktreeRemoveForce: (cwd, wtPath) => mockTryWorktreeRemoveForce(cwd, wtPath),
    TryBranchDeleteForce: (cwd, branch) => mockTryBranchDeleteForce(cwd, branch),
    ShowRefExists: (_cwd, _ref) => showRefExistsResult,
    RevParseHead: (_cwd) => revParseRefResult,
    RevParseRef: (_cwd, ref) => revParseRefOverrides[ref] || revParseRefResult,
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
    StartPolling: (ms, cb) => setInterval(cb, ms),
    StopPolling: (handle) => clearInterval(handle),
    Now: () => nowResult,
  };
}

export const setters = {
  setRevParseRef: (ref, val) => { revParseRefOverrides[ref] = val; },
  setMergeBaseResult: (v) => { mergeBaseResult = v; },
  setMergeFfResult: (v) => { mergeFfResult = v; },
  setStatusClean: (v) => { statusClean = v; },
  setHasCommits: (v) => { hasCommitsResult = v; },
  setShowRefExists: (v) => { showRefExistsResult = v; },
  setIsPidAlive: (v) => { isPidAliveResult = v; },
  setNowResult: (v) => { nowResult = v; },
};
