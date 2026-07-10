import { describe, expect, it, vi } from 'vitest';
import { handleRequest, verifySignature, type Env } from '../src/index';

const secret = "It's a Secret to Everybody";
const headSha = 'a'.repeat(40);

class MemoryKv {
  values = new Map<string, string>();

  async get(key: string): Promise<string | null> {
    return this.values.get(key) || null;
  }

  async put(key: string, value: string): Promise<void> {
    this.values.set(key, value);
  }
}

async function signature(body: string): Promise<string> {
  const key = await crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const digest = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(body));
  return `sha256=${[...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('')}`;
}

function payload(overrides: Record<string, unknown> = {}) {
  return {
    action: 'resolved',
    installation: { id: 7 },
    repository: { id: 42, full_name: 'splrad/CADFontAutoReplace' },
    pull_request: {
      number: 121,
      state: 'open',
      base: { ref: 'main' },
      head: { sha: headSha },
    },
    thread: { node_id: 'PRRT_kwDOExample' },
    ...overrides,
  };
}

async function requestFor(event: string, body: string, delivery = 'delivery-1', signed = true) {
  return new Request('https://relay.example.test', {
    method: 'POST',
    headers: {
      'x-github-event': event,
      'x-github-delivery': delivery,
      'x-hub-signature-256': signed ? await signature(body) : 'sha256=invalid',
    },
    body,
  });
}

function environment(kv = new MemoryKv()): Env {
  return {
    GITHUB_WEBHOOK_SECRET: secret,
    GITHUB_APP_ID: '1',
    GITHUB_APP_PRIVATE_KEY: 'private-key',
    TARGET_REPOSITORY: 'splrad/CADFontAutoReplace',
    WEBHOOK_DELIVERIES: kv as unknown as KVNamespace,
  };
}

describe('signature verification', () => {
  it('matches the official GitHub HMAC test vector', async () => {
    const body = new TextEncoder().encode('Hello, World!').buffer;
    expect(await verifySignature(
      body,
      'sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17',
      secret,
    )).toBe(true);
  });

  it('rejects missing and invalid signatures', async () => {
    const body = new TextEncoder().encode('{}').buffer;
    expect(await verifySignature(body, '', secret)).toBe(false);
    expect(await verifySignature(body, 'sha256=invalid', secret)).toBe(false);
  });
});

describe('webhook relay', () => {
  it('answers signed ping without creating a token', async () => {
    const body = '{}';
    const installationToken = vi.fn();
    const result = await handleRequest(await requestFor('ping', body), environment(), {
      fetch: vi.fn(),
      installationToken,
    });
    expect(result.status).toBe(200);
    expect(installationToken).not.toHaveBeenCalled();
  });

  it('rejects an invalid signature', async () => {
    const body = JSON.stringify(payload());
    const result = await handleRequest(await requestFor('pull_request_review_thread', body, 'bad', false), environment());
    expect(result.status).toBe(401);
  });

  it.each([
    ['event', 'pull_request_review', payload()],
    ['action', 'pull_request_review_thread', payload({ action: 'submitted' })],
    ['repository', 'pull_request_review_thread', payload({ repository: { id: 42, full_name: 'other/repo' } })],
    ['base', 'pull_request_review_thread', payload({ pull_request: { number: 121, state: 'open', base: { ref: 'dev' }, head: { sha: headSha } } })],
    ['closed PR', 'pull_request_review_thread', payload({ pull_request: { number: 121, state: 'closed', base: { ref: 'main' }, head: { sha: headSha } } })],
  ])('ignores a non-target %s', async (_name, event, value) => {
    const body = JSON.stringify(value);
    const installationToken = vi.fn();
    const result = await handleRequest(await requestFor(event, body), environment(), {
      fetch: vi.fn(),
      installationToken,
    });
    expect(result.status).toBe(202);
    expect(installationToken).not.toHaveBeenCalled();
  });

  it('dispatches a resolved thread once with the fixed payload', async () => {
    const kv = new MemoryKv();
    const body = JSON.stringify(payload());
    const githubFetch = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    const dependencies = {
      fetch: githubFetch as typeof fetch,
      installationToken: vi.fn().mockResolvedValue('installation-token'),
    };
    const first = await handleRequest(await requestFor('pull_request_review_thread', body), environment(kv), dependencies);
    const second = await handleRequest(await requestFor('pull_request_review_thread', body), environment(kv), dependencies);
    expect(first.status).toBe(202);
    expect(second.status).toBe(200);
    expect(githubFetch).toHaveBeenCalledTimes(1);
    const init = githubFetch.mock.calls[0][1] as RequestInit;
    expect(JSON.parse(String(init.body))).toEqual({
      event_type: 'pr-review-thread-resolved',
      client_payload: {
        repository_id: 42,
        pr_number: 121,
        head_sha: headSha,
        thread_node_id: 'PRRT_kwDOExample',
        action: 'resolved',
        delivery_id: 'delivery-1',
      },
    });
  });

  it('does not deduplicate a failed GitHub dispatch', async () => {
    const kv = new MemoryKv();
    const body = JSON.stringify(payload());
    const result = await handleRequest(await requestFor('pull_request_review_thread', body), environment(kv), {
      fetch: vi.fn().mockResolvedValue(new Response('failure', { status: 500 })) as typeof fetch,
      installationToken: vi.fn().mockResolvedValue('installation-token'),
    });
    expect(result.status).toBe(502);
    expect(await kv.get('delivery-1')).toBeNull();
  });
});
