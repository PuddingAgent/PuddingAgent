import { request } from '@umijs/max';
import type {
  ConversationHistoryPage,
  ConversationHistoryRequestOptions,
  ConversationOpenRequestOptions,
  ConversationOpenSnapshot,
  ConversationProcessPage,
  ConversationProcessRequestOptions,
} from './conversationOpenTypes';

/**
 * 阶段 D2：统一打开 / 历史分页 / 过程明细分页 API 客户端。
 *
 * 边界：
 * - 打开请求支持 `If-None-Match`，304 映射为 `null`（调用方保留缓存快照）；
 * - 所有请求可被 `AbortSignal` 真实中止（切换 agent/卸载时由调用方取消）；
 * - 失败**不**吞成空会话：除 304 外原样抛出，由 store 显示错误/partial。
 */

const isNotModifiedResponse = (error: unknown): boolean => {
  const responseStatus = (error as { response?: { status?: unknown } })
    ?.response?.status;
  const status = responseStatus ?? (error as { status?: unknown })?.status;
  return Number(status) === 304;
};

const buildQuery = (
  entries: Array<[string, string | number | null | undefined]>,
): string => {
  const parts = entries
    .filter(([, value]) => value !== undefined && value !== null && value !== '')
    .map(
      ([key, value]) =>
        `${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`,
    );
  return parts.length > 0 ? `?${parts.join('&')}` : '';
};

export async function getConversationOpen(
  workspaceId: string,
  agentId: string,
  options?: ConversationOpenRequestOptions,
): Promise<ConversationOpenSnapshot | null> {
  const query = buildQuery([['messageLimit', options?.messageLimit]]);
  const url = `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/conversation-open${query}`;
  try {
    return await request(url, {
      method: 'GET',
      skipErrorHandler: true,
      signal: options?.signal,
      headers: options?.etag
        ? { 'If-None-Match': options.etag }
        : undefined,
    });
  } catch (error) {
    if (isNotModifiedResponse(error)) return null;
    throw error;
  }
}

export async function getConversationHistory(
  conversationId: string,
  options?: ConversationHistoryRequestOptions,
): Promise<ConversationHistoryPage> {
  const query = buildQuery([
    ['beforeCreatedAt', options?.beforeCreatedAt],
    ['beforeMessageId', options?.beforeMessageId],
    ['limit', options?.limit],
  ]);
  return request(
    `/api/conversations/${encodeURIComponent(conversationId)}/history${query}`,
    {
      method: 'GET',
      skipErrorHandler: true,
      signal: options?.signal,
    },
  );
}

export async function getConversationProcess(
  conversationId: string,
  options?: ConversationProcessRequestOptions,
): Promise<ConversationProcessPage> {
  const query = buildQuery([
    ['turnId', options?.turnId],
    ['beforeSequence', options?.beforeSequence],
    ['throughSequence', options?.throughSequence],
    ['limit', options?.limit],
  ]);
  return request(
    `/api/conversations/${encodeURIComponent(conversationId)}/process${query}`,
    {
      method: 'GET',
      skipErrorHandler: true,
      signal: options?.signal,
    },
  );
}
