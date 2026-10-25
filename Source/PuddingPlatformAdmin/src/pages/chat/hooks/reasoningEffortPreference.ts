/**
 * 执行偏好（思考强度）的浏览器端持久化。
 *
 * 只接受产品实际使用的两档：high（深入）/ max（极致）。
 * 其它值（包括历史虚构的 medium、产品口径不用的 low）一律视为「无保存值」，
 * 由调用方回落到默认 high —— 与后端 ReasoningEffortPolicy 的合法集合保持一致。
 *
 * key 以 agentId 结尾：多 Agent 各自记忆，互不覆盖。
 */
export type ReasoningEffortPreference = 'high' | 'max';

const KEY_PREFIX = 'pudding.chat.reasoningEffort:';

export function reasoningEffortStorageKey(agentId: string): string {
  return `${KEY_PREFIX}${agentId}`;
}

/** 读取该 Agent 保存的档位；无保存值、值非法、或存储不可用时返回 undefined。 */
export function readReasoningEffort(
  agentId: string | undefined,
): ReasoningEffortPreference | undefined {
  if (!agentId) return undefined;
  try {
    const raw = window.localStorage.getItem(reasoningEffortStorageKey(agentId));
    return raw === 'high' || raw === 'max' ? raw : undefined;
  } catch {
    // 隐私模式 / 存储被禁用：当作无保存值，不影响发送。
    return undefined;
  }
}

/** 保存档位；存储不可用时静默失败，不阻塞交互。 */
export function writeReasoningEffort(
  agentId: string | undefined,
  effort: ReasoningEffortPreference,
): void {
  if (!agentId) return;
  try {
    window.localStorage.setItem(reasoningEffortStorageKey(agentId), effort);
  } catch {
    // 同上：写失败只是丢失记忆，不应让发送变慢或报错。
  }
}
