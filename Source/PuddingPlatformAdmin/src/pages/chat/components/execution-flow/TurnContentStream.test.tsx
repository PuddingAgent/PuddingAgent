// ── TurnContentStream 组件验收（AgentTurnCard 重构 2026-08-25）─────────────
// 验收锚点：
//  - 正文段永久可见且只渲染一次；历史组默认折叠（成员 DOM 卸载，非 CSS 隐藏）；
//  - 最新行为组无论运行/完成都默认展开；尾部正文不会把刚完成的轨迹藏掉；
//    只有更新的行为组出现后，原组才自动折叠；
//  - 用户手动展开/折叠 override 粘性（新事件不抢夺）；
//  - TextBlock 一旦存在就永远参与块流，不能被 answerMarkdown 字符串关系关闭；
//  - 流式尾部段（run 活跃 && terminal='none'）标记 data-streaming。
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { projectExecutionFlow } from '../../projections/executionFlowProjector';
import TurnContentStream from './TurnContentStream';

const mockMessageItem = jest.fn((props: { markdownText: string }) => (
  <div data-testid="message-item" data-markdown={props.markdownText} />
));
jest.mock('../MessageItem', () => (props: { markdownText: string }) =>
  mockMessageItem(props),
);

const OCCURRED_AT = '2026-08-25T08:00:00.000Z';

function ev(type: string, seq: number, over: Record<string, unknown> = {}): any {
  return {
    eventId: `e${seq}`,
    sequence: seq,
    occurredAt: OCCURRED_AT,
    runId: 'run-1',
    turnId: 'turn-1',
    type,
    ...over,
  } as any;
}

/** 验收固定序列（节选）：文本A → 组(R1,T1) → 文本B → 组(R2,T2,T3) → 文本C → 组(R3,T4运行中)。 */
function fixtureEvents(extra: any[] = []): any[] {
  return [
    ev('message.content.appended', 1, { delta: '文本A' }),
    ev('message.thinking_summary.appended', 2, { delta: 'R1' }),
    ev('tool.call.requested', 3, { toolCallId: 't1', name: 'x', arguments: '{}' }),
    ev('tool.call.completed', 4, { toolCallId: 't1', name: 'x', exitCode: 0, output: 'ok' }),
    ev('message.content.appended', 5, { delta: '文本B' }),
    ev('message.thinking_summary.appended', 6, { delta: 'R2' }),
    ev('tool.call.requested', 7, { toolCallId: 't2', name: 'y', arguments: '{}' }),
    ev('tool.call.completed', 8, { toolCallId: 't2', name: 'y', exitCode: 0, output: 'ok' }),
    ev('tool.call.requested', 9, { toolCallId: 't3', name: 'z', arguments: '{}' }),
    ev('tool.call.completed', 10, { toolCallId: 't3', name: 'z', exitCode: 0, output: 'ok' }),
    ev('message.content.appended', 11, { delta: '文本C' }),
    ev('message.thinking_summary.appended', 12, { delta: 'R3' }),
    ev('tool.call.requested', 13, { toolCallId: 't4', name: 'w', arguments: '{}' }),
    ...extra,
  ];
}

const groupHeaders = () => screen.getAllByTestId('activity-group-header');
const textSegments = () => screen.getAllByTestId('turn-text-segment');

describe('TurnContentStream（内容块流）', () => {
  beforeEach(() => mockMessageItem.mockClear());

  it('正文 ⇄ 行为组交错渲染：3 正文段 + 3 组；正文只渲染一次', () => {
    render(
      <TurnContentStream
        projection={projectExecutionFlow(fixtureEvents())}
        isRunActive={false}
      />,
    );
    expect(
      screen.getAllByTestId('message-item').map((el) => el.getAttribute('data-markdown')),
    ).toEqual(['文本A', '文本B', '文本C']);
    expect(groupHeaders()).toHaveLength(3);
    // 历史组默认折叠；最新组即使 turn 已完成也保持展开。
    const groups = screen.getAllByTestId('activity-group');
    expect(groups.map((g) => g.getAttribute('data-expanded'))).toEqual([
      'false',
      'false',
      'true',
    ]);
    // 最新组成员行可见，但工具详情仍默认折叠。
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(1);
    expect(screen.queryByTestId('toolcall-expanded')).toBeNull();
  });

  it('尾部组默认展开：run 活跃时成员行挂载但长工具详情不自动展开', () => {
    render(
      <TurnContentStream
        projection={projectExecutionFlow(fixtureEvents())}
        isRunActive
      />,
    );
    const groups = screen.getAllByTestId('activity-group');
    expect(groups.map((g) => g.getAttribute('data-expanded'))).toEqual([
      'false',
      'false',
      'true',
    ]);
    expect(groups[2].getAttribute('data-tail')).toBe('true');
    // 运行中工具通过行扫光表达，IN/OUT 详情由用户显式展开。
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(1);
    expect(screen.queryByTestId('toolcall-expanded')).toBeNull();
  });

  it('组折叠卸载成员 DOM（展开→点击折叠→成员消失）', () => {
    const { container } = render(
      <TurnContentStream
        projection={projectExecutionFlow(fixtureEvents())}
        isRunActive
      />,
    );
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(1);
    fireEvent.click(screen.getAllByTestId('activity-group-header')[2]);
    expect(screen.queryAllByTestId('toolcall-row')).toHaveLength(0);
    expect(container.querySelector('[data-testid="toolcall-row-expanded"]')).toBeNull();
  });

  it('历史组可手动展开；用户 override 粘性（新正文到达不折叠用户选择）', () => {
    const { rerender } = render(
      <TurnContentStream
        projection={projectExecutionFlow(fixtureEvents())}
        isRunActive
      />,
    );
    // 用户展开历史组 1
    fireEvent.click(screen.getAllByTestId('activity-group-header')[0]);
    expect(screen.getAllByTestId('activity-group')[0].getAttribute('data-expanded')).toBe(
      'true',
    );
    // 文本D 到达：最新行为组仍展开，用户展开的历史组也保持展开。
    rerender(
      <TurnContentStream
        projection={projectExecutionFlow(
          fixtureEvents([
            ev('tool.call.completed', 14, { toolCallId: 't4', name: 'w', exitCode: 0, output: 'done' }),
            ev('message.content.appended', 15, { delta: '文本D' }),
          ]),
        )}
        isRunActive
      />,
    );
    const groups = screen.getAllByTestId('activity-group');
    expect(groups[0].getAttribute('data-expanded')).toBe('true'); // 用户 override
    expect(groups[1].getAttribute('data-expanded')).toBe('false');
    expect(groups[2].getAttribute('data-expanded')).toBe('true'); // 仍是最新行为组
    expect(groups[2].getAttribute('data-latest')).toBe('true');
    expect(screen.getAllByTestId('message-item')).toHaveLength(4);

    // 文本D 后出现新思考组：原最新组才转为历史，新组默认展开。
    rerender(
      <TurnContentStream
        projection={projectExecutionFlow(
          fixtureEvents([
            ev('tool.call.completed', 14, { toolCallId: 't4', name: 'w', exitCode: 0, output: 'done' }),
            ev('message.content.appended', 15, { delta: '文本D' }),
            ev('message.thinking_summary.appended', 16, { delta: 'R4' }),
          ]),
        )}
        isRunActive
      />,
    );
    const nextGroups = screen.getAllByTestId('activity-group');
    expect(nextGroups[0].getAttribute('data-expanded')).toBe('true'); // 用户 override
    expect(nextGroups[2].getAttribute('data-expanded')).toBe('false');
    expect(nextGroups[3].getAttribute('data-expanded')).toBe('true');
    expect(nextGroups[3].getAttribute('data-latest')).toBe('true');
  });

  it('canonical TextBlock 永远参与块流，不提供关闭正文的双路径开关', () => {
    render(
      <TurnContentStream
        projection={projectExecutionFlow(fixtureEvents())}
        isRunActive={false}
      />,
    );
    expect(screen.getAllByTestId('turn-text-segment')).toHaveLength(3);
    expect(groupHeaders()).toHaveLength(3);
  });

  it('append 新行为事件时不重渲染已封闭正文段', () => {
    const { rerender } = render(
      <TurnContentStream
        projection={projectExecutionFlow(fixtureEvents())}
        isRunActive={false}
      />,
    );
    expect(mockMessageItem).toHaveBeenCalledTimes(3);
    rerender(
      <TurnContentStream
        projection={projectExecutionFlow(
          fixtureEvents([
            ev('tool.call.completed', 14, {
              toolCallId: 't4',
              name: 'w',
              exitCode: 0,
              output: 'done',
            }),
          ]),
        )}
        isRunActive={false}
      />,
    );
    expect(mockMessageItem).toHaveBeenCalledTimes(3);
  });

  it('流式尾部段：run 活跃且 terminal=none → data-streaming；其余段静态', () => {
    render(
      <TurnContentStream
        projection={projectExecutionFlow([
          ev('message.content.appended', 1, { delta: '文本A' }),
          ev('tool.call.requested', 2, { toolCallId: 't1', name: 'x' }),
        ])}
        isRunActive
      />,
    );
    const segments = textSegments();
    expect(segments).toHaveLength(1);
    expect(segments[0].getAttribute('data-streaming')).toBe('true');
  });

  it('路径 A 回退：processItems 适配为行为组（无正文段），工具/委派/思考成组', () => {
    render(
      <TurnContentStream
        processItems={[
          { id: 'p1', type: 'thinking', text: '想一想', timestamp: 1, collapsed: false } as never,
          {
            id: 'p2', type: 'tool_call', toolCallId: 'c1', name: 'shell',
            arguments: '{}', timestamp: 2, collapsed: false,
          } as never,
          {
            id: 'p3', type: 'tool_result', toolCallId: 'c1', status: 'success',
            output: 'ok', timestamp: 3, collapsed: false,
          } as never,
        ]}
        isRunActive={false}
      />,
    );
    expect(screen.queryAllByTestId('turn-text-segment')).toHaveLength(0);
    expect(groupHeaders()).toHaveLength(1);
    expect(screen.getByTestId('activity-group-header').textContent).toContain('1 段思考');
    expect(screen.getByTestId('activity-group-header').textContent).toContain('1 次工具');
  });

  it('无可见块 → null 不占用布局', () => {
    const { container } = render(
      <TurnContentStream projection={projectExecutionFlow([])} isRunActive={false} />,
    );
    expect(container.firstElementChild).toBeNull();
  });
});
