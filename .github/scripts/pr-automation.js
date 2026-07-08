const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const repo = process.env.GITHUB_REPOSITORY || '';
const [owner, repoName] = repo.split('/');
const sourceBranch = process.env.SOURCE_BRANCH || process.env.GITHUB_REF_NAME || '';
const targetBranch = process.env.TARGET_BRANCH || '';
const actor = process.env.GITHUB_ACTOR || 'unknown';
const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
const runnerTemp = process.env.RUNNER_TEMP || workspace;

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
  if (normalized.startsWith('chore/') || normalized.startsWith('assets/')) return 'assets';
  return 'project';
}

function buildTitle(files) {
  const categories = files.map((line) => {
    const parts = line.split(/\t+/);
    return fileCategory(parts[parts.length - 1] || line);
  });
  const has = (category) => categories.includes(category);

  if (has('github')) return '重构 GitHub 自动化流程';
  if (has('tools')) return '优化构建与发布工具';
  if (has('source')) return '更新项目代码实现';
  if (has('tests')) return '完善测试覆盖';
  if (has('version') || has('assets')) return '更新版本与发布资源';
  if (has('docs')) return '完善项目文档说明';
  return '更新项目实现';
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
  const files = cleanLines(context.changedFiles, 80);
  const title = buildTitle(files);
  const changes = buildChanges(files);
  return {
    title,
    summary: `${title}，涉及 ${files.length || 0} 个文件。`,
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
    'JSON 格式：{"title":"中文 PR 标题","summary":"一句话摘要","changes":["主要改动"],"validation":["验证建议"],"risk":["风险或影响"]}',
    '标题要求：中文，动宾结构，不超过 50 字，体现代码内容，不得写成“分支 A 到分支 B”。',
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
  const safeTitle = title && !branchTitlePattern.test(title) && title.length <= 80
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

function buildAutoBlock(summary, context) {
  const bulletList = (items) => items.map((item) => `- ${item}`).join('\n');
  const changedFiles = cleanLines(context.changedFiles, 20)
    .map((line) => `- \`${line.replace(/\t/g, ' ')}\``)
    .join('\n') || '- 无';

  return [
    '<!-- workflow:auto-summary:start -->',
    `<!-- workflow:source-actor:${actor} -->`,
    '### 自动生成摘要',
    '',
    `- 分支流向：\`${context.sourceBranch} -> ${context.targetBranch}\``,
    `- 提交人：\`${actor}\``,
    `- 生成方式：${context.generationMode}`,
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
    '<details>',
    '<summary>变更文件</summary>',
    '',
    changedFiles,
    '',
    '</details>',
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
    '-f', 'sort=created',
    '-f', 'direction=desc',
    '-f', 'per_page=1',
  ]);
  return Array.isArray(result) ? result[0] : null;
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

function upsertSuccessComment(prNumber, title) {
  const commentToken = process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN;
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
  } else {
    gh([
      '--method', 'POST',
      `repos/${repo}/issues/${prNumber}/comments`,
      '--input', '-',
    ], JSON.stringify({ body }), commentToken);
  }
}

function generate() {
  if (!repo || !owner || !repoName || !sourceBranch || !targetBranch) {
    throw new Error('Missing repository or branch context.');
  }

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
  writeOutput({ skipped: 'false' });
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

  upsertSuccessComment(prNumber, summary.title);
  writeOutput({ pr_number: prNumber, pr_title: summary.title, generation_mode: generationMode });
}

const command = process.argv[2];
if (command === 'generate') {
  generate();
} else if (command === 'apply') {
  applySummary();
} else {
  throw new Error(`Unknown command: ${command}`);
}
