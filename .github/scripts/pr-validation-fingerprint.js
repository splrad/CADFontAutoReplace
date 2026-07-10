const crypto = require('node:crypto');
const {
  realContributorLoginsFromBody,
  uniqueLogins,
} = require('./pr-notifications');

function hashJson(value) {
  return crypto.createHash('sha256')
    .update(JSON.stringify(value))
    .digest('hex');
}

function normalizeRepoPath(file) {
  return String(file || '').replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase();
}

function classificationInputBody(body) {
  return String(body || '')
    .replace(/\n*<!-- workflow:pr-classification:start[\s\S]*?workflow:pr-classification:end -->/gi, '')
    .trimEnd();
}

function pullRequestCommitAuthorLogins(commits) {
  return uniqueLogins((commits || []).map((commit) => commit?.author?.login || ''));
}

function fingerprintForPull({ pull, commits, files }) {
  const commitShas = (commits || []).map((commit) => commit?.sha || '').filter(Boolean).sort();
  const fileParts = (files || []).map((file) => [
    normalizeRepoPath(file?.filename),
    file?.status || '',
    file?.sha || '',
    file?.additions || 0,
    file?.deletions || 0,
  ]).sort();
  const contributors = uniqueLogins([
    ...realContributorLoginsFromBody({ body: pull?.body || '', prAuthor: pull?.user?.login || '' }),
    ...pullRequestCommitAuthorLogins(commits),
  ]).sort((a, b) => a.localeCompare(b));
  const classificationInputs = {
    title: String(pull?.title || ''),
    body_digest: hashJson(classificationInputBody(pull?.body)),
  };

  const source = {
    head_sha: pull?.head?.sha || '',
    base_ref: pull?.base?.ref || '',
    base_sha: pull?.base?.sha || '',
    commits: commitShas,
    contributors,
    files_digest: hashJson(fileParts),
    classification_digest: hashJson(classificationInputs),
  };

  return {
    ...source,
    value: hashJson(source),
  };
}

module.exports = {
  classificationInputBody,
  fingerprintForPull,
  hashJson,
  normalizeRepoPath,
};
