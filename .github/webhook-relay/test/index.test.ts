import { describe, expect, it, vi } from 'vitest';
import { handleRequest, verifySignature, type Env } from '../src/index';

const secret = "It's a Secret to Everybody";
const headSha = 'a'.repeat(40);

class MemoryCoordinatorNamespace {
  values = new Map<string, { state: 'claimed' | 'dispatched'; updatedAt: number }>();

  getByName(name: string) {
    return {
      fetch: async (input: RequestInfo | URL) => {
        const action = new URL(String(input)).pathname;
        if (action === '/claim') {
          const existing = this.values.get(name);
          if (existing?.state === 'dispatched') return new Response('Duplicate delivery');
          if (existing?.state === 'claimed' && Date.now() - existing.updatedAt < 60_000) {
            return new Response('Delivery is already processing', { status: 409 });
          }
          this.values.set(name, { state: 'claimed', updatedAt: Date.now() });
          return new Response('Claimed', { status: 201 });
        }
        if (action === '/complete') {
          this.values.set(name, { state: 'dispatched', updatedAt: Date.now() });
          return new Response('Completed');
        }
        if (action === '/release') {
          this.values.delete(name);
          return new Response('Released');
        }
        return new Response('Not found', { status: 404 });
      },
    };
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
    repository: { id: 42, full_name: 'splrad/CADFontAutoReplace', default_branch: 'main' },
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

function environment(coordinator = new MemoryCoordinatorNamespace()): Env {
  return {
    GITHUB_WEBHOOK_SECRET: secret,
    GITHUB_APP_ID: '1',
    GITHUB_APP_PRIVATE_KEY: 'private-key',
    TARGET_REPOSITORY: 'splrad/CADFontAutoReplace',
    DELIVERY_COORDINATOR: coordinator as unknown as DurableObjectNamespace,
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
    const coordinator = new MemoryCoordinatorNamespace();
    const body = JSON.stringify(payload());
    const githubFetch = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    const dependencies = {
      fetch: githubFetch as typeof fetch,
      installationToken: vi.fn().mockResolvedValue('installation-token'),
    };
    const first = await handleRequest(await requestFor('pull_request_review_thread', body), environment(coordinator), dependencies);
    const second = await handleRequest(await requestFor('pull_request_review_thread', body), environment(coordinator), dependencies);
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

  it.each([
    ['repository name casing', payload({
      repository: { id: 42, full_name: 'Splrad/CADFontAutoReplace', default_branch: 'main' },
    })],
    ['non-main default branch', payload({
      repository: { id: 42, full_name: 'splrad/CADFontAutoReplace', default_branch: 'trunk' },
      pull_request: { number: 121, state: 'open', base: { ref: 'trunk' }, head: { sha: headSha } },
    })],
  ])('accepts %s from repository metadata', async (_name, value) => {
    const body = JSON.stringify(value);
    const githubFetch = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    const result = await handleRequest(await requestFor('pull_request_review_thread', body), environment(), {
      fetch: githubFetch as typeof fetch,
      installationToken: vi.fn().mockResolvedValue('installation-token'),
    });
    expect(result.status).toBe(202);
    expect(githubFetch).toHaveBeenCalledTimes(1);
  });

  it('serializes concurrent retries before dispatch', async () => {
    const coordinator = new MemoryCoordinatorNamespace();
    const body = JSON.stringify(payload());
    let completeDispatch!: (response: Response) => void;
    const githubFetch = vi.fn().mockImplementation(() => new Promise<Response>((resolve) => {
      completeDispatch = resolve;
    }));
    const dependencies = {
      fetch: githubFetch as typeof fetch,
      installationToken: vi.fn().mockResolvedValue('installation-token'),
    };

    const firstPromise = handleRequest(
      await requestFor('pull_request_review_thread', body),
      environment(coordinator),
      dependencies,
    );
    await vi.waitFor(() => expect(githubFetch).toHaveBeenCalledTimes(1));
    const second = await handleRequest(
      await requestFor('pull_request_review_thread', body),
      environment(coordinator),
      dependencies,
    );
    completeDispatch(new Response(null, { status: 204 }));
    const first = await firstPromise;

    expect(first.status).toBe(202);
    expect(second.status).toBe(503);
    expect(githubFetch).toHaveBeenCalledTimes(1);
  });

  it('does not deduplicate a failed GitHub dispatch', async () => {
    const coordinator = new MemoryCoordinatorNamespace();
    const body = JSON.stringify(payload());
    const result = await handleRequest(await requestFor('pull_request_review_thread', body), environment(coordinator), {
      fetch: vi.fn().mockResolvedValue(new Response('failure', { status: 500 })) as typeof fetch,
      installationToken: vi.fn().mockResolvedValue('installation-token'),
    });
    expect(result.status).toBe(502);
    expect(coordinator.values.has('42:delivery-1')).toBe(false);
  });

  it('releases the claim when the dispatch outcome is unknown', async () => {
    const coordinator = new MemoryCoordinatorNamespace();
    const body = JSON.stringify(payload());
    const githubFetch = vi.fn()
      .mockRejectedValueOnce(new Error('network failure'))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const dependencies = {
      fetch: githubFetch as typeof fetch,
      installationToken: vi.fn().mockResolvedValue('installation-token'),
    };

    const first = await handleRequest(
      await requestFor('pull_request_review_thread', body),
      environment(coordinator),
      dependencies,
    );
    const second = await handleRequest(
      await requestFor('pull_request_review_thread', body),
      environment(coordinator),
      dependencies,
    );

    expect(first.status).toBe(502);
    expect(second.status).toBe(202);
    expect(githubFetch).toHaveBeenCalledTimes(2);
    expect(coordinator.values.get('42:delivery-1')?.state).toBe('dispatched');
  });

  it('retries an abandoned claim after its lease expires', async () => {
    const coordinator = new MemoryCoordinatorNamespace();
    const body = JSON.stringify(payload());
    const githubFetch = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    const dependencies = {
      fetch: githubFetch as typeof fetch,
      installationToken: vi.fn().mockResolvedValue('installation-token'),
    };
    coordinator.values.set('42:delivery-1', { state: 'claimed', updatedAt: Date.now() });

    const processing = await handleRequest(
      await requestFor('pull_request_review_thread', body),
      environment(coordinator),
      dependencies,
    );
    coordinator.values.set('42:delivery-1', { state: 'claimed', updatedAt: 0 });
    const retried = await handleRequest(
      await requestFor('pull_request_review_thread', body),
      environment(coordinator),
      dependencies,
    );

    expect(processing.status).toBe(503);
    expect(retried.status).toBe(202);
    expect(githubFetch).toHaveBeenCalledTimes(1);
    expect(coordinator.values.get('42:delivery-1')?.state).toBe('dispatched');
  });
});
