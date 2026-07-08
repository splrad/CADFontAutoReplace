const { execFileSync } = require('node:child_process');

const repo = process.env.GITHUB_REPOSITORY || '';
const prNumber = Number(process.env.PR_NUMBER || '0');
const marker = '<!-- workflow:pr-close-status -->';

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

function mentionText(author) {
  const trusted = parseTrustedDevelopers();
  const mentions = [...new Set(trusted)].map((user) => `@${user}`);
  if (author && author !== 'unknown' && !trusted.includes(author)) mentions.push(`@${author}`);
  return mentions.length ? mentions.join('；') : '（未配置通知对象）';
}

function upsertComment(body) {
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

function notifyClose() {
  if (!repo || !prNumber) throw new Error('Missing PR context.');

  const author = process.env.PR_AUTHOR || 'unknown';
  const closer = process.env.PR_CLOSED_BY || 'unknown';
  const merged = process.env.PR_MERGED === 'true';
  const comments = ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}/comments`,
    '-f', 'per_page=100',
  ]) || [];

  const humanComments = comments
    .filter((comment) => String(comment.body || '').trim())
    .filter((comment) => !String(comment.body || '').includes(marker))
    .filter((comment) => String(comment.user?.type || '').toLowerCase() !== 'bot')
    .filter((comment) => !String(comment.user?.login || '').endsWith('[bot]'))
    .sort((a, b) => String(a.created_at || '').localeCompare(String(b.created_at || '')));
  const lastHuman = humanComments[humanComments.length - 1];
  const lastHumanBody = String(lastHuman?.body || '').replace(/\s+/g, ' ').trim();

  if (!merged && (!lastHuman || lastHuman.user?.login !== closer || !lastHumanBody)) {
    gh([
      '--method', 'PATCH',
      `repos/${repo}/pulls/${prNumber}`,
      '-f', 'state=open',
    ]);
    gh([
      '--method', 'POST',
      `repos/${repo}/issues/${prNumber}/comments`,
      '-f', 'body=当前 PR 未说明关闭原因，无法关闭。请在评论区补充关闭原因后再关闭。',
    ], undefined, process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN);
    throw new Error('Closed PR without a closer comment; reopened.');
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
    `- 提交人：${author}`,
    merged ? '- 关闭原因：已成功合并' : `- 关闭原因：${lastHumanBody}`,
    merged ? `- 合并人：${mergedBy}` : `- 关闭人：${closer}`,
    merged ? `- 合并提交：${mergeSha}` : '',
    `- 通知对象：${mentionText(author)}`,
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
