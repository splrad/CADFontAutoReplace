const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const {
  createIssueComment,
  deleteMarkerComments,
  listMarkerComments,
  mentionText: notificationMentionText,
  normalizeLogin,
  parseTrustedDevelopers,
  realContributorLoginsFromBody,
  uniqueLogins,
} = require('./pr-notifications');

const repo = process.env.GITHUB_REPOSITORY || '';
const [owner, repoName] = repo.split('/');
const prNumber = Number(process.env.PR_NUMBER || process.env.GITHUB_EVENT_PULL_REQUEST_NUMBER || '0');
let prAuthor = process.env.PR_AUTHOR || '';
let headRef = process.env.PR_HEAD_REF || '';
let baseRef = process.env.PR_BASE_REF || '';
let headSha = process.env.PR_HEAD_SHA || '';
const eventName = process.env.GITHUB_EVENT_NAME || '';
const copilotReviewerLogin = 'copilot-pull-request-reviewer[bot]';
const copilotCheckName = process.env.COPILOT_REVIEW_CHECK_NAME || 'Copilot Code Review Gate';
const copilotNoBlockingConclusionPattern = /(?:^|\r?\n)\s*(?:#{1,6}\s*)?结论\s*(?::|：)?\s*(?:\r?\n\s*)*未发现需要阻断合并的问题。/;
const copilotNoCommentsPattern = /Copilot reviewed \d+ out of \d+ changed files in this pull request and generated no (?:new )?comments\./i;
const copilotGeneratedCommentsPattern = /Copilot reviewed \d+ out of \d+ changed files in this pull request and generated (\d+) (?:new )?comments\./i;
const copilotBlockingSeverityPattern = /^\s*(severity\s*[:：]\s*blocking|严重程度\s*[:：]\s*阻断)(?:\s|$)/i;
const copilotSuggestionSeverityPattern = /^\s*(severity\s*[:：]\s*suggestion|严重程度\s*[:：]\s*建议)(?:\s|$)/i;
const copilotTitlePattern = /^\s*(?:#{1,6}\s*)?(?:标题|title)\s*[:：]\s*(.+?)\s*$/i;
const autoApprovalMarker = '<!-- workflow:auto-approval -->';
let prDetailsCache = null;
let realContributorsCache = null;
let pullRequestCommitsCache = null;
let commitContributorSummaryCache = null;

function gh(args, input, token) {
  const executable = process.env.GH_EXECUTABLE || 'gh';
  const prefixArgs = parseGhExecutableArgs();
  return execFileSync(executable, [...prefixArgs, 'api', ...args], {
    encoding: 'utf8',
    input,
    stdio: ['pipe', 'pipe', 'pipe'],
    env: {
      ...process.env,
      GH_TOKEN: token || process.env.GH_TOKEN || '',
    },
  }).trim();
}

function parseGhExecutableArgs() {
  const raw = process.env.GH_EXECUTABLE_ARGS;
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    if (Array.isArray(parsed) && parsed.every((arg) => typeof arg === 'string')) {
      return parsed;
    }
  } catch {
    // fall through to the guarded warning below
  }
  console.warn('Warning: GH_EXECUTABLE_ARGS must be a JSON array of strings; ignoring it.');
  return [];
}

function ghJson(args, input, token) {
  const out = gh(args, input, token);
  return out ? JSON.parse(out) : null;
}

function isOwnPullRequestApprovalError(error) {
  const output = [
    error?.stdout,
    error?.stderr,
    Array.isArray(error?.output) ? error.output.join('\n') : '',
    error?.message,
  ].map((value) => String(value || '')).join('\n');
  return /Review Can not approve your own pull request/i.test(output);
}

function ghReadToken() {
  return process.env.GH_READ_TOKEN || process.env.GH_TOKEN || '';
}

function ghChecksToken() {
  return process.env.GH_CHECKS_TOKEN || process.env.GH_TOKEN || '';
}

function parsePositiveInteger(value, fallback) {
  const parsed = Number.parseInt(String(value || ''), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function sleepMilliseconds(milliseconds) {
  if (milliseconds <= 0) return;
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

function isAtOrAfter(timestamp, threshold) {
  const time = Date.parse(timestamp || '');
  const thresholdTime = Date.parse(threshold || '');
  return Number.isFinite(time) && Number.isFinite(thresholdTime) && time >= thresholdTime;
}

function isTrusted(user) {
  const normalized = normalizeGitHubLogin(user);
  return parseTrustedDevelopers().some((trusted) => normalizeGitHubLogin(trusted) === normalized);
}

function trustedDeveloperLogins() {
  return parseTrustedDevelopers();
}

function prDetails() {
  if (!prDetailsCache) {
    prDetailsCache = ghJson([
      '--method', 'GET',
      `repos/${repo}/pulls/${prNumber}`,
    ], undefined, ghReadToken()) || {};
  }
  return prDetailsCache;
}

function hydratePrContext() {
  if (!repo || !owner || !repoName || !prNumber) return;
  const details = prDetails();
  prAuthor ||= details?.user?.login || 'unknown';
  headRef ||= details?.head?.ref || '';
  baseRef ||= details?.base?.ref || '';
  headSha ||= details?.head?.sha || '';
}

function pullRequestCommits() {
  if (pullRequestCommitsCache) return pullRequestCommitsCache;
  pullRequestCommitsCache = flattenReviews(ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/commits`,
    '-f', 'per_page=100',
    '--paginate',
    '--slurp',
  ], undefined, ghReadToken()) || []);
  return pullRequestCommitsCache;
}

function isBotCommit(commit) {
  const login = String(commit?.author?.login || commit?.committer?.login || '').toLowerCase();
  const type = String(commit?.author?.type || commit?.committer?.type || '').toLowerCase();
  const name = String(commit?.commit?.author?.name || commit?.commit?.committer?.name || '').toLowerCase();
  const email = String(commit?.commit?.author?.email || commit?.commit?.committer?.email || '').toLowerCase();
  return type === 'bot'
    || [login, name, email].some((value) => value.endsWith('[bot]') || value.includes('[bot]@'))
    || [login, name].some((value) => value === 'github-actions' || value === 'dependabot');
}

function pullRequestCommitContributorSummary(commits) {
  const logins = [];
  const unidentified = [];
  for (const commit of commits || []) {
    const login = normalizeLogin(commit?.author?.login || '');
    if (login) {
      logins.push(login);
      continue;
    }
    if (isBotCommit(commit)) continue;
    unidentified.push({
      sha: String(commit?.sha || ''),
      name: String(commit?.commit?.author?.name || '').trim(),
      email: String(commit?.commit?.author?.email || '').trim(),
    });
  }
  return { logins: uniqueLogins(logins), unidentified };
}

function commitContributorSummary() {
  if (!commitContributorSummaryCache) {
    commitContributorSummaryCache = pullRequestCommitContributorSummary(pullRequestCommits());
  }
  return commitContributorSummaryCache;
}

function pullRequestCommitAuthorLogins() {
  return commitContributorSummary().logins;
}

function unidentifiedCommitAuthors() {
  return commitContributorSummary().unidentified;
}

function unidentifiedCommitAuthorDisplay() {
  return unidentifiedCommitAuthors().map((author) => {
    const identity = author.name || author.email || 'unknown';
    return `${String(author.sha || '').slice(0, 7) || 'unknown'} (${identity})`;
  }).join(', ');
}

function realContributorLogins() {
  if (realContributorsCache) return realContributorsCache;
  const body = String(prDetails().body || '');
  realContributorsCache = uniqueLogins([
    ...realContributorLoginsFromBody({ body, prAuthor }),
    ...pullRequestCommitAuthorLogins(),
  ]);
  return realContributorsCache;
}

function realContributorDisplay() {
  const contributors = realContributorLogins();
  return contributors.length ? contributors.join(', ') : '未识别真实贡献者';
}

function untrustedContributorLogins() {
  return realContributorLogins().filter((login) => !isTrusted(login));
}

function flattenReviews(payload) {
  if (!Array.isArray(payload)) return [];
  if (payload.length > 0 && Array.isArray(payload[0])) return payload.flat();
  return payload;
}

function latestReviewsForHeadByUser() {
  const reviews = flattenReviews(ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/reviews`,
    '--paginate',
    '--slurp',
  ], undefined, ghReadToken()) || []);

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
    .filter((review) => review.commit_id === headSha);
}

function latestApprovalsForHead({ includeAutomation = true } = {}) {
  return latestReviewsForHeadByUser()
    .filter((review) => review.state === 'APPROVED')
    .filter((review) => includeAutomation || !isAutomationApproval(review));
}

function latestTrustedApproversForHead({ includeAutomation = true } = {}) {
  return latestApprovalsForHead({ includeAutomation })
    .map((review) => review.user.login)
    .filter(isTrusted);
}

function isAutomationApproval(review) {
  const login = normalizeGitHubLogin(review?.user?.login);
  const type = String(review?.user?.type || '').toLowerCase();
  const expectedReviewer = normalizeGitHubLogin(process.env.AUTO_APPROVE_REVIEWER || '');
  return review?.state === 'APPROVED'
    && review?.commit_id === headSha
    && (
      String(review?.body || '').includes(autoApprovalMarker)
      || login === 'github-actions'
      || type === 'bot'
      || (expectedReviewer && login === expectedReviewer)
    );
}

function requestedReviewerLogins() {
  const payload = ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/requested_reviewers`,
  ], undefined, ghReadToken()) || {};
  return Array.isArray(payload.users)
    ? payload.users.map((user) => user?.login || '').filter(Boolean)
    : [];
}

function issueTimelineEvents() {
  return flattenReviews(ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}/timeline`,
    '-f', 'per_page=100',
    '--paginate',
    '--slurp',
  ], undefined, ghReadToken()) || []);
}

function copilotReviewRequestEvents() {
  return issueTimelineEvents().filter((event) => {
    return event?.event === 'review_requested' && isCopilotReviewRequestLogin(event?.requested_reviewer?.login);
  });
}

function copilotReviewRequestEventsSince(threshold) {
  const thresholdTime = Date.parse(threshold || '');
  const adjustedThreshold = Number.isFinite(thresholdTime)
    ? new Date(thresholdTime - 5000).toISOString()
    : threshold;
  return copilotReviewRequestEvents().filter((event) => isAtOrAfter(event?.created_at, adjustedThreshold));
}

function isCopilotReviewRequestLogin(login) {
  const normalized = normalizeGitHubLogin(login);
  return normalized === 'copilot-pull-request-reviewer' || normalized === 'copilot';
}

function commentToken() {
  return process.env.GH_COMMENT_TOKEN || process.env.GH_TOKEN;
}

function coreReviewersToRequest({ trusted, author, requested, reviewed = [] }) {
  const authorLogin = normalizeGitHubLogin(author);
  const alreadyHandled = new Set(uniqueLogins([...requested, ...reviewed]).map(normalizeGitHubLogin));
  const eligible = uniqueLogins(trusted)
    .filter((login) => normalizeGitHubLogin(login) !== authorLogin);
  return {
    eligible,
    reviewed: uniqueLogins(reviewed),
    missing: eligible.filter((login) => !alreadyHandled.has(normalizeGitHubLogin(login))),
  };
}

function commandErrorSummary(error) {
  const text = [error?.stderr, error?.stdout, error?.message]
    .map((value) => String(value || '').trim())
    .find(Boolean) || 'unknown error';
  return text.split(/\r?\n/)[0].slice(0, 500);
}

function requestCoreDeveloperReviews({
  trusted = trustedDeveloperLogins(),
  author = prAuthor,
  requested = requestedReviewerLogins(),
  reviewed = latestReviewsForHeadByUser().map((review) => review?.user?.login || ''),
  confirm = requestedReviewerLogins,
  request = (reviewers) => gh([
    '--method', 'POST',
    `repos/${repo}/pulls/${prNumber}/requested_reviewers`,
    '--input', '-',
  ], JSON.stringify({ reviewers })),
} = {}) {
  const plan = coreReviewersToRequest({ trusted, author, requested, reviewed });
  if (plan.eligible.length === 0) {
    return { ok: false, ...plan, requested: [], error: '未配置可请求的核心开发者。' };
  }
  if (plan.missing.length === 0) {
    return { ok: true, ...plan, requested: [], error: '' };
  }
  try {
    request(plan.missing);
    const confirmedSet = new Set(uniqueLogins(confirm()).map(normalizeGitHubLogin));
    const unconfirmed = plan.missing
      .filter((login) => !confirmedSet.has(normalizeGitHubLogin(login)));
    if (unconfirmed.length > 0) {
      return {
        ok: false,
        ...plan,
        requested: plan.missing.filter((login) => !unconfirmed.includes(login)),
        error: `GitHub 未确认 Review Request：${unconfirmed.join(', ')}`,
      };
    }
    return { ok: true, ...plan, requested: plan.missing, error: '' };
  } catch (error) {
    return { ok: false, ...plan, requested: [], error: commandErrorSummary(error) };
  }
}

function deleteComment(marker) {
  const token = commentToken();
  deleteMarkerComments({ gh, ghJson, repo, prNumber, marker, token });
}

const blockingFailuresMarker = '<!-- workflow:pr-blocking-failures -->';
const blockingFailuresStatePattern = /<!--\s*workflow:pr-blocking-failures-state:([A-Za-z0-9+/=_-]+)\s*-->/;
const blockingSourceOrder = [
  'main-authorization',
  'copilot-review:blocking-comments',
  'copilot-review:comment-protocol',
  'copilot-review:request-failed',
  'copilot-review:passing-conclusion',
  'copilot-review',
];

function encodeBlockingState(state) {
  return Buffer.from(JSON.stringify(state), 'utf8').toString('base64');
}

function decodeBlockingState(body) {
  const match = String(body || '').match(blockingFailuresStatePattern);
  if (!match) return null;
  try {
    const parsed = JSON.parse(Buffer.from(match[1], 'base64').toString('utf8'));
    return parsed && Array.isArray(parsed.failures)
      ? { head: String(parsed.head || ''), failures: parsed.failures }
      : null;
  } catch {
    return null;
  }
}

function orderedBlockingFailures(failures) {
  return [...failures].sort((a, b) => {
    const left = blockingSourceOrder.indexOf(a.source);
    const right = blockingSourceOrder.indexOf(b.source);
    return (left === -1 ? 99 : left) - (right === -1 ? 99 : right);
  });
}

function normalizeBlockingFailure(failure) {
  return {
    source: String(failure?.source || ''),
    title: String(failure?.title || '待处理事项'),
    handlers: uniqueLogins(failure?.handlers || []),
    details: Array.isArray(failure?.details)
      ? failure.details.map((detail) => String(detail || '').trim()).filter(Boolean)
      : [],
  };
}

function blockingFailureBody(state) {
  const failures = orderedBlockingFailures(state.failures);
  const sections = [];
  for (const [index, rawFailure] of failures.entries()) {
    const failure = normalizeBlockingFailure(rawFailure);
    if (index > 0) sections.push('---', '');
    sections.push(`### ${failure.title}`, '');
    sections.push(`**处理人：** ${notificationMentionText(failure.handlers)}`, '');
    for (const detail of failure.details) {
      sections.push(detail, '');
    }
  }

  return [
    blockingFailuresMarker,
    `<!-- workflow:pr-blocking-failures-state:${encodeBlockingState(state)} -->`,
    '## 🚧 PR 合并前有待处理事项',
    '',
    ...sections,
    '> 🤖 本评论由 GitHub Actions 自动维护，全部阻断解除后将自动删除。',
  ].join('\n').trimEnd();
}

function nextBlockingFailureState(existingState, currentHead, {
  source,
  title,
  handlers,
  failed,
  details,
}) {
  return nextBlockingFailuresState(existingState, currentHead, {
    sourcePrefix: source,
    failures: failed ? [{ source, title, handlers, details }] : [],
  });
}

function nextBlockingFailuresState(existingState, currentHead, { sourcePrefix, failures }) {
  const state = existingState?.head === currentHead && Array.isArray(existingState.failures)
    ? { head: currentHead, failures: existingState.failures }
    : { head: currentHead, failures: [] };

  state.failures = state.failures.filter((failure) => {
    const source = String(failure?.source || '');
    return source !== sourcePrefix && !source.startsWith(`${sourcePrefix}:`);
  });
  state.failures.push(...(failures || []).map(normalizeBlockingFailure));

  return state;
}

function writeBlockingFailureState(state, repost) {
  const token = commentToken();
  const comments = listMarkerComments({
    ghJson,
    repo,
    prNumber,
    marker: blockingFailuresMarker,
    token,
  });
  const existing = comments[comments.length - 1];

  if (state.failures.length === 0) {
    deleteMarkerComments({ gh, ghJson, repo, prNumber, marker: blockingFailuresMarker, token });
    return;
  }

  const body = blockingFailureBody(state);
  if (!existing || repost) {
    deleteMarkerComments({ gh, ghJson, repo, prNumber, marker: blockingFailuresMarker, token });
    createIssueComment({ gh, repo, prNumber, body, token });
    return;
  }

  gh([
    '--method', 'PATCH',
    `repos/${repo}/issues/comments/${existing.id}`,
    '--input', '-',
  ], JSON.stringify({ body }), token);

  for (const stale of comments.slice(0, -1)) {
    gh([
      '--method', 'DELETE',
      `repos/${repo}/issues/comments/${stale.id}`,
    ], undefined, token);
  }
}

function updateBlockingFailure({ source, title, handlers, failed, details }) {
  updateBlockingFailures({
    sourcePrefix: source,
    failures: failed ? [{ source, title, handlers, details }] : [],
  });
}

function updateBlockingFailures({ sourcePrefix, failures }) {
  const token = commentToken();
  const comments = listMarkerComments({
    ghJson,
    repo,
    prNumber,
    marker: blockingFailuresMarker,
    token,
  });
  const existing = comments[comments.length - 1];
  const existingState = decodeBlockingState(existing?.body);
  const headChanged = Boolean(existing && existingState?.head && existingState.head !== headSha);
  const state = nextBlockingFailuresState(existingState, headSha, { sourcePrefix, failures });

  writeBlockingFailureState(state, headChanged);
}

function cleanupLegacyGateComment(marker) {
  deleteComment(marker);
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

function ensurePrContext() {
  if (!repo || !owner || !repoName || !prNumber) {
    throw new Error('Missing pull request context.');
  }
  hydratePrContext();
  if (!headSha) {
    throw new Error('Missing pull request head SHA.');
  }
}

function autoApprove() {
  ensurePrContext();
  const contributors = realContributorLogins();
  const contributorDisplay = realContributorDisplay();
  const unidentifiedAuthors = unidentifiedCommitAuthors();
  const untrusted = untrustedContributorLogins();
  const reviewer = process.env.AUTO_APPROVE_REVIEWER || '';
  if (unidentifiedAuthors.length > 0) {
    writeStepSummary('自动审批已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 无法识别的提交作者：${unidentifiedCommitAuthorDisplay()}`,
      '- 原因：存在无法关联 GitHub 账号的非机器人提交作者，必须由核心开发者人工确认。',
    ]);
    return;
  }
  if (contributors.length === 0) {
    writeStepSummary('自动审批已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      '- 原因：未识别真实贡献者。',
    ]);
    return;
  }

  if (untrusted.length > 0) {
    writeStepSummary('自动审批已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 真实贡献者：${contributorDisplay}`,
      `- 非核心贡献者：${untrusted.join(', ')}`,
      '- 原因：只要存在非核心贡献者，就必须由核心开发者手动 approval。',
    ]);
    return;
  }

  const existingTrustedApprovers = latestTrustedApproversForHead();
  if (existingTrustedApprovers.length > 0) {
    writeStepSummary('自动审批已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 真实贡献者：${contributorDisplay}`,
      `- 当前提交已有核心开发者 approval：${existingTrustedApprovers.join(', ')}`,
    ]);
    return;
  }

  if (reviewer && normalizeGitHubLogin(prAuthor) && normalizeGitHubLogin(prAuthor) === normalizeGitHubLogin(reviewer)) {
    writeStepSummary('自动审批已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 真实贡献者：${contributorDisplay}`,
      `- 当前提交：${headSha.slice(0, 12) || 'unknown'}`,
      '- 原因：GitHub 不允许 PR 作者审批自己的 PR；main 授权由 Main Authorization Gate 使用真实提交人判断。',
    ]);
    return;
  }

  if (!process.env.GH_TOKEN) {
    writeStepSummary('自动审批已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 真实贡献者：${contributorDisplay}`,
      '- 原因：未配置 `CORE_AUTO_APPROVAL_TOKEN`，无法提交 GitHub 原生 approval。',
    ]);
    return;
  }

  try {
    gh([
      '--method', 'POST',
      `repos/${repo}/pulls/${prNumber}/reviews`,
      '--input', '-',
    ], JSON.stringify({
      event: 'APPROVE',
      commit_id: headSha,
      body: [
        autoApprovalMarker,
        '自动审批：全部真实贡献者均在核心开发者名单中。',
      ].join('\n'),
    }));
  } catch (error) {
    if (!isOwnPullRequestApprovalError(error)) throw error;
    writeStepSummary('自动审批已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 真实贡献者：${contributorDisplay}`,
      `- 当前提交：${headSha.slice(0, 12) || 'unknown'}`,
      '- 原因：GitHub 拒绝自审 approval；main 授权由 Main Authorization Gate 使用真实提交人判断。',
    ]);
    return;
  }
  writeStepSummary('自动审批已完成', [
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 真实贡献者：${contributorDisplay}`,
    `- 当前提交：${headSha.slice(0, 12) || 'unknown'}`,
    `- 自动审批标记：${autoApprovalMarker}`,
  ]);
}

function mainAuthorizationFailurePresentation({ status, coreHandlers, reviewRequest }) {
  if (status === 'failed_unidentified_commit_authors') {
    const requestLine = reviewRequest?.ok
      ? '已向核心开发者发送 Review Request，请核对提交作者并提交 **Approve**。'
      : 'Review Request 自动发送失败，请核对提交作者并检查 Main Authorization Gate 日志。';
    return {
      title: '⚠️ 贡献者信息识别异常',
      handlers: reviewRequest?.eligible || coreHandlers,
      details: [
        '部分提交作者未关联到可识别的 GitHub 账号。',
        requestLine,
      ],
    };
  }
  if (status === 'failed_missing_real_contributors') {
    return {
      title: '⚠️ 贡献者信息识别异常',
      handlers: coreHandlers,
      details: [
        '门禁未识别到当前 PR 的真实贡献者。',
        '请检查 PR contributor metadata 和 commit author 关联，并重新运行门禁。',
      ],
    };
  }

  const requestLine = reviewRequest?.ok
    ? '已向所有可请求的核心开发者发送 Review Request，请完成审查并提交 **Approve**。'
    : 'Review Request 自动发送失败，请完成审查并检查 Main Authorization Gate 日志。';
  return {
    title: '🔒 核心开发者审批',
    handlers: reviewRequest?.eligible || coreHandlers,
    details: [
      '当前 PR 尚未获得所需审批。',
      requestLine,
    ],
  };
}

function mainAuthorizationGate() {
  ensurePrContext();
  let status = 'passed';
  const contributors = realContributorLogins();
  const contributorDisplay = realContributorDisplay();
  const unidentifiedAuthors = unidentifiedCommitAuthors();
  const untrusted = untrustedContributorLogins();
  const trustedApprovers = latestTrustedApproversForHead();
  const trustedManualApprovers = latestTrustedApproversForHead({ includeAutomation: false });
  let detail = '';
  let failed = false;

  if (unidentifiedAuthors.length > 0) {
    if (trustedManualApprovers.length > 0) {
      status = 'passed_manual_core_approval_for_unidentified_authors';
      detail = `存在无法关联 GitHub 账号的提交作者，但已获得核心开发者 ${trustedManualApprovers.join(', ')} 对当前提交的手动审批。`;
    } else {
      status = 'failed_unidentified_commit_authors';
      detail = `存在无法关联 GitHub 账号的非机器人提交作者：${unidentifiedCommitAuthorDisplay()}。`;
      failed = true;
    }
  } else if (contributors.length === 0) {
    status = 'failed_missing_real_contributors';
    detail = 'main 目标 PR 未识别真实贡献者，不能自动或人工放行。';
    failed = true;
  } else if (untrusted.length > 0) {
    if (trustedManualApprovers.length > 0) {
      status = 'passed_manual_core_approval';
      detail = `检测到非核心贡献者 ${untrusted.join(', ')}，但已获得核心开发者 ${trustedManualApprovers.join(', ')} 对当前提交的手动审批。`;
    } else {
      status = 'failed_untrusted_contributor_missing_manual_approval';
      detail = `检测到非核心贡献者 ${untrusted.join(', ')}；当前 head 必须获得至少 1 位核心开发者的手动 approval。`;
      failed = true;
    }
  } else if (trustedApprovers.length > 0) {
    status = 'passed_all_contributors_trusted_with_approval';
    detail = `全部真实贡献者均为核心开发者，且当前提交已有核心开发者 approval：${trustedApprovers.join(', ')}。`;
  } else {
    status = 'failed_trusted_contributors_missing_approval';
    detail = '全部真实贡献者均为核心开发者，但当前 head 还没有核心开发者 approval；请配置 `CORE_AUTO_APPROVAL_TOKEN` 或手动审批。';
    failed = true;
  }

  const coreHandlers = trustedDeveloperLogins();
  const needsReviewRequest = [
    'failed_unidentified_commit_authors',
    'failed_untrusted_contributor_missing_manual_approval',
    'failed_trusted_contributors_missing_approval',
  ].includes(status);
  const reviewRequest = needsReviewRequest
    ? requestCoreDeveloperReviews()
    : null;
  const presentation = mainAuthorizationFailurePresentation({
    status,
    coreHandlers,
    reviewRequest,
  });

  cleanupLegacyGateComment('<!-- workflow:main-authorization-gate -->');
  writeStepSummary(failed ? '主分支授权门禁未通过' : '主分支授权门禁已通过', [
    `- 状态：${status}`,
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 真实贡献者：${contributorDisplay}`,
    `- 无法识别的提交作者：${unidentifiedAuthors.length ? unidentifiedCommitAuthorDisplay() : '无'}`,
    `- 非核心贡献者：${untrusted.length ? untrusted.join(', ') : '无'}`,
    `- 核心开发者 approval：${trustedApprovers.length ? trustedApprovers.join(', ') : '无'}`,
    `- 核心开发者手动 approval：${trustedManualApprovers.length ? trustedManualApprovers.join(', ') : '无'}`,
    `- Review Request 可请求对象：${reviewRequest?.eligible?.join(', ') || '不适用'}`,
    `- Review Request 本次新增：${reviewRequest?.requested?.join(', ') || '无'}`,
    `- 当前 head 已完成 review：${reviewRequest?.reviewed?.join(', ') || '无'}`,
    `- Review Request 状态：${reviewRequest ? (reviewRequest.ok ? '成功' : `失败：${reviewRequest.error}`) : '不适用'}`,
    '',
    detail,
  ]);
  updateBlockingFailure({
    source: 'main-authorization',
    title: presentation.title,
    handlers: presentation.handlers,
    failed,
    details: presentation.details,
  });

  if (failed) {
    process.exit(1);
  }
}

function requestCopilotReview() {
  ensurePrContext();
  if (!process.env.GH_TOKEN) {
    writeStepSummary('Copilot 审查请求失败', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 当前提交：${headSha}`,
      '- 原因：未提供请求 Copilot 审查的 token。',
      '- 请配置仓库 secret `COPILOT_REVIEW_REQUEST_TOKEN`：拥有 Copilot 订阅的用户 fine-grained PAT，仓库权限 `Pull requests: Read and write`。',
    ]);
    process.exit(1);
  }

  const reviews = copilotReviewsForHead();
  if (reviews.length > 0) {
    writeStepSummary('Copilot 审查请求已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 当前提交：${headSha}`,
      ...requestActorDiagnosticLines(),
      `- 原因：当前提交已有 Copilot 代码审查。`,
    ]);
    return;
  }

  const requestedReviewers = requestedReviewerLogins();
  if (requestedReviewers.some(isCopilotReviewRequestLogin)) {
    const latestRequest = latestCopilotReviewRequestEvent();
    writeStepSummary('Copilot 审查请求已跳过', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 当前提交：${headSha}`,
      ...requestActorDiagnosticLines({ requestEvents: latestRequest ? [latestRequest] : [] }),
      `- 最近请求时间：${latestRequest?.created_at || '未检测到'}`,
      `- 最近请求者：${latestRequest?.actor?.login || '未检测到'}`,
      `- 原因：Copilot 已在待审查人列表中。`,
    ]);
    return;
  }

  const requestStartedAt = new Date().toISOString();
  try {
    gh([
      '--method', 'POST',
      `repos/${repo}/pulls/${prNumber}/requested_reviewers`,
      '--input', '-',
    ], JSON.stringify({ reviewers: [copilotReviewerLogin] }));
  } catch (error) {
    if (copilotReviewsForHead().length > 0 || requestedReviewerLogins().some(isCopilotReviewRequestLogin)) {
      writeStepSummary('Copilot 审查请求已跳过', [
        `- 分支流向：${headRef} -> ${baseRef}`,
        `- 当前提交：${headSha}`,
        `- 原因：Copilot 已由并发流程请求或完成审查。`,
      ]);
      return;
    }

    const detail = String(error.stderr || error.message || error).trim().slice(0, 800);
    writeStepSummary('Copilot 审查请求失败', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 当前提交：${headSha}`,
      `- 请求 reviewer：${copilotReviewerLogin}`,
      '',
      detail || 'GitHub API request failed.',
    ]);
    process.exit(1);
  }

  const confirmation = waitForCopilotRequestConfirmation(requestStartedAt);
  if (!confirmation.confirmed) {
    writeStepSummary('Copilot 审查请求未确认', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 当前提交：${headSha}`,
      `- 请求 reviewer：${copilotReviewerLogin}`,
      ...requestActorDiagnosticLines(confirmation),
      `- 请求发起时间：${requestStartedAt}`,
      `- 确认等待：${confirmation.waitedSeconds} 秒`,
      `- 检测到本次 review_requested：${confirmation.requestEvents.length > 0 ? '是' : '否'}`,
      `- Copilot 是否仍在待审查人列表：${confirmation.pendingReviewer ? '是' : '否'}`,
      `- 当前 head Copilot review 数量：${confirmation.reviews.length}`,
      '',
      'GitHub API 调用返回成功，但 timeline 中没有检测到本次 Copilot review request，且当前 head 也没有 Copilot review。',
      'GitHub 会静默忽略 GitHub App installation token 发起的 Copilot review request。',
      '请配置仓库 secret `COPILOT_REVIEW_REQUEST_TOKEN`：拥有 Copilot 订阅的用户的 fine-grained PAT，仓库权限 `Pull requests: Read and write`。',
      '同时确认仓库已启用 Copilot Code Review。',
    ]);
    process.exit(1);
  }

  writeStepSummary('Copilot 审查请求已提交', [
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 当前提交：${headSha}`,
    `- 请求 reviewer：${copilotReviewerLogin}`,
    ...requestActorDiagnosticLines(confirmation),
    `- 请求发起时间：${requestStartedAt}`,
    `- 确认等待：${confirmation.waitedSeconds} 秒`,
    `- 检测到本次 review_requested：${confirmation.requestEvents.length > 0 ? '是' : '否'}`,
    `- 当前 head Copilot review 数量：${confirmation.reviews.length}`,
  ]);
}

function pullRequestReviews() {
  return flattenReviews(ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/reviews`,
    '--paginate',
    '--slurp',
  ], undefined, ghReadToken()) || []);
}

function copilotReviews() {
  return pullRequestReviews().filter((review) => {
    const login = normalizeGitHubLogin(review?.user?.login);
    return login === 'copilot-pull-request-reviewer' && review.state === 'COMMENTED';
  });
}

function copilotReviewsForHead() {
  return copilotReviews().filter((review) => review.commit_id === headSha);
}

function hasCopilotNoBlockingConclusion(reviews) {
  return reviews.some((review) => copilotNoBlockingConclusionPattern.test(String(review?.body || '')));
}

function copilotPassingConclusionSource(reviews) {
  if (hasCopilotNoBlockingConclusion(reviews)) return 'fixed-conclusion';
  if (reviews.some((review) => copilotNoCommentsPattern.test(String(review?.body || '')))) return 'no-new-comments';
  if (reviews.some((review) => {
    const match = String(review?.body || '').match(copilotGeneratedCommentsPattern);
    return Number.parseInt(match?.[1] || '0', 10) > 0;
  })) return 'resolved-review-comments';
  return '';
}

function latestCopilotReviewRequestEvent() {
  return copilotReviewRequestEvents()
    .sort((a, b) => String(a?.created_at || '').localeCompare(String(b?.created_at || '')))
    .pop();
}

let expectedRequestActorCache = null;

function expectedRequestActor() {
  const configured = String(process.env.EXPECTED_REQUEST_ACTOR || process.env.REQUEST_ACTOR || '').trim();
  if (configured) return configured;
  if (expectedRequestActorCache !== null) return expectedRequestActorCache;
  try {
    // 使用用户 PAT 时可解析实际请求账号；App installation token 不支持 /user，忽略失败。
    expectedRequestActorCache = String(ghJson(['--method', 'GET', 'user'])?.login || '').trim();
  } catch {
    expectedRequestActorCache = '';
  }
  return expectedRequestActorCache;
}

function requestActorDiagnosticLines(confirmation) {
  const scopedToConfirmation = Boolean(confirmation);
  const recordedActor = confirmation?.requestEvents?.at(-1)?.actor?.login
    || (scopedToConfirmation ? '' : latestCopilotReviewRequestEvent()?.actor?.login)
    || '';
  const expectedActor = expectedRequestActor();
  const lines = [
    `- GitHub 记录的请求账号：${recordedActor || '未检测到'}`,
  ];
  if (expectedActor) {
    lines.push(`- 本 workflow 预期请求账号：${expectedActor}`);
  }
  return lines;
}

function copilotRequestConfirmation(requestStartedAt) {
  const reviews = copilotReviewsForHead();
  const requestEvents = copilotReviewRequestEventsSince(requestStartedAt);
  const pendingReviewer = requestedReviewerLogins().some(isCopilotReviewRequestLogin);
  return {
    confirmed: reviews.length > 0 || requestEvents.length > 0,
    reviews,
    requestEvents,
    pendingReviewer,
  };
}

function waitForCopilotRequestConfirmation(requestStartedAt) {
  const maxSeconds = parsePositiveInteger(process.env.COPILOT_REVIEW_REQUEST_CONFIRM_SECONDS, 60);
  const pollSeconds = parsePositiveInteger(process.env.COPILOT_REVIEW_REQUEST_POLL_SECONDS, 5);
  const started = Date.now();
  const deadline = started + maxSeconds * 1000;
  let confirmation = copilotRequestConfirmation(requestStartedAt);

  while (!confirmation.confirmed && Date.now() < deadline) {
    const remainingMilliseconds = deadline - Date.now();
    sleepMilliseconds(Math.min(pollSeconds * 1000, remainingMilliseconds));
    confirmation = copilotRequestConfirmation(requestStartedAt);
  }

  return {
    ...confirmation,
    waitedSeconds: Math.round((Date.now() - started) / 1000),
  };
}

function workflowRunUrl() {
  const serverUrl = process.env.GITHUB_SERVER_URL || 'https://github.com';
  const runId = process.env.GITHUB_RUN_ID || '';
  return repo && runId ? `${serverUrl}/${repo}/actions/runs/${runId}` : undefined;
}

function truncateCheckText(value) {
  const text = String(value || '');
  return text.length > 60000 ? `${text.slice(0, 60000)}\n\n... truncated ...` : text;
}

function latestCopilotCheckRun() {
  const token = ghChecksToken();
  if (!token) {
    throw new Error('Missing GH_CHECKS_TOKEN for Checks API updates.');
  }

  const payload = ghJson([
    '--method', 'GET',
    `repos/${repo}/commits/${headSha}/check-runs`,
    '-f', `check_name=${copilotCheckName}`,
    '-f', 'per_page=100',
  ], undefined, token) || {};
  const checkRuns = Array.isArray(payload.check_runs) ? payload.check_runs : [];
  return checkRuns
    .filter((checkRun) => checkRun?.name === copilotCheckName && checkRun?.head_sha === headSha)
    .sort((a, b) => String(a?.started_at || a?.created_at || '').localeCompare(String(b?.started_at || b?.created_at || '')))
    .pop();
}

function upsertCopilotCheckRun({ status, conclusion, title, summaryLines, textLines }) {
  const token = ghChecksToken();
  if (!token) {
    throw new Error('Missing GH_CHECKS_TOKEN for Checks API updates.');
  }

  const now = new Date().toISOString();
  const existing = latestCopilotCheckRun();
  const payload = {
    status,
    details_url: workflowRunUrl(),
    output: {
      title,
      summary: truncateCheckText(summaryLines.join('\n')),
      text: truncateCheckText(textLines.join('\n')),
    },
  };

  if (status === 'completed') {
    payload.conclusion = conclusion;
    payload.completed_at = now;
  } else {
    payload.started_at = existing?.started_at || now;
  }

  const shouldCreate = !existing || (existing.status === 'completed' && status !== 'completed');
  if (shouldCreate) {
    gh([
      '--method', 'POST',
      `repos/${repo}/check-runs`,
      '--input', '-',
    ], JSON.stringify({
      ...payload,
      name: copilotCheckName,
      head_sha: headSha,
      external_id: `pr-${prNumber}-${headSha}`,
    }), token);
    return;
  }

  gh([
    '--method', 'PATCH',
    `repos/${repo}/check-runs/${existing.id}`,
    '--input', '-',
  ], JSON.stringify(payload), token);
}

function normalizeGitHubLogin(login) {
  return String(login || '').toLowerCase().replace(/\[bot\]$/, '');
}

function isCopilotCodeReviewComment(comment) {
  const reviewAuthor = normalizeGitHubLogin(comment?.pullRequestReview?.author?.login);
  if (reviewAuthor) return reviewAuthor === 'copilot-pull-request-reviewer';

  return normalizeGitHubLogin(comment?.author?.login) === 'copilot-pull-request-reviewer';
}

function copilotCommentSeverity(body) {
  const firstLine = String(body || '').split(/\r?\n/, 1)[0];
  if (copilotBlockingSeverityPattern.test(firstLine)) return 'blocking';
  if (copilotSuggestionSeverityPattern.test(firstLine)) return 'suggestion';
  return '';
}

function sanitizeCopilotCommentTitle(value, maxLength = 60) {
  const normalized = String(value || '')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/^\s*(?:#{1,6}|[-+*>]|\d+[.)])\s*/, '')
    .replace(/[`*~]/g, '')
    .replace(/\s+/g, ' ')
    .replace(/[。！？；;，,：:]\s*$/, '')
    .trim();
  if (!normalized) return '';
  const characters = Array.from(normalized);
  return characters.length <= maxLength
    ? normalized
    : `${characters.slice(0, maxLength - 1).join('')}…`;
}

function copilotCommentTitle(body) {
  const text = String(body || '');
  const lines = text.split(/\r?\n/);
  const explicit = lines[1]?.match(copilotTitlePattern)?.[1] || '';
  const explicitTitle = sanitizeCopilotCommentTitle(explicit);
  if (explicitTitle) return explicitTitle;

  for (const line of lines) {
    const candidate = String(line || '').trim();
    if (!candidate || /^```/.test(candidate)) continue;
    if (copilotBlockingSeverityPattern.test(candidate)
      || copilotSuggestionSeverityPattern.test(candidate)
      || copilotTitlePattern.test(candidate)) continue;
    const firstClause = candidate.split(/[。！？；;]/, 1)[0];
    const fallback = sanitizeCopilotCommentTitle(firstClause);
    if (fallback) return fallback;
  }
  return 'Copilot 评论';
}

function copilotThreadFindings(threads) {
  const blocking = [];
  const suggestions = [];
  const unclassified = [];
  for (const thread of threads || []) {
    if (thread?.isResolved || thread?.isOutdated) continue;
    const comments = Array.isArray(thread?.comments) ? thread.comments : thread?.comments?.nodes || [];
    const copilotComments = comments.filter((comment) => isCopilotCodeReviewComment(comment));
    for (const comment of copilotComments) {
      const body = String(comment?.body || '');
      const item = {
        url: comment.url || '',
        title: copilotCommentTitle(body),
      };
      const severity = copilotCommentSeverity(body);
      if (severity === 'blocking') {
        blocking.push(item);
      } else if (severity === 'suggestion') {
        suggestions.push(item);
      } else {
        unclassified.push(item);
      }
    }
  }
  return { blocking, suggestions, unclassified };
}

function reviewThreadComments(threadId) {
  const query = `
    query($id: ID!, $cursor: String) {
      node(id: $id) {
        ... on PullRequestReviewThread {
          comments(first: 100, after: $cursor) {
            pageInfo { hasNextPage endCursor }
            nodes {
              author { login }
              pullRequestReview { author { login } }
              body
              url
            }
          }
        }
      }
    }
  `;
  const comments = [];
  let cursor = null;
  do {
    const args = ['graphql', '-f', `query=${query}`, '-f', `id=${threadId}`];
    if (cursor) args.push('-f', `cursor=${cursor}`);
    const connection = ghJson(args)?.data?.node?.comments;
    comments.push(...(connection?.nodes || []));
    cursor = connection?.pageInfo?.hasNextPage ? connection.pageInfo.endCursor : null;
  } while (cursor);
  return comments;
}

function unresolvedCopilotThreadFindings() {
  const query = `
    query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
      repository(owner: $owner, name: $name) {
        pullRequest(number: $number) {
          reviewThreads(first: 100, after: $cursor) {
            pageInfo { hasNextPage endCursor }
            nodes {
              id
              isResolved
              isOutdated
            }
          }
        }
      }
    }
  `;
  const activeThreads = [];
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
      if (thread.isResolved || thread.isOutdated) continue;
      activeThreads.push({ ...thread, comments: reviewThreadComments(thread.id) });
    }
    cursor = threads?.pageInfo?.hasNextPage ? threads.pageInfo.endCursor : null;
  } while (cursor);

  return copilotThreadFindings(activeThreads);
}

function checkCopilotReviewForHead() {
  return {
    reviews: copilotReviewsForHead(),
  };
}

function copilotReviewDiagnostics(reviewsForHead) {
  const requestEvents = copilotReviewRequestEvents();
  const latestRequest = requestEvents
    .sort((a, b) => String(a?.created_at || '').localeCompare(String(b?.created_at || '')))
    .at(-1);
  const pendingReviewer = requestedReviewerLogins().some(isCopilotReviewRequestLogin);
  const allCopilotReviews = copilotReviews();
  const oldHeadReviews = allCopilotReviews.filter((review) => review.commit_id && review.commit_id !== headSha);
  const latestOldHeadReview = oldHeadReviews
    .sort((a, b) => String(a?.submitted_at || '').localeCompare(String(b?.submitted_at || '')))
    .at(-1);

  return {
    requestEvents,
    latestRequest,
    pendingReviewer,
    reviewsForHead,
    oldHeadReviews,
    latestOldHeadReview,
  };
}

function yesNo(value) {
  return value ? '是' : '否';
}

function copilotDiagnosticLines(diagnostics) {
  return [
    `- 请求账号：${diagnostics.latestRequest?.actor?.login || '未检测到'}`,
    `- 请求时间：${diagnostics.latestRequest?.created_at || '未检测到'}`,
    `- 已检测到 review_requested：${yesNo(diagnostics.requestEvents.length > 0)}`,
    `- Copilot 是否仍在待审查人列表：${yesNo(diagnostics.pendingReviewer)}`,
    `- 当前 head Copilot review：${diagnostics.reviewsForHead.length}`,
    `- 旧 head Copilot review：${diagnostics.oldHeadReviews.length}`,
    `- 最近旧 head review：${diagnostics.latestOldHeadReview ? `${diagnostics.latestOldHeadReview.commit_id?.slice(0, 12) || 'unknown'} @ ${diagnostics.latestOldHeadReview.submitted_at}` : '无'}`,
    '- Gate 模式：事件驱动（等待 pull_request_review 重新触发）',
    '- 本次检查：即时检查，无长轮询',
  ];
}

function evaluateCopilotGate({ reviews, findings, requestFailed }) {
  const blocking = findings?.blocking || [];
  const suggestions = findings?.suggestions || [];
  const unclassified = findings?.unclassified || [];
  const result = {
    checkStatus: 'completed',
    checkConclusion: 'success',
    checkTitle: 'Copilot 审查门禁已通过',
    failureKind: '',
    detail: '当前提交已完成 Copilot 代码审查，且未发现未解决的重大问题。',
    passingSignal: '',
    passingConclusionSource: '',
    blocking,
    suggestions,
    unclassified,
  };

  if (reviews.length === 0) {
    if (requestFailed) {
      result.checkConclusion = 'failure';
      result.checkTitle = 'Copilot 审查请求失败';
      result.failureKind = 'request-failed';
      result.detail = '当前提交尚未检测到 Copilot 代码审查，且 Request Copilot Review job 未成功。请检查该 job 日志、GitHub App 安装范围和 `Pull requests: Read and write` 权限。';
    } else {
      result.checkStatus = 'in_progress';
      result.checkConclusion = undefined;
      result.checkTitle = '等待 Copilot 代码审查';
      result.detail = '当前提交尚未检测到 Copilot 代码审查；自定义 Checks API 门禁会保持 in_progress，等 Copilot 提交 review 后由 pull_request_review 事件重新触发并完成。';
    }
    return result;
  }

  if (blocking.length > 0) {
    result.checkConclusion = 'failure';
    result.checkTitle = 'Copilot 审查门禁未通过';
    result.failureKind = 'blocking-comments';
    result.detail = '检测到 Copilot 留下的未解决重大问题。';
    return result;
  }

  if (unclassified.length > 0) {
    result.checkConclusion = 'failure';
    result.checkTitle = 'Copilot 审查协议不完整';
    result.failureKind = 'comment-protocol';
    result.detail = '检测到未解决 Copilot 评论缺少可解析严重程度标记。';
    return result;
  }

  if (suggestions.length > 0) {
    result.passingSignal = 'suggestion-only-comments';
    result.detail = '当前提交已完成 Copilot 代码审查，所有当前未解决 Copilot 评论均标记为建议，且未发现未解决重大问题。';
    return result;
  }

  result.passingConclusionSource = copilotPassingConclusionSource(reviews);
  if (!result.passingConclusionSource) {
    result.checkConclusion = 'failure';
    result.checkTitle = 'Copilot 审查通过信号缺失';
    result.failureKind = 'passing-conclusion';
    result.detail = '当前 head 没有未解决 Copilot 评论，但 review 正文未包含固定无阻断结论或官方无评论模板。';
    return result;
  }

  result.passingSignal = 'no-current-comments-with-known-conclusion';
  if (result.passingConclusionSource === 'no-new-comments') {
    result.detail = '当前提交已完成 Copilot 代码审查，review 正文为官方无新增评论模板，且未发现未解决的重大问题。';
  } else if (result.passingConclusionSource === 'resolved-review-comments') {
    result.detail = '当前提交的 Copilot review 曾生成评论，相关 conversation 已全部解决。';
  } else {
    result.detail = '当前提交已完成 Copilot 代码审查，review 正文包含固定无阻断结论句，且未发现未解决的重大问题。';
  }
  return result;
}

function copilotFailurePresentations({ decision, coreHandlers, contributorHandlers }) {
  const presentations = [];
  if (decision.blocking.length > 0) {
    const titles = decision.blocking.map((item) => `- ${item.title || 'Copilot 评论'}`).join('\n');
    presentations.push({
      source: 'copilot-review:blocking-comments',
      title: '🚫 Copilot 阻断评论',
      handlers: contributorHandlers.length ? contributorHandlers : coreHandlers,
      details: [
        '请处理或回复以下评论，并将对应 Conversation 标记为 **Resolved**：',
        titles,
      ],
    });
  }
  if (decision.unclassified.length > 0) {
    presentations.push({
      source: 'copilot-review:comment-protocol',
      title: '⚠️ Copilot Review 评论格式异常',
      handlers: coreHandlers,
      details: [
        'Copilot 评论缺少可识别的严重程度标记，门禁暂无法完成判定。',
        '请检查评论内容和审查协议，必要时重新触发 Copilot 审查。',
      ],
    });
  }
  if (presentations.length > 0) return presentations;

  if (decision.failureKind === 'request-failed') {
    return [{
      source: 'copilot-review:request-failed',
      title: '⚠️ Copilot Review 请求失败',
      handlers: coreHandlers,
      details: [
        '当前提交未能完成 Copilot Review 请求。',
        '请检查 Request Copilot Review job，修正后重新触发审查。',
      ],
    }];
  }
  if (decision.failureKind !== 'passing-conclusion') return [];
  return [{
    source: 'copilot-review:passing-conclusion',
    title: '⚠️ Copilot Review 状态异常',
    handlers: coreHandlers,
    details: [
      '门禁未识别到 Copilot 的有效通过结论。',
      '请检查 Copilot Review 结果及门禁识别规则；如 Review 已完成但仍未识别，请重新触发 Copilot 审查。',
    ],
  }];
}

function copilotFailurePresentation(context) {
  return copilotFailurePresentations(context)[0] || {
    title: '',
    handlers: [],
    details: [],
  };
}

function copilotReviewGate() {
  ensurePrContext();
  const checkResult = checkCopilotReviewForHead();
  const reviews = checkResult.reviews;
  const diagnostics = copilotReviewDiagnostics(reviews);
  const requestResult = process.env.REQUEST_COPILOT_RESULT || '';
  const requestFailed = ['pull_request_target', 'workflow_dispatch'].includes(eventName)
    && (requestResult === 'failure' || requestResult === 'cancelled');
  const legacyMarker = '<!-- workflow:copilot-review-gate -->';
  const findings = reviews.length > 0
    ? unresolvedCopilotThreadFindings()
    : { blocking: [], suggestions: [], unclassified: [] };
  const decision = evaluateCopilotGate({ reviews, findings, requestFailed });
  const {
    checkStatus,
    checkConclusion,
    checkTitle,
    detail,
    passingSignal,
    passingConclusionSource,
    blocking,
    suggestions,
    unclassified,
  } = decision;

  const blockingList = blocking.length
    ? blocking.map((item) => `- ${item.url ? `[${item.title || 'Copilot 评论'}](${item.url})` : item.title}`).join('\n')
    : '- 无';
  const suggestionList = suggestions.length
    ? suggestions.map((item) => `- ${item.url ? `[${item.title || 'Copilot 评论'}](${item.url})` : item.title}`).join('\n')
    : '- 无';
  const unclassifiedList = unclassified.length
    ? unclassified.map((item) => `- ${item.url ? `[${item.title || 'Copilot 评论'}](${item.url})` : item.title}`).join('\n')
    : '- 无';
  const diagnosticList = copilotDiagnosticLines(diagnostics).join('\n');
  const requestResultDisplay = eventName === 'pull_request_target'
    ? (requestResult || '未提供')
    : '不适用于本事件';

  const summaryTitle = checkStatus === 'in_progress'
    ? 'Copilot 审查等待中'
    : checkConclusion === 'success'
      ? 'Copilot 审查门禁已通过'
      : 'Copilot 审查门禁未通过';
  const summaryLines = [
    `- Checks API 名称：${copilotCheckName}`,
    `- Checks API 状态：${checkStatus}${checkConclusion ? ` / ${checkConclusion}` : ''}`,
    `- Request Copilot Review job：${requestResultDisplay}`,
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 当前提交：${headSha}`,
    `- Copilot 审查数量：${reviews.length}`,
    `- 通过信号：${passingSignal || '未检测到'}`,
    `- 通过型结论：${passingConclusionSource || '未检测到'}`,
    `- 未解决重大问题：${blocking.length}`,
    `- 未解决建议评论：${suggestions.length}`,
    `- 未识别严重程度评论：${unclassified.length}`,
    '',
    detail,
    '',
    '### 未解决重大问题',
    blockingList,
    '',
    '### 未解决建议评论',
    suggestionList,
    '',
    '### 未识别严重程度评论',
    unclassifiedList,
    '',
    '### Copilot 请求诊断',
    diagnosticList,
  ];

  upsertCopilotCheckRun({
    status: checkStatus,
    conclusion: checkConclusion,
    title: checkTitle,
    summaryLines: [
      `${headRef} -> ${baseRef}`,
      `Head: ${headSha}`,
      detail,
    ],
    textLines: summaryLines,
  });
  writeStepSummary(summaryTitle, summaryLines);

  cleanupLegacyGateComment(legacyMarker);
  const coreHandlers = trustedDeveloperLogins();
  const presentations = copilotFailurePresentations({
    decision,
    coreHandlers,
    contributorHandlers: realContributorLogins(),
  });
  updateBlockingFailures({
    sourcePrefix: 'copilot-review',
    failures: checkStatus === 'completed' && checkConclusion === 'failure'
      ? presentations
      : [],
  });
}

function runCommand(command) {
  if (command === 'auto-approve') {
    autoApprove();
  } else if (command === 'main-authorization') {
    mainAuthorizationGate();
  } else if (command === 'request-copilot-review') {
    requestCopilotReview();
  } else if (command === 'copilot-review') {
    copilotReviewGate();
  } else {
    throw new Error(`Unknown command: ${command}`);
  }
}

if (require.main === module) {
  runCommand(process.argv[2]);
} else {
  module.exports = {
    blockingFailureBody,
    copilotCommentSeverity,
    copilotCommentTitle,
    copilotFailurePresentation,
    copilotFailurePresentations,
    copilotThreadFindings,
    coreReviewersToRequest,
    decodeBlockingState,
    encodeBlockingState,
    evaluateCopilotGate,
    mainAuthorizationFailurePresentation,
    nextBlockingFailureState,
    nextBlockingFailuresState,
    parseGhExecutableArgs,
    requestCoreDeveloperReviews,
    pullRequestCommitContributorSummary,
    runCommand,
    sanitizeCopilotCommentTitle,
  };
}
