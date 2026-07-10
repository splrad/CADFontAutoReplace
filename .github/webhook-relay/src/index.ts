import { createAppAuth } from '@octokit/auth-app';

export interface Env {
  GITHUB_WEBHOOK_SECRET: string;
  GITHUB_APP_ID: string;
  GITHUB_APP_PRIVATE_KEY: string;
  TARGET_REPOSITORY: string;
  DELIVERY_COORDINATOR: DurableObjectNamespace;
}

interface PullRequestReviewThreadPayload {
  action?: string;
  installation?: { id?: number };
  repository?: { id?: number; full_name?: string; default_branch?: string };
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
const deliveryRetentionMs = 24 * 60 * 60 * 1000;

type DeliveryState = 'claimed' | 'dispatched';

interface DeliveryRecord {
  state: DeliveryState;
  updatedAt: number;
}

export class DeliveryCoordinator {
  constructor(private readonly state: DurableObjectState) {}

  async fetch(request: Request): Promise<Response> {
    const action = new URL(request.url).pathname;
    if (request.method !== 'POST') return response(405, 'Method not allowed');

    if (action === '/claim') {
      const claimed = await this.state.storage.transaction(async (transaction) => {
        const existing = await transaction.get<DeliveryRecord>('delivery');
        if (existing) return false;
        await transaction.put<DeliveryRecord>('delivery', { state: 'claimed', updatedAt: Date.now() });
        return true;
      });
      if (claimed) await this.state.storage.setAlarm(Date.now() + deliveryRetentionMs);
      return response(claimed ? 201 : 409, claimed ? 'Claimed' : 'Duplicate delivery');
    }

    if (action === '/complete') {
      await this.state.storage.put<DeliveryRecord>('delivery', { state: 'dispatched', updatedAt: Date.now() });
      await this.state.storage.setAlarm(Date.now() + deliveryRetentionMs);
      return response(200, 'Completed');
    }

    if (action === '/release') {
      await this.state.storage.delete('delivery');
      await this.state.storage.deleteAlarm();
      return response(200, 'Released');
    }

    return response(404, 'Not found');
  }

  async alarm(): Promise<void> {
    await this.state.storage.deleteAll();
  }
}

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
  const defaultBranch = String(payload.repository?.default_branch || '');
  const prNumber = Number(payload.pull_request?.number || 0);
  const headSha = String(payload.pull_request?.head?.sha || '').toLowerCase();
  const installationId = Number(payload.installation?.id || 0);
  if (!['resolved', 'unresolved'].includes(action)
    || repository.toLowerCase() !== env.TARGET_REPOSITORY.trim().toLowerCase()
    || payload.pull_request?.state !== 'open'
    || !defaultBranch
    || payload.pull_request?.base?.ref !== defaultBranch
    || !repositoryId
    || !prNumber
    || !/^[a-f0-9]{40}$/.test(headSha)
    || !installationId) {
    return response(202, 'Ignored payload');
  }

  const deliveryId = request.headers.get('x-github-delivery') || '';
  if (!deliveryId) return response(400, 'Missing delivery ID');
  const coordinator = env.DELIVERY_COORDINATOR.getByName(`${repositoryId}:${deliveryId}`);
  const claim = await coordinator.fetch('https://delivery.internal/claim', { method: 'POST' });
  if (claim.status === 409) return response(200, 'Duplicate delivery');
  if (!claim.ok) return response(503, `Delivery claim failed (${claim.status})`);

  let token: string;
  try {
    token = await dependencies.installationToken(env, installationId, repositoryId);
  } catch (error) {
    await coordinator.fetch('https://delivery.internal/release', { method: 'POST' });
    throw error;
  }

  let dispatchResponse: Response;
  try {
    dispatchResponse = await dependencies.fetch(`https://api.github.com/repos/${repository}/dispatches`, {
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
  } catch {
    await coordinator.fetch('https://delivery.internal/release', { method: 'POST' });
    return response(502, 'GitHub dispatch outcome is unknown');
  }
  if (!dispatchResponse.ok) {
    await coordinator.fetch('https://delivery.internal/release', { method: 'POST' });
    return response(502, `GitHub dispatch failed (${dispatchResponse.status})`);
  }

  const completed = await coordinator.fetch('https://delivery.internal/complete', { method: 'POST' });
  if (!completed.ok) return response(503, `Delivery completion failed (${completed.status})`);
  return response(202, 'Dispatched');
}

export default {
  fetch(request: Request, env: Env): Promise<Response> {
    return handleRequest(request, env);
  },
} satisfies ExportedHandler<Env>;
