const { execFileSync } = require('node:child_process');
const {
  authorDisplayText,
  coreAndAuthorMentionText,
  effectiveAuthorFromBody,
  upsertMarkerComment,
} = require('./pr-notifications');

const repo = process.env.GITHUB_REPOSITORY || '';
const prNumber = Number(process.env.PR_NUMBER || '0');
const marker = '<!-- workflow:pr-close-status -->';
let prDetailsCache = null;

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

function prDetails() {
  if (!prDetailsCache) {
    prDetailsCache = ghJson([
      '--method', 'GET',
      `repos/${repo}/pulls/${prNumber}`,
    ], undefined, process.env.GH_TOKEN) || {};
  }
  return prDetailsCache;
}

function effectivePrAuthor() {
  const details = prDetails();
  return effectiveAuthorFromBody({
    body: details.body || '',
    prAuthor: process.env.PR_AUTHOR || details.user?.login || '',
  });
}

function upsertComment(body) {
  const token = process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN;
  upsertMarkerComment({ gh, ghJson, repo, prNumber, marker, body, token, position: 'first' });
}

function notifyClose() {
  if (!repo || !prNumber) throw new Error('Missing PR context.');

  const author = effectivePrAuthor();
  const merged = process.env.PR_MERGED === 'true';

  if (!merged) {
    console.log('PR was closed without merge; automated close comment skipped.');
    return;
  }

  const title = process.env.PR_TITLE || 'unknown';
  const source = process.env.PR_HEAD_REF || 'unknown';
  const target = process.env.PR_BASE_REF || 'unknown';
  const mergedBy = process.env.PR_MERGED_BY || 'unknown';
  const mergeSha = process.env.PR_MERGE_COMMIT_SHA || 'N/A';
  const body = [
    marker,
    merged ? '## PR 合并成功并关闭' : '## PR 未合并但已关闭',
    '',
    `- PR 链接：#${prNumber}`,
    `- 标题：${title}`,
    `- 分支流向：${source} -> ${target}`,
    `- 提交人：${authorDisplayText(author)}`,
    '- 关闭原因：已成功合并',
    `- 合并人：${mergedBy}`,
    `- 合并提交：${mergeSha}`,
    `- 通知对象：${coreAndAuthorMentionText(author)}`,
    '',
    '> 本通知由 GitHub Actions 自动发布。',
  ].filter(Boolean).join('\n');

  upsertComment(body);
}

const command = process.argv[2];
if (command === 'notify-close') {
  notifyClose();
} else {
  throw new Error(`Unknown command: ${command}`);
}
