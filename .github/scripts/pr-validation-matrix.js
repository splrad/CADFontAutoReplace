const { execFileSync } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const {
  realContributorLoginsFromBody,
  uniqueLogins,
} = require('./pr-notifications');

const repo = process.env.GITHUB_REPOSITORY || '';
const [owner, repoName] = repo.split('/');
const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
const prNumber = Number(process.env.PR_NUMBER || process.env.GITHUB_EVENT_PULL_REQUEST_NUMBER || '0');
const eventName = process.env.GITHUB_EVENT_NAME || '';
const eventAction = process.env.GITHUB_EVENT_ACTION || '';
const matrixMode = process.env.VALIDATION_MATRIX_MODE || 'enforce';
const requestedScope = process.env.VALIDATION_MATRIX_SCOPE || 'auto';
const configPath = process.env.PR_VALIDATION_MATRIX_CONFIG
  || path.join(workspace, '.github', 'pr-validation-matrix.json');

function parseArgs(argv) {
  return {
    apply: argv.includes('--apply'),
    dryRun: argv.includes('--dry-run') || !argv.includes('--apply'),
  };
}

function gh(args, input, token) {
  return execFileSync(process.env.GH_EXECUTABLE || 'gh', ['api', ...args], {
    encoding: 'utf8',
    input,
    stdio: ['pipe', 'pipe', 'pipe'],
    env: {
      ...process.env,
      GH_TOKEN: token || process.env.GH_TOKEN || '',
    },
  }).trim();
}

function ghJson(args, input, token) {
  const out = gh(args, input, token);
  return out ? JSON.parse(out) : null;
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function hashJson(value) {
  return crypto.createHash('sha256')
    .update(JSON.stringify(value))
    .digest('hex');
}

function normalizeRepoPath(file) {
  return String(file || '').replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase();
}

function checkToken() {
  return process.env.GH_CHECKS_TOKEN || process.env.GH_TOKEN || '';
}

function actionsToken() {
  return process.env.GH_ACTIONS_TOKEN || process.env.GH_TOKEN || '';
}

function parsePositiveInteger(value, fallback) {
  const parsed = Number.parseInt(String(value || ''), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function sleepMilliseconds(milliseconds) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

function normalizeGitHubLogin(login) {
  return String(login || '').toLowerCase().replace(/\[bot\]$/, '');
}

function isCopilotActor(login) {
  const normalized = normalizeGitHubLogin(login);
  return normalized === 'copilot' || normalized === 'copilot-pull-request-reviewer';
}

function isCopilotReviewSignal(event, eventPayload) {
  if (event === 'pull_request_review') {
    return isCopilotActor(eventPayload?.review?.user?.login);
  }
  if (event === 'pull_request_review_comment') {
    return isCopilotActor(eventPayload?.comment?.user?.login);
  }
  return false;
}

function pullRequestCommitAuthorLogins(commits) {
  return uniqueLogins((commits || []).map((commit) => commit?.author?.login || ''));
}

function fingerprintForPull({ pull, commits, files }) {
  const commitShas = (commits || []).map((commit) => commit?.sha || '').filter(Boolean).sort();
  const fileParts = (files || []).map((file) => [
    normalizeRepoPath(file?.filename),
    file?.status || '',
    file?.sha || '',
    file?.additions || 0,
    file?.deletions || 0,
  ]).sort();
  const contributors = uniqueLogins([
    ...realContributorLoginsFromBody({ body: pull?.body || '', prAuthor: pull?.user?.login || '' }),
    ...pullRequestCommitAuthorLogins(commits),
  ]).sort((a, b) => a.localeCompare(b));

  const source = {
    head_sha: pull?.head?.sha || '',
    base_ref: pull?.base?.ref || '',
    base_sha: pull?.base?.sha || '',
    commits: commitShas,
    contributors,
    files_digest: hashJson(fileParts),
  };

  return {
    ...source,
    value: hashJson(source),
  };
}

function eventScope({ event, action, requested = 'auto', previousFingerprint, currentFingerprint }) {
  if (requested === 'full' || requested === 'gate-only') return requested;
  if (!currentFingerprint?.value) return 'full';

  const sameFingerprint = previousFingerprint && previousFingerprint === currentFingerprint.value;
  if (event === 'pull_request_target') {
    if (['opened', 'synchronize', 'reopened', 'ready_for_review'].includes(action)) return 'full';
    if (['edited', 'labeled', 'unlabeled'].includes(action)) return sameFingerprint ? 'gate-only' : 'full';
    return 'full';
  }
  if (event === 'pull_request_review' || event === 'pull_request_review_comment') {
    return sameFingerprint ? 'gate-only' : 'full';
  }
  if (event === 'workflow_run' || event === 'check_run' || event === 'workflow_dispatch') {
    return 'full';
  }
  return 'full';
}

function targetBaseApplies(target, pull) {
  const branches = Array.isArray(target.baseBranches) ? target.baseBranches : [];
  if (!branches.length) return true;
  const baseRef = String(pull?.base?.ref || '');
  return branches.includes(baseRef);
}

function targetApplies(target, scope, pull) {
  if (!targetBaseApplies(target, pull)) return false;
  if (scope === 'gate-only') return target.group === 'gate';
  return target.group === 'full' || target.group === 'gate';
}

function latestByStartedAt(checkRuns) {
  return [...checkRuns].sort((a, b) => {
    return String(a.started_at || a.created_at || '').localeCompare(String(b.started_at || b.created_at || ''));
  }).at(-1);
}

function matchingCheckRun(checkRuns, target) {
  const names = new Set(target.checkNames || []);
  return latestByStartedAt((checkRuns || []).filter((run) => names.has(run.name)));
}

function checkRunState(run, target) {
  if (!run) return 'missing';
  if (['queued', 'in_progress', 'waiting', 'requested', 'pending'].includes(String(run.status || ''))) {
    return 'pending';
  }
  const conclusion = String(run.conclusion || '');
  if ((target.acceptableConclusions || ['success']).includes(conclusion)) return 'passed';
  if (['cancelled', 'timed_out', 'skipped', 'action_required', 'stale'].includes(conclusion)) return 'recoverable';
  return 'failed';
}

function targetIsRequired(target) {
  return target.required !== false;
}

function evaluateMatrix({ config, checkRuns, scope, pull, targetOverrides = {} }) {
  const targets = (config.targets || [])
    .filter((target) => targetApplies(target, scope, pull))
    .map((target) => {
      const run = matchingCheckRun(checkRuns, target);
      const override = targetOverrides[target.id] || {};
      const state = override.state || checkRunState(run, target);
      return {
        ...target,
        checkRun: run || null,
        state,
        conclusion: override.conclusion || run?.conclusion || '',
        status: override.status || run?.status || '',
        url: override.url || run?.html_url || run?.details_url || '',
        required: targetIsRequired(target),
      };
    });

  const blocking = targets.filter((target) => target.required && ['missing', 'recoverable', 'failed'].includes(target.state));
  const pending = targets.filter((target) => target.required && target.state === 'pending');
  return {
    targets,
    pending,
    blocking,
    passed: blocking.length === 0 && pending.length === 0,
  };
}

function planRepairs({ targets, workflowRuns, mode, pull, fingerprint, event = eventName, eventPayload = {} }) {
  if (!['repair', 'enforce'].includes(mode)) return [];
  const plans = [];
  const dispatchPlans = new Map();
  const ref = pull?.base?.ref || process.env.GITHUB_REF_NAME || 'main';
  const inputs = {
    pr_number: String(pull?.number || prNumber),
    head_sha: String(fingerprint?.head_sha || pull?.head?.sha || ''),
  };
  for (const target of targets) {
    if (!target.required) continue;
    const shouldRefreshPendingCopilotGate = target.id === 'copilot-review-gate'
      && target.state === 'pending'
      && target.workflowFile
      && isCopilotReviewSignal(event, eventPayload);
    if (!target.repairable || (!['missing', 'recoverable'].includes(target.state) && !shouldRefreshPendingCopilotGate)) continue;
    if (target.checkRun
      && ['queued', 'in_progress'].includes(String(target.checkRun.status || ''))
      && !shouldRefreshPendingCopilotGate) {
      continue;
    }
    if ((target.state === 'missing' || shouldRefreshPendingCopilotGate) && target.workflowFile) {
      if (!dispatchPlans.has(target.workflowFile)) {
        dispatchPlans.set(target.workflowFile, {
          target: target.id,
          targets: [],
          action: 'dispatch-workflow',
          workflow_file: target.workflowFile,
          ref,
          inputs,
          reason: shouldRefreshPendingCopilotGate ? 'copilot-review-refresh' : 'missing',
        });
      }
      dispatchPlans.get(target.workflowFile).targets.push({
        id: target.id,
        name: target.name,
        jobName: target.jobName,
        customCheck: Boolean(target.customCheck),
        acceptableConclusions: target.acceptableConclusions || ['success'],
      });
      continue;
    }
    const job = findLatestWorkflowJob({ workflowRuns, workflowName: target.workflowName, jobName: target.jobName });
    if (job) {
      plans.push({ target: target.id, action: 'rerun-job', job_id: job.id, reason: target.state });
    } else {
      plans.push({ target: target.id, action: 'manual', reason: '未找到可重跑的 workflow job' });
    }
  }
  return [...dispatchPlans.values(), ...plans];
}

function workflowRunPullRequestNumber(run) {
  const pullRequests = Array.isArray(run?.pull_requests) ? run.pull_requests : [];
  return Number(pullRequests[0]?.number || '0');
}

function workflowRunMatchesPull({ run, pull, fingerprint }) {
  const runPrNumber = workflowRunPullRequestNumber(run);
  const runHeadSha = String(run?.head_sha || '');
  const pullNumber = Number(pull?.number || prNumber || '0');
  const headSha = String(fingerprint?.head_sha || pull?.head?.sha || '');
  return runPrNumber === pullNumber && (!headSha || runHeadSha === headSha);
}

function planWorkflowRunRecovery({ event = eventName, eventPayload, pull, fingerprint, mode }) {
  if (!['repair', 'enforce'].includes(mode)) return [];
  if (event !== 'workflow_run') return [];

  const run = eventPayload?.workflow_run || {};
  const conclusion = String(run?.conclusion || '');
  if (run?.name !== 'PR Governance'
    || String(run?.event || '') !== 'pull_request_review'
    || !isCopilotActor(run?.actor?.login)
    || !workflowRunMatchesPull({ run, pull, fingerprint })) {
    return [];
  }

  if (conclusion === 'action_required') {
    return [{
      target: 'pr-governance-copilot-review-run',
      action: 'approve-run',
      run_id: run.id,
      reason: 'action_required',
    }];
  }

  if (conclusion !== 'failure' || Number(run?.run_attempt || 0) > 1) return [];
  const failedJobs = fetchWorkflowJobs(run.id)
    .filter((job) => ['failure', 'cancelled', 'timed_out'].includes(String(job?.conclusion || '')));
  const updateJob = failedJobs.find((job) => job?.name === 'Update Copilot Review Check');
  if (failedJobs.length !== 1 || !updateJob) return [];

  return [{
    target: 'copilot-review-gate',
    action: 'rerun-job',
    job_id: updateJob.id,
    reason: 'copilot-review-check-failure',
  }];
}

function findLatestWorkflowJob({ workflowRuns, workflowName, jobName }) {
  if (!workflowName || !jobName) return null;
  const runs = (workflowRuns || [])
    .filter((run) => run.name === workflowName)
    .sort((a, b) => String(a.created_at || '').localeCompare(String(b.created_at || '')));
  for (const run of runs.reverse()) {
    const jobs = Array.isArray(run.jobs) ? run.jobs : [];
    const job = jobs.find((candidate) => candidate.name === jobName);
    if (job) return job;
  }
  return null;
}

function matrixConclusion(matrix, mode = matrixMode) {
  if (matrix.pending.length > 0) {
    if (mode === 'enforce') {
      return { status: 'completed', conclusion: 'failure', title: 'PR 验证矩阵尚未完成' };
    }
    return { status: 'in_progress', conclusion: undefined, title: 'PR 验证矩阵等待中' };
  }
  if (matrix.passed) {
    return { status: 'completed', conclusion: 'success', title: 'PR 验证矩阵已通过' };
  }
  return { status: 'completed', conclusion: 'failure', title: 'PR 验证矩阵未通过' };
}

function summaryLines({ pull, fingerprint, scope, matrix, plans, mode }) {
  const targetLines = matrix.targets.map((target) => {
    const suffix = target.url ? ` (${target.url})` : '';
    const required = target.required ? 'required' : 'advisory';
    return `- ${target.name}: ${target.state}${target.conclusion ? ` / ${target.conclusion}` : ''} [${required}]${suffix}`;
  });
  const planLines = plans.length
    ? plans.map((plan) => {
      const target = plan.targets?.length ? plan.targets.map((item) => item.id).join(', ') : plan.target;
      return `- ${target}: ${plan.action}${plan.workflow_file ? ` ${plan.workflow_file}` : ''}${plan.job_id ? ` job #${plan.job_id}` : ''}${plan.run_id ? ` run #${plan.run_id}` : ''} (${plan.reason})`;
    })
    : ['- 无'];

  return [
    `- Mode: ${mode}`,
    `- Scope: ${scope}`,
    `- PR: #${pull?.number || prNumber}`,
    `- Head: ${fingerprint.head_sha || 'unknown'}`,
    `- Fingerprint: ${fingerprint.value || 'unknown'}`,
    '',
    '### Matrix targets',
    ...targetLines,
    '',
    '### Repair plan',
    ...planLines,
  ];
}

function writeStepSummary(title, lines) {
  const body = [`## ${title}`, '', ...lines, ''].join('\n');
  if (process.env.GITHUB_STEP_SUMMARY) {
    fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, body, 'utf8');
  }
  console.log(body);
}

function upsertMatrixCheck({ config, pull, fingerprint, conclusion, lines, apply }) {
  if (!apply || !checkToken()) return;
  const name = config.gateName || 'PR Validation Matrix Gate';
  const headSha = fingerprint.head_sha || pull?.head?.sha || '';
  const existing = latestByStartedAt((fetchCheckRuns(headSha) || []).filter((run) => run.name === name));
  const payload = {
    name,
    status: conclusion.status,
    output: {
      title: conclusion.title,
      summary: lines.slice(0, 5).join('\n'),
      text: lines.join('\n'),
    },
    details_url: process.env.GITHUB_SERVER_URL && repo && process.env.GITHUB_RUN_ID
      ? `${process.env.GITHUB_SERVER_URL}/${repo}/actions/runs/${process.env.GITHUB_RUN_ID}`
      : undefined,
    external_id: `pr-${pull?.number || prNumber}-${fingerprint.value || headSha}`,
  };
  if (conclusion.conclusion) payload.conclusion = conclusion.conclusion;

  if (!existing) {
    gh([
      '--method', 'POST',
      `repos/${repo}/check-runs`,
      '--input', '-',
    ], JSON.stringify({ ...payload, head_sha: headSha }), checkToken());
    return;
  }

  gh([
    '--method', 'PATCH',
    `repos/${repo}/check-runs/${existing.id}`,
    '--input', '-',
  ], JSON.stringify(payload), checkToken());
}

function dispatchWorkflow(plan) {
  gh([
    '--method', 'POST',
    `repos/${repo}/actions/workflows/${plan.workflow_file}/dispatches`,
    '--input', '-',
  ], JSON.stringify({
    ref: plan.ref,
    inputs: plan.inputs,
  }), actionsToken());
}

function workflowDispatchRunMatches(run, plan) {
  const title = String(run?.display_title || run?.name || '');
  const prNeedle = `#${plan.inputs?.pr_number || ''}`;
  const headSha = String(plan.inputs?.head_sha || '');
  return String(run?.event || '') === 'workflow_dispatch'
    && (!prNeedle || title.includes(prNeedle))
    && (!headSha || title.includes(headSha));
}

function fetchWorkflowDispatchRuns(workflowFile) {
  const payload = ghJson([
    '--method', 'GET',
    `repos/${repo}/actions/workflows/${workflowFile}/runs`,
    '-f', 'event=workflow_dispatch',
    '-f', 'per_page=20',
  ], undefined, actionsToken() || process.env.GH_TOKEN || '') || {};
  return Array.isArray(payload.workflow_runs) ? payload.workflow_runs : [];
}

function waitForDispatchedWorkflowRun(plan, dispatchedAt) {
  const waitSeconds = parsePositiveInteger(process.env.VALIDATION_MATRIX_REPAIR_WAIT_SECONDS, 180);
  const pollSeconds = parsePositiveInteger(process.env.VALIDATION_MATRIX_REPAIR_POLL_SECONDS, 5);
  const deadline = Date.now() + waitSeconds * 1000;
  const threshold = Date.parse(dispatchedAt) - 30000;
  let latest = null;

  while (Date.now() <= deadline) {
    latest = fetchWorkflowDispatchRuns(plan.workflow_file)
      .filter((run) => {
        const created = Date.parse(run?.created_at || '');
        return Number.isFinite(created)
          && created >= threshold
          && workflowDispatchRunMatches(run, plan);
      })
      .sort((a, b) => String(a.created_at || '').localeCompare(String(b.created_at || '')))
      .at(-1) || null;

    if (latest && latest.status === 'completed') {
      return {
        ...latest,
        jobs: fetchWorkflowJobs(latest.id),
      };
    }
    sleepMilliseconds(pollSeconds * 1000);
  }

  return latest ? { ...latest, jobs: latest.status === 'completed' ? fetchWorkflowJobs(latest.id) : [] } : null;
}

function dispatchResultOverrides(plan, run) {
  const overrides = [];
  if (!run) {
    return plan.targets.map((target) => ({
      id: target.id,
      state: 'recoverable',
      status: 'completed',
      conclusion: 'timed_out',
      url: '',
    }));
  }

  for (const target of plan.targets) {
    if (target.customCheck) continue;
    const job = (run.jobs || []).find((candidate) => candidate.name === target.jobName);
    overrides.push({
      id: target.id,
      state: checkRunState(job, target),
      status: job?.status || run.status || '',
      conclusion: job?.conclusion || run.conclusion || '',
      url: job?.html_url || run.html_url || '',
    });
  }
  return overrides;
}

function targetOverrideMap(overrides) {
  const map = {};
  for (const override of overrides || []) {
    if (override?.id) map[override.id] = override;
  }
  return map;
}

function applyRepairPlans(plans, apply) {
  if (!apply || !actionsToken()) return [];
  const overrides = [];
  for (const plan of plans) {
    if (plan.action === 'approve-run') {
      gh([
        '--method', 'POST',
        `repos/${repo}/actions/runs/${plan.run_id}/approve`,
      ], undefined, actionsToken());
    } else if (plan.action === 'rerun-job') {
      gh([
        '--method', 'POST',
        `repos/${repo}/actions/jobs/${plan.job_id}/rerun`,
      ], undefined, actionsToken());
    } else if (plan.action === 'dispatch-workflow') {
      const dispatchedAt = new Date().toISOString();
      dispatchWorkflow(plan);
      const run = waitForDispatchedWorkflowRun(plan, dispatchedAt);
      overrides.push(...dispatchResultOverrides(plan, run));
    }
  }
  return overrides;
}

function readEventPayload() {
  const eventPath = process.env.GITHUB_EVENT_PATH || '';
  if (!eventPath || !fs.existsSync(eventPath)) return {};
  return readJson(eventPath);
}

function fetchAll(apiPath) {
  const all = [];
  for (let page = 1; page <= 10; page += 1) {
    const items = ghJson([
      '--method', 'GET',
      apiPath,
      '-f', 'per_page=100',
      '-f', `page=${page}`,
    ]) || [];
    if (!Array.isArray(items) || !items.length) break;
    all.push(...items);
    if (items.length < 100) break;
  }
  return all;
}

function fetchCheckRuns(headSha) {
  if (!headSha) return [];
  const payload = ghJson([
    '--method', 'GET',
    `repos/${repo}/commits/${headSha}/check-runs`,
    '-f', 'per_page=100',
  ]) || {};
  return Array.isArray(payload.check_runs) ? payload.check_runs : [];
}

function fetchWorkflowRuns(headSha) {
  if (!headSha) return [];
  const payload = ghJson([
    '--method', 'GET',
    `repos/${repo}/actions/runs`,
    '-f', `head_sha=${headSha}`,
    '-f', 'per_page=50',
  ], undefined, actionsToken() || process.env.GH_TOKEN || '') || {};
  const runs = Array.isArray(payload.workflow_runs) ? payload.workflow_runs : [];
  return runs.map((run) => ({
    ...run,
    jobs: fetchWorkflowJobs(run.id),
  }));
}

function fetchWorkflowJobs(runId) {
  if (!runId) return [];
  const payload = ghJson([
    '--method', 'GET',
    `repos/${repo}/actions/runs/${runId}/jobs`,
    '-f', 'per_page=100',
  ], undefined, actionsToken() || process.env.GH_TOKEN || '') || {};
  return Array.isArray(payload.jobs) ? payload.jobs : [];
}

function previousMatrixFingerprint(checkRuns, gateName) {
  const previous = latestByStartedAt((checkRuns || []).filter((run) => run.name === gateName));
  const match = String(previous?.external_id || '').match(/pr-\d+-([a-f0-9]{64})$/i);
  return match?.[1] || '';
}

function loadContext(config) {
  if (!repo || !owner || !repoName || !prNumber) {
    throw new Error('Missing repository or pull request context.');
  }
  const pull = ghJson(['--method', 'GET', `repos/${repo}/pulls/${prNumber}`]) || {};
  const commits = fetchAll(`repos/${repo}/pulls/${prNumber}/commits`);
  const files = fetchAll(`repos/${repo}/pulls/${prNumber}/files`);
  const fingerprint = fingerprintForPull({ pull, commits, files });
  const checkRuns = fetchCheckRuns(fingerprint.head_sha);
  const previousFingerprint = previousMatrixFingerprint(checkRuns, config.gateName || 'PR Validation Matrix Gate');
  const scope = eventScope({
    event: eventName,
    action: eventAction,
    requested: requestedScope,
    previousFingerprint,
    currentFingerprint: fingerprint,
  });
  return { pull, commits, files, fingerprint, checkRuns, previousFingerprint, scope };
}

function main(argv = process.argv.slice(2)) {
  const args = parseArgs(argv);
  const config = readJson(configPath);
  const eventPayload = readEventPayload();
  const context = loadContext(config);
  const workflowRuns = fetchWorkflowRuns(context.fingerprint.head_sha);
  let matrix = evaluateMatrix({
    config,
    checkRuns: context.checkRuns,
    scope: context.scope,
    pull: context.pull,
  });
  const workflowRunRecoveryPlans = planWorkflowRunRecovery({
    event: eventName,
    eventPayload,
    pull: context.pull,
    fingerprint: context.fingerprint,
    mode: matrixMode,
  });
  const plans = workflowRunRecoveryPlans.length
    ? workflowRunRecoveryPlans
    : planRepairs({
      targets: matrix.targets,
      workflowRuns,
      mode: matrixMode,
      pull: context.pull,
      fingerprint: context.fingerprint,
      event: eventName,
      eventPayload,
    });
  const activeRepairApplied = args.apply && ['repair', 'enforce'].includes(matrixMode);
  const repairOverrides = applyRepairPlans(plans, activeRepairApplied);
  const shouldRefreshAfterRepair = activeRepairApplied
    && (repairOverrides.length > 0 || plans.some((plan) => plan.action === 'dispatch-workflow'));
  if (shouldRefreshAfterRepair) {
    matrix = evaluateMatrix({
      config,
      checkRuns: fetchCheckRuns(context.fingerprint.head_sha),
      scope: context.scope,
      pull: context.pull,
      targetOverrides: targetOverrideMap(repairOverrides),
    });
  }
  const conclusion = matrixConclusion(matrix, matrixMode);
  const lines = summaryLines({
    pull: context.pull,
    fingerprint: context.fingerprint,
    scope: context.scope,
    matrix,
    plans,
    mode: matrixMode,
  });

  upsertMatrixCheck({
    config,
    pull: context.pull,
    fingerprint: context.fingerprint,
    conclusion,
    lines,
    apply: args.apply,
  });
  writeStepSummary(conclusion.title, lines);

  if (conclusion.status === 'completed' && conclusion.conclusion === 'failure' && matrixMode === 'enforce') {
    process.exit(1);
  }
}

if (require.main === module) {
  main();
} else {
  module.exports = {
    checkRunState,
    eventScope,
    evaluateMatrix,
    fingerprintForPull,
    matrixConclusion,
    planRepairs,
    planWorkflowRunRecovery,
    previousMatrixFingerprint,
    summaryLines,
    workflowRunPullRequestNumber,
  };
}
