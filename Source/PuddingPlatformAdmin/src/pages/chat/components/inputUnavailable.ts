// ── 「窗口里不可用于输入的那部分」的来源与文案（叶子模块） ────────────────
//
// 为什么单独成文件：这个纯展示映射必须能**零重依赖**被 `ContextUsageRing` 引用。
// 它原先住在 `serviceStatus.ts`，而后者为了推导服务信号会 import
// `@/pages/index-status/health` → `@/pages/index-status/api`，进而把整个 umi 应用链
// （`src/app.tsx` 等）拉进任何引用者的模块图。`ContextUsageRing` 只需要一个文案映射，
// 不该因此获得整条应用链依赖（实测会让 `ContextUsageRing.test.tsx` 在
// `@ant-design/v5-patch-for-react-19` 处整套 import 失败）。
//
// 归属：Chat 组件层；`serviceStatus.ts` 重新导出以保持既有引用不破。

/**
 * 「窗口里不可用于输入的那部分」的来源。
 *
 * 它**不普遍等于输出预留**：`windowLimit − effectiveLimit` 的差值还可能来自
 * Provider 输入上限或安全余量（诊断报告 2026-10-07 §4.4）。只有容量来源明确时才能按
 * 来源命名，否则必须显示成「来源未知」，不许猜成「预留输出」。
 *
 * 取值与后端 `ContextHealthSnapshot.effectiveWindowSource`（`ContextEffectiveWindowSources`）
 * **逐字对齐**：同一套词汇，前端不另造一套。
 */
export type InputUnavailableReason =
  | 'output_reserve'
  | 'provider_input_limit'
  | 'safety_margin'
  | 'output_reserve_and_safety_margin'
  | 'fallback_output_reserve';

/**
 * 后端来源字符串 → 本地已知来源；未知/缺失取值 ⇒ undefined（**不猜**，
 * 由 `describeInputUnavailableReason` 落到「来源未知」）。
 */
export function toInputUnavailableReason(
  source?: string | null,
): InputUnavailableReason | undefined {
  switch (source) {
    case 'output_reserve':
    case 'provider_input_limit':
    case 'safety_margin':
    case 'output_reserve_and_safety_margin':
    case 'fallback_output_reserve':
      return source;
    default:
      return undefined;
  }
}

/** 容量来源 → 文案；未标注来源 ⇒ 明确写「来源未知」，不冒充输出预留。 */
export function describeInputUnavailableReason(
  reason?: InputUnavailableReason | null,
): string {
  switch (reason) {
    case 'output_reserve':
      return '预留输出';
    case 'provider_input_limit':
      return 'Provider 输入上限';
    case 'safety_margin':
      return '安全余量';
    case 'output_reserve_and_safety_margin':
      return '预留输出 + 安全余量';
    case 'fallback_output_reserve':
      // 容量没给出输出预算，门禁用了内置回退预留 —— 不能说成用户配置的「预留输出」。
      return '内置回退预留（未配置输出预算）';
    default:
      return '不可用于输入（来源未知）';
  }
}

