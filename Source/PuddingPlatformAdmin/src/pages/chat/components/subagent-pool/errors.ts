// ── 子代理池错误文案 ────────────────────────────────────────────
// 纯函数，不依赖 React / antd，便于单测。
//
// 后端错误体有两种形状（同一个控制器里都有）：
//   { error: "destroy_rejected", detail: "..." }   ← SubAgentPoolAdminController
//   { message: "..." }                             ← 其它 admin 端点
// 所以先读 detail 再读 message，两者都没有才退回 Error.message。
//
// **不要**把 Error.message 放在前面：umi-request 的 message 通常是
// 「request error, status: 409」这类原始串，会把后端已经写好的解释盖掉；
// 操作者看到的应该是「库拒绝了状态迁移」而不是一个状态码。

const DEFAULT_ERROR_MESSAGE = '加载子代理池失败';

interface SubAgentPoolErrorBody {
  detail?: unknown;
  message?: unknown;
}

/** 只接受非空字符串；数字/对象/空串一律当作「没给」。 */
function pickText(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null;
}

export function describeSubAgentPoolError(
  error: unknown,
  fallback: string = DEFAULT_ERROR_MESSAGE,
): string {
  const body = (error as { response?: { data?: SubAgentPoolErrorBody } } | undefined)
    ?.response?.data;

  const fromBody = pickText(body?.detail) ?? pickText(body?.message);
  if (fromBody !== null) return fromBody;

  if (error instanceof Error && error.message) return error.message;

  return fallback;
}

/** 管理面动作种类，决定失败时的兜底文案。 */
export type SubAgentPoolActionKind = 'sleep' | 'destroy' | 'patch' | 'model' | 'role';

const ACTION_FALLBACK: Record<SubAgentPoolActionKind, string> = {
  sleep: '休眠失败',
  destroy: '销毁失败',
  patch: '保存备注失败',
  model: '保存模型失败',
  role: '保存角色失败',
};

/**
 * 动作失败文案：后端给了 detail 就用它，否则按动作说人话。
 *
 * 为什么不能统一回落到「加载子代理池失败」：销毁被库拒绝时提示「加载失败」，
 * 会让操作者以为列表没刷新，而不是这次销毁没生效 —— 两种情况的下一步动作完全不同。
 */
export function describeSubAgentPoolActionError(
  kind: SubAgentPoolActionKind,
  error: unknown,
): string {
  return describeSubAgentPoolError(error, ACTION_FALLBACK[kind]);
}
