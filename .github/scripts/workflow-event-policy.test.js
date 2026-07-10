const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..', '..');
const workflowsDirectory = path.join(root, '.github', 'workflows');
const workflowFiles = fs.readdirSync(workflowsDirectory)
  .filter((file) => /\.ya?ml$/i.test(file))
  .map((file) => path.join(workflowsDirectory, file));

for (const file of workflowFiles) {
  const source = fs.readFileSync(file, 'utf8');
  assert.doesNotMatch(source, /^\s*schedule\s*:/m, `${path.basename(file)} must not use scheduled state refreshes`);
}

const temporalPollingFiles = [
  path.join(root, '.github', 'scripts', 'pr-governance.js'),
  path.join(root, '.github', 'scripts', 'pr-validation-matrix.js'),
  path.join(root, '.github', 'workflows', 'pr-governance.yml'),
  path.join(root, '.github', 'workflows', 'pr-validation-matrix.yml'),
  path.join(root, '.github', 'workflows', 'release-build.yml'),
];
const forbiddenPatterns = [
  /Start-Sleep/i,
  /Atomics\.wait/,
  /\bsetTimeout\s*\(/,
  /\b(?:WAIT|POLL)(?:ING)?_[A-Z0-9_]*\b/,
  /\b[A-Z0-9_]*(?:WAIT|POLL)(?:ING)?_[A-Z0-9_]*\b/,
  /while\s*\([^)]*(?:Date\.now|deadline|attempt)/i,
];

for (const file of temporalPollingFiles) {
  const source = fs.readFileSync(file, 'utf8');
  for (const pattern of forbiddenPatterns) {
    assert.doesNotMatch(source, pattern, `${path.relative(root, file)} contains forbidden temporal polling: ${pattern}`);
  }
}

const matrixConfig = JSON.parse(fs.readFileSync(
  path.join(root, '.github', 'pr-validation-matrix.json'),
  'utf8',
));
assert.equal(matrixConfig.targets.some((target) => target.id === 'codeql'), false);
const classificationTarget = matrixConfig.targets.find((target) => target.id === 'pr-classification');
assert.ok(classificationTarget, 'matrix must include the PR classification gate');
assert.equal(classificationTarget.customCheck, true);
assert.deepEqual(classificationTarget.checkNames, ['PR Classification Gate']);
assert.equal(classificationTarget.fingerprintBound, true);
assert.equal(matrixConfig.targets.find((target) => target.id === 'main-authorization').fingerprintBound, true);
assert.equal(matrixConfig.targets.some((target) => Array.isArray(target.baseBranches)), false);

const matrixSource = fs.readFileSync(
  path.join(root, '.github', 'scripts', 'pr-validation-matrix.js'),
  'utf8',
);
assert.match(matrixSource, /repository\?\.default_branch/);
assert.doesNotMatch(matrixSource, /pull\.base\?\.ref\s*\|\|[^\n]*['"]main['"]/);
assert.doesNotMatch(matrixSource, /\/actions\/runs\/\$\{plan\.run_id\}\/approve/);

const matrixWorkflowSource = fs.readFileSync(
  path.join(root, '.github', 'workflows', 'pr-validation-matrix.yml'),
  'utf8',
);
assert.match(matrixWorkflowSource, /github\.event\.workflow_run\.pull_requests\[0\]\.number/);
assert.match(matrixWorkflowSource, /github\.event\.workflow_run\.display_title/);
assert.match(matrixWorkflowSource, /Untrusted Review Signal/);
assert.match(matrixWorkflowSource, /Ignored Matrix Check \{0\}/);
assert.match(matrixWorkflowSource, /github\.event_name == 'check_run'[\s\S]*github\.event\.action == 'completed'[\s\S]*github\.run_id/);
assert.match(matrixWorkflowSource, /types:\s*\[pr-review-state-changed, pr-review-thread-resolved\]/);
assert.doesNotMatch(matrixWorkflowSource, /workflow_run\.head_branch\s*&&/);

for (const file of ['pr-classification.yml', 'dco-check.yml', 'pr-governance.yml']) {
  const source = fs.readFileSync(path.join(workflowsDirectory, file), 'utf8');
  assert.match(source, /^run-name: "PR Validation Target #\$\{\{/m);
}

const releaseSource = fs.readFileSync(
  path.join(root, '.github', 'workflows', 'release-build.yml'),
  'utf8',
);
assert.match(releaseSource, /^\s{2}pull_request_target:\s*$/m);
assert.match(releaseSource, /^\s{4}types:\s*\[closed\]\s*$/m);
assert.match(releaseSource, /github\.event\.pull_request\.merged\s*==\s*true/);
assert.match(releaseSource, /github\.event\.repository\.default_branch/);
assert.match(releaseSource, /ref:\s*\$\{\{\s*github\.sha\s*\}\}/);
assert.doesNotMatch(releaseSource, /github\.event\.pull_request\.merge_commit_sha/);
assert.doesNotMatch(releaseSource, /^\s{2}workflow_dispatch:\s*$/m);
assert.doesNotMatch(releaseSource, /^\s{2}push:\s*$/m);
assert.doesNotMatch(releaseSource, /commits\/[^\s]*\/pulls/);

const reviewSignalSource = fs.readFileSync(
  path.join(root, '.github', 'workflows', 'pr-review-signal.yml'),
  'utf8',
);
assert.match(reviewSignalSource, /run:\s*\|\r?\n\s+echo "Review state changed for PR #/);
assert.doesNotMatch(reviewSignalSource, /^\s{2}pull_request_review:\s*$/m);
assert.doesNotMatch(reviewSignalSource, /^\s{2}pull_request_review_comment:\s*$/m);

const relayPackage = JSON.parse(fs.readFileSync(
  path.join(root, '.github', 'webhook-relay', 'package.json'),
  'utf8',
));
assert.deepEqual(relayPackage.dependencies, { '@octokit/auth-app': '8.2.0' });
assert.deepEqual(relayPackage.devDependencies, {
  '@cloudflare/workers-types': '5.20260710.1',
  typescript: '7.0.2',
  vitest: '4.1.10',
  wrangler: '4.110.0',
});

const relayWrangler = fs.readFileSync(
  path.join(root, '.github', 'webhook-relay', 'wrangler.toml'),
  'utf8',
);
assert.match(relayWrangler, /\[\[durable_objects\.bindings\]\]/);
assert.match(relayWrangler, /new_sqlite_classes\s*=\s*\["DeliveryCoordinator"\]/);
assert.doesNotMatch(relayWrangler, /\[\[kv_namespaces\]\]/);
assert.doesNotMatch(relayWrangler, /APPROVABLE_WORKFLOW_PATHS/);

const relaySource = fs.readFileSync(
  path.join(root, '.github', 'webhook-relay', 'src', 'index.ts'),
  'utf8',
);
assert.match(relaySource, /pr-review-state-changed/);
assert.match(relaySource, /pull_request_review_comment/);
assert.match(relaySource, /pull_request_review_thread/);
assert.doesNotMatch(relaySource, /event === 'workflow_run'/);
assert.doesNotMatch(relaySource, /actions\/runs\/\$\{runId\}\/approve/);
assert.doesNotMatch(relaySource, /setTimeout|Atomics\.wait|Start-Sleep/);

const relayDeploySource = fs.readFileSync(
  path.join(root, '.github', 'workflows', 'deploy-webhook-relay.yml'),
  'utf8',
);
assert.match(
  relayDeploySource,
  /if:\s*\$\{\{\s*github\.event_name != 'workflow_dispatch' \|\| github\.ref_name == github\.event\.repository\.default_branch\s*\}\}/,
);
assert.match(
  relayDeploySource,
  /ref:\s*\$\{\{\s*github\.event\.repository\.default_branch\s*\}\}/,
);

console.log('workflow event policy tests passed');
