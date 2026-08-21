// ── CU-04: ExecutionFlowProjector 单元测试 ─────────────────────────────────
// 验收场景（split-plan CU-04 验收标准 1~4）：
//  1. bootstrap/gap/live 重放等价（同事件集三输入路径输出相同 ViewModel）；
//  2. 乱序事件 / 重复 eventId / 消息与工具顺序 / parentToolCallId 树 /
//     终态单调 / reasoning 合并均有用例；
//  3. 纯函数、无副作用（不触 DOM/Store/时间源）；
//  4. MessageProcessSummary 的 thinking 合并算法抽到 projector 后行为一致。
import {
  mergeReasoningBlocks,
  projectExecutionFlow,
  type ExecutionFlowEvent,
  type ReasoningNode,
  type ToolNode,
} from './executionFlowProjector';
import { sanitizeProcessText } from '../components/processPreview';

const OCCURRED_AT = '2026-08-21T08:00:00.000Z';

/** 构造冻结 canonical DTO 事件（sequence 同时驱动 eventId 派生，保持确定性）。 */
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

const nodeKinds = (proj: ReturnType<typeof projectExecutionFlow>): string[] =>
  proj.nodes.map((node) => node.kind);

describe('projectExecutionFlow', () => {
  describe('重放等价（bootstrap / gap replay / live 同一事实集）', () => {
    it('同事件集不同到达顺序输出深度一致', () => {
      const events: ExecutionFlowEvent[] = [
        ev('message.thinking_summary.appended', 1, { delta: '先分析' }),
        ev('message.thinking_summary.appended', 2, { delta: '用户意图' }),
        ev('tool.call.requested', 3, {
          toolCallId: 't1',
          name: 'search',
          arguments: '{"q":"x"}',
        }),
        ev('tool.call.completed', 4, {
          toolCallId: 't1',
          name: 'search',
          exitCode: 0,
          output: 'hit',
        }),
        ev('message.content.appended', 5, { delta: '答案' }),
        ev('message.content.appended', 6, { delta: '内容' }),
        ev('message.completed', 7, { reply: '答案内容' }),
        ev('turn.completed', 8, { reply: '答案内容' }),
      ];
      const canonical = projectExecutionFlow(events);
      const reversed = projectExecutionFlow([...events].reverse());
      const shuffled = projectExecutionFlow([
        events[3], events[0], events[7], events[2],
        events[5], events[1], events[6], events[4],
      ]);
      expect(reversed.nodes).toEqual(canonical.nodes);
      expect(shuffled.nodes).toEqual(canonical.nodes);
      expect(reversed.terminal).toEqual(canonical.terminal);
      expect(reversed.stats.projectedEvents).toBe(canonical.stats.projectedEvents);
      expect(nodeKinds(canonical)).toEqual([
        'reasoning', 'tool', 'message', 'terminal',
      ]);
    });

    it('result 先到 / started 先到（不同到达顺序）最终投影一致', () => {
      const resultFirst: ExecutionFlowEvent[] = [
        ev('tool.call.completed', 6, {
          toolCallId: 't1', name: 'shell', exitCode: 0, output: 'out',
        }),
        ev('tool.call.requested', 5, {
          toolCallId: 't1', name: 'shell', arguments: '{"cmd":"ls"}',
        }),
      ];
      const startedFirst: ExecutionFlowEvent[] = [
        ev('tool.call.requested', 5, {
          toolCallId: 't1', name: 'shell', arguments: '{"cmd":"ls"}',
        }),
        ev('tool.call.completed', 6, {
          toolCallId: 't1', name: 'shell', exitCode: 0, output: 'out',
        }),
      ];
      expect(projectExecutionFlow(resultFirst).nodes).toEqual(
        projectExecutionFlow(startedFirst).nodes,
      );
    });
  });

  describe('乱序 / 重复 / 顺序', () => {
    it('result 先于 started（started 更高 sequence）时建占位，started 补全且不降级', () => {
      const proj = projectExecutionFlow([
        ev('tool.call.completed', 6, {
          toolCallId: 't1', name: 'shell', exitCode: 0, output: 'out',
        }),
        ev('tool.call.requested', 9, {
          toolCallId: 't1', name: 'shell', arguments: '{"cmd":"ls"}',
        }),
      ]);
      expect(proj.nodes).toHaveLength(1);
      const tool = proj.nodes[0];
      expect(tool.kind).toBe('tool');
      if (tool.kind === 'tool') {
        expect(tool.state).toBe('completed');
        expect(tool.placeholder).toBe(false);
        expect(tool.name).toBe('shell');
        expect(tool.arguments).toBe('{"cmd":"ls"}');
        expect(tool.output).toBe('out');
        expect(tool.sequence).toBe(6); // 首个来源事件（result）的 sequence
      }
    });

    it('重复 eventId 只消费一次（幂等）', () => {
      const first = ev('message.thinking_summary.appended', 1, { delta: '思考' });
      const duplicate = { ...first };
      const proj = projectExecutionFlow([first, duplicate]);
      expect(proj.stats.duplicateEvents).toBe(1);
      expect(proj.stats.projectedEvents).toBe(1);
      const reasoning = proj.nodes.find(
        (node): node is ReasoningNode => node.kind === 'reasoning',
      );
      expect(reasoning?.text).toBe('思考');
    });

    it('消息与工具顺序按 sequence 保持', () => {
      const proj = projectExecutionFlow([
        ev('message.thinking_summary.appended', 1, { delta: 'A' }),
        ev('tool.call.requested', 2, { toolCallId: 't1', name: 'x', arguments: '{}' }),
        ev('message.content.appended', 3, { delta: '正文' }),
        ev('tool.call.requested', 4, { toolCallId: 't2', name: 'y', arguments: '{}' }),
      ]);
      expect(nodeKinds(proj)).toEqual(['reasoning', 'tool', 'message', 'tool']);
    });

    it('turnId 过滤只投影指定 turn', () => {
      const proj = projectExecutionFlow(
        [
          ev('message.content.appended', 1, { delta: 'a', turnId: 'turn-1' }),
          ev('message.content.appended', 2, { delta: 'b', turnId: 'turn-2' }),
        ],
        { turnId: 'turn-2' },
      );
      const message = proj.nodes.find((node) => node.kind === 'message');
      expect(message && message.kind === 'message' ? message.text : '').toBe('b');
    });
  });  describe('parentToolCallId 调用树', () => {
    it('子调用挂载到父节点；父缺失时保持顶层', () => {
      const proj = projectExecutionFlow([
        ev('tool.call.requested', 1, { toolCallId: 'parent', name: 'delegate', arguments: '{}' }),
        ev('tool.call.requested', 2, { toolCallId: 'child', name: 'search', arguments: '{}', parentToolCallId: 'parent' }),
        ev('tool.call.completed', 3, { toolCallId: 'parent', name: 'delegate', exitCode: 0, output: 'ok' }),
        ev('tool.call.completed', 4, { toolCallId: 'child', name: 'search', exitCode: 0, output: 'hit' }),
        ev('tool.call.requested', 5, { toolCallId: 'orphan', name: 'read', arguments: '{}', parentToolCallId: 'ghost-parent' }),
      ]);
      const tools = proj.nodes.filter(
        (node): node is ToolNode => node.kind === 'tool',
      );
      expect(tools).toHaveLength(2);
      const parent = tools.find((node) => node.toolCallId === 'parent');
      expect(parent?.children.map((child) => child.toolCallId)).toEqual(['child']);
      // 顶层只剩 parent 与 orphan（child 已入树）。
      expect(tools.map((node) => node.toolCallId)).toEqual(['parent', 'orphan']);
    });
  });

  describe('终态单调', () => {
    it('turn 终态后迟到 progress 事件忽略；子代理事件保留', () => {
      const proj = projectExecutionFlow([
        ev('message.content.appended', 1, { delta: '正文' }),
        ev('turn.completed', 2, { reply: '正文' }),
        ev('message.content.appended', 3, { delta: '迟到' }),
        ev('tool.call.requested', 4, { toolCallId: 'late', name: 'x', arguments: '{}' }),
        ev('subagent.spawned', 5, { sub_agent_id: 'sa1', task: '子任务' }),
      ]);
      expect(proj.stats.ignoredAfterTerminal).toBe(2);
      expect(nodeKinds(proj)).toEqual(['message', 'terminal', 'delegation']);
    });

    it('工具节点终态后迟到 started 不降级', () => {
      const proj = projectExecutionFlow([
        ev('tool.call.completed', 1, { toolCallId: 't1', name: 'shell', exitCode: 0, output: 'ok' }),
        ev('tool.call.requested', 2, { toolCallId: 't1', name: 'shell', arguments: '{}' }),
      ]);
      expect(proj.nodes).toHaveLength(1);
      const tool = proj.nodes[0];
      expect(tool.kind).toBe('tool');
      if (tool.kind === 'tool') {
        expect(tool.state).toBe('completed');
        expect(tool.placeholder).toBe(false);
      }
    });

    it('首个 turn 终态胜出；后续终态事件不再投影', () => {
      const proj = projectExecutionFlow([
        ev('turn.completed', 1, { reply: 'ok' }),
        ev('turn.failed', 2, { errorMessage: 'late' }),
      ]);
      expect(proj.nodes.filter((node) => node.kind === 'terminal')).toHaveLength(1);
      expect(proj.terminal?.state).toBe('completed');
      expect(proj.stats.ignoredAfterTerminal).toBe(1);
    });
  });

  describe('reasoning 合并', () => {
    it('相邻 delta 合并为单个 reasoning 节点', () => {
      const proj = projectExecutionFlow([
        ev('message.thinking_summary.appended', 1, { delta: '第一步' }),
        ev('message.thinking_summary.appended', 2, { delta: '第二步' }),
      ]);
      expect(proj.nodes).toHaveLength(1);
      const reasoning = proj.nodes[0];
      expect(reasoning.kind).toBe('reasoning');
      if (reasoning.kind === 'reasoning') {
        expect(reasoning.text).toBe('第一步第二步');
        expect(reasoning.blocks).toHaveLength(1);
        expect(reasoning.blocks[0].text).toBe('第一步第二步');
      }
    });

    it('被工具事件打断的推理段形成独立节点', () => {
      const proj = projectExecutionFlow([
        ev('message.thinking_summary.appended', 1, { delta: 'A' }),
        ev('tool.call.requested', 2, { toolCallId: 't1', name: 'x', arguments: '{}' }),
        ev('message.thinking_summary.appended', 3, { delta: 'B' }),
      ]);
      const reasonings = proj.nodes.filter(
        (node): node is ReasoningNode => node.kind === 'reasoning',
      );
      expect(reasonings).toHaveLength(2);
      expect(reasonings[0].text).toBe('A');
      expect(reasonings[1].text).toBe('B');
    });

    it('超过 900 字符阈值切分为多个 block 且全文无损', () => {
      const a = '甲'.repeat(600);
      const b = '乙'.repeat(400);
      const proj = projectExecutionFlow([
        ev('message.thinking_summary.appended', 1, { delta: a }),
        ev('message.thinking_summary.appended', 2, { delta: b }),
      ]);
      const reasoning = proj.nodes[0];
      expect(reasoning.kind).toBe('reasoning');
      if (reasoning.kind === 'reasoning') {
        expect(reasoning.blocks.length).toBeGreaterThan(1);
        expect(reasoning.text).toBe(a + b);
      }
    });

    it('空 / 无意义 delta 清洗后不渲染 reasoning 行', () => {
      const proj = projectExecutionFlow([
        ev('message.thinking_summary.appended', 1, { delta: '   ' }),
        ev('message.thinking_summary.appended', 2, { delta: 'undefined' }),
      ]);
      expect(proj.nodes.filter((node) => node.kind === 'reasoning')).toHaveLength(0);
    });

    it('mergeReasoningBlocks 与 MessageProcessSummary 合并语义一致', () => {
      // 参考实现：复刻 MessageProcessSummary.buildDisplayItems 的 thinking 分组
      // （清洗 / 空跳过 / 900 阈值 / flush 时 sanitize compact:false）。
      const referenceMerge = (segments: Array<{ id: string; text: string }>) => {
        const groups: string[] = [];
        let buffer = '';
        let bufferedCount = 0;
        const flush = () => {
          const text = sanitizeProcessText(buffer, { compact: false });
          if (text && bufferedCount > 0) groups.push(text);
          buffer = '';
          bufferedCount = 0;
        };
        for (const segment of segments) {
          const text =
            typeof segment.text === 'string'
              ? segment.text
                  .replace(/(?:undefined|null|NaN)+/gi, '')
                  .split('\u0000')
                  .join('')
              : '';
          if (!text.trim()) continue;
          if (buffer.length > 0 && buffer.length + text.length > 900) flush();
          buffer += text;
          bufferedCount += 1;
        }
        flush();
        return groups;
      };

      const cases: Array<{ id: string; text: string }[]> = [
        [{ id: 'a', text: '分析' }, { id: 'b', text: '执行' }],
        [{ id: 'a', text: 'x'.repeat(500) }, { id: 'b', text: 'y'.repeat(500) }],
        [{ id: 'a', text: '   ' }, { id: 'b', text: '有效' }],
        [{ id: 'a', text: '有\u0000效' }, { id: 'b', text: 'undefined尾部' }],
      ];
      for (const segments of cases) {
        const expected = referenceMerge(segments);
        const actual = mergeReasoningBlocks(segments).map((block) => block.text);
        expect(actual).toEqual(expected);
      }
    });
  });
  describe('协议错误与纯度', () => {
    it('工具事件缺 toolCallId：记录协议错误且不渲染工具行', () => {
      const proj = projectExecutionFlow([
        ev('tool.call.completed', 1, { name: 'shell', exitCode: 0, output: 'x' }),
      ]);
      expect(proj.nodes.filter((node) => node.kind === 'tool')).toHaveLength(0);
      expect(proj.stats.protocolErrors).toBe(1);
      expect(proj.protocolErrors[0].reason).toBe('missing-tool-call-id');
    });

    it('缺 eventId：记录协议错误但事件仍被投影', () => {
      const noId = {
        sequence: 1,
        occurredAt: OCCURRED_AT,
        runId: 'run-1',
        turnId: 'turn-1',
        type: 'message.content.appended',
        delta: 'x',
      } as unknown as ExecutionFlowEvent;
      const proj = projectExecutionFlow([noId]);
      expect(proj.stats.protocolErrors).toBe(1);
      expect(proj.protocolErrors[0].reason).toBe('missing-event-id');
      expect(proj.nodes.filter((node) => node.kind === 'message')).toHaveLength(1);
    });

    it('纯函数：重复调用结果一致且不修改输入', () => {
      const events: ExecutionFlowEvent[] = [
        ev('message.thinking_summary.appended', 1, { delta: 'A' }),
        ev('tool.call.requested', 2, { toolCallId: 't1', name: 'x', arguments: '{}' }),
        ev('tool.call.completed', 3, { toolCallId: 't1', name: 'x', exitCode: 0, output: 'ok' }),
      ];
      const snapshot = JSON.parse(JSON.stringify(events)) as ExecutionFlowEvent[];
      const first = projectExecutionFlow(events);
      const second = projectExecutionFlow(events);
      expect(second.nodes).toEqual(first.nodes);
      expect(second.stats).toEqual(first.stats);
      expect(events).toEqual(snapshot);
    });
  });

  describe('message 节点', () => {
    it('content 增量合并，completed 终态落定', () => {
      const proj = projectExecutionFlow([
        ev('message.content.appended', 1, { delta: '你' }),
        ev('message.content.appended', 2, { delta: '好' }),
        ev('message.completed', 3, { reply: '你好' }),
      ]);
      const message = proj.nodes.find((node) => node.kind === 'message');
      expect(message && message.kind === 'message' ? message.text : '').toBe('你好');
      expect(message && message.kind === 'message' ? message.terminal : '').toBe('completed');
    });

    it('message.failed 终态携带错误信息', () => {
      const proj = projectExecutionFlow([
        ev('message.failed', 1, { errorMessage: 'upstream timeout' }),
      ]);
      const message = proj.nodes.find((node) => node.kind === 'message');
      expect(message && message.kind === 'message' ? message.terminal : '').toBe('failed');
      expect(
        message && message.kind === 'message' ? message.errorMessage : '',
      ).toBe('upstream timeout');
    });
  });

  describe('delegation 节点', () => {
    it('spawned → running，completed(success) → completed 并携带摘要', () => {
      const proj = projectExecutionFlow([
        ev('subagent.spawned', 1, { sub_agent_id: 'sa1', model: 'deepseek', task: '检索文档' }),
        ev('subagent.completed', 2, { sub_agent_id: 'sa1', success: true, result_summary: '完成' }),
      ]);
      const delegation = proj.nodes.find((node) => node.kind === 'delegation');
      expect(delegation && delegation.kind === 'delegation' ? delegation.state : '').toBe('completed');
      expect(delegation && delegation.kind === 'delegation' ? delegation.taskSummary : '').toBe('检索文档');
      expect(delegation && delegation.kind === 'delegation' ? delegation.replySummary : '').toBe('完成');
      expect(delegation && delegation.kind === 'delegation' ? delegation.model : '').toBe('deepseek');
    });

    it('completed(success=false) → failed', () => {
      const proj = projectExecutionFlow([
        ev('subagent.spawned', 1, { sub_agent_id: 'sa2' }),
        ev('subagent.completed', 2, { sub_agent_id: 'sa2', success: false, error: 'boom' }),
      ]);
      const delegation = proj.nodes.find((node) => node.kind === 'delegation');
      expect(delegation && delegation.kind === 'delegation' ? delegation.state : '').toBe('failed');
      expect(delegation && delegation.kind === 'delegation' ? delegation.error : '').toBe('boom');
    });
  });

  describe('retry 节点', () => {
    it('LLM retry 形态 subconscious_step 投影为 retry 节点', () => {
      const proj = projectExecutionFlow([
        ev('subconscious_step', 1, { status: 'loading', message: 'LLM call retry 2/3. upstream timeout' }),
      ]);
      expect(proj.nodes).toHaveLength(1);
      const retry = proj.nodes[0];
      expect(retry.kind).toBe('retry');
      if (retry.kind === 'retry') {
        expect(retry.attempt).toBe(2);
        expect(retry.maxRetries).toBe(3);
        expect(retry.reasonSummary).toContain('retry');
      }
    });

    it('非 retry 形态 subconscious_step 不投影为节点', () => {
      const proj = projectExecutionFlow([
        ev('subconscious_step', 1, { status: 'thinking', message: '整理上下文' }),
      ]);
      expect(proj.nodes).toHaveLength(0);
    });
  });

  describe('terminal 节点', () => {
    it('turn.failed → terminal failed + errorMessage', () => {
      const proj = projectExecutionFlow([
        ev('turn.failed', 1, { errorMessage: 'rate limited' }),
      ]);
      expect(proj.terminal?.state).toBe('failed');
      expect(proj.terminal?.errorMessage).toBe('rate limited');
      expect(nodeKinds(proj)).toEqual(['terminal']);
    });

    it('turn.cancelled → terminal cancelled', () => {
      const proj = projectExecutionFlow([
        ev('turn.cancelled', 1, { message: 'user cancelled' }),
      ]);
      expect(proj.terminal?.state).toBe('cancelled');
      expect(proj.terminal?.message).toBe('user cancelled');
    });
  });
});
