const assert = require('node:assert/strict');

const {
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
  sanitizeCopilotCommentTitle,
} = require('./pr-governance');

function review(body) {
  return { body };
}

function finding(title = '测试问题') {
  return { title, url: 'https://example.test/comment' };
}

function decision({
  reviews = ['Copilot reviewed 6 out of 6 changed files in this pull request and generated 3 comments.'],
  blocking = [],
  suggestions = [],
  unclassified = [],
  requestFailed = false,
} = {}) {
  return evaluateCopilotGate({
    reviews: reviews.map(review),
    findings: { blocking, suggestions, unclassified },
    requestFailed,
  });
}

function withGhExecutableArgs(value, callback) {
  const previous = process.env.GH_EXECUTABLE_ARGS;
  if (value === undefined) {
    delete process.env.GH_EXECUTABLE_ARGS;
  } else {
    process.env.GH_EXECUTABLE_ARGS = value;
  }
  try {
    callback();
  } finally {
    if (previous === undefined) {
      delete process.env.GH_EXECUTABLE_ARGS;
    } else {
      process.env.GH_EXECUTABLE_ARGS = previous;
    }
  }
}

assert.equal(copilotCommentSeverity('严重程度：阻断\n必须修复。'), 'blocking');
assert.equal(copilotCommentSeverity('Severity: blocking\nMust fix.'), 'blocking');
assert.equal(copilotCommentSeverity('严重程度：建议\n可选改进。'), 'suggestion');
assert.equal(copilotCommentSeverity('Severity: suggestion\nOptional improvement.'), 'suggestion');
assert.equal(copilotCommentSeverity('缺少严重程度。'), '');
assert.equal(
  copilotCommentSeverity('严重程度：建议\n标题：示例\n\n严重程度：阻断 仅用于说明协议'),
  'suggestion',
);
assert.equal(copilotCommentSeverity('前言\n严重程度：阻断\n必须修复。'), '');

assert.equal(
  copilotCommentTitle('严重程度：阻断\n标题：GH_EXECUTABLE_ARGS 解析缺少异常处理\n\n正文。'),
  'GH_EXECUTABLE_ARGS 解析缺少异常处理',
);
assert.equal(
  copilotCommentTitle('Severity: suggestion\nTitle: [Avoid unsafe parsing](https://example.test)\n\nBody.'),
  'Avoid unsafe parsing',
);
assert.equal(
  copilotCommentTitle('严重程度：建议\n\n`GH_EXECUTABLE_ARGS` 直接 `JSON.parse(...)` 没有 try/catch；一旦环境变量值不是合法 JSON，脚本会中断。'),
  'GH_EXECUTABLE_ARGS 直接 JSON.parse(...) 没有 try/catch',
);
assert.equal(copilotCommentTitle('严重程度：阻断'), 'Copilot 评论');
assert.equal(
  copilotCommentTitle('严重程度：建议\n正文标题。\n标题：错误位置'),
  '正文标题',
);
const truncatedTitle = sanitizeCopilotCommentTitle('A'.repeat(80));
assert.equal(Array.from(truncatedTitle).length, 60);
assert.match(truncatedTitle, /…$/);

assert.equal(
  decision({ suggestions: [finding('建议改进')] }).checkConclusion,
  'success',
);
assert.equal(
  decision({ suggestions: [finding('建议改进')] }).passingSignal,
  'suggestion-only-comments',
);
assert.equal(decision({ blocking: [finding('阻断问题')] }).failureKind, 'blocking-comments');
assert.equal(decision({ unclassified: [finding('格式异常')] }).failureKind, 'comment-protocol');

const markdownConclusion = decision({
  reviews: ['## 结论\n\n未发现需要阻断合并的问题。'],
});
assert.equal(markdownConclusion.checkConclusion, 'success');
assert.equal(markdownConclusion.passingSignal, 'no-current-comments-with-known-conclusion');
assert.equal(markdownConclusion.passingConclusionSource, 'fixed-conclusion');

const noNewComments = decision({
  reviews: ['Copilot reviewed 6 out of 6 changed files in this pull request and generated no new comments.'],
});
assert.equal(noNewComments.checkConclusion, 'success');
assert.equal(noNewComments.passingConclusionSource, 'no-new-comments');

const resolvedReviewComments = decision();
assert.equal(resolvedReviewComments.checkConclusion, 'success');
assert.equal(resolvedReviewComments.passingConclusionSource, 'resolved-review-comments');
const unknownConclusion = decision({ reviews: ['Review completed without a recognized conclusion.'] });
assert.equal(unknownConclusion.checkConclusion, 'failure');
assert.equal(unknownConclusion.failureKind, 'passing-conclusion');
const waiting = decision({ reviews: [] });
assert.equal(waiting.checkStatus, 'in_progress');
assert.equal(waiting.checkConclusion, undefined);
const requestFailure = decision({ reviews: [], requestFailed: true });
assert.equal(requestFailure.checkConclusion, 'failure');
assert.equal(requestFailure.failureKind, 'request-failed');

const blockingPresentation = copilotFailurePresentation({
  decision: decision({ blocking: [finding('修复门禁误放行'), finding('修复发布权限扩大')] }),
  coreHandlers: ['core-dev'],
  contributorHandlers: ['contributor'],
});
assert.equal(blockingPresentation.title, '🚫 Copilot 阻断评论');
assert.deepEqual(blockingPresentation.handlers, ['contributor']);
assert.match(blockingPresentation.details[1], /- 修复门禁误放行\n- 修复发布权限扩大/);
assert.doesNotMatch(blockingPresentation.details.join('\n'), /https?:\/\//);

const workflowPresentation = copilotFailurePresentation({
  decision: unknownConclusion,
  coreHandlers: ['core-dev'],
  contributorHandlers: ['contributor'],
});
assert.equal(workflowPresentation.title, '⚠️ Copilot Review 状态异常');
assert.deepEqual(workflowPresentation.handlers, ['core-dev']);

const mixedPresentations = copilotFailurePresentations({
  decision: decision({
    blocking: [finding('修复阻断问题')],
    unclassified: [finding('补充严重程度')],
  }),
  coreHandlers: ['core-dev'],
  contributorHandlers: ['contributor'],
});
assert.deepEqual(mixedPresentations.map((item) => item.source), [
  'copilot-review:blocking-comments',
  'copilot-review:comment-protocol',
]);
assert.deepEqual(mixedPresentations[0].handlers, ['contributor']);
assert.deepEqual(mixedPresentations[1].handlers, ['core-dev']);

const requestPlan = coreReviewersToRequest({
  trusted: ['author', 'core-one', 'core-two'],
  author: 'AUTHOR',
  requested: ['CORE-ONE'],
  reviewed: ['CORE-TWO'],
});
assert.deepEqual(requestPlan.eligible, ['core-one', 'core-two']);
assert.deepEqual(requestPlan.reviewed, ['CORE-TWO']);
assert.deepEqual(requestPlan.missing, []);

let requestedReviewers = [];
const requestResult = requestCoreDeveloperReviews({
  trusted: ['author', 'core-one', 'core-two'],
  author: 'author',
  requested: ['core-one'],
  reviewed: [],
  request(reviewers) {
    requestedReviewers = reviewers;
  },
  confirm: () => ['core-one', 'core-two'],
});
assert.equal(requestResult.ok, true);
assert.deepEqual(requestedReviewers, ['core-two']);
assert.deepEqual(requestResult.requested, ['core-two']);

const noRepeatRequest = requestCoreDeveloperReviews({
  trusted: ['core-one'],
  author: 'author',
  requested: [],
  reviewed: ['core-one'],
  request() {
    throw new Error('reviewer should not be requested twice');
  },
});
assert.equal(noRepeatRequest.ok, true);
assert.deepEqual(noRepeatRequest.requested, []);

const failedRequest = requestCoreDeveloperReviews({
  trusted: ['core-one'],
  author: 'author',
  requested: [],
  reviewed: [],
  confirm: () => [],
  request() {
    throw new Error('review request failed');
  },
});
assert.equal(failedRequest.ok, false);
assert.match(failedRequest.error, /review request failed/);

const unconfirmedRequest = requestCoreDeveloperReviews({
  trusted: ['core-one'],
  author: 'author',
  requested: [],
  reviewed: [],
  request() {},
  confirm: () => [],
});
assert.equal(unconfirmedRequest.ok, false);
assert.match(unconfirmedRequest.error, /未确认 Review Request/);

const mainPresentation = mainAuthorizationFailurePresentation({
  status: 'failed_untrusted_contributor_missing_manual_approval',
  coreHandlers: ['core-one'],
  reviewRequest: { ok: true, eligible: ['core-one'] },
});
assert.equal(mainPresentation.title, '🔒 核心开发者审批');
assert.deepEqual(mainPresentation.handlers, ['core-one']);
assert.match(mainPresentation.details[1], /Review Request/);

const contributorFailurePresentation = mainAuthorizationFailurePresentation({
  status: 'failed_missing_real_contributors',
  coreHandlers: ['core-one'],
  reviewRequest: null,
});
assert.equal(contributorFailurePresentation.title, '⚠️ 贡献者信息识别异常');

const unidentifiedPresentation = mainAuthorizationFailurePresentation({
  status: 'failed_unidentified_commit_authors',
  coreHandlers: ['core-one'],
  reviewRequest: { ok: true, eligible: ['core-one'] },
});
assert.equal(unidentifiedPresentation.title, '⚠️ 贡献者信息识别异常');
assert.match(unidentifiedPresentation.details[1], /Review Request/);

const contributorSummary = pullRequestCommitContributorSummary([
  {
    sha: 'identified',
    author: { login: 'core-one', type: 'User' },
    commit: { author: { name: 'Core One', email: 'core@example.test' } },
  },
  {
    sha: 'unidentified',
    author: null,
    commit: { author: { name: 'External', email: 'external@example.test' } },
  },
  {
    sha: 'bot',
    author: null,
    commit: { author: { name: 'github-actions[bot]', email: '41898282+github-actions[bot]@users.noreply.github.com' } },
  },
]);
assert.deepEqual(contributorSummary.logins, ['core-one']);
assert.deepEqual(contributorSummary.unidentified, [{
  sha: 'unidentified',
  name: 'External',
  email: 'external@example.test',
}]);

const longThreadComments = Array.from({ length: 25 }, (_, index) => ({
  author: { login: index === 24 ? 'copilot-pull-request-reviewer[bot]' : 'contributor' },
  body: index === 24 ? '严重程度：阻断\n标题：第 25 条阻断评论' : `reply ${index + 1}`,
  url: `https://example.test/${index + 1}`,
}));
const longThreadFindings = copilotThreadFindings([{
  isResolved: false,
  isOutdated: false,
  comments: longThreadComments,
}]);
assert.equal(longThreadFindings.blocking.length, 1);
assert.equal(longThreadFindings.blocking[0].title, '第 25 条阻断评论');

const legacyState = {
  head: 'head-legacy',
  failures: [{
    source: 'copilot-review',
    title: '旧版标题',
    details: ['问题：旧版详情', '处理：旧版操作'],
  }],
};
const legacyBody = `<!-- workflow:pr-blocking-failures-state:${encodeBlockingState(legacyState)} -->`;
assert.deepEqual(decodeBlockingState(legacyBody), legacyState);
assert.match(blockingFailureBody(legacyState), /### 旧版标题/);

const mainFailure = {
  source: 'main-authorization',
  title: '🔒 核心开发者审批',
  handlers: ['core-one', 'core-two'],
  details: [
    '当前 PR 尚未获得所需审批。',
    '已向所有可请求的核心开发者发送 Review Request，请完成审查并提交 **Approve**。',
  ],
};
const copilotFailure = {
  source: 'copilot-review',
  title: '🚫 Copilot 阻断评论',
  handlers: ['contributor'],
  details: [
    '请处理或回复以下评论，并将对应 Conversation 标记为 **Resolved**：',
    '- 修复门禁误放行\n- 修复发布权限扩大',
  ],
};
const combinedBody = blockingFailureBody({
  head: 'head-current',
  failures: [copilotFailure, mainFailure],
});
assert.match(combinedBody, /## 🚧 PR 合并前有待处理事项/);
assert.ok(combinedBody.indexOf('### 🔒 核心开发者审批')
  < combinedBody.indexOf('### 🚫 Copilot 阻断评论'));
assert.match(combinedBody, /\*\*处理人：\*\* @core-one；@core-two/);
assert.match(combinedBody, /\n---\n\n### 🚫 Copilot 阻断评论/);
assert.match(combinedBody, /- 修复门禁误放行\n- 修复发布权限扩大/);
assert.doesNotMatch(combinedBody, /https?:\/\/|\]\(/);
assert.match(combinedBody, /全部阻断解除后将自动删除。$/);

const singleBody = blockingFailureBody({
  head: 'head-current',
  failures: [{
    source: 'copilot-review',
    ...workflowPresentation,
  }],
});
assert.match(singleBody, /### ⚠️ Copilot Review 状态异常/);
assert.doesNotMatch(singleBody, /\n---\n/);
assert.doesNotMatch(singleBody, /@contributor/);

let aggregateState = nextBlockingFailureState(null, 'head-a', {
  ...mainFailure,
  failed: true,
});
assert.deepEqual(aggregateState.failures, [mainFailure]);
aggregateState = nextBlockingFailureState(aggregateState, 'head-a', {
  ...mainFailure,
  title: '🔒 更新后的审批标题',
  failed: true,
});
assert.equal(aggregateState.failures.length, 1);
assert.equal(aggregateState.failures[0].title, '🔒 更新后的审批标题');
aggregateState = nextBlockingFailureState(aggregateState, 'head-a', {
  ...copilotFailure,
  failed: true,
});
assert.equal(aggregateState.failures.length, 2);
aggregateState = nextBlockingFailureState(aggregateState, 'head-a', {
  ...mainFailure,
  failed: false,
});
assert.deepEqual(aggregateState.failures, [copilotFailure]);
aggregateState = nextBlockingFailureState(aggregateState, 'head-a', {
  ...copilotFailure,
  failed: false,
});
assert.deepEqual(aggregateState.failures, []);
const resetForNewHead = nextBlockingFailureState({
  head: 'head-a',
  failures: [mainFailure],
}, 'head-b', {
  ...copilotFailure,
  failed: true,
});
assert.equal(resetForNewHead.head, 'head-b');
assert.deepEqual(resetForNewHead.failures, [copilotFailure]);

const replacedCopilotFailures = nextBlockingFailuresState({
  head: 'head-a',
  failures: [mainFailure, copilotFailure],
}, 'head-a', {
  sourcePrefix: 'copilot-review',
  failures: mixedPresentations,
});
assert.equal(replacedCopilotFailures.failures.length, 3);
assert.equal(replacedCopilotFailures.failures[0].source, 'main-authorization');
assert.deepEqual(replacedCopilotFailures.failures.slice(1).map((item) => item.source), [
  'copilot-review:blocking-comments',
  'copilot-review:comment-protocol',
]);

withGhExecutableArgs('["--hostname","github.com"]', () => {
  assert.deepEqual(parseGhExecutableArgs(), ['--hostname', 'github.com']);
});
withGhExecutableArgs('--paginate', () => {
  assert.deepEqual(parseGhExecutableArgs(), []);
});
withGhExecutableArgs('{"arg":"--paginate"}', () => {
  assert.deepEqual(parseGhExecutableArgs(), []);
});
withGhExecutableArgs('["--hostname", 1]', () => {
  assert.deepEqual(parseGhExecutableArgs(), []);
});

console.log('pr-governance local tests passed');
