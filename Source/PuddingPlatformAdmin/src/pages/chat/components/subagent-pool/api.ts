// ── 子代理池只读取数（任务 B 前端骨架）────────────────────────────
// 契约（由后端任务 A 提供，可能尚未就绪）：
//   GET /api/admin/sub-agent-pool   (Admin JWT，与其它 admin 端点同一套 request)
//   200 → SubAgentPoolEntry[]
//   空池返回 200 + []，**不是 404** —— UI 必须能把「空池」与「失败」分开。
//
// 本文件只做两件事：声明条目类型、发一次 GET。不复用
// `@/services/platform/api` 是因为该文件正被并行任务改动；此处走同一个
// `request`（umi-request）实例，取数方式与 `pages/*/api.ts` 一致。
import { request } from '@umijs/max';

/** 子代理池条目（与后端 DTO 字段一一对应，勿在此处加派生字段）。 */
export interface SubAgentPoolEntry {
  poolId: string;
  name: string;
  role: string | null;
  responsibility: string | null;
  avatarKey: string | null;
  modelId: string | null;
  templateId: string | null;
  /** idle | busy | sleeping | dead；后端若新增未知值，UI 按未知档渲染而不是崩溃。 */
  status: string;
  taskCount: number;
  lastUsedAt: string | null;
  /**
   * 任务书契约字段：最近成功时刻（ISO-8601）。后端当前**没有这一列**，
   * 故为可选；有则用它，没有则走下面三态的 lastSuccess。
   */
  lastSuccessAt?: string | null;
  /**
   * 后端实际 DTO 字段（三态）：null = 从未执行；false = 上次失败；true = 上次成功。
   * 不把它伪装成时间，否则界面会把「没有时刻列」显示成「从未成功」。
   */
  lastSuccess?: boolean | null;
  subSessionId: string;
  createdAt: string;
}

export const SUB_AGENT_POOL_ENDPOINT = '/api/admin/sub-agent-pool';

/**
 * 拉取子代理池。
 *
 * 空数组是正常结果；**非数组响应视为格式错误并抛出**——静默兜底成 [] 会把
 * 「接口返回了别的东西」伪装成「池子是空的」，三态区分就失效了。
 */
export async function fetchSubAgentPool(): Promise<SubAgentPoolEntry[]> {
  const result = await request<SubAgentPoolEntry[]>(SUB_AGENT_POOL_ENDPOINT, {
    method: 'GET',
  });

  if (!Array.isArray(result)) {
    throw new Error('子代理池响应格式不正确（期望数组）');
  }

  return result as SubAgentPoolEntry[];
}

/**
 * 休眠池条目：库迁 `sleeping`（不写 `deleted_at`）。
 * 条目仍在池里，下次执行仍复用同一 `subSessionId`（KV-cache 靠这个）。
 */
export async function sleepSubAgentPoolEntry(name: string): Promise<void> {
  await request(`${SUB_AGENT_POOL_ENDPOINT}/${encodeURIComponent(name)}/sleep`, {
    method: 'POST',
  });
}

/**
 * 销毁池条目：库迁 `dead` 并写 `deleted_at`。
 * 名字随后可被新条目占用；若条目正在执行，后端会级联取消。
 */
export async function destroySubAgentPoolEntry(name: string): Promise<void> {
  await request(`${SUB_AGENT_POOL_ENDPOINT}/${encodeURIComponent(name)}`, {
    method: 'DELETE',
  });
}
