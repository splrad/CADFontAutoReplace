const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const repo = process.env.GITHUB_REPOSITORY || '';
const [owner, repoName] = repo.split('/');
const sourceBranch = process.env.SOURCE_BRANCH || process.env.GITHUB_REF_NAME || '';
let targetBranch = process.env.TARGET_BRANCH || '';
const actor = process.env.GITHUB_ACTOR || 'unknown';
const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
const runnerTemp = process.env.RUNNER_TEMP || workspace;
const releaseLabelDefinitions = [
  { name: 'breaking-change', color: 'b60205', description: 'Release notes: breaking or incompatible changes.' },
  { name: 'security', color: 'd73a4a', description: 'Release notes: security fixes or vulnerability hardening.' },
  { name: 'feature', color: '1d76db', description: 'Release notes: new user-facing features or enhancements.' },
  { name: 'bug', color: 'd73a4a', description: 'Release notes: bug fixes or regressions.' },
  { name: 'performance', color: 'fbca04', description: 'Release notes: performance improvements.' },
  { name: 'build', color: 'fef2c0', description: 'Release notes: installer, packaging, or runtime build changes.' },
  { name: 'plugin', color: 'cfd3d7', description: 'Release notes: runtime plugin changes.' },
];
const kindLabelDefinitions = [
  { name: 'kind:feature', color: '1d76db', description: 'PR type: feature or enhancement.' },
  { name: 'kind:fix', color: 'd73a4a', description: 'PR type: bug fix or regression fix.' },
  { name: 'kind:performance', color: 'fbca04', description: 'PR type: performance improvement.' },
  { name: 'kind:refactor', color: 'c5def5', description: 'PR type: internal refactor without intended behavior change.' },
  { name: 'kind:docs', color: '0075ca', description: 'PR type: documentation-only change.' },
  { name: 'kind:chore', color: 'ededed', description: 'PR type: maintenance, build, workflow, or repository housekeeping.' },
];

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

function gh(args, input, token) {
  return execFileSync('gh', ['api', ...args], {
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

function appendEnv(values) {
  const envPath = process.env.GITHUB_ENV;
  if (!envPath) return;
  const lines = [];
  for (const [key, value] of Object.entries(values)) {
    lines.push(`${key}=${String(value ?? '')}`);
  }
  fs.appendFileSync(envPath, `${lines.join('\n')}\n`, 'utf8');
}

function writeOutput(values) {
  const outputPath = process.env.GITHUB_OUTPUT;
  if (!outputPath) return;
  const lines = [];
  for (const [key, value] of Object.entries(values)) {
    lines.push(`${key}=${String(value ?? '')}`);
  }
  fs.appendFileSync(outputPath, `${lines.join('\n')}\n`, 'utf8');
}

function cleanLines(text, limit = 40) {
  return String(text || '')
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .slice(0, limit);
}

function fileCategory(file) {
  const normalized = file.replace(/\\/g, '/');
  if (normalized.startsWith('.github/')) return 'github';
  if (/^(version\.props|package\.json|package-lock\.json|pnpm-lock\.yaml|yarn\.lock|.*\.csproj|.*\.slnx?)$/i.test(normalized)) return 'version';
  if (normalized.startsWith('src/') || normalized.startsWith('app/') || normalized.startsWith('lib/')) return 'source';
  if (normalized.startsWith('test/') || normalized.startsWith('tests/') || normalized.includes('/tests/')) return 'tests';
  if (normalized.startsWith('tools/') || normalized.startsWith('scripts/')) return 'tools';
  if (normalized.startsWith('docs/') || normalized.toLowerCase().includes('readme')) return 'docs';
  if (normalized.toLowerCase() === 'chore/fonts.zip') return 'assets';
  return 'project';
}

function statusFilePath(line) {
  const parts = String(line || '').split(/\t+/);
  return (parts[parts.length - 1] || line).replace(/\\/g, '/');
}

function titleSubject(title) {
  return String(title || '').replace(/^(feat|fix|refactor|perf|style|docs|test|build|ci|chore|revert)(\([a-z0-9-]+\))?!?:\s*/i, '').trim();
}

function conventionalPrefix(type, scope) {
  return scope ? `${type}(${scope})` : type;
}

function buildTitle(files) {
  const paths = files.map(statusFilePath);
  const categories = paths.map(fileCategory);
  const has = (category) => categories.includes(category);
  const changed = (pattern) => paths.some((file) => pattern.test(file));

  if (has('github')) {
    const scope = changed(/^\.github\/workflows\/release-/) || changed(/^\.github\/release\.yml$/) ? 'release' : 'workflow';
    return `${conventionalPrefix('ci', scope)}: 调整 GitHub 自动化流程`;
  }
  if (has('tools')) return 'build(release): 优化构建与发布工具';
  if (has('tests')) return 'test: 完善测试覆盖';
  if (has('docs')) return 'docs: 完善项目文档说明';
  if (has('version') || has('assets')) return 'chore(release): 更新版本与发布资源';
  if (has('source')) {
    if (changed(/^src\/AFR\.Deployer\//i)) return 'feat(deployer): 更新部署工具实现';
    if (changed(/^src\/AFR\.UI\//i)) return 'feat(ui): 更新界面交互实现';
    if (changed(/^src\/AFR\.Core\//i)) return 'refactor(core): 更新核心服务实现';
    if (changed(/^src\/AutoCAD\//i)) return 'fix(autocad): 更新 AutoCAD 插件实现';
    return 'refactor: 更新项目代码实现';
  }
  return 'chore: 更新项目维护内容';
}

function buildChanges(files) {
  const buckets = new Map();
  for (const line of files) {
    const parts = line.split(/\t+/);
    const file = parts[parts.length - 1] || line;
    const category = fileCategory(file);
    buckets.set(category, (buckets.get(category) || 0) + 1);
  }

  const changes = [];
  if (buckets.has('github')) changes.push('调整 GitHub Actions 自动化、权限门禁或发布流程配置。');
  if (buckets.has('tools')) changes.push('更新构建、部署或发布工具链路。');
  if (buckets.has('source')) changes.push('更新项目源码实现，影响对应功能行为。');
  if (buckets.has('tests')) changes.push('更新测试用例、测试数据或验证配置。');
  if (buckets.has('version') || buckets.has('assets')) changes.push('更新版本号或发布资源，影响 Release 交付内容。');
  if (buckets.has('docs')) changes.push('更新项目文档、贡献说明或使用说明。');

  if (changes.length === 0) {
    changes.push('根据当前代码差异更新项目实现。');
  }
  return changes.slice(0, 5);
}

function buildFallback(context) {
  const allFiles = cleanLines(context.changedFiles, 10000);
  const files = allFiles.slice(0, 80);
  const title = buildTitle(files);
  const changes = buildChanges(files);
  return {
    title,
    summary: `${titleSubject(title)}，涉及 ${allFiles.length || 0} 个文件。`,
    changes,
    validation: [
      '请根据变更范围运行对应构建或手工验证。',
      '若涉及工作流变更，请在 PR 检查列表确认新门禁均已运行。',
    ],
    risk: [
      files.some((line) => line.includes('.github/'))
        ? '工作流和权限规则变更会影响 PR 流转与发布门禁。'
        : '风险取决于本次代码差异覆盖的功能范围。',
    ],
  };
}

function buildPrompt(context, fallback) {
  return [
    '你是 GitHub Pull Request 标题与说明生成器。',
    '只根据下面提供的代码差异、文件清单、提交信息生成内容，不要使用来源分支名或目标分支名代替变更主题。',
    '请输出严格 JSON，不要输出 Markdown 代码块或额外解释。',
    'JSON 格式：{"title":"Conventional Commits 风格 PR 标题","summary":"一句话摘要","changes":["主要改动"],"validation":["验证建议"],"risk":["风险或影响"]}',
    '所有 JSON 字符串内容必须使用简体中文；只保留代码标识符、文件路径、命令、API 名称和 label 名称为英文。',
    '标题要求：必须使用 Conventional Commits 风格，格式为 type(scope): 中文标题；scope 可省略。',
    '允许的 type：feat、fix、refactor、perf、style、docs、test、build、ci、chore、revert。',
    'scope 使用小写英文、数字或连字符，例如 deployer、release、workflow、core、ui、autocad。',
    '标题 subject 用中文动宾结构，不超过 50 字，不加句号，体现代码内容，不得写成“分支 A 到分支 B”。',
    '标题示例：feat(deployer): 新增关于窗口；ci(release): 限制发布流程仅由版本变更触发。',
    '正文要求：简洁，面向审查者，避免夸张宣传语。',
    '',
    `兜底标题参考：${fallback.title}`,
    `分支流向：${context.sourceBranch} -> ${context.targetBranch}`,
    '',
    '提交信息：',
    context.commits || '无',
    '',
    '变更统计：',
    context.stat || '无',
    '',
    '变更文件：',
    context.changedFiles || '无',
    '',
    '差异片段：',
    context.diffSnippet || '无',
  ].join('\n');
}

function resolveJsonCandidate(raw) {
  const text = String(raw || '').trim()
    .replace(/^```json\s*/i, '')
    .replace(/^```\s*/i, '')
    .replace(/\s*```$/i, '')
    .trim();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    const match = text.match(/\{[\s\S]*\}/);
    if (!match) return null;
    try {
      return JSON.parse(match[0]);
    } catch {
      return null;
    }
  }
}

function sanitizeGenerated(generated, fallback) {
  const title = String(generated?.title || '').trim();
  const branchTitlePattern = new RegExp(`^\\s*${escapeRegExp(sourceBranch)}\\s*(-|→|->|➔|to)\\s*${escapeRegExp(targetBranch)}\\s*$`, 'i');
  const conventionalTitlePattern = /^(feat|fix|refactor|perf|style|docs|test|build|ci|chore|revert)(\([a-z0-9-]+\))?!?:\s*\S.{0,80}$/;
  const safeTitle = title && conventionalTitlePattern.test(title) && !branchTitlePattern.test(title) && title.length <= 100
    ? title
    : fallback.title;

  const toList = (value, backup) => {
    if (Array.isArray(value)) return value.map((item) => String(item).trim()).filter(Boolean).slice(0, 6);
    const text = String(value || '').trim();
    return text ? [text] : backup;
  };

  return {
    title: safeTitle,
    summary: String(generated?.summary || fallback.summary).trim(),
    changes: toList(generated?.changes, fallback.changes),
    validation: toList(generated?.validation, fallback.validation),
    risk: toList(generated?.risk, fallback.risk),
  };
}

function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function htmlCommentValue(value) {
  return String(value ?? '')
    .replace(/--/g, '- -')
    .replace(/>/g, '&gt;')
    .replace(/\r?\n/g, ' ');
}

function changedFilePaths(context) {
  return cleanLines(context.changedFiles, 200).map((line) => {
    const parts = line.split(/\t+/);
    return (parts[parts.length - 1] || line).replace(/\\/g, '/');
  });
}

function normalizeRepoPath(file) {
  return String(file || '').replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase();
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

function hasConventionalType(text, types) {
  return new RegExp(`(^|\\s|\\n)(${types})(\\([a-z0-9-]+\\))?!?:`, 'i').test(text);
}

function inferReleaseLabels(context, summary) {
  const files = changedFilePaths(context);
  const runtimeFiles = files.filter(isRuntimeReleasePath);
  if (!runtimeFiles.length) return [];

  const text = [
    summary.title,
    summary.summary,
    ...(summary.changes || []),
    context.commits,
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

  return [...labels];
}

function inferKindLabels(context, summary) {
  const files = changedFilePaths(context);
  const normalizedFiles = files.map(normalizeRepoPath);
  const docsOnly = normalizedFiles.length > 0 && normalizedFiles.every((file) => {
    return file.startsWith('docs/')
      || file === 'readme.md'
      || (!file.startsWith('.github/') && file.endsWith('.md'));
  });
  const titleType = String(summary.title || '').match(/^(feat|fix|refactor|perf|docs|test|build|ci|chore|style|revert)(?:\([a-z0-9-]+\))?!?:/i)?.[1]?.toLowerCase();

  if (titleType === 'feat') return ['kind:feature'];
  if (titleType === 'fix') return ['kind:fix'];
  if (titleType === 'perf') return ['kind:performance'];
  if (titleType === 'refactor') return ['kind:refactor'];
  if (titleType === 'docs' || docsOnly) return ['kind:docs'];
  return ['kind:chore'];
}

function labelDefinition(labelName) {
  return [...releaseLabelDefinitions, ...kindLabelDefinitions].find((item) => item.name === labelName);
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
    env: { ...process.env, GH_TOKEN: process.env.GH_TOKEN || '' },
  });
  if (existing) return;

  gh([
    '--method', 'POST',
    `repos/${repo}/labels`,
    '--input', '-',
  ], JSON.stringify(definition));
}

function applyLabels(prNumber, labels, groupName) {
  const knownLabels = labels.filter((label) => labelDefinition(label));
  if (!knownLabels.length) return;

  try {
    for (const label of knownLabels) {
      ensureLabel(label);
    }
    gh([
      '--method', 'POST',
      `repos/${repo}/issues/${prNumber}/labels`,
      '--input', '-',
    ], JSON.stringify({ labels: knownLabels }));
  } catch (error) {
    console.warn(`::warning::Failed to apply ${groupName} labels: ${error.message}`);
  }
}

function applyReleaseLabels(prNumber, labels) {
  const releaseLabels = labels.filter((label) => releaseLabelDefinitions.some((item) => item.name === label));
  applyLabels(prNumber, releaseLabels, 'release note');
}

function applyKindLabels(prNumber, labels) {
  const kindLabels = labels.filter((label) => kindLabelDefinitions.some((item) => item.name === label));
  applyLabels(prNumber, kindLabels, 'PR type');
}

function formatLabels(labels) {
  return labels.length ? labels.map((label) => `\`${label}\``).join('、') : '无';
}

function buildAutoBlock(summary, context) {
  const bulletList = (items) => items.map((item) => `- ${item}`).join('\n');
  const changedFileCount = cleanLines(context.changedFiles, 10000).length;

  return [
    '<!-- workflow:auto-summary:start -->',
    `<!-- workflow:source-actor:${actor} -->`,
    `<!-- workflow:auto-context:source=${htmlCommentValue(context.sourceBranch)};target=${htmlCommentValue(context.targetBranch)};generation=${htmlCommentValue(context.generationMode)};changed-files=${changedFileCount} -->`,
    '### 自动生成摘要',
    '',
    summary.summary,
    '',
    '### 主要改动',
    bulletList(summary.changes),
    '',
    '### 验证建议',
    bulletList(summary.validation),
    '',
    '### 风险与影响',
    bulletList(summary.risk),
    '',
    '<!-- workflow:auto-summary:end -->',
  ].join('\n');
}

function buildBody(existingBody, summary, context) {
  const templatePath = path.join(workspace, '.github', 'pull_request_template.md');
  const autoBlock = buildAutoBlock(summary, context);
  const markerPattern = /<!-- workflow:auto-summary:start -->[\s\S]*?<!-- workflow:auto-summary:end -->/;

  if (existingBody && markerPattern.test(existingBody)) {
    return existingBody.replace(markerPattern, autoBlock);
  }

  if (existingBody && existingBody.trim() && !existingBody.includes('正在基于当前代码差异生成')) {
    return `${existingBody.trim()}\n\n---\n\n${autoBlock}\n`;
  }

  const template = fs.existsSync(templatePath)
    ? fs.readFileSync(templatePath, 'utf8')
    : '## 变更摘要\n\n<!-- workflow:auto-summary:start -->\n等待自动生成。\n<!-- workflow:auto-summary:end -->\n';

  if (markerPattern.test(template)) {
    return template.replace(markerPattern, autoBlock);
  }
  return `${template.trim()}\n\n${autoBlock}\n`;
}

function findOpenPullRequest() {
  const result = ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls`,
    '-f', 'state=open',
    '-f', `head=${owner}:${sourceBranch}`,
    '-f', `base=${targetBranch}`,
    '-f', 'sort=updated',
    '-f', 'direction=desc',
    '-f', 'per_page=20',
  ]);
  return Array.isArray(result) ? result[0] || null : null;
}

function mentionText() {
  let trusted = [];
  try {
    const parsed = JSON.parse(process.env.TRUSTED_DEVELOPERS || '[]');
    if (Array.isArray(parsed)) trusted = parsed.filter((item) => typeof item === 'string' && item.trim());
  } catch {
    trusted = [];
  }
  const unique = [...new Set(trusted.map((item) => item.trim()))];
  const mentions = unique.map((item) => `@${item}`);
  if (actor && actor !== 'unknown' && !unique.includes(actor)) mentions.push(`@${actor}`);
  return mentions.length ? mentions.join('；') : '（未配置通知对象）';
}

function upsertSuccessComment(prNumber, title, labelInfo = {}, options = {}) {
  const commentToken = process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN;
  const createIfMissing = options.createIfMissing !== false;
  const marker = '<!-- workflow:pr-success-notice -->';
  const comments = ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}/comments`,
    '-f', 'per_page=100',
  ], undefined, commentToken) || [];
  const existing = comments.find((comment) => String(comment.body || '').includes(marker));
  const body = [
    marker,
    '## PR 自动化已完成',
    '',
    `- PR 标题：**${title}**`,
    `- 提交人：**${actor}**`,
    `- 分支流向：**${sourceBranch} -> ${targetBranch}**`,
    `- PR 链接：https://github.com/${repo}/pull/${prNumber}`,
    `- 区域标签：由 **PR Labeler** workflow 根据 \`.github/labeler.yml\` 维护`,
    `- PR 类型标签：${formatLabels(labelInfo.kindLabels || [])}`,
    `- Release Notes 标签：${formatLabels(labelInfo.releaseLabels || [])}`,
    `- 通知对象：${mentionText()}`,
    '',
    '> 本通知由 GitHub Actions 自动发布。',
  ].join('\n');

  if (existing) {
    gh([
      '--method', 'PATCH',
      `repos/${repo}/issues/comments/${existing.id}`,
      '--input', '-',
    ], JSON.stringify({ body }), commentToken);
  } else if (createIfMissing) {
    gh([
      '--method', 'POST',
      `repos/${repo}/issues/${prNumber}/comments`,
      '--input', '-',
    ], JSON.stringify({ body }), commentToken);
  } else {
    console.log('PR success notice does not exist; skipped creating one for an existing PR update.');
  }
}

function generate() {
  if (!repo || !owner || !repoName || !sourceBranch || !targetBranch) {
    throw new Error('Missing repository or branch context.');
  }

  const existingPullRequest = findOpenPullRequest();
  runAllowFail('git', [
    'fetch',
    '--no-tags',
    'origin',
    `+refs/heads/${targetBranch}:refs/remotes/origin/${targetBranch}`,
    `+refs/heads/${sourceBranch}:refs/remotes/origin/${sourceBranch}`,
  ]);
  const aheadText = runAllowFail('git', ['rev-list', '--count', `origin/${targetBranch}..origin/${sourceBranch}`]) || '0';
  const ahead = Number.parseInt(aheadText, 10) || 0;
  if (ahead <= 0) {
    appendEnv({ SKIP_PR_AUTOMATION: 'true' });
    writeOutput({ skipped: 'true' });
    return;
  }

  const range = `origin/${targetBranch}...origin/${sourceBranch}`;
  const context = {
    sourceBranch,
    targetBranch,
    actor,
    commits: runAllowFail('git', ['log', '--format=%s', `origin/${targetBranch}..origin/${sourceBranch}`, '-n', '20']),
    stat: runAllowFail('git', ['diff', '--stat', '--find-renames', range]),
    changedFiles: runAllowFail('git', ['diff', '--name-status', '--find-renames', range]),
    diffSnippet: runAllowFail('git', ['diff', '--find-renames', '--unified=80', range]).slice(0, 22000),
  };
  const fallback = buildFallback(context);
  const prompt = buildPrompt(context, fallback);
  const contextPath = path.join(runnerTemp, 'workflow-pr-context.json');
  const fallbackPath = path.join(runnerTemp, 'workflow-pr-fallback.json');
  const promptPath = path.join(runnerTemp, 'workflow-pr-copilot-prompt.txt');

  fs.writeFileSync(contextPath, JSON.stringify(context, null, 2), 'utf8');
  fs.writeFileSync(fallbackPath, JSON.stringify(fallback, null, 2), 'utf8');
  fs.writeFileSync(promptPath, prompt, 'utf8');
  appendEnv({
    SKIP_PR_AUTOMATION: 'false',
    PR_CONTEXT_PATH: contextPath,
    PR_FALLBACK_PATH: fallbackPath,
    PR_COPILOT_PROMPT_PATH: promptPath,
  });
  writeOutput({
    skipped: 'false',
    existing_pr_number: existingPullRequest?.number || '',
    target_branch: targetBranch,
  });
}

function applySummary() {
  if (!process.env.GH_TOKEN) {
    throw new Error('Automation GitHub App token is required to create/update pull requests so follow-up workflows are triggered.');
  }

  const contextPath = process.env.PR_CONTEXT_PATH;
  const fallbackPath = process.env.PR_FALLBACK_PATH;
  if (!contextPath || !fallbackPath) {
    throw new Error('Missing PR summary paths.');
  }

  const context = JSON.parse(fs.readFileSync(contextPath, 'utf8'));
  const fallback = JSON.parse(fs.readFileSync(fallbackPath, 'utf8'));
  const copilotOutputPath = process.env.PR_COPILOT_OUTPUT_PATH || '';
  let generated = null;
  let generationMode = '确定性代码差异兜底';
  if (copilotOutputPath && fs.existsSync(copilotOutputPath)) {
    generated = resolveJsonCandidate(fs.readFileSync(copilotOutputPath, 'utf8'));
    if (generated) generationMode = 'Copilot CLI';
  }

  const summary = sanitizeGenerated(generated, fallback);
  const kindLabels = inferKindLabels(context, summary);
  const releaseLabels = inferReleaseLabels(context, summary);
  context.generationMode = generationMode;
  const current = findOpenPullRequest();
  const currentBody = current?.body || '';
  const body = buildBody(currentBody, summary, context);
  let prNumber = current?.number;
  let createdPullRequest = false;

  if (prNumber) {
    gh([
      '--method', 'PATCH',
      `repos/${repo}/pulls/${prNumber}`,
      '--input', '-',
    ], JSON.stringify({ title: summary.title, body }));
  } else {
    const created = ghJson([
      '--method', 'POST',
      `repos/${repo}/pulls`,
      '--input', '-',
    ], JSON.stringify({
      base: targetBranch,
      head: sourceBranch,
      title: summary.title,
      body,
    }));
    prNumber = created.number;
    createdPullRequest = true;
  }

  applyKindLabels(prNumber, kindLabels);
  applyReleaseLabels(prNumber, releaseLabels);
  upsertSuccessComment(prNumber, summary.title, { kindLabels, releaseLabels }, {
    createIfMissing: createdPullRequest,
  });
  writeOutput({
    pr_number: prNumber,
    pr_title: summary.title,
    generation_mode: generationMode,
    kind_labels: kindLabels.join(','),
    release_labels: releaseLabels.join(','),
  });
}

const command = process.argv[2];
if (command === 'generate') {
  generate();
} else if (command === 'apply') {
  applySummary();
} else {
  throw new Error(`Unknown command: ${command}`);
}
