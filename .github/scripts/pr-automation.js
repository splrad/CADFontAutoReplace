const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const {
  isInstallOrPackagePath,
  isRuntimeReleasePath,
  loadPolicyOrDefault,
  normalizeRepoPath,
} = require('./pr-classification-policy');

const repo = process.env.GITHUB_REPOSITORY || '';
const [owner, repoName] = repo.split('/');
const sourceBranch = process.env.SOURCE_BRANCH || process.env.GITHUB_REF_NAME || '';
let targetBranch = process.env.TARGET_BRANCH || '';
const actor = process.env.GITHUB_ACTOR || 'unknown';
const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
const runnerTemp = process.env.RUNNER_TEMP || workspace;
const policy = loadPolicyOrDefault(process.env.PR_CLASSIFICATION_RULES
  || path.join(workspace, '.github', 'pr-classification-rules.json'));

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
  const normalized = normalizeRepoPath(file);
  if (normalized.startsWith('.github/')) return 'github';
  if (/^(version\.props|package\.json|package-lock\.json|pnpm-lock\.yaml|yarn\.lock|.*\.csproj|.*\.slnx?)$/i.test(normalized)
    || isInstallOrPackagePath(normalized, policy)) return 'version';
  if (normalized.startsWith('src/') || normalized.startsWith('app/') || normalized.startsWith('lib/')
    || isRuntimeReleasePath(normalized, policy)) return 'source';
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

function htmlAttribute(value) {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function normalizeLogin(value) {
  const login = String(value || '').trim();
  return /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/.test(login) ? login : '';
}

function unique(values) {
  const seen = new Set();
  const result = [];
  for (const value of values) {
    const key = String(value || '').toLowerCase();
    if (!key || seen.has(key)) continue;
    seen.add(key);
    result.push(value);
  }
  return result;
}

function metadataValue(body, name) {
  const match = String(body || '').match(new RegExp(`<!--\\s*${escapeRegExp(name)}:([^>]*)-->`, 'i'));
  return match?.[1]?.trim() || '';
}

function parseContributorLogins(body) {
  const raw = metadataValue(body, 'workflow:source-contributors');
  return unique(raw
    .split(/[,\s]+/)
    .map(normalizeLogin)
    .filter(Boolean));
}

function parseSourceActor(body) {
  return normalizeLogin(metadataValue(body, 'workflow:source-actor'));
}

function contributorLogins(existingBody) {
  return unique([
    ...parseContributorLogins(existingBody),
    parseSourceActor(existingBody),
    normalizeLogin(actor),
  ].filter(Boolean));
}

function contributorAvatar(login) {
  const safeLogin = htmlAttribute(login);
  const encodedLogin = encodeURIComponent(login);
  return `<a href="https://github.com/${encodedLogin}" title="${safeLogin}"><img src="https://github.com/${encodedLogin}.png?size=64" width="32" height="32" alt="${safeLogin}" /></a>`;
}

function contributorBlock(logins) {
  if (!logins.length) return '';
  return [
    '### 贡献者',
    '',
    logins.map(contributorAvatar).join(' '),
    '',
  ].join('\n');
}

function sanitizeTrailerName(name) {
  return String(name || '')
    .replace(/[<>\r\n]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function sanitizeTrailerEmail(email) {
  const value = String(email || '').trim();
  return /^[^<>\s@]+@[^<>\s@]+$/.test(value) ? value : '';
}

function coAuthorTrailersFromGit() {
  const out = runAllowFail('git', ['log', '--format=%aN <%aE>', `origin/${targetBranch}..origin/${sourceBranch}`]);
  const seen = new Set();
  const trailers = [];
  for (const line of cleanLines(out, 100)) {
    const match = line.match(/^(.+?)\s+<([^<>]+)>$/);
    if (!match) continue;
    const name = sanitizeTrailerName(match[1]);
    const email = sanitizeTrailerEmail(match[2]);
    const key = email.toLowerCase();
    if (!name || !email || seen.has(key)) continue;
    seen.add(key);
    trailers.push(`Co-authored-by: ${name} <${email}>`);
  }
  return trailers;
}

function stripManagedCoAuthorBlock(body) {
  return String(body || '').replace(/\n*\s*<!-- workflow:co-authored-by -->[\s\S]*$/i, '').trimEnd();
}

function appendCoAuthorBlock(body, trailers) {
  if (!Array.isArray(trailers) || trailers.length === 0) return body;
  return [
    String(body || '').trimEnd(),
    '',
    '<!-- workflow:co-authored-by -->',
    '<details>',
    '<summary>Co-authored-by</summary>',
    '',
    ...trailers,
    '',
    '</details>',
    '',
  ].join('\n');
}

function buildAutoBlock(summary, context, existingBody = '') {
  const bulletList = (items) => items.map((item) => `- ${item}`).join('\n');
  const changedFileCount = cleanLines(context.changedFiles, 10000).length;
  const contributors = contributorLogins(existingBody);
  const sourceActor = parseSourceActor(existingBody) || normalizeLogin(actor) || 'unknown';
  const contributorSection = contributorBlock(contributors);

  return [
    '<!-- workflow:auto-summary:start -->',
    `<!-- workflow:source-actor:${sourceActor} -->`,
    `<!-- workflow:source-contributors:${contributors.join(',')} -->`,
    `<!-- workflow:auto-context:source=${htmlCommentValue(context.sourceBranch)};target=${htmlCommentValue(context.targetBranch)};generation=${htmlCommentValue(context.generationMode)};changed-files=${changedFileCount} -->`,
    '### 摘要',
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
    ...(contributorSection ? [contributorSection] : []),
    '<!-- workflow:auto-summary:end -->',
  ].join('\n');
}

function buildBody(existingBody, summary, context) {
  const templatePath = path.join(workspace, '.github', 'pull_request_template.md');
  const bodyWithoutCoAuthors = stripManagedCoAuthorBlock(existingBody);
  const autoBlock = buildAutoBlock(summary, context, bodyWithoutCoAuthors);
  const markerPattern = /<!-- workflow:auto-summary:start -->[\s\S]*?<!-- workflow:auto-summary:end -->/;
  let body;

  if (bodyWithoutCoAuthors && markerPattern.test(bodyWithoutCoAuthors)) {
    body = bodyWithoutCoAuthors.replace(markerPattern, autoBlock);
    return appendCoAuthorBlock(body, context.authorTrailers);
  }

  if (bodyWithoutCoAuthors && bodyWithoutCoAuthors.trim() && !bodyWithoutCoAuthors.includes('正在基于当前代码差异生成')) {
    body = `${bodyWithoutCoAuthors.trim()}\n\n---\n\n${autoBlock}\n`;
    return appendCoAuthorBlock(body, context.authorTrailers);
  }

  const template = fs.existsSync(templatePath)
    ? fs.readFileSync(templatePath, 'utf8')
    : '## 变更摘要\n\n<!-- workflow:auto-summary:start -->\n等待自动生成。\n<!-- workflow:auto-summary:end -->\n';

  if (markerPattern.test(template)) {
    body = template.replace(markerPattern, autoBlock);
    return appendCoAuthorBlock(body, context.authorTrailers);
  }
  body = `${template.trim()}\n\n${autoBlock}\n`;
  return appendCoAuthorBlock(body, context.authorTrailers);
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
    authorTrailers: coAuthorTrailersFromGit(),
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
  context.generationMode = generationMode;
  const current = findOpenPullRequest();
  const currentBody = current?.body || '';
  const body = buildBody(currentBody, summary, context);
  let prNumber = current?.number;

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
  }

  writeOutput({
    pr_number: prNumber,
    pr_title: summary.title,
    generation_mode: generationMode,
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
