import type { ApiEnvelope } from '@/types/api';

export class ApiError extends Error {
  constructor(message: string, public response?: Response, public data?: unknown) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * 通用 API 请求函数
 * 
 * @param path API 路径
 * @param options fetch 选项
 * @returns 解析后的响应体
 * @throws {ApiError} 当请求失败或响应 ok 为 false 时抛出
 */
export async function api<T = any>(
  path: string,
  options?: RequestInit
): Promise<T> {
  const response = await fetch(path, options);
  const data: ApiEnvelope<T> = await response.json();

  if (!response.ok || data.ok === false) {
    throw new ApiError(
      data.error || `请求失败 (${response.status})`,
      response,
      data
    );
  }

  return data as T;
}

/**
 * GET 请求快捷方法
 */
export async function get<T = any>(path: string): Promise<T> {
  return api<T>(path, { method: 'GET' });
}

/**
 * POST 请求快捷方法
 */
export async function post<T = any>(path: string, body?: any): Promise<T> {
  return api<T>(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined
  });
}

/**
 * DELETE 请求快捷方法
 */
export async function del<T = any>(path: string): Promise<T> {
  return api<T>(path, { method: 'DELETE' });
}
