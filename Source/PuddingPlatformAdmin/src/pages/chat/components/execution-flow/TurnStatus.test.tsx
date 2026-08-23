// ── CU-05: TurnStatus 单行运行态测试 ───────────────────────────────────────
// 验收（split-plan CU-05 验收标准 1/3/4）：
//  - 首事件未到达时只显示一个 TurnStatus，无伪造推理文本
//  - 15s 前后计时显隐正确，刷新（重挂载）不归零
//  - 单 aria-live；终态到达立即移除；阶段文案来自已知事实
import { render, screen } from '@testing-library/react';
import * as React from 'react';
import {
  type ExecutionFlowEvent,
  projectExecutionFlow,
} from '../../projections/executionFlowProjector';
import {
  deriveTurnStatusFromFacts,
  deriveTurnStatusFromProjection,
  TURN_STATUS_PHASE_COPY,
  TurnStatus,
} from './TurnStatus';

const OCCURRED_AT = '2026-08-22T08:00:00.000Z';

/** 构造冻结 canonical DTO 事件（与 projector.test 同模式）。 */
function ev(
  type: string,
  seq: number,
  over: Record<string, unknown> = {},
): ExecutionFlowEvent {
  return {
    eventId: `e${seq}`,
    sequence: seq,
    occurredAt: OCCURRED_AT,
    runId: 'run-1',
    turnId: 'turn-1',
    type,
    ...over,
  } as ExecutionFlowEvent;
}

describe('deriveTurnStatusFromProjection（canonical 派生）', () => {
  it('无节点无终态 → pending', () => {
    const projection = projectExecutionFlow([]);
    expect(deriveTurnStatusFromProjection(projection)).toEqual({
      kind: 'pending',
    });
  });

  it('终态 completed → succeeded + terminalEventId', () => {
    const projection = projectExecutionFlow([
      ev('turn.completed', 1, { reply: 'ok' }),
    ]);
    const status = deriveTurnStatusFromProjection(projection);
    expect(status.kind).toBe('succeeded');
    expect(status.terminalEventId).toBe('e1');
  });

  it('终态 failed → failed；cancelled → cancelled', () => {
    const failed = projectExecutionFlow([
      ev('turn.failed', 1, { errorMessage: 'boom' }),
    ]);
    expect(deriveTurnStatusFromProjection(failed).kind).toBe('failed');
    const cancelled = projectExecutionFlow([
      ev('turn.cancelled', 1, { message: 'user' }),
    ]);
    expect(deriveTurnStatusFromProjection(cancelled).kind).toBe('cancelled');
  });

  it('末节点 kind → 对应阶段（reasoning/tool/delegation/message）', () => {
    expect(
      deriveTurnStatusFromProjection(
        projectExecutionFlow([
          ev('message.thinking_summary.appended', 1, { delta: '想' }),
        ]),
      ),
    ).toEqual({ kind: 'running', phase: 'reasoning' });
    expect(
      deriveTurnStatusFromProjection(
        projectExecutionFlow([
          ev('tool.call.requested', 1, {
            toolCallId: 't1',
            name: 'shell',
            arguments: '{}',
          }),
        ]),
      ),
    ).toEqual({ kind: 'running', phase: 'executing' });
    expect(
      deriveTurnStatusFromProjection(
        projectExecutionFlow([
          ev('subagent.spawned', 1, { subAgentId: 'sa1' }),
        ]),
      ),
    ).toEqual({ kind: 'running', phase: 'delegating' });
    expect(
      deriveTurnStatusFromProjection(
        projectExecutionFlow([
          ev('message.content.appended', 1, { delta: '答' }),
        ]),
      ),
    ).toEqual({ kind: 'running', phase: 'answering' });
  });

  it('retry 节点 → connecting（等待/重连模型）', () => {
    const projection = projectExecutionFlow([
      ev('subconscious_step', 1, {
        message: 'LLM call retry（attempt 1/3）',
      }),
    ]);
    expect(deriveTurnStatusFromProjection(projection)).toEqual({
      kind: 'running',
      phase: 'connecting',
    });
  });
});

describe('deriveTurnStatusFromFacts（消费点事实派生）', () => {
  it('inactive → succeeded（终态：TurnStatus 不渲染）', () => {
    expect(
      deriveTurnStatusFromFacts({ active: false, hasVisibleEvents: false }),
    ).toEqual({ kind: 'succeeded' });
  });

  it('active 且无可见事件 → pending', () => {
    expect(
      deriveTurnStatusFromFacts({ active: true, hasVisibleEvents: false }),
    ).toEqual({ kind: 'pending' });
  });

  it('active 有可见事件 → running + phase；phase 缺失回落 connecting', () => {
    expect(
      deriveTurnStatusFromFacts({
        active: true,
        hasVisibleEvents: true,
        phase: 'executing',
      }),
    ).toEqual({ kind: 'running', phase: 'executing' });
    expect(
      deriveTurnStatusFromFacts({ active: true, hasVisibleEvents: true }),
    ).toEqual({ kind: 'running', phase: 'connecting' });
  });
});

describe('TurnStatus 组件', () => {
  const NOW = new Date('2026-08-22T08:00:00.000Z').getTime();

  it('pending：显示「{agentName} 正在运行」且无计时（<15s）', () => {
    render(
      <TurnStatus
        status={{ kind: 'pending' }}
        turnStartedAt={NOW - 5_000}
        agentName="Pudding"
        now={NOW}
      />,
    );
    expect(screen.getByText('Pudding 正在运行')).toBeTruthy();
    expect(screen.queryByTestId('turn-status-elapsed')).toBeNull();
    expect(screen.getByTestId('turn-status').getAttribute('aria-live')).toBe(
      'polite',
    );
  });

  it('pending：默认 agentName = 默认助手', () => {
    render(
      <TurnStatus status={{ kind: 'pending' }} turnStartedAt={NOW} now={NOW} />,
    );
    expect(screen.getByText('默认助手 正在运行')).toBeTruthy();
  });

  it('running：阶段文案来自已知事实（五类）', () => {
    const { rerender } = render(
      <TurnStatus
        status={{ kind: 'running', phase: 'connecting' }}
        turnStartedAt={NOW}
        now={NOW}
      />,
    );
    expect(screen.getByText(TURN_STATUS_PHASE_COPY.connecting)).toBeTruthy();
    rerender(
      <TurnStatus
        status={{ kind: 'running', phase: 'reasoning' }}
        turnStartedAt={NOW}
        now={NOW}
      />,
    );
    expect(screen.getByText(TURN_STATUS_PHASE_COPY.reasoning)).toBeTruthy();
    rerender(
      <TurnStatus
        status={{ kind: 'running', phase: 'executing' }}
        turnStartedAt={NOW}
        now={NOW}
      />,
    );
    expect(screen.getByText(TURN_STATUS_PHASE_COPY.executing)).toBeTruthy();
    rerender(
      <TurnStatus
        status={{ kind: 'running', phase: 'delegating' }}
        turnStartedAt={NOW}
        now={NOW}
      />,
    );
    expect(screen.getByText(TURN_STATUS_PHASE_COPY.delegating)).toBeTruthy();
    rerender(
      <TurnStatus
        status={{ kind: 'running', phase: 'answering' }}
        turnStartedAt={NOW}
        now={NOW}
      />,
    );
    expect(screen.getByText(TURN_STATUS_PHASE_COPY.answering)).toBeTruthy();
    // 不展示推断文案
    expect(screen.queryByText(/复杂推理|深入分析/)).toBeNull();
  });

  it('计时：<15s 隐藏，≥15s 显示；重挂载（刷新）不归零', () => {
    const startedAt = NOW - 10_000;
    const { rerender, unmount } = render(
      <TurnStatus
        status={{ kind: 'pending' }}
        turnStartedAt={startedAt}
        agentName="Pudding"
        now={NOW}
      />,
    );
    expect(screen.queryByTestId('turn-status-elapsed')).toBeNull();

    // ≥15s：同一持久化起点，now 前移 20s → 显示「已等待 30s」
    rerender(
      <TurnStatus
        status={{ kind: 'pending' }}
        turnStartedAt={startedAt}
        agentName="Pudding"
        now={NOW + 20_000}
      />,
    );
    expect(screen.getByTestId('turn-status-elapsed').textContent).toContain(
      '已等待 30s',
    );

    // 刷新不归零：卸载后重新挂载（相同 turnStartedAt）时钟仍在
    rerender(
      <TurnStatus
        status={{ kind: 'pending' }}
        turnStartedAt={startedAt}
        agentName="Pudding"
        now={NOW + 40_000}
      />,
    );
    expect(screen.getByTestId('turn-status-elapsed').textContent).toContain(
      '已等待 50s',
    );
    unmount();
    render(
      <TurnStatus
        status={{ kind: 'pending' }}
        turnStartedAt={startedAt}
        agentName="Pudding"
        now={NOW + 40_000}
      />,
    );
    expect(screen.getByTestId('turn-status-elapsed').textContent).toContain(
      '已等待 50s',
    );
  });

  it('计时格式：≥60s 显示整分钟（Xm）', () => {
    render(
      <TurnStatus
        status={{ kind: 'pending' }}
        turnStartedAt={NOW - 600_000}
        agentName="Pudding"
        now={NOW}
      />,
    );
    expect(screen.getByTestId('turn-status-elapsed').textContent).toContain(
      '已等待 10m',
    );
  });

  it('answering 阶段计时文案为「已运行」（生成中而非等待）', () => {
    render(
      <TurnStatus
        status={{ kind: 'running', phase: 'answering' }}
        turnStartedAt={NOW - 30_000}
        agentName="Pudding"
        now={NOW}
      />,
    );
    expect(screen.getByTestId('turn-status-elapsed').textContent).toContain(
      '已运行 30s',
    );
  });

  it('终态（succeeded/failed/cancelled）立即不渲染', () => {
    const { container } = render(
      <TurnStatus
        status={{ kind: 'succeeded' }}
        turnStartedAt={NOW}
        agentName="Pudding"
        now={NOW}
      />,
    );
    expect(container.textContent).toBe('');
  });

  it('单 aria-live 区域（同一时刻仅一个 polite 播报区）', () => {
    render(
      <TurnStatus
        status={{ kind: 'running', phase: 'reasoning' }}
        turnStartedAt={NOW}
        now={NOW}
      />,
    );
    const liveRegions = document.querySelectorAll('[aria-live="polite"]');
    expect(liveRegions.length).toBe(1);
    expect(liveRegions[0].textContent).toContain('正在推理');
  });

  it('无伪造推理文本：首事件未到达只有 pending 文案', () => {
    render(
      <TurnStatus
        status={{ kind: 'pending' }}
        turnStartedAt={NOW}
        agentName="Pudding"
        now={NOW}
      />,
    );
    expect(document.body.textContent).toContain('Pudding 正在运行');
    expect(document.body.textContent).not.toMatch(
      /正在推理|深入分析|复杂推理|正在请求模型/,
    );
  });
});
