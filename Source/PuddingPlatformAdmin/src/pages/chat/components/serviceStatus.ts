// ── 服务态映射（纯函数）：Composer 运行摘要里的四个服务信号 ──────────────
// 背景：`IntentConsole` 曾把这四个值**硬编码**为 contextService:'available' /
// index:'disabled' / backgroundMemory:'idle' / modelService:'available'，
// 于是「没采到」被显示成「可用/未启用」这种假装知道的事实（诊断报告 §5–§6）。
//
// 纪律（与 index-status 的三态纪律同源）：
//   · **没采到数据一律 `unknown`**，绝不回落成 `available` / `disabled`；
//   · 每个信号只有一个真源，全部由**已有**端点推导，不新增后端端点：
//       contextService   ← GET /api/sessions/{id}/context-health（state）
//       index            ← GET /api/admin/index/status + deriveIndexHealth
//       backgroundMemory ← GET /api/debug/subconscious/debug（state: running/paused）
//       modelService     ← GET /api/llm/providers（isEnabled && hasApiKey）
//   · 入参一律是 `Promise.allSettled` 的结果本身 ⇒ 「请求失败」与「还没请求」在类型上可分，
//     且这些函数可离线单测（不 mock 网络）。
//
// 纪律延伸（2026-10-07 诊断 §4.2）：「容量压力」不是「服务故障」。
//   有效输入窗口压力（默认 75% 阈值）说明**上下文该整理了**，它没有证明网络/API/
//   上下文服务出错；把它显示成 `error`（旧文案「上下文服务异常」）会让用户去排查
//   一个并未失败的服务。因此 Unhealthy/Critical/Blocking 一律映射为 `capacity`，
//   `'error'` 在上下文健康度这条链路上**不可达**（保留在联合类型里是因为索引/模型
//   服务等其它信号确实有真实的失败证据）；采集失败保持 `unknown`，既不是 `error`
//   也不是 `capacity` —— 「没采到」不证明故障，也不证明有压力。
import {
  deriveIndexHealth,
  type IndexHealthLevel,
} from '@/pages/index-status/health';
import type { FullTextIndexStatusSnapshot } from '@/pages/index-status/types';
import type {
  ContextHealthSnapshot,
  LlmProviderDto,
  SubconsciousRuntimeControlSnapshotDto,
} from '@/services/platform/api';

/**
 * 上下文服务取值域。`warning`（需注意）与 `unknown`（未采集）是新增的如实取值。
 *
 * `capacity`（容量压力）与 `error`（服务故障）必须分开：前者是「上下文用满了，
 * 建议整理」，后者才是「服务/采集出了问题」。`error` 保留在联合类型里，供索引、
 * 模型服务等**有真实失败证据**的信号使用；上下文健康度这条链路不再产出它。
 */
export type ContextServiceStatus =
  | 'available'
  | 'warning'
  | 'capacity'
  | 'idle'
  | 'disabled'
  | 'error'
  | 'unknown';

/** 索引取值域。`off`（配置明确关闭）⇒ disabled；`warn` ⇒ warning；采不到 ⇒ unknown。 */
export type IndexServiceStatus =
  | 'available'
  | 'building'
  | 'warning'
  | 'disabled'
  | 'error'
  | 'unknown';

/** 后台记忆整理取值域。后端只有 running/paused，其余（含 404）⇒ unknown。 */
export type BackgroundMemoryStatus =
  | 'idle'
  | 'running'
  | 'disabled'
  | 'error'
  | 'unknown';

/** 模型服务取值域（没有健康端点，只能按 provider 可用性推导）。 */
export type ModelServiceStatus = 'available' | 'warning' | 'error' | 'unknown';

/** 四个服务信号的组合（`ComposerRuntimeSummary` 的直接来源）。 */
export interface RuntimeServiceSignals {
  contextService: ContextServiceStatus;
  index: IndexServiceStatus;
  backgroundMemory: BackgroundMemoryStatus;
  modelService: ModelServiceStatus;
}

/**
 * 「全部未知」基线：请求还没发生、或刷新整体失败时使用。
 * 这是**唯一**允许的兜底值 —— 兜底成 `available` 就是把未知读成健康。
 */
export const UNKNOWN_SERVICE_SIGNALS: RuntimeServiceSignals = {
  contextService: 'unknown',
  index: 'unknown',
  backgroundMemory: 'unknown',
  modelService: 'unknown',
};

/**
 * 上下文健康态 → 摘要取值域。
 *
 * Healthy → 可用 · Warning → 需注意 · Unhealthy/Critical/Blocking → **容量压力**
 * （`capacity`，文案「建议整理上下文」）· 未知/缺失 → `unknown`。
 *
 * 这里**绝不**返回 `'error'`：诊断报告 §4.2 的 76.4% 有效输入压力只是容量信号，
 * 不构成服务故障证据；把它标成故障会让用户去排查一个并没有坏的服务。
 */
export function mapContextHealthState(
  state: ContextHealthSnapshot['state'] | undefined,
): ContextServiceStatus {
  switch (state) {
    case 'Healthy':
      return 'available';
    case 'Warning':
      return 'warning';
    case 'Unhealthy':
    case 'Critical':
    case 'Blocking':
      return 'capacity';
    default:
      return 'unknown';
  }
}

/**
 * contextService：只认**已经到手**的 `contextHealth.state`。
 *
 * 请求被拒（含 409 `context_window_unresolved`：当前会话的窗口暂时解析不出来，
 * 那不是上下文服务的故障）或还没数据 ⇒ 一律 `unknown`；
 * 采到的 Unhealthy/Critical/Blocking ⇒ `capacity`（容量压力，不是故障）。
 * 本函数**不可能**返回 `'error'`：没有采到任何「服务失败」的证据。
 */
export function deriveContextServiceStatus(
  result: PromiseSettledResult<ContextHealthSnapshot> | undefined,
): ContextServiceStatus {
  if (!result || result.status !== 'fulfilled') return 'unknown';
  return mapContextHealthState(result.value?.state);
}

/**
 * 「窗口里不可用于输入的那部分」的来源。
 *
 * 实现已移到叶子模块 `./inputUnavailable`（`ContextUsageRing` 只需要这个文案映射，
 * 不该为此继承本文件通往 `index-status` 与 umi 应用链的依赖）。此处**重新导出**，
 * 保持既有引用与测试不破。
 */
export {
  describeInputUnavailableReason,
  type InputUnavailableReason,
} from './inputUnavailable';

/**
 * 索引健康层级 → 摘要取值域。
 * `off` 是**配置明确关闭**（`enabled === false`），所以 `disabled` 在这里是事实而不是猜测；
 * 探测失败走 `unknown` 分支，绝不落到 `disabled`。
 */
export function mapIndexHealthLevel(level: IndexHealthLevel): IndexServiceStatus {
  switch (level) {
    case 'ok':
      return 'available';
    case 'busy':
      return 'building';
    case 'warn':
      return 'warning';
    case 'off':
      return 'disabled';
    case 'error':
      return 'error';
    default:
      return 'unknown';
  }
}

/**
 * index：`GET /api/admin/index/status` 需要 admin JWT，非管理员 / 网络失败 / 还没请求
 * ⇒ `unknown`（**绝不** `available`，也**绝不** `disabled`）。
 */
export function deriveIndexServiceStatus(
  result: PromiseSettledResult<FullTextIndexStatusSnapshot> | undefined,
  nowMs: number = Date.now(),
): IndexServiceStatus {
  if (!result || result.status !== 'fulfilled') return 'unknown';
  return mapIndexHealthLevel(deriveIndexHealth(result.value, nowMs).level);
}

/**
 * backgroundMemory：`GET /api/debug/subconscious/debug`。
 * 后端 `state` 只有 `running` / `paused` 两个取值：
 *   running → running（正在整理）· paused → idle（待机）。
 * 未知取值 / 请求失败 / 404（Debug API 关闭）⇒ `unknown` —— **不是** `disabled`：
 * 「调试端点关了」不证明「潜意识整理关了」。
 */
export function deriveBackgroundMemoryStatus(
  result: PromiseSettledResult<SubconsciousRuntimeControlSnapshotDto> | undefined,
): BackgroundMemoryStatus {
  if (!result || result.status !== 'fulfilled') return 'unknown';
  const state = result.value?.state;
  if (state === 'running') return 'running';
  if (state === 'paused') return 'idle';
  return 'unknown';
}

/**
 * modelService：**没有**健康端点，只能按「是否存在真正可用的 provider」推导：
 *   至少一个 `isEnabled && hasApiKey` ⇒ available
 *   请求成功但没有任何可用 provider（含空列表）⇒ warning
 *   请求失败 ⇒ error
 *   还没数据 / 响应不是数组 ⇒ unknown
 */
export function deriveModelServiceStatus(
  result: PromiseSettledResult<LlmProviderDto[]> | undefined,
): ModelServiceStatus {
  if (!result) return 'unknown';
  if (result.status !== 'fulfilled') return 'error';
  const providers = result.value;
  if (!Array.isArray(providers)) return 'unknown';
  return providers.some(
    (provider) => provider?.isEnabled === true && provider?.hasApiKey === true,
  )
    ? 'available'
    : 'warning';
}
