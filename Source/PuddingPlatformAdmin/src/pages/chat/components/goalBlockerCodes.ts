// ── Goal 受阻码 → 中文结构化说明（前端常量，不发网络请求）────────────
// 后端 GoalSnapshot.BlockedCode 的展示映射。约定：
// - 已知码：显示中文说明 + 建议动作；
// - 未知码：给通用兜底文案，且必须把原始码一并显示（诊断可追溯）；
// - 码缺失：不渲染受阻卡片，回退为后端原因文本（GoalBanner 兜底）。

export interface GoalBlockerDescriptor {
  /** 受阻原因中文说明（≤60 字） */
  title: string;
  /** 建议动作中文说明（≤60 字） */
  action: string;
  /** 该码是否默认意味着需要用户决策 */
  needsUser: boolean;
}

export const GOAL_BLOCKER_CODES: Record<string, GoalBlockerDescriptor> = {
  no_progress_circuit_open: {
    title: '无进展熔断：本轮未产生可验证的进展，且本回合无可用重规划额度',
    action: '重启宿主或人工介入后恢复；若目标已达成可直接停止',
    needsUser: true,
  },
};

/** 未知受阻码的通用兜底（展示时必须连同原始码一起显示）。 */
export const UNKNOWN_GOAL_BLOCKER: GoalBlockerDescriptor = {
  title: '目标受阻，原因码未被前端识别',
  action: '依据受阻码与后端原因判断是否需要人工介入',
  needsUser: true,
};

/** 解析受阻码；空码返回 null（调用方回退为后端原因文本）。 */
export const describeGoalBlocker = (
  code: string | null | undefined,
): GoalBlockerDescriptor | null => {
  const key = (code ?? '').trim();
  if (!key) return null;
  return GOAL_BLOCKER_CODES[key] ?? UNKNOWN_GOAL_BLOCKER;
};

/** 是否为前端已登记的受阻码（未知码需以「未知受阻码：<code>」展示）。 */
export const isKnownGoalBlockerCode = (code: string | null | undefined) => {
  const key = (code ?? '').trim();
  return key.length > 0 && Object.hasOwn(GOAL_BLOCKER_CODES, key);
};
