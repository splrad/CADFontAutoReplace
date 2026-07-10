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
const { fingerprintForPull } = require('./pr-validation-fingerprint');
const { fetchPullRequestPages } = require('./pr-api-pagination');

const repo = process.env.GITHUB_REPOSITORY || '';
const prNumber = process.env.PR_NUMBER || '';
const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
const rulesPath = process.env.PR_CLASSIFICATION_RULES
  || path.join(workspace, '.github', 'pr-classification-rules.json');
const policy = loadPolicy(rulesPath);
const labelDefinitions = publicLabelDefinitions(policy);
const classificationCheckName = process.env.PR_CLASSIFICATION_CHECK_NAME || 'PR Classification Gate';
const checkRunAppSlug = process.env.CHECK_RUN_APP_SLUG || '';

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
  return fetchPullRequestPages((page, pageSize) => ghJson([
    '--method', 'GET',
    apiPath,
    '-f', `per_page=${pageSize}`,
    '-f', `page=${page}`,
  ]) || []);
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

function workflowRunUrl() {
  const serverUrl = process.env.GITHUB_SERVER_URL || 'https://github.com';
  const runId = process.env.GITHUB_RUN_ID || '';
  return repo && runId ? `${serverUrl}/${repo}/actions/runs/${runId}` : undefined;
}

function latestClassificationCheckRun(headSha) {
  if (!process.env.GH_CHECKS_TOKEN || !checkRunAppSlug) {
    throw new Error('Missing GH_CHECKS_TOKEN or CHECK_RUN_APP_SLUG for classification Check updates.');
  }
  const payload = ghJson([
    '--method', 'GET',
    `repos/${repo}/commits/${headSha}/check-runs`,
    '-f', `check_name=${classificationCheckName}`,
    '-f', 'per_page=100',
  ]) || {};
  return (payload.check_runs || [])
    .filter((run) => run?.name === classificationCheckName && run?.app?.slug === checkRunAppSlug)
    .sort((left, right) => String(left?.started_at || left?.created_at || '')
      .localeCompare(String(right?.started_at || right?.created_at || '')))
    .pop();
}

function upsertClassificationCheck(pull, fingerprint) {
  const headSha = pull?.head?.sha || '';
  const existing = latestClassificationCheckRun(headSha);
  const payload = {
    name: classificationCheckName,
    status: 'completed',
    conclusion: 'success',
    completed_at: new Date().toISOString(),
    details_url: workflowRunUrl(),
    external_id: `classification:pr:${prNumber}:fingerprint:${fingerprint.value}`,
    output: {
      title: 'PR 分类已更新',
      summary: '标题、正文、提交、贡献者和文件输入均与当前分类结果一致。',
    },
  };
  if (!existing) {
    gh([
      '--method', 'POST',
      `repos/${repo}/check-runs`,
      '--input', '-',
    ], JSON.stringify({ ...payload, head_sha: headSha }));
    return;
  }
  gh([
    '--method', 'PATCH',
    `repos/${repo}/check-runs/${existing.id}`,
    '--input', '-',
  ], JSON.stringify(payload));
}

function main() {
  if (!repo || !prNumber) {
    throw new Error('Missing repository or pull request number.');
  }

  const pull = ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}`,
  ]);
  const commits = fetchAll(`repos/${repo}/pulls/${prNumber}/commits`);
  const files = fetchAll(`repos/${repo}/pulls/${prNumber}/files`);
  const fingerprint = fingerprintForPull({ pull, commits, files });
  const areas = inferAreas(files, policy);
  const kind = inferKind(pull, files);
  const releaseLabels = inferReleaseLabelsForPull(pull, files, policy);
  const publicLabels = inferPublicLabels(pull, files, areas, kind, releaseLabels, policy);

  applyPublicLabels(publicLabels);
  removeVisibleInternalLabels();
  upsertHiddenMetadata(pull, areas, kind, publicLabels, releaseLabels);
  upsertClassificationCheck(pull, fingerprint);
  appendStepSummary(areas, kind, publicLabels, releaseLabels);
}

main();
