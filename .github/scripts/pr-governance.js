const { execFileSync } = require('node:child_process');

const repo = process.env.GITHUB_REPOSITORY || '';
const [owner, repoName] = repo.split('/');
const prNumber = Number(process.env.PR_NUMBER || process.env.GITHUB_EVENT_PULL_REQUEST_NUMBER || '0');
const prAuthor = process.env.PR_AUTHOR || 'unknown';
const headRef = process.env.PR_HEAD_REF || '';
const baseRef = process.env.PR_BASE_REF || '';
const headSha = process.env.PR_HEAD_SHA || '';
const headRepo = process.env.PR_HEAD_REPO || '';
const actor = process.env.GITHUB_ACTOR || 'unknown';
let prDetailsCache = null;
let effectiveAuthorCache = null;

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

function parseTrustedDevelopers() {
  try {
    const parsed = JSON.parse(process.env.TRUSTED_DEVELOPERS || '[]');
    return Array.isArray(parsed)
      ? parsed.filter((item) => typeof item === 'string' && item.trim()).map((item) => item.trim())
      : [];
  } catch {
    return [];
  }
}

function isTrusted(user) {
  return parseTrustedDevelopers().includes(user);
}

function prDetails() {
  if (!prDetailsCache) {
    prDetailsCache = ghJson([
      '--method', 'GET',
      `repos/${repo}/pulls/${prNumber}`,
    ]) || {};
  }
  return prDetailsCache;
}

function effectivePrAuthor() {
  if (effectiveAuthorCache) return effectiveAuthorCache;
  const body = String(prDetails().body || '');
  const match = body.match(/<!--\s*workflow:source-actor:([A-Za-z0-9-]+)\s*-->/);
  effectiveAuthorCache = match?.[1] || prAuthor;
  return effectiveAuthorCache;
}

function flattenReviews(payload) {
  if (!Array.isArray(payload)) return [];
  if (payload.length > 0 && Array.isArray(payload[0])) return payload.flat();
  return payload;
}

function latestApproversForHead() {
  const reviews = flattenReviews(ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/reviews`,
    '--paginate',
    '--slurp',
  ]) || []);

  const byUser = new Map();
  for (const review of reviews) {
    const login = review?.user?.login || '';
    if (!login) continue;
    const previous = byUser.get(login);
    if (!previous || String(review.submitted_at || '') > String(previous.submitted_at || '')) {
      byUser.set(login, review);
    }
  }

  return [...byUser.values()]
    .filter((review) => review.state === 'APPROVED' && review.commit_id === headSha)
    .map((review) => review.user.login);
}

function changedFiles() {
  const files = ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/files`,
    '--paginate',
    '--slurp',
  ]) || [];
  const flattened = Array.isArray(files[0]) ? files.flat() : files;
  return flattened.map((file) => String(file.filename || '')).filter(Boolean);
}

function isProtectedConfig(file) {
  const normalized = file.replace(/\\/g, '/');
  return normalized.startsWith('.github/workflows/')
    || normalized.startsWith('.github/actions/')
    || normalized.startsWith('.github/skills/')
    || normalized.startsWith('.github/scripts/')
    || normalized === '.github/copilot-instructions.md'
    || normalized === '.github/pull_request_template.md'
    || normalized === '.github/release.yml';
}

function mentionText() {
  const trusted = parseTrustedDevelopers();
  const mentions = [...new Set(trusted)].map((user) => `@${user}`);
  const effectiveAuthor = effectivePrAuthor();
  if (effectiveAuthor && effectiveAuthor !== 'unknown' && !trusted.includes(effectiveAuthor)) {
    mentions.push(`@${effectiveAuthor}`);
  }
  return mentions.length ? mentions.join('；') : '（未配置通知对象）';
}

function upsertComment(marker, body) {
  const token = process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN;
  const comments = ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}/comments`,
    '-f', 'per_page=100',
  ], undefined, token) || [];
  const existing = comments.find((comment) => String(comment.body || '').includes(marker));
  if (existing) {
    gh([
      '--method', 'PATCH',
      `repos/${repo}/issues/comments/${existing.id}`,
      '--input', '-',
    ], JSON.stringify({ body }), token);
  } else {
    gh([
      '--method', 'POST',
      `repos/${repo}/issues/${prNumber}/comments`,
      '--input', '-',
    ], JSON.stringify({ body }), token);
  }
}

function formatFiles(files) {
  return files.length ? files.map((file) => `- \`${file}\``).join('\n') : '- 无';
}

function ensurePrContext() {
  if (!repo || !owner || !repoName || !prNumber) {
    throw new Error('Missing pull request context.');
  }
}

function autoApprove() {
  ensurePrContext();
  const effectiveAuthor = effectivePrAuthor();
  if (!isTrusted(effectiveAuthor)) {
    console.log(`PR submitter ${effectiveAuthor} is not trusted; auto approval skipped.`);
    return;
  }

  const body = [
    '## 自动审批已完成',
    '',
    '- 动作类型：APPROVE',
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- PR 提交人：${effectiveAuthor}`,
    '',
    '> 本审批由 GitHub Actions 自动完成。',
  ].join('\n');

  gh([
    '--method', 'POST',
    `repos/${repo}/pulls/${prNumber}/reviews`,
    '-f', 'event=APPROVE',
    '-f', `body=${body}`,
  ]);
}

function protectedConfigGate() {
  ensurePrContext();
  const protectedFiles = changedFiles().filter(isProtectedConfig);
  const effectiveAuthor = effectivePrAuthor();
  let status = 'passed_no_protected_changes';
  let detail = '未检测到受保护自动化配置变更。';
  let failed = false;

  if (protectedFiles.length > 0) {
    if (isTrusted(effectiveAuthor)) {
      status = 'passed_trusted_author';
      detail = `检测到受保护自动化配置变更，PR 提交人 ${effectiveAuthor} 属于核心开发者。`;
    } else {
      const approvers = latestApproversForHead();
      const trustedApprover = approvers.find(isTrusted);
      if (trustedApprover) {
        status = 'passed_trusted_approval';
        detail = `检测到受保护自动化配置变更，已获得核心开发者 ${trustedApprover} 对当前提交的审批。`;
      } else {
        status = 'failed_missing_trusted_approval';
        detail = '检测到受保护自动化配置变更，但当前提交尚未获得核心开发者有效审批。';
        failed = true;
      }
    }
  }

  const marker = '<!-- workflow:protected-config-gate -->';
  const body = [
    marker,
    failed ? '## Protected Configuration Gate 未通过' : '## Protected Configuration Gate 已通过',
    '',
    `- 状态：${status}`,
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- PR 提交人：${effectiveAuthor}`,
    `- 通知对象：${mentionText()}`,
    '',
    detail,
    '',
    '### 受保护文件',
    formatFiles(protectedFiles),
    '',
    '> 本通知由 GitHub Actions 自动发布。',
  ].join('\n');
  upsertComment(marker, body);
  if (failed) process.exit(1);
}

function mainAuthorizationGate() {
  ensurePrContext();
  let status = 'passed_author_trusted';
  const effectiveAuthor = effectivePrAuthor();
  let detail = `PR 提交人 ${effectiveAuthor} 属于核心开发者。`;
  let failed = false;

  if (!isTrusted(effectiveAuthor)) {
    const approvers = latestApproversForHead();
    const trustedApprover = approvers.find(isTrusted);
    if (trustedApprover) {
      status = 'passed_trusted_approval';
      detail = `已获得核心开发者 ${trustedApprover} 对当前提交的审批。`;
    } else {
      status = 'failed_missing_trusted_approval';
      detail = 'main 目标 PR 需要核心开发者对当前 head SHA 的有效审批。';
      failed = true;
    }
  }

  const marker = '<!-- workflow:main-authorization-gate -->';
  const body = [
    marker,
    failed ? '## Main Authorization Gate 未通过' : '## Main Authorization Gate 已通过',
    '',
    `- 状态：${status}`,
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- PR 提交人：${effectiveAuthor}`,
    `- 通知对象：${mentionText()}`,
    '',
    detail,
    '',
    '> 本通知由 GitHub Actions 自动发布。',
  ].join('\n');
  upsertComment(marker, body);
  if (failed) process.exit(1);
}

function sourceBranchGate() {
  ensurePrContext();
  const sameRepo = !headRepo || headRepo === repo;
  const effectiveAuthor = effectivePrAuthor();
  const trustedAuthor = isTrusted(effectiveAuthor);
  const allowed = sameRepo && (headRef === 'test' || trustedAuthor);
  const marker = '<!-- workflow:source-branch-gate -->';

  const body = [
    marker,
    allowed ? '## Source Branch Gate 已通过' : '## Source Branch Gate 未通过',
    '',
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 来源仓库：${headRepo || 'unknown'}`,
    `- PR 提交人：${effectiveAuthor}`,
    `- 通知对象：${mentionText()}`,
    '',
    allowed
      ? (trustedAuthor && headRef !== 'test' ? '核心开发者快速通道允许非 test 分支合入 main。' : '来源分支符合 test -> main 规则。')
      : 'main 仅允许 test 分支发起 PR；核心开发者可走快速通道；外部 fork 禁止直达 main。',
    '',
    '> 本通知由 GitHub Actions 自动发布。',
  ].join('\n');
  upsertComment(marker, body);
  if (!allowed) process.exit(1);
}

function copilotReviewsForHead() {
  const reviews = flattenReviews(ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/reviews`,
    '--paginate',
    '--slurp',
  ]) || []);
  return reviews.filter((review) => {
    const login = String(review?.user?.login || '').toLowerCase();
    return login.includes('copilot') && review.commit_id === headSha;
  });
}

function unresolvedBlockingCopilotThreads() {
  const query = `
    query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
      repository(owner: $owner, name: $name) {
        pullRequest(number: $number) {
          reviewThreads(first: 100, after: $cursor) {
            pageInfo { hasNextPage endCursor }
            nodes {
              isResolved
              comments(first: 20) {
                nodes {
                  author { login }
                  body
                  url
                }
              }
            }
          }
        }
      }
    }
  `;
  const blockingPattern = /(severity\s*:\s*(blocking|high|critical)|\b(blocking|critical)\b|重大|严重|高风险)/i;
  const blocking = [];
  let cursor = null;

  do {
    const args = [
      'graphql',
      '-f', `query=${query}`,
      '-f', `owner=${owner}`,
      '-f', `name=${repoName}`,
      '-F', `number=${prNumber}`,
    ];
    if (cursor) args.push('-f', `cursor=${cursor}`);
    const payload = ghJson(args);
    const threads = payload?.data?.repository?.pullRequest?.reviewThreads;
    for (const thread of threads?.nodes || []) {
      if (thread.isResolved) continue;
      const comments = thread.comments?.nodes || [];
      const copilotComment = comments.find((comment) => {
        const login = String(comment?.author?.login || '').toLowerCase();
        const body = String(comment?.body || '');
        return login.includes('copilot') && blockingPattern.test(body);
      });
      if (copilotComment) {
        blocking.push({
          url: copilotComment.url || '',
          body: String(copilotComment.body || '').split(/\r?\n/)[0].slice(0, 140),
        });
      }
    }
    cursor = threads?.pageInfo?.hasNextPage ? threads.pageInfo.endCursor : null;
  } while (cursor);

  return blocking;
}

function copilotReviewGate() {
  ensurePrContext();
  const reviews = copilotReviewsForHead();
  const marker = '<!-- workflow:copilot-review-gate -->';
  let failed = false;
  let title = '## Copilot Review Gate 已通过';
  let detail = '当前 head SHA 已完成 Copilot code review，且未发现未解决的重大问题。';
  let blocking = [];

  if (reviews.length === 0) {
    failed = true;
    title = '## Copilot Review Gate 未通过';
    detail = '当前 head SHA 尚未检测到 Copilot code review。请等待 ruleset 自动审查完成，或重新推送触发审查。';
  } else {
    blocking = unresolvedBlockingCopilotThreads();
    if (blocking.length > 0) {
      failed = true;
      title = '## Copilot Review Gate 未通过';
      detail = '检测到 Copilot 留下的未解决重大问题。';
    }
  }

  const blockingList = blocking.length
    ? blocking.map((item) => `- ${item.url ? `[${item.body || 'Copilot comment'}](${item.url})` : item.body}`).join('\n')
    : '- 无';

  const body = [
    marker,
    title,
    '',
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 当前 head：${headSha}`,
    `- Copilot review 数量：${reviews.length}`,
    `- 通知对象：${mentionText()}`,
    '',
    detail,
    '',
    '### 未解决重大问题',
    blockingList,
    '',
    '> 本通知由 GitHub Actions 自动发布。',
  ].join('\n');
  upsertComment(marker, body);
  if (failed) process.exit(1);
}

const command = process.argv[2];
if (command === 'auto-approve') {
  autoApprove();
} else if (command === 'protected-config') {
  protectedConfigGate();
} else if (command === 'main-authorization') {
  mainAuthorizationGate();
} else if (command === 'source-branch') {
  sourceBranchGate();
} else if (command === 'copilot-review') {
  copilotReviewGate();
} else {
  throw new Error(`Unknown command: ${command}`);
}
