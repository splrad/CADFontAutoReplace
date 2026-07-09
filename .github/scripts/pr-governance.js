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
const prAuthor = process.env.PR_AUTHOR || 'unknown';
const headRef = process.env.PR_HEAD_REF || '';
const baseRef = process.env.PR_BASE_REF || '';
const headSha = process.env.PR_HEAD_SHA || '';
const eventName = process.env.GITHUB_EVENT_NAME || '';
const copilotReviewerLogin = 'copilot-pull-request-reviewer[bot]';
const copilotCheckName = process.env.COPILOT_REVIEW_CHECK_NAME || 'Copilot Code Review Gate';
const copilotNoBlockingConclusion = '结论：未发现需要阻断合并的问题。';
const copilotNoCommentsPattern = /Copilot reviewed \d+ out of \d+ changed files in this pull request and generated no (?:new )?comments\./i;
const autoApprovalMarker = '<!-- workflow:auto-approval -->';
let prDetailsCache = null;
let realContributorsCache = null;

function gh(args, input, token) {
  const executable = process.env.GH_EXECUTABLE || 'gh';
  const prefixArgs = process.env.GH_EXECUTABLE_ARGS
    ? JSON.parse(process.env.GH_EXECUTABLE_ARGS)
    : [];
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

function ghActionsToken() {
  return process.env.GH_ACTIONS_TOKEN || process.env.GH_TOKEN || '';
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

function pullRequestCommitAuthorLogins() {
  const commits = flattenReviews(ghJson([
    '--method', 'GET',
    `repos/${repo}/pulls/${prNumber}/commits`,
    '-f', 'per_page=100',
    '--paginate',
    '--slurp',
  ], undefined, ghReadToken()) || []);

  return uniqueLogins(commits.map((commit) => commit?.author?.login || ''));
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

function failureMentionText() {
  return notificationMentionText([...trustedDeveloperLogins(), ...realContributorLogins()]);
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

function deleteComment(marker) {
  const token = commentToken();
  deleteMarkerComments({ gh, ghJson, repo, prNumber, marker, token });
}

const blockingFailuresMarker = '<!-- workflow:pr-blocking-failures -->';
const blockingFailuresStatePattern = /<!--\s*workflow:pr-blocking-failures-state:([A-Za-z0-9+/=_-]+)\s*-->/;
const blockingSourceOrder = ['main-authorization', 'copilot-review'];

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

function blockingFailureBody(state) {
  const failures = orderedBlockingFailures(state.failures);
  const sections = [];
  for (const failure of failures) {
    sections.push(`#### ${failure.title}`);
    for (const detail of failure.details) {
      sections.push(`- ${detail}`);
    }
    sections.push('');
  }

  return [
    blockingFailuresMarker,
    `<!-- workflow:pr-blocking-failures-state:${encodeBlockingState(state)} -->`,
    `## PR 暂不能合并（${failures.length} 项阻断）`,
    '',
    failureMentionText(),
    '',
    '### 当前上下文',
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 当前提交：${headSha}`,
    `- 真实贡献者：${realContributorDisplay()}`,
    `- 阻断项：${failures.map((failure) => failure.title).join('；')}`,
    '',
    '### 需要处理',
    ...sections,
    '> 本通知由 GitHub Actions 自动发布；相关问题全部恢复后会自动删除。',
  ].join('\n').trimEnd();
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

function updateBlockingFailure({ source, title, failed, details }) {
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
  const state = headChanged || !existingState
    ? { head: headSha, failures: [] }
    : { head: headSha, failures: existingState.failures };

  state.failures = state.failures.filter((failure) => failure.source !== source);
  if (failed) {
    state.failures.push({
      source,
      title,
      details: Array.isArray(details)
        ? details.map((detail) => String(detail || '').trim()).filter(Boolean)
        : [],
    });
  }

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
}

function autoApprove() {
  ensurePrContext();
  const contributors = realContributorLogins();
  const contributorDisplay = realContributorDisplay();
  const untrusted = untrustedContributorLogins();
  const reviewer = process.env.AUTO_APPROVE_REVIEWER || '';
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

function mainAuthorizationGate() {
  ensurePrContext();
  let status = 'passed';
  const contributors = realContributorLogins();
  const contributorDisplay = realContributorDisplay();
  const untrusted = untrustedContributorLogins();
  const trustedApprovers = latestTrustedApproversForHead();
  const trustedManualApprovers = latestTrustedApproversForHead({ includeAutomation: false });
  let detail = '';
  let failed = false;

  if (contributors.length === 0) {
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

  cleanupLegacyGateComment('<!-- workflow:main-authorization-gate -->');
  writeStepSummary(failed ? '主分支授权门禁未通过' : '主分支授权门禁已通过', [
    `- 状态：${status}`,
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- 真实贡献者：${contributorDisplay}`,
    `- 非核心贡献者：${untrusted.length ? untrusted.join(', ') : '无'}`,
    `- 核心开发者 approval：${trustedApprovers.length ? trustedApprovers.join(', ') : '无'}`,
    `- 核心开发者手动 approval：${trustedManualApprovers.length ? trustedManualApprovers.join(', ') : '无'}`,
    '',
    detail,
  ]);
  updateBlockingFailure({
    source: 'main-authorization',
    title: '主分支授权门禁未通过',
    failed,
    details: [
      `状态：${status}`,
      `真实贡献者：${contributorDisplay}`,
      `非核心贡献者：${untrusted.length ? untrusted.join(', ') : '无'}`,
      `问题：${detail}`,
      '处理：请核心开发者审核并提交 approval；如果全部贡献者都是核心开发者，也可以配置 CORE_AUTO_APPROVAL_TOKEN 自动审批。',
    ],
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
  return reviews.some((review) => String(review?.body || '').includes(copilotNoBlockingConclusion));
}

function copilotPassingConclusionSource(reviews) {
  if (hasCopilotNoBlockingConclusion(reviews)) return 'fixed-conclusion';
  if (reviews.some((review) => copilotNoCommentsPattern.test(String(review?.body || '')))) return 'no-new-comments';
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

function copilotReviewGate() {
  ensurePrContext();
  const checkResult = checkCopilotReviewForHead();
  const reviews = checkResult.reviews;
  const diagnostics = copilotReviewDiagnostics(reviews);
  const requestResult = process.env.REQUEST_COPILOT_RESULT || '';
  const requestFailed = eventName === 'pull_request_target'
    && (requestResult === 'failure' || requestResult === 'cancelled');
  const legacyMarker = '<!-- workflow:copilot-review-gate -->';
  let checkStatus = 'completed';
  let checkConclusion = 'success';
  let checkTitle = 'Copilot 审查门禁已通过';
  let detail = '当前提交已完成 Copilot 代码审查，且未发现未解决的重大问题。';
  let handling = '处理：请检查 Request Copilot Review job 日志，修复后重新触发工作流。';
  let blocking = [];
  let unclassified = [];
  let passingConclusionSource = '';

  if (reviews.length === 0) {
    if (requestFailed) {
      checkStatus = 'completed';
      checkConclusion = 'failure';
      checkTitle = 'Copilot 审查请求失败';
      detail = '当前提交尚未检测到 Copilot 代码审查，且 Request Copilot Review job 未成功。请检查该 job 日志、GitHub App 安装范围和 `Pull requests: Read and write` 权限。';
    } else {
      checkStatus = 'in_progress';
      checkConclusion = undefined;
      checkTitle = '等待 Copilot 代码审查';
      detail = '当前提交尚未检测到 Copilot 代码审查；自定义 Checks API 门禁会保持 in_progress，等 Copilot 提交 review 后由 pull_request_review 事件重新触发并完成。';
    }
  } else {
    passingConclusionSource = copilotPassingConclusionSource(reviews);
    const findings = unresolvedCopilotThreadFindings();
    blocking = findings.blocking;
    unclassified = findings.unclassified;
    if (blocking.length > 0) {
      checkStatus = 'completed';
      checkConclusion = 'failure';
      checkTitle = 'Copilot 审查门禁未通过';
      detail = '检测到 Copilot 留下的未解决重大问题。';
      handling = '处理：请修复或回复并 resolve 上述 Copilot 阻断评论。';
    } else if (!passingConclusionSource) {
      checkStatus = 'completed';
      checkConclusion = 'failure';
      checkTitle = 'Copilot 审查通过信号缺失';
      detail = '未识别到 Copilot 结论或 Copilot 无问题评论。';
      handling = '处理：请重新触发 Copilot 审查。';
    } else {
      detail = passingConclusionSource === 'no-new-comments'
        ? '当前提交已完成 Copilot 代码审查，review 正文为官方无新增评论模板，且未发现未解决的重大问题。'
        : '当前提交已完成 Copilot 代码审查，review 正文包含固定无阻断结论句，且未发现未解决的重大问题。';
      handling = '';
    }
  }

  const blockingList = blocking.length
    ? blocking.map((item) => `- ${item.url ? `[${item.body || 'Copilot 评论'}](${item.url})` : item.body}`).join('\n')
    : '- 无';
  const unclassifiedList = unclassified.length
    ? unclassified.map((item) => `- ${item.url ? `[${item.body || 'Copilot 评论'}](${item.url})` : item.body}`).join('\n')
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
    `- 通过型结论：${passingConclusionSource || '未检测到'}`,
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
  updateBlockingFailure({
    source: 'copilot-review',
    title: checkTitle,
    failed: checkStatus === 'completed' && checkConclusion === 'failure',
    details: [
      `Request Copilot Review job：${requestResultDisplay}`,
      `Copilot 审查数量：${reviews.length}`,
      `通过型结论：${passingConclusionSource || '未检测到'}`,
      `问题：${detail}`,
      ...blocking.map((item) => item.url
        ? `未解决重大问题：[${item.body || 'Copilot 评论'}](${item.url})`
        : `未解决重大问题：${item.body || 'Copilot 评论'}`),
      handling,
    ],
  });
}

function isCopilotActor(login) {
  const normalized = normalizeGitHubLogin(login);
  return normalized === 'copilot' || normalized === 'copilot-pull-request-reviewer';
}

function workflowRunPullRequestNumber(run) {
  const pullRequests = Array.isArray(run?.pull_requests) ? run.pull_requests : [];
  return Number(pullRequests[0]?.number || prNumber || '0');
}

function workflowRunMatchesCurrentPr(run) {
  const runPrNumber = workflowRunPullRequestNumber(run);
  const runHeadSha = String(run?.head_sha || '');
  return runPrNumber === prNumber && (!headSha || runHeadSha === headSha);
}

function listWorkflowRunJobs(runId) {
  const payload = ghJson([
    '--method', 'GET',
    `repos/${repo}/actions/runs/${runId}/jobs`,
    '-f', 'per_page=100',
  ], undefined, ghActionsToken()) || {};
  return Array.isArray(payload.jobs) ? payload.jobs : [];
}

function recoverCopilotReviewRun() {
  const runId = Number(process.env.RECOVERY_RUN_ID || process.env.GITHUB_EVENT_WORKFLOW_RUN_ID || '0');
  if (!repo || !owner || !repoName || !runId || !prNumber) {
    writeStepSummary('Copilot 审查恢复已跳过', [
      `- run id：${runId || '未提供'}`,
      `- PR：${prNumber || '未提供'}`,
      '- 原因：缺少仓库、run 或 PR 上下文。',
    ]);
    return;
  }

  if (!ghActionsToken()) {
    writeStepSummary('Copilot 审查恢复失败', [
      `- run id：${runId}`,
      '- 原因：未提供 `Actions: write` token。',
    ]);
    process.exit(1);
  }

  const run = ghJson([
    '--method', 'GET',
    `repos/${repo}/actions/runs/${runId}`,
  ], undefined, ghActionsToken()) || {};
  const actor = run?.actor?.login || '';
  const conclusion = String(run?.conclusion || '');
  const runEvent = String(run?.event || '');

  if (run?.name !== 'PR Governance'
    || runEvent !== 'pull_request_review'
    || !isCopilotActor(actor)
    || !workflowRunMatchesCurrentPr(run)) {
    writeStepSummary('Copilot 审查恢复已跳过', [
      `- run id：${runId}`,
      `- workflow：${run?.name || 'unknown'}`,
      `- event：${runEvent || 'unknown'}`,
      `- actor：${actor || 'unknown'}`,
      `- PR：${workflowRunPullRequestNumber(run) || 'unknown'}`,
      `- head：${run?.head_sha || 'unknown'}`,
      '- 原因：不是当前 PR/head 上由 Copilot review 触发的 PR Governance run。',
    ]);
    return;
  }

  const reviews = copilotReviewsForHead();
  if (reviews.length === 0) {
    writeStepSummary('Copilot 审查恢复已跳过', [
      `- run id：${runId}`,
      `- 当前提交：${headSha}`,
      '- 原因：当前 head 尚未检测到 Copilot review。',
    ]);
    return;
  }

  if (conclusion === 'action_required') {
    gh([
      '--method', 'POST',
      `repos/${repo}/actions/runs/${runId}/approve`,
    ], undefined, ghActionsToken());
    writeStepSummary('Copilot 审查恢复已批准', [
      `- run id：${runId}`,
      `- 当前提交：${headSha}`,
      `- Copilot review 数量：${reviews.length}`,
      '- 操作：批准该 Copilot review 触发的 PR Governance workflow run。',
    ]);
    return;
  }

  if (conclusion !== 'failure') {
    writeStepSummary('Copilot 审查恢复已跳过', [
      `- run id：${runId}`,
      `- conclusion：${conclusion || 'unknown'}`,
      '- 原因：该 run 不需要恢复。',
    ]);
    return;
  }

  const jobs = listWorkflowRunJobs(runId);
  const failedJobs = jobs.filter((job) => ['failure', 'cancelled', 'timed_out'].includes(String(job?.conclusion || '')));
  const updateJob = failedJobs.find((job) => job?.name === 'Update Copilot Review Check');
  const onlyUpdateJobFailed = failedJobs.length === 1 && updateJob;
  const runAttempt = Number(run?.run_attempt || 0);
  if (!onlyUpdateJobFailed || runAttempt > 1) {
    writeStepSummary('Copilot 审查恢复已跳过', [
      `- run id：${runId}`,
      `- run attempt：${runAttempt || 'unknown'}`,
      `- 失败 job：${failedJobs.map((job) => job?.name || 'unknown').join(', ') || '无'}`,
      '- 原因：不是仅 `Update Copilot Review Check` 失败，或已经重试过。',
    ]);
    return;
  }

  gh([
    '--method', 'POST',
    `repos/${repo}/actions/jobs/${updateJob.id}/rerun`,
  ], undefined, ghActionsToken());
  writeStepSummary('Copilot 审查恢复已重跑', [
    `- run id：${runId}`,
    `- job id：${updateJob.id}`,
    '- 操作：仅重跑 `Update Copilot Review Check` job。',
  ]);
}

const command = process.argv[2];
if (command === 'auto-approve') {
  autoApprove();
} else if (command === 'main-authorization') {
  mainAuthorizationGate();
} else if (command === 'request-copilot-review') {
  requestCopilotReview();
} else if (command === 'copilot-review') {
  copilotReviewGate();
} else if (command === 'recover-copilot-review-run') {
  recoverCopilotReviewRun();
} else {
  throw new Error(`Unknown command: ${command}`);
}
