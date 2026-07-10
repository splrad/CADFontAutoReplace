import { createAppAuth } from '@octokit/auth-app';

export interface Env {
  GITHUB_WEBHOOK_SECRET: string;
  GITHUB_APP_ID: string;
  GITHUB_APP_PRIVATE_KEY: string;
  TARGET_REPOSITORY: string;
  DELIVERY_COORDINATOR: DurableObjectNamespace;
}

interface PullRequestSignalPayload {
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
  review?: { id?: number; node_id?: string };
  comment?: { id?: number; node_id?: string; pull_request_review_id?: number };
}

type InstallationPermissions = {
  contents?: 'write';
};

interface Dependencies {
  fetch: typeof fetch;
  installationToken: (
    env: Env,
    installationId: number,
    repositoryId: number,
    permissions: InstallationPermissions,
  ) => Promise<string>;
}

const encoder = new TextEncoder();
const deliveryRetentionMs = 24 * 60 * 60 * 1000;
const deliveryClaimLeaseMs = 60 * 1000;
const reviewSignalActions = new Map([
  ['pull_request_review_thread', new Set(['resolved', 'unresolved'])],
  ['pull_request_review', new Set(['submitted', 'edited', 'dismissed'])],
  ['pull_request_review_comment', new Set(['created', 'edited', 'deleted'])],
]);

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
      const now = Date.now();
      const claimState = await this.state.storage.transaction(async (transaction) => {
        const existing = await transaction.get<DeliveryRecord>('delivery');
        if (existing?.state === 'dispatched') return 'dispatched';
        if (existing?.state === 'claimed' && now - existing.updatedAt < deliveryClaimLeaseMs) {
          return 'processing';
        }
        await transaction.put<DeliveryRecord>('delivery', { state: 'claimed', updatedAt: now });
        return 'claimed';
      });
      if (claimState === 'claimed') {
        await this.state.storage.setAlarm(now + deliveryRetentionMs);
        return response(201, 'Claimed');
      }
      return claimState === 'dispatched'
        ? response(200, 'Duplicate delivery')
        : response(409, 'Delivery is already processing');
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

async function createInstallationToken(
  env: Env,
  installationId: number,
  repositoryId: number,
  permissions: InstallationPermissions,
): Promise<string> {
  const auth = createAppAuth({ appId: env.GITHUB_APP_ID, privateKey: env.GITHUB_APP_PRIVATE_KEY });
  const result = await auth({
    type: 'installation',
    installationId,
    repositoryIds: [repositoryId],
    permissions,
  });
  return result.token;
}

const defaultDependencies: Dependencies = {
  // Cloudflare runtime functions depend on their invocation receiver. Wrapping
  // fetch keeps `dependencies.fetch(...)` from rebinding the runtime function.
  fetch: (input, init) => fetch(input, init),
  installationToken: createInstallationToken,
};

function response(status: number, message: string): Response {
  return new Response(message, { status, headers: { 'content-type': 'text/plain; charset=utf-8' } });
}

function githubHeaders(token: string): HeadersInit {
  return {
    accept: 'application/vnd.github+json',
    authorization: `Bearer ${token}`,
    'content-type': 'application/json',
    'user-agent': 'cadfontautoreplace-webhook-relay',
    'x-github-api-version': '2026-03-10',
  };
}

async function claimDelivery(env: Env, repositoryId: number, deliveryId: string) {
  const coordinator = env.DELIVERY_COORDINATOR.getByName(`${repositoryId}:${deliveryId}`);
  const claim = await coordinator.fetch('https://delivery.internal/claim', { method: 'POST' });
  if (claim.status === 200) return { coordinator, result: response(200, 'Duplicate delivery') };
  if (claim.status === 409) return { coordinator, result: response(503, 'Delivery is already processing') };
  if (!claim.ok) return { coordinator, result: response(503, `Delivery claim failed (${claim.status})`) };
  return { coordinator, result: null };
}

async function releaseDelivery(coordinator: DurableObjectStub): Promise<void> {
  await coordinator.fetch('https://delivery.internal/release', { method: 'POST' });
}

async function completeDelivery(coordinator: DurableObjectStub): Promise<Response | null> {
  const completed = await coordinator.fetch('https://delivery.internal/complete', { method: 'POST' });
  return completed.ok ? null : response(503, `Delivery completion failed (${completed.status})`);
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
  const allowedActions = reviewSignalActions.get(event);
  if (!allowedActions) return response(202, 'Ignored event');

  let payload: PullRequestSignalPayload;
  try {
    payload = JSON.parse(new TextDecoder().decode(rawBody)) as PullRequestSignalPayload;
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
  if (!allowedActions.has(action)
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
  const { coordinator, result } = await claimDelivery(env, repositoryId, deliveryId);
  if (result) return result;

  let token: string;
  try {
    token = await dependencies.installationToken(env, installationId, repositoryId, { contents: 'write' });
  } catch {
    await releaseDelivery(coordinator);
    return response(502, 'GitHub installation token creation failed');
  }

  let dispatchResponse: Response;
  try {
    dispatchResponse = await dependencies.fetch(`https://api.github.com/repos/${repository}/dispatches`, {
      method: 'POST',
      headers: {
        ...githubHeaders(token),
      },
      body: JSON.stringify({
        event_type: 'pr-review-state-changed',
        client_payload: {
          repository_id: repositoryId,
          pr_number: prNumber,
          head_sha: headSha,
          source_event: event,
          action,
          delivery_id: deliveryId,
          thread_node_id: payload.thread?.node_id || payload.review_thread?.node_id || '',
          review_id: Number(payload.review?.id || payload.comment?.pull_request_review_id || 0),
          comment_id: Number(payload.comment?.id || 0),
        },
      }),
    });
  } catch {
    await releaseDelivery(coordinator);
    return response(502, 'GitHub dispatch outcome is unknown');
  }
  if (!dispatchResponse.ok) {
    await releaseDelivery(coordinator);
    return response(502, `GitHub dispatch failed (${dispatchResponse.status})`);
  }

  const completionFailure = await completeDelivery(coordinator);
  return completionFailure || response(202, 'Dispatched');
}

export default {
  fetch(request: Request, env: Env): Promise<Response> {
    return handleRequest(request, env);
  },
} satisfies ExportedHandler<Env>;
