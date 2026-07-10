const assert = require('node:assert/strict');

const {
  applyRepairPlans,
  checkRunState,
  eventScope,
  evaluateMatrix,
  fingerprintForPull,
  isTrustedCheckRun,
  matrixConclusion,
  planRepairs,
  planWorkflowRunRecovery,
  previousMatrixFingerprint,
  proxyExternalId,
  reconcileWorkflowRunCompletion,
  resolveEventPullRequestContext,
  validateEventPullRequestContext,
  workflowRunPullRequestNumber,
} = require('./pr-validation-matrix');
const { classificationInputBody } = require('./pr-validation-fingerprint');
const { fetchPullRequestPages } = require('./pr-api-pagination');

const config = {
  gateName: 'PR Validation Matrix Gate',
  targets: [
    {
      id: 'pr-classification',
      name: 'PR Classification Gate',
      checkNames: ['PR Classification Gate'],
      workflowName: 'PR Classification',
      workflowFile: 'pr-classification.yml',
      jobName: 'Classify Pull Request',
      group: 'full',
      acceptableConclusions: ['success'],
      repairable: true,
      customCheck: true,
    },
    {
      id: 'main-gate',
      name: 'PR Governance / Main Authorization Gate',
      checkNames: ['Main Authorization Gate'],
      workflowName: 'PR Governance',
      workflowFile: 'pr-governance.yml',
      jobName: 'Main Authorization Gate',
      group: 'gate',
      acceptableConclusions: ['success'],
      repairable: true,
    },
    {
      id: 'copilot-review-gate',
      name: 'Copilot Code Review Gate',
      checkNames: ['Copilot Code Review Gate'],
      workflowName: 'PR Governance',
      workflowFile: 'pr-governance.yml',
      jobName: 'Update Copilot Review Check',
      group: 'gate',
      acceptableConclusions: ['success'],
      repairable: true,
      customCheck: true,
    },
    {
      id: 'dco',
      name: 'DCO Sign-off Advisory',
      checkNames: ['DCO Sign-off Advisory'],
      workflowName: 'DCO Sign-off Advisory',
      jobName: 'DCO Sign-off Advisory',
      group: 'full',
      acceptableConclusions: ['success'],
      required: false,
      repairable: true,
    },
  ],
};

function run(name, status, conclusion, extra = {}) {
  return {
    name,
    status,
    conclusion,
    started_at: extra.started_at || '2026-07-10T00:00:00Z',
    external_id: extra.external_id || '',
    id: extra.id || 1,
    app: extra.app,
    details_url: extra.details_url || '',
  };
}

const fingerprint = fingerprintForPull({
  pull: {
    number: 1,
    body: '<!-- workflow:source-contributors:splrad -->',
    user: { login: 'splrad-workflow-automation' },
    head: { sha: 'head1' },
    base: { ref: 'main', sha: 'base1' },
  },
  commits: [
    { sha: 'a', author: { login: 'splrad' } },
    { sha: 'b', author: { login: 'external-dev' } },
  ],
  files: [
    { filename: 'src/A.cs', status: 'modified', sha: 'file1', additions: 1, deletions: 0 },
  ],
});
assert.equal(fingerprint.head_sha, 'head1');
assert.deepEqual(fingerprint.contributors, ['external-dev', 'splrad', 'splrad-workflow-automation']);
assert.equal(fingerprint.value.length, 64);

const requestedPages = [];
const maximumFiles = fetchPullRequestPages((page, pageSize) => {
  requestedPages.push(page);
  return Array.from({ length: pageSize }, (_, index) => `${page}:${index}`);
});
assert.equal(maximumFiles.length, 3000);
assert.deepEqual(requestedPages, Array.from({ length: 30 }, (_, index) => index + 1));
let partialPageCalls = 0;
assert.equal(fetchPullRequestPages((page, pageSize) => {
  partialPageCalls += 1;
  return Array.from({ length: page === 1 ? pageSize : 7 }, (_, index) => index);
}).length, 107);
assert.equal(partialPageCalls, 2);

const classificationMetadata = `\n<!-- workflow:pr-classification:start\nlabels: bug\nworkflow:pr-classification:end -->`;
const classificationFingerprint = (overrides = {}) => fingerprintForPull({
  pull: {
    number: 1,
    title: overrides.title || 'fix: correct gate',
    body: overrides.body || `Contributor context${classificationMetadata}`,
    user: { login: 'splrad' },
    head: { sha: 'head1' },
    base: { ref: 'main', sha: 'base1' },
  },
  commits: [],
  files: [],
});
assert.equal(classificationInputBody(`Contributor context${classificationMetadata}`), 'Contributor context');
assert.equal(
  classificationFingerprint().value,
  classificationFingerprint({ body: `Contributor context\n<!-- workflow:pr-classification:start\nlabels: docs\nworkflow:pr-classification:end -->` }).value,
);
assert.notEqual(classificationFingerprint().value, classificationFingerprint({ title: 'docs: correct gate' }).value);
assert.notEqual(classificationFingerprint().value, classificationFingerprint({ body: 'Different contributor context' }).value);

const trustedPull = { number: 1, head: { sha: 'head1' } };
const classificationCheck = run('PR Classification Gate', 'completed', 'success', {
  app: { slug: 'splrad-workflow-automation' },
  external_id: `classification:pr:1:fingerprint:${fingerprint.value}`,
});
assert.equal(isTrustedCheckRun({
  run: classificationCheck,
  target: config.targets.find((target) => target.id === 'pr-classification'),
  pull: trustedPull,
  fingerprint,
  appSlug: 'splrad-workflow-automation',
}), true);
assert.equal(isTrustedCheckRun({
  run: { ...classificationCheck, app: { slug: 'github-actions' } },
  target: config.targets.find((target) => target.id === 'pr-classification'),
  pull: trustedPull,
  fingerprint,
  appSlug: 'splrad-workflow-automation',
}), false);
assert.equal(isTrustedCheckRun({
  run: { ...classificationCheck, external_id: 'classification:pr:1:fingerprint:stale' },
  target: config.targets.find((target) => target.id === 'pr-classification'),
  pull: trustedPull,
  fingerprint,
  appSlug: 'splrad-workflow-automation',
}), false);

const mainGateTarget = config.targets.find((target) => target.id === 'main-gate');
const mainGateCheck = run('Main Authorization Gate', 'completed', 'success', {
  app: { slug: 'github-actions' },
  details_url: 'https://github.com/splrad/CADFontAutoReplace/actions/runs/123/job/456',
});
const trustedMainWorkflow = {
  id: 123,
  name: 'PR Governance',
  path: '.github/workflows/pr-governance.yml@refs/heads/main',
  event: 'pull_request_target',
  head_sha: 'head1',
  pull_requests: [{ number: 1 }],
};
assert.equal(isTrustedCheckRun({
  run: mainGateCheck,
  target: mainGateTarget,
  pull: trustedPull,
  fingerprint,
  workflowRuns: [trustedMainWorkflow],
  appSlug: 'splrad-workflow-automation',
}), true);
for (const workflowOverride of [
  { path: '.github/workflows/untrusted.yml@refs/heads/main' },
  { event: 'push' },
  { head_sha: 'stale-head' },
]) {
  assert.equal(isTrustedCheckRun({
    run: mainGateCheck,
    target: mainGateTarget,
    pull: trustedPull,
    fingerprint,
    workflowRuns: [{ ...trustedMainWorkflow, ...workflowOverride }],
    appSlug: 'splrad-workflow-automation',
  }), false);
}

const copilotTargetForTrust = config.targets.find((target) => target.id === 'copilot-review-gate');
const copilotCheck = run('Copilot Code Review Gate', 'completed', 'success', {
  app: { slug: 'splrad-workflow-automation' },
  external_id: 'pr-1-head1',
});
assert.equal(isTrustedCheckRun({
  run: copilotCheck,
  target: copilotTargetForTrust,
  pull: trustedPull,
  fingerprint,
  appSlug: 'splrad-workflow-automation',
}), true);
assert.equal(isTrustedCheckRun({
  run: { ...copilotCheck, app: { slug: 'github-actions' } },
  target: copilotTargetForTrust,
  pull: trustedPull,
  fingerprint,
  appSlug: 'splrad-workflow-automation',
}), false);

const trustedProxy = run('Main Authorization Gate', 'in_progress', '', {
  app: { slug: 'splrad-workflow-automation' },
  external_id: 'matrix-proxy:main-gate:pr:1:head:head1',
});
assert.equal(isTrustedCheckRun({
  run: trustedProxy,
  target: mainGateTarget,
  pull: trustedPull,
  fingerprint,
  appSlug: 'splrad-workflow-automation',
}), true);

assert.equal(eventScope({
  event: 'pull_request_target',
  action: 'synchronize',
  previousFingerprint: fingerprint.value,
  currentFingerprint: fingerprint,
}), 'full');

assert.equal(eventScope({
  event: 'pull_request_review',
  action: 'submitted',
  previousFingerprint: fingerprint.value,
  currentFingerprint: fingerprint,
}), 'gate-only');

assert.equal(eventScope({
  event: 'pull_request_review',
  action: 'submitted',
  previousFingerprint: '',
  currentFingerprint: fingerprint,
}), 'full');

assert.equal(eventScope({
  event: 'workflow_run',
  previousFingerprint: fingerprint.value,
  currentFingerprint: fingerprint,
  eventPayload: {
    workflow_run: { name: 'PR Review Signal', event: 'pull_request_review' },
  },
}), 'gate-only');

assert.equal(eventScope({
  event: 'pull_request_target',
  action: 'labeled',
  previousFingerprint: fingerprint.value,
  currentFingerprint: fingerprint,
}), 'gate-only');

assert.equal(eventScope({
  event: 'pull_request_target',
  action: 'edited',
  previousFingerprint: 'different',
  currentFingerprint: fingerprint,
}), 'full');

assert.equal(checkRunState(null, config.targets[0]), 'missing');
assert.equal(checkRunState(run('x', 'queued', ''), config.targets[0]), 'pending');
assert.equal(checkRunState(run('x', 'completed', 'success'), config.targets[0]), 'passed');
assert.equal(checkRunState(run('x', 'completed', 'cancelled'), config.targets[0]), 'recoverable');
assert.equal(checkRunState(run('x', 'completed', 'failure'), config.targets[0]), 'failed');

const activeProxyWins = evaluateMatrix({
  config,
  scope: 'gate-only',
  checkRuns: [
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'in_progress', '', {
      external_id: 'matrix-proxy:copilot-review-gate:pr:1:head:head1',
      started_at: '2026-07-10T00:00:00Z',
    }),
    run('Copilot Code Review Gate', 'completed', 'success', {
      started_at: '2026-07-10T00:01:00Z',
      id: 2,
    }),
  ],
});
assert.equal(activeProxyWins.targets.find((target) => target.id === 'copilot-review-gate').state, 'pending');

const pendingMatrix = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('PR Classification Gate', 'queued', ''),
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
  ],
});
assert.equal(matrixConclusion(pendingMatrix, 'observe').status, 'in_progress');
assert.equal(matrixConclusion(pendingMatrix, 'enforce').status, 'in_progress');
assert.equal(matrixConclusion(pendingMatrix, 'enforce').conclusion, undefined);

const fullPassed = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('PR Classification Gate', 'completed', 'success'),
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
  ],
});
assert.equal(fullPassed.passed, true);
assert.equal(fullPassed.targets.find((target) => target.id === 'dco').state, 'missing');
assert.equal(fullPassed.targets.find((target) => target.id === 'dco').required, false);
assert.equal(matrixConclusion(fullPassed).conclusion, 'success');

const gateOnly = evaluateMatrix({
  config,
  scope: 'gate-only',
  checkRuns: [
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
  ],
});
assert.deepEqual(gateOnly.targets.map((target) => target.id), ['main-gate', 'copilot-review-gate']);
assert.equal(gateOnly.passed, true);

const reviewSignalPlans = planRepairs({
  targets: gateOnly.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
  event: 'workflow_run',
  eventPayload: {
    workflow_run: { name: 'PR Review Signal', event: 'pull_request_review' },
  },
});
assert.equal(reviewSignalPlans.length, 1);
assert.equal(reviewSignalPlans[0].reason, 'review-state-refresh');
assert.equal(reviewSignalPlans[0].inputs.governance_scope, 'all');

const reviewCommentSignalPlans = planRepairs({
  targets: gateOnly.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
  event: 'workflow_run',
  eventPayload: {
    workflow_run: { name: 'PR Review Signal', event: 'pull_request_review_comment' },
  },
});
assert.equal(reviewCommentSignalPlans.length, 1);
assert.equal(reviewCommentSignalPlans[0].inputs.governance_scope, 'copilot-review');

const reviewRequestedSignalPlans = planRepairs({
  targets: gateOnly.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
  event: 'workflow_run',
  eventPayload: {
    workflow_run: { name: 'PR Review Signal', event: 'pull_request' },
  },
});
assert.equal(reviewRequestedSignalPlans.length, 1);
assert.equal(reviewRequestedSignalPlans[0].inputs.governance_scope, 'copilot-review');

const missingMainGate = evaluateMatrix({
  config,
  scope: 'gate-only',
  checkRuns: [run('Copilot Code Review Gate', 'completed', 'success')],
});
const missingMainPlans = planRepairs({
  targets: missingMainGate.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
});
assert.equal(missingMainPlans[0].inputs.governance_scope, 'main-authorization');

const missingBothGates = evaluateMatrix({ config, scope: 'gate-only', checkRuns: [] });
const missingBothPlans = planRepairs({
  targets: missingBothGates.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
});
assert.equal(missingBothPlans.length, 1);
assert.equal(missingBothPlans[0].inputs.governance_scope, 'all');

const missingClassification = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
  ],
});
const dispatchPlans = planRepairs({
  targets: missingClassification.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
});
assert.deepEqual(dispatchPlans, [{
  target: 'pr-classification',
  targets: [{
    id: 'pr-classification',
    name: 'PR Classification Gate',
    checkName: 'PR Classification Gate',
    workflowName: 'PR Classification',
    jobName: 'Classify Pull Request',
    customCheck: true,
    acceptableConclusions: ['success'],
  }],
  action: 'dispatch-workflow',
  workflow_file: 'pr-classification.yml',
  ref: 'main',
  inputs: { pr_number: '1', head_sha: 'head1' },
  reason: 'missing',
}]);

const cancelledClassification = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('PR Classification Gate', 'completed', 'cancelled'),
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
  ],
});
const repairPlans = planRepairs({
  targets: cancelledClassification.targets,
  workflowRuns: [
    {
      name: 'PR Classification',
      created_at: '2026-07-10T00:00:00Z',
      jobs: [{ name: 'Classify Pull Request', id: 42 }],
    },
  ],
  mode: 'repair',
});
assert.deepEqual(repairPlans, [{
  target: 'pr-classification',
  action: 'rerun-job',
  job_id: 42,
  reason: 'recoverable',
}]);

const pendingCopilotGate = evaluateMatrix({
  config,
  scope: 'gate-only',
  checkRuns: [
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'in_progress', ''),
  ],
});
assert.deepEqual(planRepairs({
  targets: pendingCopilotGate.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
  event: 'pull_request_review',
  eventPayload: {
    review: { user: { login: 'copilot-pull-request-reviewer[bot]' } },
  },
}), [{
  target: 'copilot-review-gate',
  targets: [{
    id: 'copilot-review-gate',
    name: 'Copilot Code Review Gate',
    checkName: 'Copilot Code Review Gate',
    workflowName: 'PR Governance',
    jobName: 'Update Copilot Review Check',
    customCheck: true,
    acceptableConclusions: ['success'],
  }],
  action: 'dispatch-workflow',
  workflow_file: 'pr-governance.yml',
  ref: 'main',
  inputs: { pr_number: '1', head_sha: 'head1', governance_scope: 'copilot-review' },
  reason: 'copilot-review-refresh',
}]);
assert.equal(planRepairs({
  targets: pendingCopilotGate.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
  event: 'pull_request_review_comment',
  eventPayload: {
    comment: { user: { login: 'splrad' } },
  },
}).length, 0);

const failedCopilotGate = evaluateMatrix({
  config,
  scope: 'gate-only',
  checkRuns: [
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'failure'),
  ],
});
const failedRefreshPlans = planRepairs({
  targets: failedCopilotGate.targets,
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 1, base: { ref: 'main' }, head: { sha: 'head1' } },
  fingerprint,
  event: 'workflow_dispatch',
});
assert.equal(failedRefreshPlans.length, 1);
assert.equal(failedRefreshPlans[0].reason, 'copilot-state-refresh');
assert.equal(failedRefreshPlans[0].inputs.governance_scope, 'copilot-review');

assert.equal(previousMatrixFingerprint([
  run('PR Validation Matrix Gate', 'completed', 'success', {
    external_id: `pr-1-${fingerprint.value}`,
  }),
], 'PR Validation Matrix Gate'), fingerprint.value);
assert.equal(previousMatrixFingerprint([
  run('PR Validation Matrix Gate', 'completed', 'success', {
    external_id: `pr-1-${fingerprint.value}`,
    app: { slug: 'untrusted-app' },
  }),
], 'PR Validation Matrix Gate', 'splrad-workflow-automation'), '');
assert.equal(previousMatrixFingerprint([
  run('PR Validation Matrix Gate', 'completed', 'success', {
    external_id: `pr-1-${fingerprint.value}`,
    app: { slug: 'splrad-workflow-automation' },
  }),
], 'PR Validation Matrix Gate', 'splrad-workflow-automation'), fingerprint.value);

assert.equal(workflowRunPullRequestNumber({ pull_requests: [{ number: 121 }] }), 121);
assert.equal(workflowRunPullRequestNumber({ pull_requests: [] }), 0);
assert.equal(workflowRunPullRequestNumber({}), 0);

assert.deepEqual(planWorkflowRunRecovery({
  event: 'workflow_run',
  mode: 'enforce',
  pull: { number: 1, head: { sha: 'head1' } },
  fingerprint,
  eventPayload: {
    workflow_run: {
      id: 123,
      name: 'PR Governance',
      path: '.github/workflows/pr-governance.yml@refs/heads/main',
      event: 'pull_request_review',
      conclusion: 'action_required',
      actor: { login: 'copilot-pull-request-reviewer[bot]' },
      head_sha: 'head1',
      pull_requests: [{ number: 1 }],
    },
  },
}), [{
  target: 'pr-governance-copilot-review-run',
  action: 'approve-run',
  run_id: 123,
  reason: 'action_required',
}]);

assert.deepEqual(planWorkflowRunRecovery({
  event: 'workflow_run',
  mode: 'observe',
  pull: { number: 1, head: { sha: 'head1' } },
  fingerprint,
  eventPayload: {
    workflow_run: {
      id: 123,
      name: 'PR Governance',
      path: '.github/workflows/pr-governance.yml@refs/heads/main',
      event: 'pull_request_review',
      conclusion: 'action_required',
      actor: { login: 'copilot-pull-request-reviewer[bot]' },
      head_sha: 'head1',
      pull_requests: [{ number: 1 }],
    },
  },
}), []);

assert.deepEqual(planWorkflowRunRecovery({
  event: 'workflow_run',
  mode: 'enforce',
  pull: { number: 1, head: { sha: 'head1' } },
  fingerprint,
  eventPayload: {
    workflow_run: {
      id: 123,
      name: 'PR Governance',
      path: '.github/workflows/untrusted.yml',
      event: 'pull_request_review',
      conclusion: 'action_required',
      actor: { login: 'copilot-pull-request-reviewer[bot]' },
      head_sha: 'head1',
      pull_requests: [{ number: 1 }],
    },
  },
}), []);

const failedGovernanceRecovery = {
  id: 124,
  name: 'PR Governance',
  path: '.github/workflows/pr-governance.yml',
  event: 'pull_request_review',
  conclusion: 'failure',
  run_attempt: 1,
  actor: { login: 'copilot-pull-request-reviewer[bot]' },
  head_sha: 'head1',
  pull_requests: [{ number: 1 }],
};
assert.deepEqual(planWorkflowRunRecovery({
  event: 'workflow_run',
  mode: 'enforce',
  pull: { number: 1, head: { sha: 'head1' } },
  fingerprint,
  eventPayload: { workflow_run: failedGovernanceRecovery },
  jobs: [{ id: 456, name: 'Update Copilot Review Check', conclusion: 'failure' }],
}), [{
  target: 'copilot-review-gate',
  action: 'rerun-job',
  job_id: 456,
  reason: 'copilot-review-check-failure',
}]);
assert.deepEqual(planWorkflowRunRecovery({
  event: 'workflow_run',
  mode: 'enforce',
  pull: { number: 1, head: { sha: 'head1' } },
  fingerprint,
  eventPayload: {
    workflow_run: { ...failedGovernanceRecovery, path: '.github/workflows/untrusted.yml' },
  },
  jobs: [{ id: 456, name: 'Update Copilot Review Check', conclusion: 'failure' }],
}), []);

const eventHeadSha = 'a'.repeat(40);
assert.deepEqual(resolveEventPullRequestContext({
  payload: {
    workflow_run: {
      name: 'PR Governance',
      display_title: `PR Governance #121 / ${eventHeadSha}`,
      pull_requests: [],
    },
  },
  env: { GITHUB_EVENT_NAME: 'workflow_run' },
}), {
  prNumber: 121,
  expectedHeadSha: eventHeadSha,
  source: 'workflow-run-title',
});

const dispatchPayload = {
  repository: { id: 42, full_name: 'splrad/CADFontAutoReplace', default_branch: 'main' },
  client_payload: {
    repository_id: 42,
    pr_number: 121,
    head_sha: eventHeadSha,
    action: 'resolved',
    delivery_id: 'delivery-1',
  },
};
const dispatchContext = resolveEventPullRequestContext({
  payload: dispatchPayload,
  env: { GITHUB_EVENT_NAME: 'repository_dispatch' },
});
assert.equal(validateEventPullRequestContext({
  context: dispatchContext,
  payload: dispatchPayload,
  repository: 'splrad/CADFontAutoReplace',
  pull: { number: 121, state: 'open', base: { ref: 'main' }, head: { sha: eventHeadSha } },
}), '');
assert.match(validateEventPullRequestContext({
  context: dispatchContext,
  payload: dispatchPayload,
  repository: 'splrad/CADFontAutoReplace',
  pull: { number: 121, state: 'open', base: { ref: 'main' }, head: { sha: 'b'.repeat(40) } },
}), /head 已过期/);
assert.match(validateEventPullRequestContext({
  context: { ...dispatchContext, repositoryId: 99 },
  payload: dispatchPayload,
  repository: 'splrad/CADFontAutoReplace',
  pull: { number: 121, state: 'open', base: { ref: 'main' }, head: { sha: eventHeadSha } },
}), /仓库 ID 不匹配/);
assert.equal(validateEventPullRequestContext({
  context: dispatchContext,
  payload: {
    ...dispatchPayload,
    repository: { ...dispatchPayload.repository, default_branch: 'trunk' },
  },
  repository: 'splrad/CADFontAutoReplace',
  pull: { number: 121, state: 'open', base: { ref: 'trunk' }, head: { sha: eventHeadSha } },
}), '');

const resolvedRefreshPlans = planRepairs({
  targets: [{
    ...config.targets.find((target) => target.id === 'copilot-review-gate'),
    required: true,
    state: 'failed',
    checkRun: run('Copilot Code Review Gate', 'completed', 'failure'),
  }],
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 121, base: { ref: 'main' }, head: { sha: eventHeadSha } },
  fingerprint: { head_sha: eventHeadSha },
  event: 'repository_dispatch',
  eventPayload: dispatchPayload,
});
assert.equal(resolvedRefreshPlans.length, 1);
assert.equal(resolvedRefreshPlans[0].reason, 'review-thread-refresh');
assert.equal(resolvedRefreshPlans[0].inputs.governance_scope, 'copilot-review');

const previousActionsToken = process.env.GH_ACTIONS_TOKEN;
const previousChecksToken = process.env.GH_CHECKS_TOKEN;
process.env.GH_ACTIONS_TOKEN = 'test-actions-token';
process.env.GH_CHECKS_TOKEN = 'test-checks-token';
try {
  const dispatched = [];
  const proxies = [];
  assert.deepEqual(applyRepairPlans(resolvedRefreshPlans, true, {
    pull: { number: 121, head: { sha: eventHeadSha } },
    checkRuns: [],
    dispatch: (plan) => dispatched.push(plan),
    createProxy: (value) => proxies.push(value),
  }), [{
    id: 'copilot-review-gate',
    state: 'pending',
    status: 'in_progress',
    conclusion: '',
  }]);
  assert.equal(dispatched.length, 1);
  assert.equal(proxies.length, 1);
} finally {
  if (previousActionsToken === undefined) delete process.env.GH_ACTIONS_TOKEN;
  else process.env.GH_ACTIONS_TOKEN = previousActionsToken;
  if (previousChecksToken === undefined) delete process.env.GH_CHECKS_TOKEN;
  else process.env.GH_CHECKS_TOKEN = previousChecksToken;
}

assert.equal(planRepairs({
  targets: [{
    ...config.targets.find((target) => target.id === 'copilot-review-gate'),
    required: true,
    state: 'pending',
    checkRun: {
      status: 'in_progress',
      external_id: `matrix-proxy:copilot-review-gate:pr:121:head:${eventHeadSha}`,
    },
  }],
  workflowRuns: [],
  mode: 'enforce',
  pull: { number: 121, base: { ref: 'main' }, head: { sha: eventHeadSha } },
  fingerprint: { head_sha: eventHeadSha },
  event: 'repository_dispatch',
  eventPayload: dispatchPayload,
}).length, 0);

const proxyPull = { number: 121, head: { sha: eventHeadSha } };
const classificationTarget = config.targets.find((target) => target.id === 'pr-classification');
const classificationProxy = {
  id: 9001,
  name: 'PR Classification Gate',
  status: 'in_progress',
  external_id: proxyExternalId(classificationTarget, proxyPull),
  started_at: '2026-07-10T00:00:00Z',
};
assert.deepEqual(reconcileWorkflowRunCompletion({
  event: 'workflow_run',
  config,
  eventPayload: {
    workflow_run: {
      name: 'PR Classification',
      display_title: `PR Classification #121 / ${eventHeadSha}`,
      status: 'completed',
      conclusion: 'success',
      pull_requests: [],
    },
  },
  pull: proxyPull,
  checkRuns: [classificationProxy],
  jobs: [{ name: 'Classify Pull Request', status: 'completed', conclusion: 'success' }],
  apply: false,
}), [{
  id: 'pr-classification',
  state: 'failed',
  status: 'completed',
  conclusion: 'failure',
  url: '',
}]);

const copilotTarget = config.targets.find((target) => target.id === 'copilot-review-gate');
const copilotProxy = {
  id: 9002,
  name: 'Copilot Code Review Gate',
  status: 'in_progress',
  external_id: proxyExternalId(copilotTarget, proxyPull),
  started_at: '2026-07-10T00:00:00Z',
};
assert.deepEqual(reconcileWorkflowRunCompletion({
  event: 'workflow_run',
  config,
  eventPayload: {
    workflow_run: {
      name: 'PR Governance',
      display_title: `PR Governance #121 / ${eventHeadSha}`,
      status: 'completed',
      conclusion: 'failure',
      pull_requests: [],
    },
  },
  pull: proxyPull,
  checkRuns: [copilotProxy],
  jobs: [{ name: 'Update Copilot Review Check', status: 'completed', conclusion: 'failure' }],
  apply: false,
}), [{
  id: 'copilot-review-gate',
  state: 'failed',
  status: 'completed',
  conclusion: 'failure',
  url: '',
}]);

const untouchedCopilotProxy = { ...copilotProxy, status: 'in_progress', conclusion: '' };
assert.deepEqual(reconcileWorkflowRunCompletion({
  event: 'workflow_run',
  config,
  eventPayload: {
    workflow_run: {
      name: 'PR Governance',
      display_title: `PR Governance #121 / ${eventHeadSha}`,
      status: 'completed',
      conclusion: 'success',
      pull_requests: [],
    },
  },
  pull: proxyPull,
  checkRuns: [untouchedCopilotProxy],
  jobs: [{ name: 'Update Copilot Review Check', status: 'completed', conclusion: 'success' }],
  apply: false,
}), [{
  id: 'copilot-review-gate',
  state: 'failed',
  status: 'completed',
  conclusion: 'failure',
  url: '',
}]);

console.log('pr-validation-matrix local tests passed');
