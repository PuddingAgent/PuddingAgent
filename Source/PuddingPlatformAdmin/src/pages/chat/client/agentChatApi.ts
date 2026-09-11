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

// ─── P1#4 权限模式 REST 持久化 ─────────────────────────────
// 契约（对齐后端 workspace 级用户偏好）：
//   PUT /api/workspaces/{workspaceId}/user-preferences/permission-mode
//     body: { mode: "manual" | "acceptEdits" | "plan" | "auto" }
//   GET /api/workspaces/{workspaceId}/user-preferences/permission-mode
//     response: { mode: "manual" | ... }；未设置时 404/204 → null
// 后端端点缺失/离线时静默降级：权限模式仅保留在 localStorage，不打断主聊天流程。

/** P1#4：将权限模式写回当前工作空间（幂等 PUT，失败静默）。 */
export async function savePermissionMode(
  workspaceId: string,
  mode: PermissionMode,
): Promise<void> {
  try {
    await request(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/user-preferences/permission-mode`,
      {
        method: 'PUT',
        data: { mode },
        skipErrorHandler: true,
      },
    );
  } catch {
    // 忽略：端点未实现/网络失败时由 localStorage 兜底，不打断聊天。
  }
}

/** P1#4：读取当前工作空间保存的权限模式；未设置或不可用时返回 null。 */
export async function loadPermissionMode(
  workspaceId: string,
): Promise<PermissionMode | null> {
  try {
    const data = await request<{ mode?: unknown }>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/user-preferences/permission-mode`,
      { method: 'GET', skipErrorHandler: true },
    );
    const mode = data?.mode;
    return PERMISSION_MODES.includes(mode as PermissionMode)
      ? (mode as PermissionMode)
      : null;
  } catch {
    return null;
  }
}
