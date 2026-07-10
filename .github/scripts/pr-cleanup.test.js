const assert = require('node:assert/strict');

process.env.GITHUB_REPOSITORY = 'example/repo';
process.env.PR_NUMBER = '42';
process.env.PR_MERGED = 'false';

const {
  cleanupGovernanceComments,
  governanceMarkers,
  notifyClose,
} = require('./pr-cleanup');

const removedMarkers = [];
const removedCount = cleanupGovernanceComments({
  remove(marker) {
    removedMarkers.push(marker);
    return 1;
  },
});
assert.equal(removedCount, 3);
assert.deepEqual(removedMarkers, governanceMarkers);

let cleanupCalls = 0;
const closedResult = notifyClose({
  cleanup() {
    cleanupCalls += 1;
    return 3;
  },
});
assert.equal(cleanupCalls, 1);
assert.equal(closedResult.merged, false);

let publishedBody = '';
const mergedResult = notifyClose({
  cleanup: () => 2,
  merged: true,
  resolveAuthor: () => 'contributor',
  publish(body) {
    publishedBody = body;
  },
});
assert.equal(mergedResult.merged, true);
assert.equal(mergedResult.removedComments, 2);
assert.match(publishedBody, /## PR 合并成功并关闭/);
assert.match(publishedBody, /提交人：contributor/);

console.log('pr-cleanup local tests passed');
