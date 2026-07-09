const { execFileSync } = require('node:child_process');
const fs = require('node:fs');

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
    ], undefined, ghReadToken()) || {};
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
    .filter((review) => review.state === 'APPROVED' && review.commit_id === headSha)
    .map((review) => review.user.login);
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

  const approvalBody = [
    '## 自动审批说明',
    '',
    '本审批由 PR Governance 自动提交，用于满足 main ruleset 的 required approval 要求。',
    '',
    '### 判定依据',
    `- 有效提交者：\`${effectiveAuthor}\``,
    `- 分支流向：\`${headRef} -> ${baseRef}\``,
    `- 当前提交：\`${headSha.slice(0, 12) || 'unknown'}\``,
    '- 授权依据：有效提交者属于 `TRUSTED_DEVELOPERS`。',
    '',
    '### 注意',
    '- 这不是人工代码审查结论。',
    '- 合并仍需通过 `Main Authorization Gate`、`Copilot Code Review Gate` 和 CodeQL。',
  ].join('\n');

  gh([
    '--method', 'POST',
    `repos/${repo}/pulls/${prNumber}/reviews`,
    '--input', '-',
  ], JSON.stringify({ event: 'APPROVE', body: approvalBody }));
  writeStepSummary('自动审批已完成', [
    `- 分支流向：${headRef} -> ${baseRef}`,
    `- PR 提交人：${effectiveAuthor}`,
    `- 当前提交：${headSha.slice(0, 12) || 'unknown'}`,
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

function requestCopilotReview() {
  ensurePrContext();
  if (!process.env.GH_TOKEN) {
    writeStepSummary('Copilot 审查请求失败', [
      `- 分支流向：${headRef} -> ${baseRef}`,
      `- 当前提交：${headSha}`,
      '- 原因：未提供请求 Copilot 审查的 GitHub App installation token。',
      '- 请确认 `Create Copilot review app token` 步骤成功，且 GitHub App 已安装到本仓库并具有 `Pull requests: Read and write`。',
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
      '请确认 GitHub App 已安装到本仓库、拥有 `Pull requests: Read and write`，且仓库已启用 Copilot Code Review。',
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

function latestCopilotReviewRequestEvent() {
  return copilotReviewRequestEvents()
    .sort((a, b) => String(a?.created_at || '').localeCompare(String(b?.created_at || '')))
    .pop();
}

function expectedRequestActor() {
  return String(process.env.EXPECTED_REQUEST_ACTOR || process.env.REQUEST_ACTOR || '').trim();
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
    '- Gate 模式：事件驱动（等待 pull_request_review / pull_request_review_comment 重新触发）',
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
  const marker = '<!-- workflow:copilot-review-gate -->';
  let checkStatus = 'completed';
  let checkConclusion = 'success';
  let commentPolicy = 'upsert';
  let title = '## Copilot 审查门禁已通过';
  let checkTitle = 'Copilot 审查门禁已通过';
  let detail = '当前提交已完成 Copilot 代码审查，且未发现未解决的重大问题。';
  let blocking = [];
  let unclassified = [];

  if (reviews.length === 0) {
    commentPolicy = 'upsert';
    if (requestFailed) {
      checkStatus = 'completed';
      checkConclusion = 'failure';
      title = '## Copilot 审查请求失败';
      checkTitle = 'Copilot 审查请求失败';
      detail = '当前提交尚未检测到 Copilot 代码审查，且 Request Copilot Review job 未成功。请检查该 job 日志、GitHub App 安装范围和 `Pull requests: Read and write` 权限。';
    } else {
      checkStatus = 'in_progress';
      checkConclusion = undefined;
      title = '## Copilot 审查等待中';
      checkTitle = '等待 Copilot 代码审查';
      detail = '当前提交尚未检测到 Copilot 代码审查；自定义 Checks API 门禁会保持 in_progress，等 Copilot 提交 review 后由 pull_request_review 事件重新触发并完成。';
    }
  } else {
    const findings = unresolvedCopilotThreadFindings();
    blocking = findings.blocking;
    unclassified = findings.unclassified;
    if (blocking.length > 0) {
      checkStatus = 'completed';
      checkConclusion = 'failure';
      commentPolicy = 'replace';
      title = '## Copilot 审查门禁未通过';
      checkTitle = 'Copilot 审查门禁未通过';
      detail = '检测到 Copilot 留下的未解决重大问题。';
    }
  }

  const blockingList = blocking.length
    ? blocking.map((item) => `- ${item.url ? `[${item.body || 'Copilot 评论'}](${item.url})` : item.body}`).join('\n')
    : '- 无';
  const unclassifiedList = unclassified.length
    ? unclassified.map((item) => `- ${item.url ? `[${item.body || 'Copilot 评论'}](${item.url})` : item.body}`).join('\n')
    : '- 无';
  const diagnosticList = copilotDiagnosticLines(diagnostics).join('\n');

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
    '### Copilot 请求诊断',
    diagnosticList,
    '',
    unclassified.length
      ? '> 未识别严重程度评论不会阻断合并，但说明 Copilot 未完全遵守本仓库中文审查指令。'
      : '> Copilot 评论均符合本仓库严重程度标记约定。',
    '',
    `> Required check 由自定义 Checks API check run \`${copilotCheckName}\` 承担。`,
  ].join('\n');

  const summaryTitle = checkStatus === 'in_progress'
    ? 'Copilot 审查等待中'
    : checkConclusion === 'success'
      ? 'Copilot 审查门禁已通过'
      : 'Copilot 审查门禁未通过';
  const summaryLines = [
    `- Checks API 名称：${copilotCheckName}`,
    `- Checks API 状态：${checkStatus}${checkConclusion ? ` / ${checkConclusion}` : ''}`,
    `- Request Copilot Review job：${requestResult || '未提供'}`,
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

  if (checkStatus === 'completed' && checkConclusion === 'success') {
    deleteComment(marker);
  } else if (commentPolicy === 'replace') {
    deleteComment(marker);
    createComment(body);
  } else {
    upsertComment(marker, body);
  }
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
} else {
  throw new Error(`Unknown command: ${command}`);
}
