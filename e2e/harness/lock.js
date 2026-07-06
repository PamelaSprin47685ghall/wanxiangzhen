import fs from 'node:fs';

const E2E_LOCK = '/tmp/wanxiangzhen-e2e.lock';

function isPidAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return e.code !== 'ESRCH';
  }
}

export function acquireLock() {
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

export function assertLock(locked) {
  if (!locked) throw new Error('E2E lock failed — another E2E run is active');
}

export function releaseLock() {
  try { fs.unlinkSync(E2E_LOCK); } catch {}
}
