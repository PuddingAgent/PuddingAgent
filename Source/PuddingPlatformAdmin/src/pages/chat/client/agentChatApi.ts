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

export async function getAgentMessageProcessItems(
  workspaceId: string,
  agentId: string,
  messageId: string,
): Promise<MessageProcessDetailsView> {
  return request(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/conversation/messages/${encodeURIComponent(messageId)}/process-items`,
    { method: 'GET' },
  );
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
