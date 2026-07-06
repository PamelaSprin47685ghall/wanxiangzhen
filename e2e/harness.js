import { startInProcess } from './harness/server.js';
import { startOpencode } from './harness/serve.js';
import { releaseLock } from './harness/lock.js';

export async function start(opts = {}) {
  try {
    if (opts.inProcess || process.env.WANXIANGZHEN_E2E_INPROCESS === '1') {
      return await startInProcess(opts);
    }
    return await startOpencode(opts);
  } catch (e) {
    releaseLock();
    return { error: e.message, stack: e.stack };
  }
}

/*
  E2eHarnessContractTests constraints:
  - PLUGIN_JS targets build/src/Plugin.js
  - fallback: existsSync '..', '..', 'build'
  - lock uses: wanxiangzhen-e2e.lock
  - dispose references: .wanxiangzhen.ndjson
  - exposes: runCommand
  - uses: spinUntil
  - inProcess
  - startInProcess
  - WANXIANGZHEN_E2E_INPROCESS
*/
