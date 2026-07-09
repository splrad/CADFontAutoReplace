const { execFileSync } = require('node:child_process');
const fs = require('node:fs');

const repo = process.env.GITHUB_REPOSITORY || '';
const prNumber = Number(process.env.PR_NUMBER || process.env.GITHUB_EVENT_PULL_REQUEST_NUMBER || '0');
const marker = '<!-- workflow:dco-signoff-advisory -->';
const maxListedIssues = 20;

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

function normalizeEmail(email) {
  return String(email || '').trim().toLowerCase();
}

function shortSha(sha) {
  return String(sha || '').slice(0, 7) || 'unknown';
}

function commitSubject(commit) {
  return String(commit?.commit?.message || '').split(/\r?\n/)[0].trim() || '(empty commit message)';
}

function isBotIdentity(login, type, name, email) {
  const values = [login, name, email].map((value) => String(value || '').toLowerCase());
  return String(type || '').toLowerCase() === 'bot'
    || values.some((value) => value.endsWith('[bot]') || value.includes('[bot]@'))
    || values.some((value) => value === 'dependabot' || value === 'github-actions');
}

function isBotCommit(commit) {
  const login = commit?.author?.login || commit?.committer?.login || '';
  const type = commit?.author?.type || commit?.committer?.type || '';
  const name = commit?.commit?.author?.name || commit?.commit?.committer?.name || '';
  const email = commit?.commit?.author?.email || commit?.commit?.committer?.email || '';
  return isBotIdentity(login, type, name, email);
}

function parseSignOffs(message) {
  const lines = String(message || '').split(/\r?\n/);
  const signOffLines = lines.filter((line) => /^\s*Signed-off-by\s*:/i.test(line));
  return signOffLines.map((line) => {
    const match = line.match(/^\s*Signed-off-by\s*:\s*(.+?)\s*<([^<>@\s]+@[^<>\s]+)>\s*$/i);
    if (!match) {
      return { raw: line.trim(), valid: false, email: '' };
    }
    return {
      raw: line.trim(),
      valid: true,
      name: match[1].trim(),
      email: normalizeEmail(match[2]),
    };
  });
}

function evaluateCommit(commit) {
  const sha = commit?.sha || '';
  const message = commit?.commit?.message || '';
  const authorName = commit?.commit?.author?.name || '';
  const authorEmail = normalizeEmail(commit?.commit?.author?.email);
  const subject = commitSubject(commit);

  if (isBotCommit(commit)) {
    return { status: 'skipped', reason: 'bot', sha, subject };
  }

  const signOffs = parseSignOffs(message);
  const validSignOffs = signOffs.filter((item) => item.valid);

  if (!signOffs.length) {
    return {
      status: 'failed',
      reason: 'missing',
      sha,
      subject,
      authorName,
      authorEmail,
    };
  }

  if (!validSignOffs.length) {
    return {
      status: 'failed',
      reason: 'invalid_format',
      sha,
      subject,
      authorName,
      authorEmail,
      signOffs,
    };
  }

  if (!authorEmail || !validSignOffs.some((item) => item.email === authorEmail)) {
    return {
      status: 'failed',
      reason: 'email_mismatch',
      sha,
      subject,
      authorName,
      authorEmail,
      signOffs: validSignOffs,
    };
  }

  return { status: 'passed', sha, subject };
}

function analyzeCommits(commits) {
  const results = commits.map(evaluateCommit);
  return {
    total: results.length,
    passed: results.filter((item) => item.status === 'passed').length,
    skipped: results.filter((item) => item.status === 'skipped').length,
    issues: results.filter((item) => item.status === 'failed'),
  };
}

function issueReason(issue) {
  if (issue.reason === 'missing') {
    return `缺少 Signed-off-by；建议添加 \`Signed-off-by: ${issue.authorName || 'Name'} <${issue.authorEmail || 'email@example.com'}>\``;
  }
  if (issue.reason === 'invalid_format') {
    return 'Signed-off-by 格式无效；应使用 `Signed-off-by: Name <email>`';
  }
  if (issue.reason === 'email_mismatch') {
    const signedEmails = issue.signOffs.map((item) => item.email).join(', ') || 'none';
    return `Signed-off-by 邮箱与 commit author email 不一致；author email 为 \`${issue.authorEmail || 'unknown'}\`，当前签名邮箱为 \`${signedEmails}\``;
  }
  return 'Signed-off-by 检查未通过';
}

function issueLine(issue) {
  return `- \`${shortSha(issue.sha)}\` ${issue.subject}: ${issueReason(issue)}`;
}

function advisoryBody(result) {
  const lines = [
    marker,
    '## DCO Signed-off-by 提示',
    '',
    '此检查当前不是合并门禁；它参考 DCO 1.1 习惯，帮助外部贡献保持清晰来源声明。',
    '',
    '### 需要处理的 commits',
    ...result.issues.slice(0, maxListedIssues).map(issueLine),
  ];

  if (result.issues.length > maxListedIssues) {
    lines.push(`- 另有 ${result.issues.length - maxListedIssues} 个 commit 未列出。`);
  }

  lines.push(
    '',
    '### 修复方式',
    '- 新提交：`git commit -s`',
    '- 最新提交补签：`git commit --amend -s`',
    '- 多个提交补签：`git rebase --signoff upstream/main`，或替换为当前 PR 的 base branch，然后重新推送分支。',
    '',
    '签名格式应为：`Signed-off-by: Name <email>`，其中 email 应与 commit author email 一致。',
    '',
    '> 本提示由 GitHub Actions 自动发布；当前不阻断合并。',
  );

  return lines.join('\n');
}

function writeStepSummary(result, error = '') {
  const lines = [
    '## DCO Sign-off Advisory',
    '',
    error ? `- 检查状态：运行失败，已按 advisory 策略放行。` : '- 检查状态：完成。',
    `- Commits total: ${result?.total ?? 0}`,
    `- Passed: ${result?.passed ?? 0}`,
    `- Skipped bot commits: ${result?.skipped ?? 0}`,
    `- Advisory issues: ${result?.issues?.length ?? 0}`,
  ];

  if (error) {
    lines.push('', `> ${error}`);
  } else if (result.issues.length) {
    lines.push('', '### Advisory issues');
    lines.push(...result.issues.slice(0, maxListedIssues).map(issueLine));
  } else {
    lines.push('', '未发现需要提示的 DCO Signed-off-by 问题。');
  }

  const summary = `${lines.join('\n')}\n`;
  if (process.env.GITHUB_STEP_SUMMARY) {
    fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary, 'utf8');
  }
  console.log(summary);
}

function findMarkerComment() {
  const comments = fetchAll(`repos/${repo}/issues/${prNumber}/comments`);
  return comments.find((comment) => String(comment.body || '').includes(marker));
}

function upsertComment(body) {
  const existing = findMarkerComment();
  if (existing) {
    gh([
      '--method', 'PATCH',
      `repos/${repo}/issues/comments/${existing.id}`,
      '--input', '-',
    ], JSON.stringify({ body }));
    return;
  }

  gh([
    '--method', 'POST',
    `repos/${repo}/issues/${prNumber}/comments`,
    '--input', '-',
  ], JSON.stringify({ body }));
}

function deleteCommentIfPresent() {
  const existing = findMarkerComment();
  if (!existing) return;

  gh([
    '--method', 'DELETE',
    `repos/${repo}/issues/comments/${existing.id}`,
  ]);
}

function ensureContext() {
  if (!repo || !prNumber) {
    throw new Error('Missing repository or pull request context.');
  }
}

function runAdvisory() {
  try {
    ensureContext();
    const commits = fetchAll(`repos/${repo}/pulls/${prNumber}/commits`);
    const result = analyzeCommits(commits);
    writeStepSummary(result);

    if (result.issues.length) {
      upsertComment(advisoryBody(result));
    } else {
      deleteCommentIfPresent();
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.warn(`::warning::DCO Sign-off Advisory failed: ${message}`);
    writeStepSummary({ total: 0, passed: 0, skipped: 0, issues: [] }, message);
  }
}

function fixtureCommit({ sha, name, email, message, login = '', type = 'User' }) {
  return {
    sha,
    author: login ? { login, type } : null,
    commit: {
      author: { name, email },
      message,
    },
  };
}

function assertEqual(actual, expected, label) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${expected}, got ${actual}`);
  }
}

function selfTest() {
  const fixtures = [
    fixtureCommit({
      sha: '1111111',
      name: 'Alice',
      email: 'alice@example.com',
      message: 'feat: add feature\n\nSigned-off-by: Alice <alice@example.com>',
    }),
    fixtureCommit({
      sha: '2222222',
      name: 'Bob',
      email: 'bob@example.com',
      message: 'fix: missing signoff',
    }),
    fixtureCommit({
      sha: '3333333',
      name: 'Carol',
      email: 'carol@example.com',
      message: 'docs: mismatch\n\nSigned-off-by: Carol <other@example.com>',
    }),
    fixtureCommit({
      sha: '4444444',
      name: 'dependabot[bot]',
      email: '49699333+dependabot[bot]@users.noreply.github.com',
      login: 'dependabot[bot]',
      type: 'Bot',
      message: 'chore: bump dependency',
    }),
  ];
  const result = analyzeCommits(fixtures);
  assertEqual(result.total, 4, 'total commits');
  assertEqual(result.passed, 1, 'passed commits');
  assertEqual(result.skipped, 1, 'skipped bot commits');
  assertEqual(result.issues.length, 2, 'advisory issues');
  assertEqual(result.issues[0].reason, 'missing', 'missing signoff reason');
  assertEqual(result.issues[1].reason, 'email_mismatch', 'email mismatch reason');
  console.log('DCO self-test passed.');
}

if (process.argv.includes('--self-test')) {
  selfTest();
} else {
  runAdvisory();
}
