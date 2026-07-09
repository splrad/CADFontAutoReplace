const { execFileSync } = require('node:child_process');
const fs = require('node:fs');

const repo = process.env.GITHUB_REPOSITORY || '';
const [owner, repoName] = repo.split('/');
const prNumber = Number(process.env.PR_NUMBER || process.env.GITHUB_EVENT_PULL_REQUEST_NUMBER || '0');
const prAuthor = process.env.PR_AUTHOR || 'unknown';
const headRef = process.env.PR_HEAD_REF || '';
const baseRef = process.env.PR_BASE_REF || '';
const headSha = process.env.PR_HEAD_SHA || '';
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

function mentionText() {
  const trusted = parseTrustedDevelopers();
  const mentions = [...new Set(trusted)].map((user) => `@${user}`);
  const effectiveAuthor = effectivePrAuthor();
  if (effectiveAuthor && effectiveAuthor !== 'unknown' && !trusted.includes(effectiveAuthor)) {
    mentions.push(`@${effectiveAuthor}`);
  }
  return mentions.length ? mentions.join('；') : '（未配置通知对象）';
}

function findMarkerComment(marker, token) {
  const comments = ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}/comments`,
    '-f', 'per_page=100',
  ], undefined, token) || [];
  return comments.find((comment) => String(comment.body || '').includes(marker));
}

function upsertComment(marker, body) {
  const token = process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN;
  const existing = findMarkerComment(marker, token);
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

function createComment(body) {
  const token = process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN;
  gh([
    '--method', 'POST',
    `repos/${repo}/issues/${prNumber}/comments`,
    '--input', '-',
  ], JSON.stringify({ body }), token);
}

function deleteComment(marker) {
  const token = process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN;
  const existing = findMarkerComment(marker, token);
  if (!existing) return;

  gh([
    '--method', 'DELETE',
    `repos/${repo}/issues/comments/${existing.id}`,
  ], undefined, token);
}

function writeStepSummary(title, lines) {
  const summaryLines = [
    `## ${title}`,
    '',
    ...lines,
    '',
  ];
  const summary = summaryLines.join('\n');
  if (process.env.GITHUB_STEP_SUMMARY) {
    fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary, 'utf8');
  }
  console.log(summary);
}

function finishGate({ marker, failed, body, summaryTitle, summaryLines, commentPolicy = 'upsert' }) {
  writeStepSummary(summaryTitle, summaryLines);
  if (failed) {
    if (commentPolicy === 'none') {
      deleteComment(marker);
    } else if (commentPolicy === 'replace') {
      deleteComment(marker);
      createComment(body);
    } else {
      upsertComment(marker, body);
    }
    process.exit(1);
  }

  deleteComment(marker);
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

  gh([
    '--method', 'POST',
    `repos/${repo}/pulls/${prNumber}/reviews`,
    '-f', 'event=APPROVE',
  ]);
  writeStepSummary('自动审批已完成', [
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- PR 提交人：${effectiveAuthor}`,
  ]);
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
      detail = 'main 目标 PR 需要核心开发者对当前提交的有效审批。';
      failed = true;
    }
  }

  const marker = '<!-- workflow:main-authorization-gate -->';
  const body = [
    marker,
    failed ? '## 主分支授权门禁未通过' : '## 主分支授权门禁已通过',
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
  finishGate({
    marker,
    failed,
    body,
    summaryTitle: failed ? '主分支授权门禁未通过' : '主分支授权门禁已通过',
    summaryLines: [
      `- 状态：${status}`,
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- PR 提交人：${effectiveAuthor}`,
      '',
      detail,
    ],
  });
}

function copilotReviewsForHead() {
  const reviews = flattenReviews(ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/reviews`,
    '--paginate',
    '--slurp',
  ]) || []);
  return reviews.filter((review) => {
    const login = normalizeGitHubLogin(review?.user?.login);
    return login === 'copilot-pull-request-reviewer' && review.commit_id === headSha;
  });
}

function normalizeGitHubLogin(login) {
  return String(login || '').toLowerCase().replace(/\[bot\]$/, '');
}

function isCopilotCodeReviewComment(comment) {
  const reviewAuthor = normalizeGitHubLogin(comment?.pullRequestReview?.author?.login);
  if (reviewAuthor) return reviewAuthor === 'copilot-pull-request-reviewer';

  return normalizeGitHubLogin(comment?.author?.login) === 'copilot-pull-request-reviewer';
}

function unresolvedCopilotThreadFindings() {
  const query = `
    query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
      repository(owner: $owner, name: $name) {
        pullRequest(number: $number) {
          reviewThreads(first: 100, after: $cursor) {
            pageInfo { hasNextPage endCursor }
            nodes {
              isResolved
              isOutdated
              comments(first: 20) {
                nodes {
                  author { login }
                  pullRequestReview {
                    author { login }
                  }
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
  const blockingPattern = /^\s*(severity\s*[:：]\s*blocking|严重程度\s*[:：]\s*阻断)(?:\s|$)/im;
  const severityPattern = /^\s*(severity\s*[:：]\s*(blocking|suggestion)|严重程度\s*[:：]\s*(阻断|建议))(?:\s|$)/im;
  const blocking = [];
  const unclassified = [];
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
      // Outdated 线程属于旧 head 的代码上下文。当前 head 是否可合并由最新 Copilot review、
      // 未解决的当前线程，以及 ruleset 的 required review thread resolution 共同约束。
      if (thread.isResolved || thread.isOutdated) continue;
      const comments = thread.comments?.nodes || [];
      const copilotComments = comments.filter((comment) => isCopilotCodeReviewComment(comment));
      const blockingComment = copilotComments.find((comment) => {
        return blockingPattern.test(String(comment?.body || ''));
      });

      if (blockingComment) {
        blocking.push({
          url: blockingComment.url || '',
          body: String(blockingComment.body || '').split(/\r?\n/)[0].slice(0, 140),
        });
      }

      const unclassifiedComment = copilotComments.find((comment) => {
        return !severityPattern.test(String(comment?.body || ''));
      });
      if (unclassifiedComment) {
        unclassified.push({
          url: unclassifiedComment.url || '',
          body: String(unclassifiedComment.body || '').split(/\r?\n/)[0].slice(0, 140) || 'Copilot 评论',
        });
      }
    }
    cursor = threads?.pageInfo?.hasNextPage ? threads.pageInfo.endCursor : null;
  } while (cursor);

  return { blocking, unclassified };
}

function copilotReviewGate() {
  ensurePrContext();
  const reviews = copilotReviewsForHead();
  const marker = '<!-- workflow:copilot-review-gate -->';
  let failed = false;
  let commentPolicy = 'upsert';
  let title = '## Copilot 审查门禁已通过';
  let detail = '当前提交已完成 Copilot 代码审查，且未发现未解决的重大问题。';
  let blocking = [];
  let unclassified = [];

  if (reviews.length === 0) {
    failed = true;
    commentPolicy = 'none';
    title = '## Copilot 审查门禁未通过';
    detail = '当前提交尚未检测到 Copilot 代码审查。请等待规则集自动审查完成，或重新推送触发审查。';
  } else {
    const findings = unresolvedCopilotThreadFindings();
    blocking = findings.blocking;
    unclassified = findings.unclassified;
    if (blocking.length > 0) {
      failed = true;
      commentPolicy = 'replace';
      title = '## Copilot 审查门禁未通过';
      detail = '检测到 Copilot 留下的未解决重大问题。';
    }
  }

  const blockingList = blocking.length
    ? blocking.map((item) => `- ${item.url ? `[${item.body || 'Copilot 评论'}](${item.url})` : item.body}`).join('\n')
    : '- 无';
  const unclassifiedList = unclassified.length
    ? unclassified.map((item) => `- ${item.url ? `[${item.body || 'Copilot 评论'}](${item.url})` : item.body}`).join('\n')
    : '- 无';

  const body = [
    marker,
    title,
    '',
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 当前提交：${headSha}`,
    `- Copilot 审查数量：${reviews.length}`,
    `- 未识别严重程度评论：${unclassified.length}`,
    `- 通知对象：${mentionText()}`,
    '',
    detail,
    '',
    '### 未解决重大问题',
    blockingList,
    '',
    '### 未识别严重程度评论',
    unclassifiedList,
    '',
    unclassified.length
      ? '> 未识别严重程度评论不会阻断合并，但说明 Copilot 未完全遵守本仓库中文审查指令。'
      : '> Copilot 评论均符合本仓库严重程度标记约定。',
    '',
    '> 本通知由 GitHub Actions 自动发布。',
  ].join('\n');
  finishGate({
    marker,
    failed,
    body,
    commentPolicy,
    summaryTitle: failed ? 'Copilot 审查门禁未通过' : 'Copilot 审查门禁已通过',
    summaryLines: [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 当前提交：${headSha}`,
      `- Copilot 审查数量：${reviews.length}`,
      `- 未解决重大问题：${blocking.length}`,
      `- 未识别严重程度评论：${unclassified.length}`,
      '',
      detail,
      '',
      '### 未解决重大问题',
      blockingList,
      '',
      '### 未识别严重程度评论',
      unclassifiedList,
    ],
  });
}

const command = process.argv[2];
if (command === 'auto-approve') {
  autoApprove();
} else if (command === 'main-authorization') {
  mainAuthorizationGate();
} else if (command === 'copilot-review') {
  copilotReviewGate();
} else {
  throw new Error(`Unknown command: ${command}`);
}
