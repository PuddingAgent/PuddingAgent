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
  check_results_pending: {
    title: '检查结果尚未产出：已声明的验收检查还没返回事实，暂时不能判定完成',
    action: '等待检查跑完；若长时间无结果，检查是否有被中断的检查租约（宿主重启会致其悬挂），必要时恢复目标',
    needsUser: true,
  },
  criterion_failed: {
    title: '必需条件未通过：有一条或多条必需验收检查失败',
    action: '按失败证据在当前工作单元内修复，然后重跑同一批检查',
    needsUser: false,
  },
  acceptance_not_verified: {
    title: '验收尚未通过：必需条件还没有同版本的通过证据',
    action: '在当前工作单元产出缺失的验收证据后重新结算',
    needsUser: false,
  },
  acceptance_contract_missing: {
    title: '缺少验收合同：该目标尚无版本化验收条件，不能凭任务状态宣告完成',
    action: '在本轮内派生版本化验收合同（必需条件 + 检查定义），然后重跑验证',
    needsUser: true,
  },
  check_contract_missing: {
    title: '缺少检查定义：已有必需条件但没有版本化检查定义，无法判定完成',
    action: '为既有必需条件声明版本化检查定义（类型、定义哈希、输入指纹）',
    needsUser: true,
  },
  task_blocked: {
    title: '绑定任务受阻：关联任务需要用户或评审者介入',
    action: '处理绑定任务上的阻塞或评审请求后恢复目标',
    needsUser: true,
  },
  task_terminal_without_completion: {
    title: '绑定任务已终止但目标未完成：任务被标记失败或取消',
    action: '确认任务终止原因，必要时重开任务或调整目标范围',
    needsUser: true,
  },
  dependency_wait: {
    title: '等待依赖：当前工作单元在等前置依赖完成',
    action: '等待依赖满足后继续；若依赖已失效需人工处理',
    needsUser: false,
  },
  contract_coverage_insufficient: {
    title: '合同覆盖不足：验收合同只覆盖工程门禁，未映射目标级必要条件',
    action: '在本迭代做一次有界合同整理：补齐版本化目标级条件与检查定义后重跑验证',
    needsUser: false,
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
