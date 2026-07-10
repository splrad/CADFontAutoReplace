const { execFileSync } = require('node:child_process');
const {
  authorDisplayText,
  coreAndAuthorMentionText,
  deleteMarkerComments,
  effectiveAuthorFromBody,
  upsertMarkerComment,
} = require('./pr-notifications');

const repo = process.env.GITHUB_REPOSITORY || '';
const prNumber = Number(process.env.PR_NUMBER || '0');
const marker = '<!-- workflow:pr-close-status -->';
const governanceMarkers = [
  '<!-- workflow:pr-blocking-failures -->',
  '<!-- workflow:main-authorization-gate -->',
  '<!-- workflow:copilot-review-gate -->',
];
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

function cleanupGovernanceComments({
  remove = (governanceMarker) => deleteMarkerComments({
    gh,
    ghJson,
    repo,
    prNumber,
    marker: governanceMarker,
    token: process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN,
  }),
} = {}) {
  return governanceMarkers.reduce((count, governanceMarker) => count + remove(governanceMarker), 0);
}

function notifyClose({
  cleanup = cleanupGovernanceComments,
  merged = process.env.PR_MERGED === 'true',
  resolveAuthor = effectivePrAuthor,
  publish = upsertComment,
} = {}) {
  if (!repo || !prNumber) throw new Error('Missing PR context.');

  const removedComments = cleanup();

  if (!merged) {
    console.log(`PR was closed without merge; removed ${removedComments} governance comment(s).`);
    return { merged: false, removedComments, body: '' };
  }

  const author = resolveAuthor();
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

  publish(body);
  return { merged: true, removedComments, body };
}

function runCommand(command) {
  if (command === 'notify-close') {
    notifyClose();
  } else {
    throw new Error(`Unknown command: ${command}`);
  }
}

if (require.main === module) {
  runCommand(process.argv[2]);
} else {
  module.exports = {
    cleanupGovernanceComments,
    governanceMarkers,
    notifyClose,
    runCommand,
  };
}
