import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { BUILD_SRC } from './git.js';

export async function spinUntil(predicate, maxSteps = 200) {
  let data;
  for (let i = 0; i < maxSteps; i++) {
    const stop = await predicate(data);
    const shouldStop = typeof stop === 'object' && stop !== null ? stop.stop === true : !!stop;
    if (shouldStop) return;
    await Promise.resolve();
    data = typeof stop === 'object' && stop !== null && 'nextData' in stop ? stop.nextData : data;
  }
}

export async function runningCount(runtime) {
  const url = pathToFileURL(path.join(BUILD_SRC, 'Kernel', 'Dag.js')).href;
  const dag = await import(url);
  return dag.runningCount(runtime.Dag);
}

export async function tickScheduler(runtime, log) {
  try {
    const url = pathToFileURL(path.join(BUILD_SRC, 'Shell', 'CoordinatorOps.js')).href;
    const ops = await import(url);
    runtime.Scheduling = false;
    await ops.schedulerTick(runtime);
  } catch (e) {
    log.push(['tickSchedulerError', e.message]);
  }
}

export async function findTaskInDag(runtime, taskId) {
  const url = pathToFileURL(path.join(BUILD_SRC, 'Kernel', 'Dag.js')).href;
  const dag = await import(url);
  return dag.findTask(taskId, runtime.Dag);
}

export function extractTaskIds(events) {
  const ids = [];
  for (const evt of events || []) {
    if (evt?.type === 'tasks_created' && Array.isArray(evt.tasks)) {
      for (const t of evt.tasks) {
        if (t?.taskId) ids.push(t.taskId);
      }
    }
  }
  return ids;
}

export function shapeSquadEvent(evt, at) {
  let tag = evt.tag;
  if (typeof tag === 'number') {
    const mapping = {
      0: 'SquadCreated',
      1: 'TasksCreated',
      2: 'TaskStarted',
      3: 'TaskSubmitted',
      4: 'TaskMerged',
      5: 'TaskDone',
      6: 'TaskError',
      7: 'SquadCancelled'
    };
    tag = mapping[tag];
  } else if (!tag) {
    tag = evt.constructor?.name;
  }
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
