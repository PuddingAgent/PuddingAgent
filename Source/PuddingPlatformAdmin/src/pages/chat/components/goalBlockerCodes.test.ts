import {
  GOAL_BLOCKER_CODES,
  UNKNOWN_GOAL_BLOCKER,
  describeGoalBlocker,
  isKnownGoalBlockerCode,
} from './goalBlockerCodes';

/**
 * 后端可静态写入 `GoalSnapshot.BlockedCode` 的取值集合（2026-09-18 核对源码）：
 * - `ConservativeGoalIterationVerifier`：check_results_pending / criterion_failed /
 *   acceptance_not_verified / acceptance_contract_missing / check_contract_missing /
 *   task_blocked / task_terminal_without_completion / contract_coverage_insufficient（G92-1 S1-a）
 *   （另有动态码 `iteration_{TerminalKind}`，无法静态登记，故不列入）
 * - `GoalSettlementStore`：no_progress_circuit_open / dependency_wait
 * - 另有 `decision.ErrorCode` 透传，属运行期动态码，同样不列入。
 *
 * 该清单是"前端必须有中文说明"的守护集：后端新增静态码而未同步登记时，用户会看到
 * 「未知受阻码：xxx」这类不可诊断的兜底文案。
 * 实测教训：`check_results_pending` 曾长期缺失，导致目标真实受阻原因无法呈现。
 */
const BACKEND_STATIC_BLOCKER_CODES = [
  'no_progress_circuit_open',
  'dependency_wait',
  'check_results_pending',
  'criterion_failed',
  'acceptance_not_verified',
  'acceptance_contract_missing',
  'check_contract_missing',
  'task_blocked',
  'task_terminal_without_completion',
  'contract_coverage_insufficient',
];

describe('goalBlockerCodes', () => {
  it('登记了后端全部静态受阻码，不留未知兜底', () => {
    const missing = BACKEND_STATIC_BLOCKER_CODES.filter((c) => !isKnownGoalBlockerCode(c));
    expect(missing).toEqual([]);
  });

  it('check_results_pending 有专门说明，不落入通用兜底', () => {
    const d = describeGoalBlocker('check_results_pending');
    expect(d).not.toBeNull();
    expect(d).not.toEqual(UNKNOWN_GOAL_BLOCKER);
    expect(d!.title).toContain('检查');
    expect(d!.title).not.toContain('未被前端识别');
  });

  it('contract_coverage_insufficient 有专门说明，指向有界合同整理', () => {
    const d = describeGoalBlocker('contract_coverage_insufficient');
    expect(d).not.toBeNull();
    expect(d).not.toEqual(UNKNOWN_GOAL_BLOCKER);
    expect(d!.title).toContain('覆盖不足');
    expect(d!.action).toContain('合同整理');
    expect(d!.needsUser).toBe(false);
  });

  it('每个登记项都满足文案长度与语义约束', () => {
    // jest 30 的 expect() 不接受第二个“消息”参数（那是 vitest 扩展），
    // 因此用“过滤出不合法项 → 断言空集”的方式，失败时仍能看到具体 code。
    const offenders = Object.entries(GOAL_BLOCKER_CODES)
      .filter(([, d]) => {
        const titleOk = d.title.length > 0 && d.title.length <= 60;
        const actionOk = d.action.length > 0 && d.action.length <= 60;
        return !titleOk || !actionOk || typeof d.needsUser !== 'boolean';
      })
      .map(([code]) => code);
    expect(offenders).toEqual([]);
  });

  it('未知码仍走通用兜底，空码返回 null', () => {
    expect(describeGoalBlocker('mystery_blocker_42')).toEqual(UNKNOWN_GOAL_BLOCKER);
    expect(isKnownGoalBlockerCode('mystery_blocker_42')).toBe(false);
    expect(describeGoalBlocker('')).toBeNull();
    expect(describeGoalBlocker(null)).toBeNull();
    expect(describeGoalBlocker(undefined)).toBeNull();
  });
});
