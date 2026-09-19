import { request } from '@umijs/max';
import type {
  AgentConversationView,
  AgentStatusProjection,
  MessageProcessDetailsView,
} from './types';
import type { PermissionMode } from '../types/chatStateTypes';
import { PERMISSION_MODES } from '../types/chatStateTypes';

export async function listAgentStatuses(
  workspaceId: string,
): Promise<AgentStatusProjection[]> {
  return request(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/status`,
    { method: 'GET' },
  );
}

const isNotModifiedResponse = (error: unknown): boolean => {
  const responseStatus = (error as { response?: { status?: unknown } })
    ?.response?.status;
  const status = responseStatus ?? (error as { status?: unknown })?.status;
  return Number(status) === 304;
};

export async function getAgentConversation(
  workspaceId: string,
  agentId: string,
  knownCursor?: number,
): Promise<AgentConversationView | null> {
  const qs =
    knownCursor && knownCursor > 0 ? `?knownCursor=${knownCursor}` : '';
  const url = `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/conversation${qs}`;
  try {
    return await request(url, { method: 'GET', skipErrorHandler: true });
  } catch (error) {
    if (isNotModifiedResponse(error)) return null;
    throw error;
  }
}

/** 明细请求选项（F01/AU-F01-1）：外部取消信号 + 有界超时。 */
export interface AgentMessageProcessItemsOptions {
  /** 调度器持有的取消信号：会话切换/卸载时由 AbortController 触发。 */
  signal?: AbortSignal;
  /** 有界超时（毫秒）；到时主动 abort，真实终止底层 transport。 */
  timeoutMs?: number;
}

export async function getAgentMessageProcessItems(
  workspaceId: string,
  agentId: string,
  messageId: string,
  options?: AgentMessageProcessItemsOptions,
): Promise<MessageProcessDetailsView> {
  const external = options?.signal;
  // 内部 controller 统一承载两类取消：外部 signal（会话切换/卸载）与
  // 超时定时器。abort 直接作用于传给 transport 的 signal，HTTP 请求被
// 真实终止；不用 Promise.race 把仍在跑的请求从并发计数里摘掉。
  const controller = new AbortController();
  const abortFromExternal = () => controller.abort();
  if (external) {
    if (external.aborted) controller.abort();
    else external.addEventListener('abort', abortFromExternal, { once: true });
  }
  const timer =
    options?.timeoutMs && options.timeoutMs > 0
      ? setTimeout(() => controller.abort(), options.timeoutMs)
      : null;
  try {
    return await request(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/conversation/messages/${encodeURIComponent(messageId)}/process-items`,
      { method: 'GET', signal: controller.signal },
    );
  } finally {
    if (timer) clearTimeout(timer);
    if (external) external.removeEventListener('abort', abortFromExternal);
  }
}

// ─── Agent 级访问级别 REST（用户 2026-09-19）─────────────
// 权限配置跟随 **Agent** 主体（不是工作区、不是全局），契约对齐后端
// WorkspaceAgentApiController：
//   GET /api/workspaces/{workspaceId}/agents/{agentId}/access-level
//     → { agentId, level: "auto"|"full", fullAccessActive, expiresAtUtc, temporary }
//   PUT 同路径 body { level, durationSeconds? }（留空=持久、非空=临时）[需 admin]
// 读失败返回 null（不伪造状态，调用方保持现值）；写失败返回 false 由调用方回读后端。

/** 后端访问级别读结果（已做过期回落：level 不会返回陈旧的 full）。 */
export interface AgentAccessLevelState {
  mode: PermissionMode;
  /** 仅 fullTemporary 有值；用于安排到期后的自动刷新。 */
  expiresAtUtc: string | null;
}

/** 「完全访问（5 分钟）」的时长，与后端一致。 */
const TEMPORARY_DURATION_SECONDS = 300;

function toAgentAccessLevelState(data: unknown): AgentAccessLevelState | null {
  if (!data || typeof data !== 'object') return null;
  const raw = data as {
    level?: unknown;
    fullAccessActive?: unknown;
    expiresAtUtc?: unknown;
    temporary?: unknown;
  };
  const level = typeof raw.level === 'string' ? raw.level.toLowerCase() : null;
  if (level !== 'auto' && level !== 'full') return null;
  if (raw.fullAccessActive === false || level === 'auto') {
    return { mode: 'auto', expiresAtUtc: null };
  }
  const expiresAtUtc =
    typeof raw.expiresAtUtc === 'string' && raw.expiresAtUtc.length > 0
      ? raw.expiresAtUtc
      : null;
  const mode: PermissionMode =
    raw.temporary === true || expiresAtUtc ? 'fullTemporary' : 'full';
  return PERMISSION_MODES.includes(mode) ? { mode, expiresAtUtc } : null;
}

/** 读取指定 Agent 的访问级别；端点不可用/网络失败时返回 null。 */
export async function loadAgentAccessLevel(
  workspaceId: string,
  agentId: string,
): Promise<AgentAccessLevelState | null> {
  try {
    const data = await request<unknown>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/access-level`,
      { method: 'GET', skipErrorHandler: true },
    );
    return toAgentAccessLevelState(data);
  } catch {
    return null;
  }
}

/** 写回指定 Agent 的访问级别（幂等 PUT）；成功返回 true。 */
export async function saveAgentAccessLevel(
  workspaceId: string,
  agentId: string,
  mode: PermissionMode,
): Promise<boolean> {
  try {
    await request(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/access-level`,
      {
        method: 'PUT',
        data: {
          level: mode === 'auto' ? 'auto' : 'full',
          durationSeconds:
            mode === 'fullTemporary' ? TEMPORARY_DURATION_SECONDS : undefined,
        },
        skipErrorHandler: true,
      },
    );
    return true;
  } catch {
    return false;
  }
}
