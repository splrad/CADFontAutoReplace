const assert = require('node:assert/strict');

const {
  copilotCommentSeverity,
  evaluateCopilotGate,
  parseGhExecutableArgs,
} = require('./pr-governance');

function review(body) {
  return { body };
}

function comment(body) {
  return { body, url: 'https://example.test/comment' };
}

function decision({ reviews = ['Copilot reviewed 6 out of 6 changed files in this pull request and generated 3 comments.'], blocking = [], suggestions = [], unclassified = [], requestFailed = false } = {}) {
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

assert.equal(
  decision({ suggestions: [comment('严重程度：建议\n可选改进。')] }).checkConclusion,
  'success',
);
assert.equal(
  decision({ suggestions: [comment('Severity: suggestion\nOptional improvement.')] }).passingSignal,
  'suggestion-only-comments',
);
assert.equal(copilotCommentSeverity('严重程度：阻断\n必须修复。'), 'blocking');
assert.equal(copilotCommentSeverity('Severity: blocking\nMust fix.'), 'blocking');
assert.equal(copilotCommentSeverity('严重程度：建议\n可选改进。'), 'suggestion');
assert.equal(copilotCommentSeverity('Severity: suggestion\nOptional improvement.'), 'suggestion');
assert.equal(copilotCommentSeverity('缺少严重程度。'), '');

assert.equal(
  decision({ blocking: [comment('严重程度：阻断\n必须修复。')] }).checkConclusion,
  'failure',
);
assert.equal(
  decision({ unclassified: [comment('缺少严重程度。')] }).checkTitle,
  'Copilot 审查协议不完整',
);

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

assert.equal(decision().checkConclusion, 'failure');
assert.equal(decision().checkTitle, 'Copilot 审查通过信号缺失');

const waiting = decision({ reviews: [] });
assert.equal(waiting.checkStatus, 'in_progress');
assert.equal(waiting.checkConclusion, undefined);

const requestFailure = decision({ reviews: [], requestFailed: true });
assert.equal(requestFailure.checkConclusion, 'failure');
assert.equal(requestFailure.checkTitle, 'Copilot 审查请求失败');

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
