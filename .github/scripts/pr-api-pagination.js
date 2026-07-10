const maxPullRequestPages = 30;
const pullRequestPageSize = 100;

function fetchPullRequestPages(fetchPage) {
  const all = [];
  for (let page = 1; page <= maxPullRequestPages; page += 1) {
    const items = fetchPage(page, pullRequestPageSize) || [];
    if (!Array.isArray(items) || !items.length) break;
    all.push(...items);
    if (items.length < pullRequestPageSize) break;
  }
  return all;
}

module.exports = {
  fetchPullRequestPages,
  maxPullRequestPages,
  pullRequestPageSize,
};
