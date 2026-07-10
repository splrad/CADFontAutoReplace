import { createAppAuth } from '@octokit/auth-app';

export interface Env {
  GITHUB_WEBHOOK_SECRET: string;
  GITHUB_APP_ID: string;
  GITHUB_APP_PRIVATE_KEY: string;
  TARGET_REPOSITORY: string;
  WEBHOOK_DELIVERIES: KVNamespace;
}

interface PullRequestReviewThreadPayload {
  action?: string;
  installation?: { id?: number };
  repository?: { id?: number; full_name?: string };
  pull_request?: {
    number?: number;
    state?: string;
    base?: { ref?: string };
    head?: { sha?: string };
  };
  thread?: { node_id?: string };
  review_thread?: { node_id?: string };
}

interface Dependencies {
  fetch: typeof fetch;
  installationToken: (env: Env, installationId: number, repositoryId: number) => Promise<string>;
}

const encoder = new TextEncoder();

function hex(bytes: ArrayBuffer): string {
  return [...new Uint8Array(bytes)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

function constantTimeEqual(left: string, right: string): boolean {
  const leftBytes = encoder.encode(left);
  const rightBytes = encoder.encode(right);
  let difference = leftBytes.length ^ rightBytes.length;
  const length = Math.max(leftBytes.length, rightBytes.length);
  for (let index = 0; index < length; index += 1) {
    difference |= (leftBytes[index] || 0) ^ (rightBytes[index] || 0);
  }
  return difference === 0;
}

export async function verifySignature(rawBody: ArrayBuffer, signature: string, secret: string): Promise<boolean> {
  if (!signature.startsWith('sha256=') || !secret) return false;
  const key = await crypto.subtle.importKey(
    'raw',
    encoder.encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const digest = await crypto.subtle.sign('HMAC', key, rawBody);
  return constantTimeEqual(`sha256=${hex(digest)}`, signature.toLowerCase());
}

async function createInstallationToken(env: Env, installationId: number, repositoryId: number): Promise<string> {
  const auth = createAppAuth({ appId: env.GITHUB_APP_ID, privateKey: env.GITHUB_APP_PRIVATE_KEY });
  const result = await auth({
    type: 'installation',
    installationId,
    repositoryIds: [repositoryId],
    permissions: { contents: 'write' },
  });
  return result.token;
}

const defaultDependencies: Dependencies = {
  fetch,
  installationToken: createInstallationToken,
};

function response(status: number, message: string): Response {
  return new Response(message, { status, headers: { 'content-type': 'text/plain; charset=utf-8' } });
}

export async function handleRequest(
  request: Request,
  env: Env,
  dependencies: Dependencies = defaultDependencies,
): Promise<Response> {
  if (request.method !== 'POST') return response(405, 'Method not allowed');
  const rawBody = await request.arrayBuffer();
  const signature = request.headers.get('x-hub-signature-256') || '';
  if (!await verifySignature(rawBody, signature, env.GITHUB_WEBHOOK_SECRET)) {
    return response(401, 'Invalid signature');
  }

  const event = request.headers.get('x-github-event') || '';
  if (event === 'ping') return response(200, 'pong');
  if (event !== 'pull_request_review_thread') return response(202, 'Ignored event');

  let payload: PullRequestReviewThreadPayload;
  try {
    payload = JSON.parse(new TextDecoder().decode(rawBody)) as PullRequestReviewThreadPayload;
  } catch {
    return response(400, 'Invalid JSON');
  }

  const action = String(payload.action || '');
  const repositoryId = Number(payload.repository?.id || 0);
  const repository = String(payload.repository?.full_name || '');
  const prNumber = Number(payload.pull_request?.number || 0);
  const headSha = String(payload.pull_request?.head?.sha || '').toLowerCase();
  const installationId = Number(payload.installation?.id || 0);
  if (!['resolved', 'unresolved'].includes(action)
    || repository !== env.TARGET_REPOSITORY
    || payload.pull_request?.state !== 'open'
    || payload.pull_request?.base?.ref !== 'main'
    || !repositoryId
    || !prNumber
    || !/^[a-f0-9]{40}$/.test(headSha)
    || !installationId) {
    return response(202, 'Ignored payload');
  }

  const deliveryId = request.headers.get('x-github-delivery') || '';
  if (!deliveryId) return response(400, 'Missing delivery ID');
  if (await env.WEBHOOK_DELIVERIES.get(deliveryId)) return response(200, 'Duplicate delivery');

  const token = await dependencies.installationToken(env, installationId, repositoryId);
  const dispatchResponse = await dependencies.fetch(`https://api.github.com/repos/${repository}/dispatches`, {
    method: 'POST',
    headers: {
      accept: 'application/vnd.github+json',
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
      'user-agent': 'cadfontautoreplace-webhook-relay',
      'x-github-api-version': '2022-11-28',
    },
    body: JSON.stringify({
      event_type: 'pr-review-thread-resolved',
      client_payload: {
        repository_id: repositoryId,
        pr_number: prNumber,
        head_sha: headSha,
        thread_node_id: payload.thread?.node_id || payload.review_thread?.node_id || '',
        action,
        delivery_id: deliveryId,
      },
    }),
  });
  if (!dispatchResponse.ok) return response(502, `GitHub dispatch failed (${dispatchResponse.status})`);

  await env.WEBHOOK_DELIVERIES.put(deliveryId, 'dispatched', { expirationTtl: 86400 });
  return response(202, 'Dispatched');
}

export default {
  fetch(request: Request, env: Env): Promise<Response> {
    return handleRequest(request, env);
  },
} satisfies ExportedHandler<Env>;
