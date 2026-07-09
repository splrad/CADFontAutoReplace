const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const {
  inferAreas,
  inferKind,
  inferPublicLabels,
  inferReleaseLabelsForPull,
  internalLabelPrefixes,
  loadPolicy,
  publicLabelDefinitions,
} = require('./pr-classification-policy');

const repo = process.env.GITHUB_REPOSITORY || '';
const prNumber = process.env.PR_NUMBER || '';
const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
const rulesPath = process.env.PR_CLASSIFICATION_RULES
  || path.join(workspace, '.github', 'pr-classification-rules.json');
const policy = loadPolicy(rulesPath);
const labelDefinitions = publicLabelDefinitions(policy);

function run(command, args, options = {}) {
  return execFileSync(command, args, {
    encoding: 'utf8',
    stdio: ['pipe', 'pipe', 'pipe'],
    ...options,
  }).trim();
}

function runAllowFail(command, args, options = {}) {
  try {
    return run(command, args, options);
  } catch {
    return '';
  }
}

function gh(args, input) {
  return execFileSync('gh', ['api', ...args], {
    encoding: 'utf8',
    input,
    stdio: ['pipe', 'pipe', 'pipe'],
    env: {
      ...process.env,
      GH_TOKEN: process.env.GH_TOKEN || process.env.GITHUB_TOKEN || '',
    },
  }).trim();
}

function ghJson(args, input) {
  const out = gh(args, input);
  return out ? JSON.parse(out) : null;
}

function fetchAll(apiPath) {
  const all = [];
  for (let page = 1; page <= 20; page += 1) {
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

function labelDefinition(labelName) {
  return labelDefinitions.find((label) => label.name === labelName);
}

function ensureLabel(labelName) {
  const definition = labelDefinition(labelName);
  if (!definition) return;

  const encoded = encodeURIComponent(definition.name);
  const existing = runAllowFail('gh', [
    'api',
    '--method', 'GET',
    `repos/${repo}/labels/${encoded}`,
  ], {
    env: { ...process.env, GH_TOKEN: process.env.GH_TOKEN || process.env.GITHUB_TOKEN || '' },
  });
  if (existing) return;

  gh([
    '--method', 'POST',
    `repos/${repo}/labels`,
    '--input', '-',
  ], JSON.stringify(definition));
}

function applyPublicLabels(labels) {
  const knownLabels = labels.filter(labelDefinition);
  const desiredLabels = new Set(knownLabels);
  const managedLabels = new Set(labelDefinitions.map((label) => label.name));
  const issue = ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}`,
  ]) || {};
  const currentLabels = Array.isArray(issue.labels)
    ? issue.labels.map((label) => label.name).filter(Boolean)
    : [];

  const labelsToRemove = currentLabels.filter((label) => {
    return managedLabels.has(label) && !desiredLabels.has(label);
  });
  for (const label of labelsToRemove) {
    const encoded = encodeURIComponent(label);
    gh([
      '--method', 'DELETE',
      `repos/${repo}/issues/${prNumber}/labels/${encoded}`,
    ]);
  }

  const currentLabelSet = new Set(currentLabels);
  const labelsToAdd = knownLabels.filter((label) => !currentLabelSet.has(label));
  if (!labelsToAdd.length) return;

  for (const label of labelsToAdd) {
    ensureLabel(label);
  }
  gh([
    '--method', 'POST',
    `repos/${repo}/issues/${prNumber}/labels`,
    '--input', '-',
  ], JSON.stringify({ labels: labelsToAdd }));
}

function removeVisibleInternalLabels() {
  const issue = ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}`,
  ]) || {};
  const labels = Array.isArray(issue.labels) ? issue.labels : [];
  const prefixes = internalLabelPrefixes(policy);
  const internalLabels = labels
    .map((label) => label.name)
    .filter((name) => prefixes.some((prefix) => name.startsWith(prefix)));

  for (const label of internalLabels) {
    const encoded = encodeURIComponent(label);
    gh([
      '--method', 'DELETE',
      `repos/${repo}/issues/${prNumber}/labels/${encoded}`,
    ]);
  }
}

function hiddenMetadata(areas, kind, publicLabels, releaseLabels) {
  const value = (items) => (items.length ? items.join(',') : 'none');
  return [
    '<!-- workflow:pr-classification:start',
    `areas=${value(areas)}`,
    `kind=${kind || 'none'}`,
    `visible-labels=${value(publicLabels)}`,
    `release-labels=${value(releaseLabels)}`,
    'workflow:pr-classification:end -->',
  ].join('\n');
}

function upsertHiddenMetadata(pull, areas, kind, publicLabels, releaseLabels) {
  const markerPattern = /<!-- workflow:pr-classification:start[\s\S]*?workflow:pr-classification:end -->/;
  const currentBody = pull.body || '';
  const metadata = hiddenMetadata(areas, kind, publicLabels, releaseLabels);
  const nextBody = markerPattern.test(currentBody)
    ? currentBody.replace(markerPattern, metadata)
    : `${currentBody.trimEnd()}${currentBody.trimEnd() ? '\n\n' : ''}${metadata}`;

  if (nextBody === currentBody) return;

  gh([
    '--method', 'PATCH',
    `repos/${repo}/pulls/${prNumber}`,
    '--input', '-',
  ], JSON.stringify({ body: nextBody }));
}

function appendStepSummary(areas, kind, publicLabels, releaseLabels) {
  const summaryPath = process.env.GITHUB_STEP_SUMMARY;
  if (!summaryPath) return;
  const lines = [
    '## PR Classification',
    '',
    `- Visible labels: ${publicLabels.length ? publicLabels.join(', ') : 'none'}`,
    `- Release labels: ${releaseLabels.length ? releaseLabels.join(', ') : 'none'}`,
    `- Internal areas: ${areas.length ? areas.join(', ') : 'none'}`,
    `- Internal kind: ${kind || 'none'}`,
    '',
  ];
  fs.appendFileSync(summaryPath, `${lines.join('\n')}\n`, 'utf8');
}

function main() {
  if (!repo || !prNumber) {
    throw new Error('Missing repository or pull request number.');
  }

  const pull = ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}`,
  ]);
  const files = fetchAll(`repos/${repo}/pulls/${prNumber}/files`);
  const areas = inferAreas(files, policy);
  const kind = inferKind(pull, files);
  const releaseLabels = inferReleaseLabelsForPull(pull, files, policy);
  const publicLabels = inferPublicLabels(pull, files, areas, kind, releaseLabels, policy);

  applyPublicLabels(publicLabels);
  removeVisibleInternalLabels();
  upsertHiddenMetadata(pull, areas, kind, publicLabels, releaseLabels);
  appendStepSummary(areas, kind, publicLabels, releaseLabels);
}

main();
