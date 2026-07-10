const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const {
  fingerprintForPull,
} = require('./pr-validation-fingerprint');
const { fetchPullRequestPages } = require('./pr-api-pagination');

const repo = process.env.GITHUB_REPOSITORY || '';
const [owner, repoName] = repo.split('/');
const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
let prNumber = Number(process.env.PR_NUMBER || process.env.GITHUB_EVENT_PULL_REQUEST_NUMBER || '0');
const eventName = process.env.GITHUB_EVENT_NAME || '';
const eventAction = process.env.GITHUB_EVENT_ACTION || '';
const matrixMode = process.env.VALIDATION_MATRIX_MODE || 'enforce';
const requestedScope = process.env.VALIDATION_MATRIX_SCOPE || 'auto';
const validationAppSlug = process.env.VALIDATION_MATRIX_APP_SLUG || '';
const configPath = process.env.PR_VALIDATION_MATRIX_CONFIG
  || path.join(workspace, '.github', 'pr-validation-matrix.json');

function parseArgs(argv) {
  return {
    apply: argv.includes('--apply'),
    dryRun: argv.includes('--dry-run') || !argv.includes('--apply'),
  };
}

function mutationsEnabled(apply, mode) {
  return Boolean(apply) && mode !== 'observe';
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

function checkToken() {
  return process.env.GH_CHECKS_TOKEN || process.env.GH_TOKEN || '';
}

function actionsToken() {
  return process.env.GH_ACTIONS_TOKEN || process.env.GH_TOKEN || '';
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

function reviewSignalWorkflowRun(event, eventPayload) {
  if (event !== 'workflow_run') return null;
  const run = eventPayload?.workflow_run || {};
  return run?.name === 'PR Review Signal' ? run : null;
}

const trustedWorkflowRunTitleSources = new Map([
  ['PR Classification', { workflowFile: 'pr-classification.yml', events: ['pull_request_target', 'workflow_dispatch'] }],
  ['DCO Sign-off Advisory', { workflowFile: 'dco-check.yml', events: ['pull_request_target', 'workflow_dispatch'] }],
  ['PR Governance', { workflowFile: 'pr-governance.yml', events: ['pull_request_target', 'workflow_dispatch'] }],
]);

function parseTrustedWorkflowRunContext(run) {
  const source = trustedWorkflowRunTitleSources.get(String(run?.name || ''));
  if (!source
    || workflowRunPath(run) !== `.github/workflows/${source.workflowFile}`
    || !source.events.includes(String(run?.event || ''))) return null;
  const match = String(run.display_title || '').match(/#[1-9][0-9]* \/ [a-f0-9]{40}(?: \/|$)/i);
  if (!match) return null;
  const parts = match[0].match(/#([1-9][0-9]*) \/ ([a-f0-9]{40})/i);
  return parts ? { prNumber: Number(parts[1]), headSha: parts[2].toLowerCase() } : null;
}

function selectPullRequestByHead(pulls, headSha, defaultBranch, repository = repo) {
  const normalizedHead = String(headSha || '').toLowerCase();
  const normalizedRepository = String(repository || '').toLowerCase();
  const matches = (pulls || []).filter((pull) => (
    String(pull?.state || '') === 'open'
    && String(pull?.head?.sha || '').toLowerCase() === normalizedHead
    && String(pull?.base?.ref || '') === String(defaultBranch || '')
    && String(pull?.base?.repo?.full_name || '').toLowerCase() === normalizedRepository
  ));
  return matches.length === 1 ? matches[0] : null;
}

function findPullRequestByHead(headSha, defaultBranch) {
  if (!/^[a-f0-9]{40}$/i.test(String(headSha || '')) || !defaultBranch) return null;
  const pulls = ghJson([`repos/${repo}/commits/${headSha}/pulls?per_page=100`]) || [];
  return selectPullRequestByHead(pulls, headSha, defaultBranch);
}

function resolveEventPullRequestContext({ payload = {}, env = process.env, findPullByHead } = {}) {
  const nativePull = payload.pull_request
    || payload.workflow_run?.pull_requests?.[0]
    || payload.check_run?.pull_requests?.[0]
    || null;
  if (nativePull?.number) {
    const trustedRunContext = parseTrustedWorkflowRunContext(payload.workflow_run);
    return {
      prNumber: Number(nativePull.number),
      expectedHeadSha: String(
        payload.pull_request?.head?.sha
          || payload.check_run?.head_sha
          || trustedRunContext?.headSha
          || payload.workflow_run?.head_sha
          || '',
      ).toLowerCase(),
      source: 'native',
    };
  }

  const inputPrNumber = Number(env.WORKFLOW_INPUT_PR_NUMBER || env.PR_NUMBER || '0');
  if (inputPrNumber > 0) {
    return {
      prNumber: inputPrNumber,
      expectedHeadSha: String(env.WORKFLOW_INPUT_HEAD_SHA || '').toLowerCase(),
      source: 'workflow-input',
    };
  }

  if (String(env.GITHUB_EVENT_NAME || '') === 'repository_dispatch') {
    const clientPayload = payload.client_payload || {};
    return {
      prNumber: Number(clientPayload.pr_number || '0'),
      expectedHeadSha: String(clientPayload.head_sha || '').toLowerCase(),
      source: 'repository-dispatch',
      repositoryId: Number(clientPayload.repository_id || '0'),
      action: String(clientPayload.action || ''),
      deliveryId: String(clientPayload.delivery_id || ''),
    };
  }

  const workflowRun = payload.workflow_run || {};
  const workflowRunHeadSha = String(workflowRun.head_sha || '').toLowerCase();
  const defaultBranch = String(payload.repository?.default_branch || '');
  if (workflowRun.name === 'PR Review Signal'
    && /^[a-f0-9]{40}$/.test(workflowRunHeadSha)
    && defaultBranch
    && typeof findPullByHead === 'function') {
    const pull = findPullByHead(workflowRunHeadSha, defaultBranch);
    if (pull?.number) {
      return {
        prNumber: Number(pull.number),
        expectedHeadSha: workflowRunHeadSha,
        source: 'workflow-run-head',
      };
    }
  }

  const parsedRun = parseTrustedWorkflowRunContext(payload.workflow_run);
  return parsedRun
    ? { prNumber: parsedRun.prNumber, expectedHeadSha: parsedRun.headSha, source: 'workflow-run-title' }
    : { prNumber: 0, expectedHeadSha: '', source: 'unresolved' };
}

function validateEventPullRequestContext({ context, payload = {}, pull, repository = repo }) {
  if (!context?.prNumber) return '无法解析 PR 编号';
  if (!pull?.number || Number(pull.number) !== Number(context.prNumber)) return 'PR 编号不匹配';
  if (String(pull.state || '') !== 'open') return 'PR 已关闭';
  const defaultBranch = String(payload.repository?.default_branch || pull.base?.repo?.default_branch || '');
  if (!defaultBranch) return '无法确认仓库默认分支';
  if (String(pull.base?.ref || '') !== defaultBranch) return `PR 目标分支不是默认分支 ${defaultBranch}`;
  if (context.expectedHeadSha && String(pull.head?.sha || '').toLowerCase() !== context.expectedHeadSha) {
    return '事件对应的 head 已过期';
  }
  if (context.source === 'repository-dispatch') {
    if (!['resolved', 'unresolved'].includes(context.action)) return 'repository dispatch action 无效';
    if (!context.deliveryId) return 'repository dispatch 缺少 delivery_id';
    if (!context.repositoryId || context.repositoryId !== Number(payload.repository?.id || '0')) {
      return 'repository dispatch 仓库 ID 不匹配';
    }
    if (String(payload.repository?.full_name || '') !== repository) return 'repository dispatch 仓库不匹配';
  }
  return '';
}

function eventScope({
  event,
  action,
  requested = 'auto',
  previousFingerprint,
  currentFingerprint,
  eventPayload = {},
}) {
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
  if (event === 'repository_dispatch') return 'gate-only';
  if (reviewSignalWorkflowRun(event, eventPayload)) return 'gate-only';
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

function isValidationAppCheck(run, appSlug = validationAppSlug) {
  return !appSlug || String(run?.app?.slug || '') === appSlug;
}

function checkRunWorkflowRunId(run) {
  const match = String(run?.details_url || '').match(/\/actions\/runs\/(\d+)(?:\/job\/\d+)?(?:\?.*)?$/);
  return Number(match?.[1] || '0');
}

function workflowRunPath(run) {
  return String(run?.path || '').split('@')[0].replace(/\\/g, '/').toLowerCase();
}

function workflowRunMatchesTarget(run, target) {
  const trustedEvents = target.trustedEvents || ['pull_request_target', 'workflow_dispatch'];
  return run?.name === target.workflowName
    && workflowRunPath(run) === `.github/workflows/${String(target.workflowFile || '').toLowerCase()}`
    && trustedEvents.includes(String(run?.event || ''));
}

function trustedDirectExternalId(target, pull, fingerprint) {
  if (target.id === 'pr-classification') {
    return `classification:pr:${pull.number}:fingerprint:${fingerprint.value}`;
  }
  if (target.id === 'copilot-review-gate') {
    return `pr-${pull.number}-${pull.head.sha}`;
  }
  return '';
}

function isTrustedCheckRun({ run, target, pull, fingerprint, workflowRuns = [], appSlug = validationAppSlug }) {
  if (!run || !target || !pull?.number || !pull?.head?.sha) return false;
  const externalId = String(run.external_id || '');
  if (isValidationAppCheck(run, appSlug)) {
    if (externalId === proxyExternalId(target, pull, fingerprint)) return true;
    const directExternalId = trustedDirectExternalId(target, pull, fingerprint || {});
    return Boolean(directExternalId) && externalId === directExternalId;
  }
  if (String(run?.app?.slug || '') !== 'github-actions' || target.customCheck) return false;

  const workflowRunId = checkRunWorkflowRunId(run);
  const workflowRun = (workflowRuns || []).find((candidate) => Number(candidate?.id) === workflowRunId);
  if (!workflowRunMatchesTarget(workflowRun, target)) return false;
  return workflowRunMatchesPull({ run: workflowRun, pull, fingerprint });
}

function matchingCheckRun(checkRuns, target, trust) {
  const names = new Set(target.checkNames || []);
  const matches = (checkRuns || []).filter((run) => (
    names.has(run.name) && (!trust || isTrustedCheckRun({ run, target, ...trust }))
  ));
  const activeProxies = matches.filter((run) => (
    String(run.external_id || '').startsWith('matrix-proxy:')
      && ['queued', 'in_progress'].includes(String(run.status || ''))
  ));
  return latestByStartedAt(activeProxies.length ? activeProxies : matches);
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

function evaluateMatrix({ config, checkRuns, scope, pull, targetOverrides = {}, trust }) {
  const targets = (config.targets || [])
    .filter((target) => targetApplies(target, scope, pull))
    .map((target) => {
      const run = matchingCheckRun(checkRuns, target, trust);
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
  const ref = pull?.base?.ref || process.env.REPOSITORY_DEFAULT_BRANCH || process.env.GITHUB_REF_NAME || '';
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
    const shouldRefreshFailedCopilotGate = target.id === 'copilot-review-gate'
      && target.state === 'failed'
      && target.workflowFile
      && event === 'workflow_dispatch';
    const reviewSignal = reviewSignalWorkflowRun(event, eventPayload);
    const reviewThreadSignal = event === 'repository_dispatch'
      && ['resolved', 'unresolved'].includes(String(eventPayload?.client_payload?.action || ''));
    const shouldRefreshReviewSignalTarget = Boolean(reviewSignal)
      && target.workflowFile === 'pr-governance.yml'
      && (
        (String(reviewSignal.event || '') === 'pull_request_review'
          && ['main-authorization', 'main-gate', 'copilot-review-gate'].includes(target.id))
        || (String(reviewSignal.event || '') === 'pull_request_review_comment'
          && target.id === 'copilot-review-gate')
        || (String(reviewSignal.event || '') === 'pull_request'
          && target.id === 'copilot-review-gate')
      );
    const shouldRefreshReviewThreadTarget = reviewThreadSignal && target.id === 'copilot-review-gate';
    if (!target.repairable
      || (!['missing', 'recoverable'].includes(target.state)
        && !shouldRefreshPendingCopilotGate
        && !shouldRefreshFailedCopilotGate
        && !shouldRefreshReviewSignalTarget
        && !shouldRefreshReviewThreadTarget)) continue;
    const hasActiveProxy = ['queued', 'in_progress'].includes(String(target.checkRun?.status || ''))
      && String(target.checkRun?.external_id || '').startsWith('matrix-proxy:');
    if (hasActiveProxy) continue;
    if (target.checkRun
      && ['queued', 'in_progress'].includes(String(target.checkRun.status || ''))
      && !shouldRefreshPendingCopilotGate
      && !shouldRefreshReviewSignalTarget
      && !shouldRefreshReviewThreadTarget) {
      continue;
    }
    if ((target.state === 'missing'
      || shouldRefreshPendingCopilotGate
      || shouldRefreshFailedCopilotGate
      || shouldRefreshReviewSignalTarget
      || shouldRefreshReviewThreadTarget)
      && target.workflowFile) {
      if (!dispatchPlans.has(target.workflowFile)) {
        dispatchPlans.set(target.workflowFile, {
          target: target.id,
          targets: [],
          action: 'dispatch-workflow',
          workflow_file: target.workflowFile,
          ref,
          inputs: { ...inputs },
          reason: shouldRefreshPendingCopilotGate
            ? 'copilot-review-refresh'
            : shouldRefreshFailedCopilotGate
              ? 'copilot-state-refresh'
              : shouldRefreshReviewSignalTarget
                ? 'review-state-refresh'
                : shouldRefreshReviewThreadTarget
                  ? 'review-thread-refresh'
                : 'missing',
        });
      }
      dispatchPlans.get(target.workflowFile).targets.push({
        id: target.id,
        name: target.name,
        checkName: target.checkNames?.[0] || target.name,
        workflowName: target.workflowName,
        jobName: target.jobName,
        customCheck: Boolean(target.customCheck),
        acceptableConclusions: target.acceptableConclusions || ['success'],
      });
      continue;
    }
    const job = findLatestWorkflowJob({ workflowRuns, target, pull, fingerprint });
    if (job) {
      plans.push({ target: target.id, action: 'rerun-job', job_id: job.id, reason: target.state });
    } else {
      plans.push({ target: target.id, action: 'manual', reason: '未找到可重跑的 workflow job' });
    }
  }
  for (const plan of dispatchPlans.values()) {
    if (plan.workflow_file !== 'pr-governance.yml') continue;
    const targetIds = new Set(plan.targets.map((target) => target.id));
    plan.inputs.governance_scope = targetIds.size === 1 && targetIds.has('copilot-review-gate')
      ? 'copilot-review'
      : targetIds.size === 1 && (targetIds.has('main-authorization') || targetIds.has('main-gate'))
        ? 'main-authorization'
        : 'all';
  }
  return [...dispatchPlans.values(), ...plans];
}

function workflowRunPullRequestNumber(run) {
  const pullRequests = Array.isArray(run?.pull_requests) ? run.pull_requests : [];
  return Number(pullRequests[0]?.number || '0');
}

function workflowRunMatchesPull({ run, pull, fingerprint }) {
  const runPrNumber = workflowRunPullRequestNumber(run);
  const parsed = parseTrustedWorkflowRunContext(run);
  const resolvedPrNumber = runPrNumber || parsed?.prNumber || 0;
  const runHeadSha = String(parsed?.headSha || run?.head_sha || '');
  const pullNumber = Number(pull?.number || prNumber || '0');
  const headSha = String(fingerprint?.head_sha || pull?.head?.sha || '');
  return resolvedPrNumber === pullNumber && (!headSha || runHeadSha === headSha);
}

function findLatestWorkflowJob({ workflowRuns, target, pull, fingerprint }) {
  if (!target?.workflowName || !target?.jobName) return null;
  const runs = (workflowRuns || [])
    .filter((run) => (
      workflowRunMatchesTarget(run, target)
        && workflowRunMatchesPull({ run, pull, fingerprint })
    ))
    .sort((a, b) => String(a.created_at || '').localeCompare(String(b.created_at || '')));
  for (const run of runs.reverse()) {
    const jobs = Array.isArray(run.jobs) ? run.jobs : fetchWorkflowJobs(run.id);
    const job = jobs.find((candidate) => candidate.name === target.jobName);
    if (job) return job;
  }
  return null;
}

function matrixConclusion(matrix, mode = matrixMode) {
  if (matrix.pending.length > 0) {
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
    `- Repair scope: ${scope}`,
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

function upsertMatrixCheck({ config, pull, fingerprint, conclusion, lines, apply, checkRuns }) {
  if (!apply || !checkToken()) return null;
  const name = config.gateName || 'PR Validation Matrix Gate';
  const headSha = fingerprint.head_sha || pull?.head?.sha || '';
  const existing = latestByStartedAt(((checkRuns || fetchCheckRuns(headSha)) || []).filter((run) => (
    run.name === name && isValidationAppCheck(run)
  )));
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

  const shouldCreate = !existing || (existing.status === 'completed' && conclusion.status !== 'completed');
  if (shouldCreate) {
    return ghJson([
      '--method', 'POST',
      `repos/${repo}/check-runs`,
      '--input', '-',
    ], JSON.stringify({ ...payload, head_sha: headSha }), checkToken());
  }

  return ghJson([
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

function proxyExternalId(target, pull, fingerprint) {
  return `matrix-proxy:${target.id}:pr:${pull.number}:head:${pull.head.sha}:fingerprint:${fingerprint?.value || ''}`;
}

function currentWorkflowRunUrl() {
  const serverUrl = process.env.GITHUB_SERVER_URL || 'https://github.com';
  const runId = process.env.GITHUB_RUN_ID || '';
  return repo && runId ? `${serverUrl}/${repo}/actions/runs/${runId}` : undefined;
}

function activeProxyCheck(checkRuns, target, pull, fingerprint) {
  const externalId = proxyExternalId(target, pull, fingerprint);
  return latestByStartedAt((checkRuns || []).filter((run) => (
    run.external_id === externalId
      && isValidationAppCheck(run)
      && ['queued', 'in_progress'].includes(String(run.status || ''))
  )));
}

function createProxyCheck({ target, pull, fingerprint, checkRuns }) {
  const existing = activeProxyCheck(checkRuns, target, pull, fingerprint);
  if (existing) return existing;
  const name = target.checkNames?.[0] || target.name;
  const payload = {
    name,
    head_sha: pull.head.sha,
    status: 'in_progress',
    external_id: proxyExternalId(target, pull, fingerprint),
    details_url: currentWorkflowRunUrl(),
    output: {
      title: '等待一次性补跑结果',
      summary: `验证矩阵已触发 ${target.workflowName || target.name}，后续 workflow_run 事件将更新状态。`,
    },
  };
  return ghJson([
    '--method', 'POST',
    `repos/${repo}/check-runs`,
    '--input', '-',
  ], JSON.stringify(payload), checkToken());
}

function completeProxyCheck({ proxy, state, conclusion, url }) {
  if (!proxy) return;
  gh([
    '--method', 'PATCH',
    `repos/${repo}/check-runs/${proxy.id}`,
    '--input', '-',
  ], JSON.stringify({
    status: 'completed',
    conclusion,
    details_url: url || proxy.details_url,
    output: {
      title: state === 'passed' ? '一次性补跑已通过' : '一次性补跑未通过',
      summary: `子 workflow 已完成：${conclusion || 'unknown'}`,
    },
  }), checkToken());
}

function reconcileWorkflowRunCompletion({ config, eventPayload, pull, fingerprint, checkRuns, jobs, apply, event = eventName }) {
  if (event !== 'workflow_run' || eventPayload?.workflow_run?.status !== 'completed') return [];
  const run = eventPayload.workflow_run;
  if (!workflowRunMatchesPull({ run, pull, fingerprint })) return [];
  const overrides = [];
  for (const target of (config.targets || []).filter((item) => item.workflowName === run.name)) {
    if (!workflowRunMatchesTarget(run, target)) continue;
    const proxy = activeProxyCheck(checkRuns, target, pull, fingerprint);
    if (!proxy) continue;
    const job = (jobs || []).find((candidate) => candidate.name === target.jobName);
    const state = target.customCheck ? 'failed' : checkRunState(job, target);
    const jobConclusion = String(job?.conclusion || run.conclusion || '');
    const conclusion = state === 'passed'
      ? 'success'
      : ['cancelled', 'timed_out', 'skipped', 'action_required', 'stale'].includes(jobConclusion)
        ? jobConclusion
        : 'failure';
    if (apply) completeProxyCheck({ proxy, state, conclusion, url: job?.html_url || run.html_url || '' });
    proxy.status = 'completed';
    proxy.conclusion = conclusion;
    overrides.push({
      id: target.id,
      state,
      status: 'completed',
      conclusion,
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

function fingerprintInvalidationOverrides(config, previousFingerprint, currentFingerprint) {
  if (!previousFingerprint || previousFingerprint === currentFingerprint?.value) return [];
  return (config.targets || [])
    .filter((target) => target.fingerprintBound)
    .map((target) => ({ id: target.id, state: 'missing', status: '', conclusion: '' }));
}

function applyRepairPlans(plans, apply, {
  pull,
  fingerprint,
  checkRuns,
  dispatch = dispatchWorkflow,
  api = gh,
  createProxy = createProxyCheck,
} = {}) {
  if (!apply || !actionsToken()) return [];
  const overrides = [];
  for (const plan of plans) {
    if (plan.action === 'rerun-job') {
      api([
        '--method', 'POST',
        `repos/${repo}/actions/jobs/${plan.job_id}/rerun`,
      ], undefined, actionsToken());
      overrides.push({ id: plan.target, state: 'pending', status: 'in_progress', conclusion: '' });
    } else if (plan.action === 'dispatch-workflow') {
      dispatch(plan);
      for (const target of plan.targets || []) {
        const configTarget = { ...target, checkNames: [target.checkName], workflowName: target.workflowName };
        if (pull && checkToken()) createProxy({ target: configTarget, pull, fingerprint, checkRuns });
        overrides.push({ id: target.id, state: 'pending', status: 'in_progress', conclusion: '' });
      }
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
  return fetchPullRequestPages((page, pageSize) => ghJson([
    '--method', 'GET',
    apiPath,
    '-f', `per_page=${pageSize}`,
    '-f', `page=${page}`,
  ]) || []);
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
  return runs;
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

function previousMatrixFingerprint(checkRuns, gateName, appSlug = validationAppSlug) {
  const previous = latestByStartedAt((checkRuns || []).filter((run) => (
    run.name === gateName && isValidationAppCheck(run, appSlug)
  )));
  const match = String(previous?.external_id || '').match(/pr-\d+-([a-f0-9]{64})$/i);
  return match?.[1] || '';
}

function loadContext(config, eventPayload = {}, eventContext = resolveEventPullRequestContext({ payload: eventPayload })) {
  if (!repo || !owner || !repoName || !prNumber) {
    throw new Error('Missing repository or pull request context.');
  }
  const pull = ghJson(['--method', 'GET', `repos/${repo}/pulls/${prNumber}`]) || {};
  const ignoredReason = validateEventPullRequestContext({ context: eventContext, payload: eventPayload, pull });
  if (ignoredReason) return { pull, ignoredReason };
  const commits = fetchAll(`repos/${repo}/pulls/${prNumber}/commits`);
  const files = fetchAll(`repos/${repo}/pulls/${prNumber}/files`);
  const fingerprint = fingerprintForPull({ pull, commits, files });
  const checkRuns = fetchCheckRuns(fingerprint.head_sha);
  const previousFingerprint = previousMatrixFingerprint(
    checkRuns,
    config.gateName || 'PR Validation Matrix Gate',
  );
  const scope = eventScope({
    event: eventName,
    action: eventAction,
    requested: requestedScope,
    previousFingerprint,
    currentFingerprint: fingerprint,
    eventPayload,
  });
  return { pull, commits, files, fingerprint, checkRuns, previousFingerprint, scope };
}

function main(argv = process.argv.slice(2)) {
  const args = parseArgs(argv);
  const config = readJson(configPath);
  const eventPayload = readEventPayload();
  const eventContext = resolveEventPullRequestContext({
    payload: eventPayload,
    findPullByHead: findPullRequestByHead,
  });
  prNumber = eventContext.prNumber;
  if (!prNumber) {
    writeStepSummary('PR 验证矩阵已忽略事件', [`- 原因：${eventContext.source === 'unresolved' ? '无法从受信任事件解析 PR' : 'PR 编号无效'}`]);
    return;
  }
  if (!validationAppSlug) {
    throw new Error('Missing VALIDATION_MATRIX_APP_SLUG for trusted Check validation.');
  }
  const context = loadContext(config, eventPayload, eventContext);
  if (context.ignoredReason) {
    writeStepSummary('PR 验证矩阵已忽略事件', [`- PR：#${prNumber}`, `- 原因：${context.ignoredReason}`]);
    return;
  }
  const currentRunJobs = eventName === 'workflow_run' ? fetchWorkflowJobs(eventPayload.workflow_run?.id) : [];
  const workflowRuns = fetchWorkflowRuns(context.fingerprint.head_sha);
  const applyMutations = mutationsEnabled(args.apply, matrixMode);
  if (eventName === 'workflow_run'
    && eventPayload.workflow_run?.id
    && !workflowRuns.some((run) => Number(run.id) === Number(eventPayload.workflow_run.id))) {
    workflowRuns.push({ ...eventPayload.workflow_run, jobs: currentRunJobs });
  }
  const trust = {
    workflowRuns,
    pull: context.pull,
    fingerprint: context.fingerprint,
    appSlug: validationAppSlug,
  };
  const fingerprintOverrides = fingerprintInvalidationOverrides(
    config,
    context.previousFingerprint,
    context.fingerprint,
  );
  const completionOverrides = reconcileWorkflowRunCompletion({
    config,
    eventPayload,
    pull: context.pull,
    fingerprint: context.fingerprint,
    checkRuns: context.checkRuns,
    jobs: currentRunJobs,
    apply: applyMutations,
  });
  let matrix = evaluateMatrix({
    config,
    checkRuns: context.checkRuns,
    scope: 'full',
    pull: context.pull,
    targetOverrides: targetOverrideMap([...fingerprintOverrides, ...completionOverrides]),
    trust,
  });
  const plans = planRepairs({
    targets: matrix.targets.filter((target) => targetApplies(target, context.scope, context.pull)),
    workflowRuns,
    mode: matrixMode,
    pull: context.pull,
    fingerprint: context.fingerprint,
    event: eventName,
    eventPayload,
  });
  const activeRepairApplied = applyMutations && ['repair', 'enforce'].includes(matrixMode);
  const actionablePlans = plans.filter((plan) => plan.action !== 'manual');
  if (activeRepairApplied && actionablePlans.length > 0) {
    const pendingMatrixCheck = upsertMatrixCheck({
      config,
      pull: context.pull,
      fingerprint: context.fingerprint,
      conclusion: { status: 'in_progress', conclusion: undefined, title: 'PR 验证矩阵执行一次性补跑' },
      lines: actionablePlans.map((plan) => `- ${plan.target}: ${plan.action}`),
      apply: true,
      checkRuns: context.checkRuns,
    });
    if (pendingMatrixCheck && !context.checkRuns.some((run) => run.id === pendingMatrixCheck.id)) {
      context.checkRuns.push(pendingMatrixCheck);
    }
  }
  let repairOverrides = [];
  try {
    repairOverrides = applyRepairPlans(plans, activeRepairApplied, {
      pull: context.pull,
      fingerprint: context.fingerprint,
      checkRuns: context.checkRuns,
    });
  } catch (error) {
    upsertMatrixCheck({
      config,
      pull: context.pull,
      fingerprint: context.fingerprint,
      conclusion: { status: 'completed', conclusion: 'failure', title: 'PR 验证矩阵补跑失败' },
      lines: [`- ${String(error?.message || error).slice(0, 1000)}`],
      apply: applyMutations,
      checkRuns: context.checkRuns,
    });
    throw error;
  }
  if (repairOverrides.length > 0) {
    matrix = evaluateMatrix({
      config,
      checkRuns: context.checkRuns,
      scope: 'full',
      pull: context.pull,
      targetOverrides: targetOverrideMap([
        ...fingerprintOverrides,
        ...completionOverrides,
        ...repairOverrides,
      ]),
      trust,
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
    apply: applyMutations,
    checkRuns: context.checkRuns,
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
    activeProxyCheck,
    applyRepairPlans,
    checkRunState,
    eventScope,
    evaluateMatrix,
    fingerprintForPull,
    fingerprintInvalidationOverrides,
    isTrustedCheckRun,
    matrixConclusion,
    mutationsEnabled,
    parseTrustedWorkflowRunContext,
    planRepairs,
    previousMatrixFingerprint,
    proxyExternalId,
    reconcileWorkflowRunCompletion,
    resolveEventPullRequestContext,
    selectPullRequestByHead,
    summaryLines,
    validateEventPullRequestContext,
    workflowRunPullRequestNumber,
  };
}
