// ── 服务态映射单测：重点是「采不到 ⇒ unknown」，绝不回落成 available/disabled ──
import type { FullTextIndexStatusSnapshot } from '@/pages/index-status/types';
import type {
  ContextHealthSnapshot,
  LlmProviderDto,
  SubconsciousRuntimeControlSnapshotDto,
} from '@/services/platform/api';
import {
  deriveBackgroundMemoryStatus,
  deriveContextServiceStatus,
  deriveIndexServiceStatus,
  deriveModelServiceStatus,
  describeInputUnavailableReason,
  mapContextHealthState,
  mapIndexHealthLevel,
} from './serviceStatus';

const contextSnapshot = (
  state: ContextHealthSnapshot['state'],
): ContextHealthSnapshot =>
  ({ sessionId: 's-1', state }) as unknown as ContextHealthSnapshot;

/** `enabled === false` 的快照：deriveIndexHealth 第 1 行命中 ⇒ off ⇒ disabled。 */
const offIndexSnapshot = {
  generatedAtUtc: '2026-10-01T05:00:00Z',
  fullText: { enabled: false },
} as unknown as FullTextIndexStatusSnapshot;

const provider = (overrides: Partial<LlmProviderDto> = {}): LlmProviderDto =>
  ({
    id: 1,
    providerId: 'deepseek',
    name: 'DeepSeek',
    baseUrl: 'https://api.deepseek.com',
    hasApiKey: true,
    isEnabled: true,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
    ...overrides,
  }) as LlmProviderDto;

describe('serviceStatus · contextService', () => {
  it('maps only real health states, and treats every unavailable signal as unknown', () => {
    expect(mapContextHealthState('Healthy')).toBe('available');
    expect(mapContextHealthState('Warning')).toBe('warning');
    // 诊断 §4.2：容量压力（≥75% 有效输入）是「建议整理上下文」，不是服务故障。
    expect(mapContextHealthState('Unhealthy')).toBe('capacity');
    expect(mapContextHealthState('Critical')).toBe('capacity');
    expect(mapContextHealthState('Blocking')).toBe('capacity');
    expect(mapContextHealthState(undefined)).toBe('unknown');
  });

  it('never maps any context health value to a service error', () => {
    // `'error'` 语义是「有失败证据的服务故障」；上下文健康度只报容量，报不了故障。
    const states: Array<ContextHealthSnapshot['state'] | undefined> = [
      'Healthy',
      'Warning',
      'Unhealthy',
      'Critical',
      'Blocking',
      undefined,
    ];
    for (const state of states) {
      expect(mapContextHealthState(state)).not.toBe('error');
    }
    // 采集失败也只能是「未知」：既不是故障，也不是容量压力。
    const rejected: PromiseSettledResult<ContextHealthSnapshot> = {
      status: 'rejected',
      reason: new Error('boom'),
    };
    expect(deriveContextServiceStatus(rejected)).toBe('unknown');
  });

  it('never reports available when the fetch failed or never ran', () => {
    expect(deriveContextServiceStatus(undefined)).toBe('unknown');
    expect(
      deriveContextServiceStatus({ status: 'rejected', reason: new Error('boom') }),
    ).toBe('unknown');
    // 409 context_window_unresolved（窗口暂不可解析）也不是上下文服务故障。
    expect(
      deriveContextServiceStatus({
        status: 'rejected',
        reason: { response: { status: 409 }, data: { code: 'context_window_unresolved' } },
      }),
    ).toBe('unknown');
  });

  it('reads the state from a fulfilled snapshot', () => {
    expect(
      deriveContextServiceStatus({ status: 'fulfilled', value: contextSnapshot('Healthy') }),
    ).toBe('available');
    expect(
      deriveContextServiceStatus({ status: 'fulfilled', value: contextSnapshot('Blocking') }),
    ).toBe('capacity');
  });
});

describe('serviceStatus · input-unavailable reason', () => {
  it('names the reason only when a source is actually known', () => {
    expect(describeInputUnavailableReason('reserved_output')).toBe('预留输出');
    expect(describeInputUnavailableReason('provider_input_limit')).toBe(
      'Provider 输入上限',
    );
    expect(describeInputUnavailableReason('safety_margin')).toBe('安全余量');
    // 没来源时不许猜成「预留输出」。
    expect(describeInputUnavailableReason(undefined)).toBe('不可用于输入（来源未知）');
    expect(describeInputUnavailableReason(null)).toBe('不可用于输入（来源未知）');
  });
});

describe('serviceStatus · index', () => {
  it('maps every index health level onto the summary union', () => {
    expect(mapIndexHealthLevel('ok')).toBe('available');
    expect(mapIndexHealthLevel('busy')).toBe('building');
    expect(mapIndexHealthLevel('warn')).toBe('warning');
    expect(mapIndexHealthLevel('off')).toBe('disabled');
    expect(mapIndexHealthLevel('error')).toBe('error');
    expect(mapIndexHealthLevel('unknown')).toBe('unknown');
  });

  it('reports unknown (never available/disabled) when the admin endpoint is unavailable', () => {
    expect(deriveIndexServiceStatus(undefined)).toBe('unknown');
    expect(
      deriveIndexServiceStatus({
        status: 'rejected',
        reason: { response: { status: 401 } },
      }),
    ).toBe('unknown');
  });

  it('uses the real snapshot: explicit off => disabled, probe failure => unknown', () => {
    expect(
      deriveIndexServiceStatus({ status: 'fulfilled', value: offIndexSnapshot }),
    ).toBe('disabled');
    expect(
      deriveIndexServiceStatus({
        status: 'fulfilled',
        value: { generatedAtUtc: 'x' } as unknown as FullTextIndexStatusSnapshot,
      }),
    ).toBe('unknown');
  });
});

describe('serviceStatus · backgroundMemory', () => {
  const settled = (state: string) =>
    ({
      status: 'fulfilled',
      value: { state } as SubconsciousRuntimeControlSnapshotDto,
    }) as const;

  it('maps the only two real states honestly', () => {
    expect(deriveBackgroundMemoryStatus(settled('running'))).toBe('running');
    expect(deriveBackgroundMemoryStatus(settled('paused'))).toBe('idle');
  });

  it('reports unknown for an unknown state or a 404 (debug API disabled)', () => {
    expect(deriveBackgroundMemoryStatus(settled('whatever'))).toBe('unknown');
    expect(deriveBackgroundMemoryStatus(undefined)).toBe('unknown');
    expect(
      deriveBackgroundMemoryStatus({
        status: 'rejected',
        reason: { response: { status: 404 } },
      }),
    ).toBe('unknown');
  });
});

describe('serviceStatus · modelService', () => {
  it('is available only when some provider is enabled and has a key', () => {
    expect(
      deriveModelServiceStatus({ status: 'fulfilled', value: [provider()] }),
    ).toBe('available');
    expect(
      deriveModelServiceStatus({
        status: 'fulfilled',
        value: [provider({ hasApiKey: false }), provider({ isEnabled: false })],
      }),
    ).toBe('warning');
    expect(deriveModelServiceStatus({ status: 'fulfilled', value: [] })).toBe('warning');
  });

  it('reports error on a failed request and unknown before any data', () => {
    expect(
      deriveModelServiceStatus({ status: 'rejected', reason: new Error('offline') }),
    ).toBe('error');
    expect(deriveModelServiceStatus(undefined)).toBe('unknown');
  });
});
