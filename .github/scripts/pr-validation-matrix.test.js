const assert = require('node:assert/strict');

const {
  checkRunState,
  dispatchOpenPullRefreshes,
  eventScope,
  evaluateMatrix,
  fingerprintForPull,
  matrixConclusion,
  planRepairs,
  planWorkflowRunRecovery,
  previousMatrixFingerprint,
  pullNeedsCopilotRefresh,
  workflowRunPullRequestNumber,
} = require('./pr-validation-matrix');

const config = {
  gateName: 'PR Validation Matrix Gate',
  targets: [
    {
      id: 'classification',
      name: 'PR Classification / Classify Pull Request',
      checkNames: ['Classify Pull Request'],
      workflowName: 'PR Classification',
      workflowFile: 'pr-classification.yml',
      jobName: 'Classify Pull Request',
      group: 'full',
      acceptableConclusions: ['success'],
      repairable: true,
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
    {
      id: 'codeql',
      name: 'CodeQL',
      checkNames: ['CodeQL'],
      group: 'full',
      acceptableConclusions: ['success'],
      repairable: false,
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

const pendingMatrix = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('Classify Pull Request', 'queued', ''),
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
    run('CodeQL', 'completed', 'success'),
  ],
});
assert.equal(matrixConclusion(pendingMatrix, 'observe').status, 'in_progress');
assert.equal(matrixConclusion(pendingMatrix, 'enforce').conclusion, 'failure');

const fullPassed = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('Classify Pull Request', 'completed', 'success'),
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
    run('CodeQL', 'completed', 'success'),
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

const missingCodeql = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('Classify Pull Request', 'completed', 'success'),
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
  ],
});
assert.equal(missingCodeql.passed, false);
assert.equal(missingCodeql.blocking.find((target) => target.id === 'codeql').state, 'missing');
assert.equal(planRepairs({
  targets: missingCodeql.targets,
  workflowRuns: [],
  mode: 'repair',
}).some((plan) => plan.target === 'codeql'), false);

const missingClassification = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
    run('CodeQL', 'completed', 'success'),
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
  target: 'classification',
  targets: [{
    id: 'classification',
    name: 'PR Classification / Classify Pull Request',
    jobName: 'Classify Pull Request',
    customCheck: false,
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
    run('Classify Pull Request', 'completed', 'cancelled'),
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
    run('CodeQL', 'completed', 'success'),
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
  target: 'classification',
  action: 'rerun-job',
  job_id: 42,
  reason: 'recoverable',
}]);

const failedCodeCheck = evaluateMatrix({
  config,
  scope: 'full',
  checkRuns: [
    run('Classify Pull Request', 'completed', 'success'),
    run('Main Authorization Gate', 'completed', 'success'),
    run('Copilot Code Review Gate', 'completed', 'success'),
    run('CodeQL', 'completed', 'failure'),
  ],
});
assert.equal(planRepairs({ targets: failedCodeCheck.targets, workflowRuns: [], mode: 'repair' }).length, 0);
assert.equal(matrixConclusion(failedCodeCheck).conclusion, 'failure');

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

assert.equal(pullNeedsCopilotRefresh(
  { number: 1, head: { sha: 'head1' } },
  [run('Copilot Code Review Gate', 'completed', 'failure')],
), true);
assert.equal(pullNeedsCopilotRefresh(
  { number: 1, head: { sha: 'head1' } },
  [run('Copilot Code Review Gate', 'completed', 'success')],
), false);

const scheduledDispatches = [];
const refreshedPulls = dispatchOpenPullRefreshes({
  pulls: [
    { number: 1, head: { sha: 'failed-head' } },
    { number: 2, head: { sha: 'passed-head' } },
  ],
  fetchChecks: (sha) => [run(
    'Copilot Code Review Gate',
    'completed',
    sha === 'failed-head' ? 'failure' : 'success',
  )],
  dispatch: (plan) => scheduledDispatches.push(plan),
  ref: 'main',
  workflowFile: 'pr-validation-matrix.yml',
});
assert.deepEqual(refreshedPulls, [1]);
assert.deepEqual(scheduledDispatches, [{
  workflow_file: 'pr-validation-matrix.yml',
  ref: 'main',
  inputs: { pr_number: '1', scope: 'gate-only', mode: 'enforce' },
}]);

assert.equal(previousMatrixFingerprint([
  run('PR Validation Matrix Gate', 'completed', 'success', {
    external_id: `pr-1-${fingerprint.value}`,
  }),
], 'PR Validation Matrix Gate'), fingerprint.value);

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
      event: 'pull_request_review',
      conclusion: 'action_required',
      actor: { login: 'copilot-pull-request-reviewer[bot]' },
      head_sha: 'head1',
      pull_requests: [{ number: 1 }],
    },
  },
}), []);

console.log('pr-validation-matrix local tests passed');
