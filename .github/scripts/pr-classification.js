const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const repo = process.env.GITHUB_REPOSITORY || '';
const prNumber = process.env.PR_NUMBER || '';
const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
const rulesPath = process.env.PR_CLASSIFICATION_RULES
  || path.join(workspace, '.github', 'pr-classification-rules.json');

const publicLabelDefinitions = [
  { name: 'breaking-change', color: 'b60205', description: 'Release notes: breaking or incompatible changes.' },
  { name: 'security', color: 'd73a4a', description: 'Release notes: security fixes or vulnerability hardening.' },
  { name: 'feature', color: '1d76db', description: 'Release notes: new user-facing features or enhancements.' },
  { name: 'bug', color: 'd73a4a', description: 'Release notes: bug fixes or regressions.' },
  { name: 'performance', color: 'fbca04', description: 'Release notes: performance improvements.' },
  { name: 'build', color: 'fef2c0', description: 'Release notes: installer, packaging, or runtime build changes.' },
  { name: 'plugin', color: 'cfd3d7', description: 'Release notes: runtime plugin changes.' },
  { name: 'documentation', color: '0075ca', description: 'PR label: documentation changes.' },
  { name: 'workflow', color: '5319e7', description: 'PR label: GitHub Actions or repository automation changes.' },
  { name: 'chore', color: 'ededed', description: 'PR label: maintenance, dependency, or repository housekeeping.' },
];
const labelOrder = new Map(publicLabelDefinitions.map((label, index) => [label.name, index]));

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

function normalizeRepoPath(file) {
  return String(file || '').replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase();
}

function escapeRegex(text) {
  return text.replace(/[|\\{}()[\]^$+?.]/g, '\\$&');
}

function globToRegExp(pattern) {
  const value = normalizeRepoPath(pattern);
  let regex = '^';
  for (let index = 0; index < value.length; index += 1) {
    const char = value[index];
    if (char === '*') {
      if (value[index + 1] === '*') {
        regex += '.*';
        index += 1;
      } else {
        regex += '[^/]*';
      }
    } else {
      regex += escapeRegex(char);
    }
  }
  regex += '$';
  return new RegExp(regex);
}

function matchesAnyPattern(file, patterns) {
  const normalized = normalizeRepoPath(file);
  return patterns.some((pattern) => globToRegExp(pattern).test(normalized));
}

function changedFilePaths(files) {
  return files.map((file) => file.filename).filter(Boolean);
}

function readRules() {
  const parsed = JSON.parse(fs.readFileSync(rulesPath, 'utf8'));
  if (!Array.isArray(parsed.areas)) {
    throw new Error('PR classification rules must define an areas array.');
  }
  return parsed;
}

function inferAreas(files, rules) {
  const paths = changedFilePaths(files);
  return rules.areas
    .filter((area) => Array.isArray(area.patterns) && paths.some((file) => matchesAnyPattern(file, area.patterns)))
    .map((area) => area.name)
    .filter(Boolean);
}

function conventionalType(title) {
  return String(title || '').match(/^(feat|fix|refactor|perf|docs|test|build|ci|chore|style|revert)(?:\([a-z0-9-]+\))?!?:/i)?.[1]?.toLowerCase() || '';
}

function docsOnly(files) {
  const paths = changedFilePaths(files).map(normalizeRepoPath);
  return paths.length > 0 && paths.every((file) => {
    return file.startsWith('docs/')
      || file === 'readme.md'
      || (!file.startsWith('.github/') && file.endsWith('.md'));
  });
}

function hasConventionalType(text, types) {
  return new RegExp(`(^|\\s|\\n)(${types})(\\([a-z0-9-]+\\))?!?:`, 'i').test(text);
}

function isRuntimeReleasePath(file) {
  const normalized = normalizeRepoPath(file);
  if (normalized.startsWith('src/')) return true;
  if (normalized === 'tools/publish-releaseassets.ps1') return true;
  if (['directory.build.props', 'directory.build.targets', 'directory.packages.props', 'global.json'].includes(normalized)) return true;
  if (normalized === 'chore/fonts.zip') return true;
  return false;
}

function isInstallOrPackagePath(file) {
  const normalized = normalizeRepoPath(file);
  return normalized === 'tools/publish-releaseassets.ps1'
    || normalized === 'chore/fonts.zip'
    || ['directory.build.props', 'directory.build.targets', 'directory.packages.props', 'global.json'].includes(normalized);
}

function inferReleaseLabels(pull, files) {
  const paths = changedFilePaths(files);
  const runtimeFiles = paths.filter(isRuntimeReleasePath);
  if (!runtimeFiles.length) return [];

  const text = [
    pull.title,
    pull.body,
    pull.head?.ref,
    pull.base?.ref,
  ].join('\n').toLowerCase();
  const labels = new Set();

  if (/(breaking|破坏性|不兼容|semver-major)/i.test(text)) labels.add('breaking-change');
  if (/(security|安全|漏洞|vulnerab|cve)/i.test(text)) labels.add('security');
  if (hasConventionalType(text, 'fix|bug|bugfix|regression') || /(修复|缺陷|问题)/i.test(text)) labels.add('bug');
  if (hasConventionalType(text, 'feat|feature|enhancement') || /(新增|添加|功能)/i.test(text)) labels.add('feature');
  if (hasConventionalType(text, 'perf|performance') || /性能/i.test(text)) labels.add('performance');
  if (runtimeFiles.some(isInstallOrPackagePath)
    || hasConventionalType(text, 'build')
    || /(packag|package|installer|打包|构建|安装|发布包)/i.test(text)) labels.add('build');
  if (!labels.size) labels.add('plugin');

  return orderedLabels([...labels]);
}

function inferKind(pull, files) {
  const type = conventionalType(pull.title);
  if (type === 'feat') return 'kind:feature';
  if (type === 'fix') return 'kind:fix';
  if (type === 'perf') return 'kind:performance';
  if (type === 'refactor') return 'kind:refactor';
  if (type === 'docs' || docsOnly(files)) return 'kind:docs';
  return 'kind:chore';
}

function orderedLabels(labels) {
  return [...new Set(labels)].sort((left, right) => {
    return (labelOrder.get(left) ?? 1000) - (labelOrder.get(right) ?? 1000);
  });
}

function inferPublicLabels(pull, files, areas, kind, releaseLabels) {
  const labels = new Set(releaseLabels);
  const type = conventionalType(pull.title);
  const hasArea = (name) => areas.includes(name);
  const isBot = String(pull.user?.login || '').endsWith('[bot]') || pull.user?.type === 'Bot';

  if (kind === 'kind:docs' || hasArea('area:docs')) labels.add('documentation');
  if (hasArea('area:workflow') || hasArea('area:automation') || type === 'ci') labels.add('workflow');
  if (type === 'chore' || type === 'build' || isBot) labels.add('chore');

  if (!labels.size) {
    if (kind === 'kind:feature') labels.add('feature');
    else if (kind === 'kind:fix') labels.add('bug');
    else if (kind === 'kind:performance') labels.add('performance');
    else labels.add('chore');
  }

  return orderedLabels([...labels]);
}

function labelDefinition(labelName) {
  return publicLabelDefinitions.find((label) => label.name === labelName);
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
  if (!knownLabels.length) return;

  for (const label of knownLabels) {
    ensureLabel(label);
  }
  gh([
    '--method', 'POST',
    `repos/${repo}/issues/${prNumber}/labels`,
    '--input', '-',
  ], JSON.stringify({ labels: knownLabels }));
}

function removeVisibleInternalLabels() {
  const issue = ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}`,
  ]) || {};
  const labels = Array.isArray(issue.labels) ? issue.labels : [];
  const internalLabels = labels
    .map((label) => label.name)
    .filter((name) => /^area:|^kind:/.test(name));

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

  const rules = readRules();
  const pull = ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}`,
  ]);
  const files = fetchAll(`repos/${repo}/pulls/${prNumber}/files`);
  const areas = inferAreas(files, rules);
  const kind = inferKind(pull, files);
  const releaseLabels = inferReleaseLabels(pull, files);
  const publicLabels = inferPublicLabels(pull, files, areas, kind, releaseLabels);

  applyPublicLabels(publicLabels);
  removeVisibleInternalLabels();
  upsertHiddenMetadata(pull, areas, kind, publicLabels, releaseLabels);
  appendStepSummary(areas, kind, publicLabels, releaseLabels);
}

main();
