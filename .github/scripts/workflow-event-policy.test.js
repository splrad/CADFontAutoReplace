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
assert.equal(matrixConfig.targets.some((target) => Array.isArray(target.baseBranches)), false);

const matrixSource = fs.readFileSync(
  path.join(root, '.github', 'scripts', 'pr-validation-matrix.js'),
  'utf8',
);
assert.match(matrixSource, /repository\?\.default_branch/);
assert.doesNotMatch(matrixSource, /pull\.base\?\.ref\s*\|\|[^\n]*['"]main['"]/);

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
assert.doesNotMatch(releaseSource, /^\s{2}push:\s*$/m);
assert.doesNotMatch(releaseSource, /commits\/[^\s]*\/pulls/);

const reviewSignalSource = fs.readFileSync(
  path.join(root, '.github', 'workflows', 'pr-review-signal.yml'),
  'utf8',
);
assert.match(reviewSignalSource, /run:\s*\|\r?\n\s+echo "Review state changed for PR #/);

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

console.log('workflow event policy tests passed');
