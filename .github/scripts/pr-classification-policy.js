const fs = require('node:fs');
const path = require('node:path');

function normalizeRepoPath(file) {
  return String(file || '').replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase();
}

function escapeRegex(text) {
  return String(text).replace(/[|\\{}()[\]^$+?.]/g, '\\$&');
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
  return new RegExp(`${regex}$`);
}

function matchesAnyPattern(file, patterns) {
  const normalized = normalizeRepoPath(file);
  return (patterns || []).some((pattern) => globToRegExp(pattern).test(normalized));
}

function changedFilePaths(files) {
  return (files || [])
    .map((file) => (typeof file === 'string' ? file : file?.filename))
    .filter(Boolean);
}

function defaultRulesPath(workspace = process.cwd()) {
  return path.join(workspace, '.github', 'pr-classification-rules.json');
}

function loadPolicy(rulesPath = defaultRulesPath()) {
  const policy = JSON.parse(fs.readFileSync(rulesPath, 'utf8'));
  if (!Array.isArray(policy.areas)) {
    throw new Error('PR classification rules must define an areas array.');
  }
  if (!Array.isArray(policy.labels?.public)) {
    throw new Error('PR classification rules must define labels.public.');
  }
  if (!Array.isArray(policy.releaseCategories)) {
    throw new Error('PR classification rules must define releaseCategories.');
  }
  return policy;
}

function fallbackPolicy() {
  return {
    areas: [],
    runtimeRelease: {},
    installOrPackage: {},
    labels: {
      public: [],
      release: [],
      internalPrefixes: ['area:', 'kind:'],
    },
    releaseCategories: [],
  };
}

function loadPolicyOrDefault(rulesPath = defaultRulesPath()) {
  try {
    return loadPolicy(rulesPath);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.warn(`::warning::Falling back to empty PR classification policy: ${message}`);
    return fallbackPolicy();
  }
}

function publicLabelDefinitions(policy) {
  return policy.labels.public || [];
}

function releaseLabelNames(policy) {
  return policy.labels.release || [];
}

function internalLabelPrefixes(policy) {
  return policy.labels.internalPrefixes || ['area:', 'kind:'];
}

function labelOrderMap(policy) {
  return new Map(publicLabelDefinitions(policy).map((label, index) => [label.name, index]));
}

function orderedLabels(labels, policy) {
  const order = labelOrderMap(policy);
  return [...new Set(labels)].sort((left, right) => {
    return (order.get(left) ?? 1000) - (order.get(right) ?? 1000);
  });
}

function inferAreas(files, policy) {
  const paths = changedFilePaths(files);
  return policy.areas
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

function inferKind(pull, files) {
  const type = conventionalType(pull.title);
  if (type === 'feat') return 'kind:feature';
  if (type === 'fix') return 'kind:fix';
  if (type === 'perf') return 'kind:performance';
  if (type === 'refactor') return 'kind:refactor';
  if (type === 'docs' || docsOnly(files)) return 'kind:docs';
  return 'kind:chore';
}

function pathRuleMatches(file, rule = {}) {
  const normalized = normalizeRepoPath(file);
  const excludePrefixes = (rule.excludePrefixes || []).map(normalizeRepoPath);
  const excludeFiles = (rule.excludeFiles || []).map(normalizeRepoPath);
  if (excludePrefixes.some((prefix) => normalized.startsWith(prefix))) return false;
  if (excludeFiles.includes(normalized)) return false;

  const includePrefixes = (rule.includePrefixes || []).map(normalizeRepoPath);
  const includeFiles = (rule.includeFiles || []).map(normalizeRepoPath);
  return includePrefixes.some((prefix) => normalized.startsWith(prefix))
    || includeFiles.includes(normalized);
}

function isRuntimeReleasePath(file, policy) {
  return pathRuleMatches(file, policy.runtimeRelease);
}

function isInstallOrPackagePath(file, policy) {
  return pathRuleMatches(file, policy.installOrPackage);
}

function textMatchesAnyPattern(text, patterns) {
  return (patterns || []).some((pattern) => new RegExp(pattern, 'i').test(text));
}

function inferReleaseLabelsFromText(textParts, files, policy) {
  const paths = changedFilePaths(files);
  const runtimeFiles = paths.filter((file) => isRuntimeReleasePath(file, policy));
  if (!runtimeFiles.length) return [];

  const text = textParts.filter(Boolean).join('\n');
  const labels = new Set();
  const categories = policy.releaseCategories || [];

  for (const category of categories) {
    if (!category.releaseLabel || category.fallback) continue;
    const matchesText = textMatchesAnyPattern(text, category.textPatterns);
    const matchesInstallPackage = category.installOrPackage
      && runtimeFiles.some((file) => isInstallOrPackagePath(file, policy));
    if (matchesText || matchesInstallPackage) {
      labels.add(category.releaseLabel);
    }
  }

  if (!labels.size) {
    const fallback = categories.find((category) => category.fallback && category.releaseLabel);
    labels.add(fallback?.releaseLabel || 'plugin');
  }

  return orderedLabels([...labels].filter((label) => releaseLabelNames(policy).includes(label)), policy);
}

function inferReleaseLabelsForPull(pull, files, policy) {
  return inferReleaseLabelsFromText([
    pull.title,
    pull.body,
    pull.head?.ref,
    pull.base?.ref,
  ], files, policy);
}

function inferPublicLabels(pull, files, areas, kind, releaseLabels, policy) {
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

  return orderedLabels([...labels], policy);
}

module.exports = {
  changedFilePaths,
  conventionalType,
  defaultRulesPath,
  inferAreas,
  inferKind,
  inferPublicLabels,
  inferReleaseLabelsForPull,
  inferReleaseLabelsFromText,
  internalLabelPrefixes,
  isInstallOrPackagePath,
  isRuntimeReleasePath,
  loadPolicy,
  loadPolicyOrDefault,
  matchesAnyPattern,
  normalizeRepoPath,
  orderedLabels,
  publicLabelDefinitions,
  releaseLabelNames,
};
