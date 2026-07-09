function normalizeLogin(value) {
  const login = String(value || '').trim().replace(/^@+/, '');
  return /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/.test(login) ? login : '';
}

function isBotLogin(value) {
  const login = normalizeLogin(value).toLowerCase();
  return !login || login === 'unknown' || login.endsWith('[bot]') || login === 'github-actions' || login === 'dependabot';
}

function parseTrustedDevelopers(raw = process.env.TRUSTED_DEVELOPERS || '[]') {
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? uniqueLogins(parsed) : [];
  } catch {
    return [];
  }
}

function uniqueLogins(values) {
  const seen = new Set();
  const result = [];
  for (const value of values) {
    const login = normalizeLogin(value);
    const key = login.toLowerCase();
    if (!key || isBotLogin(login) || seen.has(key)) continue;
    seen.add(key);
    result.push(login);
  }
  return result;
}

function mentionText(logins) {
  const mentions = uniqueLogins(logins).map((login) => `@${login}`);
  return mentions.length ? mentions.join('；') : '（未配置通知对象）';
}

function coreAndAuthorMentionText(author, trustedRaw = process.env.TRUSTED_DEVELOPERS || '[]') {
  return mentionText([...parseTrustedDevelopers(trustedRaw), author]);
}

function authorDisplayText(author) {
  const login = normalizeLogin(author);
  return login && !isBotLogin(login) ? login : '未识别真实提交人（机器人 PR）';
}

function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function metadataValue(body, name) {
  const match = String(body || '').match(new RegExp(`<!--\\s*${escapeRegExp(name)}:([^>]*)-->`, 'i'));
  return match?.[1]?.trim() || '';
}

function sourceActorFromBody(body) {
  return normalizeLogin(metadataValue(body, 'workflow:source-actor'));
}

function contributorLoginsFromBody(body) {
  return uniqueLogins(metadataValue(body, 'workflow:source-contributors')
    .split(/[,\s]+/)
    .filter(Boolean));
}

function realContributorLoginsFromBody({ body, prAuthor = '' }) {
  return uniqueLogins([
    ...contributorLoginsFromBody(body),
    sourceActorFromBody(body),
    normalizeLogin(prAuthor),
  ]);
}

function effectiveAuthorFromBody({ body, prAuthor = '', actor = '', fallback = 'unknown' }) {
  return [
    sourceActorFromBody(body),
    ...contributorLoginsFromBody(body),
    normalizeLogin(prAuthor),
    normalizeLogin(actor),
  ].find((login) => login && !isBotLogin(login))
    || fallback;
}

function flattenPayload(payload) {
  if (!Array.isArray(payload)) return [];
  return payload.length > 0 && Array.isArray(payload[0]) ? payload.flat() : payload;
}

function listMarkerComments({ ghJson, repo, prNumber, marker, token }) {
  if (!ghJson || !repo || !prNumber || !marker) return [];
  const comments = flattenPayload(ghJson([
    '--method', 'GET',
    `repos/${repo}/issues/${prNumber}/comments`,
    '-f', 'per_page=100',
    '--paginate',
    '--slurp',
  ], undefined, token) || []);

  return comments
    .filter((comment) => String(comment.body || '').includes(marker))
    .sort((a, b) => String(a.created_at || '').localeCompare(String(b.created_at || '')));
}

function createIssueComment({ gh, repo, prNumber, body, token }) {
  gh([
    '--method', 'POST',
    `repos/${repo}/issues/${prNumber}/comments`,
    '--input', '-',
  ], JSON.stringify({ body }), token);
}

function deleteMarkerComments({ gh, ghJson, repo, prNumber, marker, token }) {
  const comments = listMarkerComments({ ghJson, repo, prNumber, marker, token });
  for (const comment of comments) {
    gh([
      '--method', 'DELETE',
      `repos/${repo}/issues/comments/${comment.id}`,
    ], undefined, token);
  }
  return comments.length;
}

function upsertMarkerComment({
  gh,
  ghJson,
  repo,
  prNumber,
  marker,
  body,
  token,
  createIfMissing = true,
  position = 'latest',
}) {
  const comments = listMarkerComments({ ghJson, repo, prNumber, marker, token });
  const existing = position === 'first' ? comments[0] : comments[comments.length - 1];
  if (existing) {
    gh([
      '--method', 'PATCH',
      `repos/${repo}/issues/comments/${existing.id}`,
      '--input', '-',
    ], JSON.stringify({ body }), token);

    for (const stale of comments.filter((comment) => comment.id !== existing.id)) {
      gh([
        '--method', 'DELETE',
        `repos/${repo}/issues/comments/${stale.id}`,
      ], undefined, token);
    }
    return existing.id;
  }

  if (!createIfMissing) return null;
  createIssueComment({ gh, repo, prNumber, body, token });
  return null;
}

module.exports = {
  authorDisplayText,
  coreAndAuthorMentionText,
  createIssueComment,
  deleteMarkerComments,
  effectiveAuthorFromBody,
  realContributorLoginsFromBody,
  isBotLogin,
  listMarkerComments,
  mentionText,
  normalizeLogin,
  parseTrustedDevelopers,
  sourceActorFromBody,
  uniqueLogins,
  upsertMarkerComment,
};
