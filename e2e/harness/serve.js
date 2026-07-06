import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { PLUGIN_JS, gitInit } from './git.js';

const E2E_META = '.wanxiangzhen-e2e-meta.json';
const NDJSON = '.wanxiangzhen.ndjson';

function resolveAuthToken(authToken, meta) {
  if (authToken === '__NO_AUTH__') return null;
  return (!authToken) ? meta.token : authToken;
}

export function spawnOpencodeChild(tmpDir) {
  const env = { ...process.env, WANXIANGZHEN_E2E: '1', OPENCODE_PLUGIN: PLUGIN_JS };
  return spawn('opencode', ['serve'], { cwd: tmpDir, env, stdio: ['ignore', 'pipe', 'pipe'] });
}

export async function waitForMetaFile(metaPath, child) {
  return new Promise((resolve, reject) => {
    const to = setTimeout(() => reject(new Error('opencode serve timeout')), 30000);
    const iv = setInterval(() => {
      if (fs.existsSync(metaPath)) {
        clearTimeout(to);
        clearInterval(iv);
        try {
          resolve(JSON.parse(fs.readFileSync(metaPath, 'utf-8')));
        } catch (e) { reject(e); }
      }
    }, 100);
    child.on('exit', (code) => {
      clearTimeout(to);
      clearInterval(iv);
      reject(new Error(`opencode serve exited with code ${code}`));
    });
  });
}

async function coordinatorGet(meta, p, authToken) {
  const token = resolveAuthToken(authToken, meta);
  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  const res = await fetch(`${meta.coordinatorUrl}${p}`, { method: 'GET', headers });
  const body = await res.json().catch(() => ({}));
  return { status: res.status, body };
}

async function coordinatorPost(meta, p, body, authToken) {
  const token = resolveAuthToken(authToken, meta);
  const headers = {
    'Content-Type': 'application/json',
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
  };
  const res = await fetch(`${meta.coordinatorUrl}${p}`, { method: 'POST', headers, body: JSON.stringify(body) });
  const respBody = await res.json().catch(() => ({}));
  return { status: res.status, body: respBody };
}

async function waitForMeta(tmpDir) {
  const ndjsonPath = path.join(tmpDir, NDJSON);
  for (let i = 0; i < 5000; i++) {
    if (fs.existsSync(ndjsonPath) && fs.readFileSync(ndjsonPath, 'utf-8').trim().length > 0) {
      break;
    }
    await Promise.resolve();
  }
  return fs.existsSync(ndjsonPath) ? fs.readFileSync(ndjsonPath, 'utf-8') : '';
}

export function assembleServeHarness(tmpDir, child, meta) {
  return {
    mode: 'opencode',
    tmpDir,
    child,
    token: meta.token,
    url: meta.coordinatorUrl,
    coordinatorGet: (p, tok) => coordinatorGet(meta, p, tok),
    coordinatorPost: (p, body, tok) => coordinatorPost(meta, p, body, tok),
    readMeta: () => {
      const ndjsonPath = path.join(tmpDir, NDJSON);
      return fs.existsSync(ndjsonPath) ? fs.readFileSync(ndjsonPath, 'utf-8') : '';
    },
    waitForMeta: () => waitForMeta(tmpDir),
    dispose: async () => {
      try { child.kill('SIGTERM'); } catch {}
      try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch {}
    },
  };
}

export async function startOpencode(opts) {
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wxz-e2e-'));
  gitInit(tmpDir, opts);

  const child = spawnOpencodeChild(tmpDir);
  const metaPath = path.join(tmpDir, E2E_META);

  let meta;
  try {
    meta = await waitForMetaFile(metaPath, child);
  } catch (e) {
    try { child.kill('SIGKILL'); } catch {}
    try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch {}
    throw e;
  }
  return assembleServeHarness(tmpDir, child, meta);
}
