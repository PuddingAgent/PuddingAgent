import dayjs from 'dayjs';
import type { ChatTurn } from '../types';
import { buildVirtualMessageItems } from './messageProjection';
import type { VirtualMessageItem } from './types';

const makeTurn = (
  turnId: string,
  timestamp: number,
  answer = 'answer',
): ChatTurn => ({
  turnId,
  source: {
    sourceId: 'agent-1',
    sourceType: 'agent',
    displayName: 'Agent',
    avatarColor: '#7c3aed',
    avatarEmoji: '🤖',
  },
  userMessage: {
    id: `user-${turnId}`,
    text: `Question ${turnId}`,
    timestamp,
    status: 'success',
  },
  assistant: {
    id: `assistant-${turnId}`,
    status: 'success',
    timelineItems: [],
    answerMarkdown: answer,
    isStreaming: false,
    renderMode: 'structured',
  },
});

describe('buildVirtualMessageItems', () => {
  it('creates stable message-level ids for user and agent blocks', () => {
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
    });

    expect(result.items.map((item) => item.id)).toEqual([
      `divider:${dayjs(1000).format('YYYY-MM-DD')}`,
      'message:user:user-t1:user',
      'message:agent:assistant-t1:assistant:0',
    ]);
  });

  it('keeps message keys unique when several messages share a canonical turn', () => {
    const first = makeTurn('shared-turn', 1000, 'first answer');
    first.userMessage.id = 'user-first';
    first.assistant.id = 'assistant-first';
    const second = makeTurn('shared-turn', 2000, 'second answer');
    second.userMessage.id = 'user-second';
    second.assistant.id = 'assistant-second';

    const result = buildVirtualMessageItems({
      turns: [first, second],
      agentName: 'Agent',
    });
    const ids = result.items.map((item) => item.id);

    expect(new Set(ids).size).toBe(ids.length);
    expect(ids).toEqual([
      `divider:${dayjs(1000).format('YYYY-MM-DD')}`,
      'message:user:user-first:user',
      'message:agent:assistant-first:assistant:0',
      'message:user:user-second:user',
      'message:agent:assistant-second:assistant:0',
    ]);
  });

  it('keeps an appended long-running status at the current edge of the stream', () => {
    const recent = makeTurn('recent', 2_000, 'recent answer');
    const longRunning = makeTurn('long-running', 1_000, '');
    longRunning.userMessage.text = '';
    longRunning.assistant.status = 'thinking';
    longRunning.assistant.isStreaming = true;

    const result = buildVirtualMessageItems({
      turns: [recent, longRunning],
      agentName: 'Agent',
    });

    expect(result.items.map((item) => item.id)).toEqual([
      `divider:${dayjs(2000).format('YYYY-MM-DD')}`,
      'message:user:user-recent:user',
      'message:agent:assistant-recent:assistant:0',
      'message:agent:assistant-long-running:assistant:0',
    ]);
    expect(result.lastMessageItemId).toBe(
      'message:agent:assistant-long-running:assistant:0',
    );
  });

  it('preserves system-command source identity through message projection', () => {
    const turn = makeTurn('system-turn', 1000, 'Runtime mode is now Yolo');
    turn.source = {
      sourceId: 'system',
      sourceType: 'system_command',
      displayName: 'System',
      avatarColor: '#1677ff',
      avatarEmoji: '⚙',
    };

    const result = buildVirtualMessageItems({
      turns: [turn],
      agentName: 'Agent',
    });
    const systemResponse = result.items.find(
      (item) => item.kind === 'message' && item.block.role === 'agent',
    );

    expect(systemResponse).toMatchObject({
      kind: 'message',
      block: {
        agentId: 'system',
        sourceType: 'system_command',
        agentName: 'System',
        agentAvatarEmoji: '⚙',
      },
    });
  });

  it('adds loader before messages when older history exists', () => {
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
      sessionId: 'session-1',
      hasMoreBefore: true,
    });

    expect(result.items[0]).toMatchObject({
      kind: 'loader',
      id: 'loader:before:session-1',
      direction: 'before',
    });
  });

  it('uses compact height hints for every message row when focus view is enabled', () => {
    const result = buildVirtualMessageItems({
      turns: [
        makeTurn('t1', 1000),
        makeTurn('t2', 2000, `rich answer ${'markdown '.repeat(200)}`),
      ],
      agentName: 'Agent',
      focusView: true,
    });
    const messageItems = result.items.filter(
      (item): item is Extract<VirtualMessageItem, { kind: 'message' }> =>
        item.kind === 'message',
    );
    expect(messageItems.length).toBeGreaterThan(0);
    for (const item of messageItems) {
      expect(item.heightHint).toBe('compact');
    }
  });

  it('keeps rich height hints when focus view is off', () => {
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000, `rich answer ${'markdown '.repeat(200)}`)],
      agentName: 'Agent',
      focusView: false,
    });
    const messageItems = result.items.filter(
      (item): item is Extract<VirtualMessageItem, { kind: 'message' }> =>
        item.kind === 'message',
    );
    expect(
      messageItems.some((item) => item.heightHint === 'rich'),
    ).toBe(true);
  });

  it('inserts a date divider before the first message and on each local-day change', () => {
    const threeDaysAgo = dayjs()
      .startOf('day')
      .subtract(3, 'day')
      .add(10, 'hour')
      .valueOf();
    const yesterday = dayjs()
      .startOf('day')
      .subtract(1, 'day')
      .add(10, 'hour')
      .valueOf();
    const today = dayjs().startOf('day').add(10, 'hour').valueOf();

    const result = buildVirtualMessageItems({
      turns: [
        makeTurn('t-older', threeDaysAgo),
        makeTurn('t-yesterday', yesterday),
        makeTurn('t-today', today),
      ],
      agentName: 'Agent',
    });

    expect(result.items.map((item) => item.id)).toEqual([
      `divider:${dayjs(threeDaysAgo).format('YYYY-MM-DD')}`,
      'message:user:user-t-older:user',
      'message:agent:assistant-t-older:assistant:0',
      `divider:${dayjs(yesterday).format('YYYY-MM-DD')}`,
      'message:user:user-t-yesterday:user',
      'message:agent:assistant-t-yesterday:assistant:0',
      `divider:${dayjs(today).format('YYYY-MM-DD')}`,
      'message:user:user-t-today:user',
      'message:agent:assistant-t-today:assistant:0',
    ]);
  });

  it('labels dividers as 今天 / 昨天 / MM-DD', () => {
    const older = dayjs()
      .startOf('day')
      .subtract(3, 'day')
      .add(10, 'hour')
      .valueOf();
    const yesterday = dayjs()
      .startOf('day')
      .subtract(1, 'day')
      .add(10, 'hour')
      .valueOf();
    const today = dayjs().startOf('day').add(10, 'hour').valueOf();

    const result = buildVirtualMessageItems({
      turns: [
        makeTurn('t-older', older),
        makeTurn('t-yesterday', yesterday),
        makeTurn('t-today', today),
      ],
      agentName: 'Agent',
    });

    const dividers = result.items.filter(
      (item): item is Extract<VirtualMessageItem, { kind: 'divider' }> =>
        item.kind === 'divider',
    );
    expect(dividers.map((divider) => divider.label)).toEqual([
      dayjs(older).format('MM-DD'),
      '昨天',
      '今天',
    ]);
  });

  it('does not insert a divider between messages on the same local day', () => {
    const base = dayjs().startOf('day').add(10, 'hour');
    const result = buildVirtualMessageItems({
      turns: [
        makeTurn('t1', base.valueOf()),
        makeTurn('t2', base.add(5, 'minute').valueOf()),
      ],
      agentName: 'Agent',
    });

    const dividers = result.items.filter(
      (item): item is Extract<VirtualMessageItem, { kind: 'divider' }> =>
        item.kind === 'divider',
    );
    expect(dividers).toHaveLength(1);
    expect(dividers[0]?.id).toBe(
      `divider:${dayjs(base).format('YYYY-MM-DD')}`,
    );
  });

  it('keeps the loader row before the first date divider when older history exists', () => {
    const yesterday = dayjs()
      .startOf('day')
      .subtract(1, 'day')
      .add(10, 'hour')
      .valueOf();
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', yesterday)],
      agentName: 'Agent',
      sessionId: 'session-1',
      hasMoreBefore: true,
    });

    expect(result.items[0]).toMatchObject({
      kind: 'loader',
      id: 'loader:before:session-1',
    });
    expect(result.items[1]).toMatchObject({
      kind: 'divider',
      id: `divider:${dayjs(yesterday).format('YYYY-MM-DD')}`,
      label: '昨天',
      heightHint: 'compact',
    });
  });

  it('keeps divider keys unique when the same date reappears in the sequence', () => {
    const today = dayjs().startOf('day').add(10, 'hour').valueOf();
    const yesterday = dayjs()
      .startOf('day')
      .subtract(1, 'day')
      .add(10, 'hour')
      .valueOf();
    // 活跃态重排场景：较新的日期先出现，旧日期再次出现（不重排序、保持 supplied order）。
    const result = buildVirtualMessageItems({
      turns: [
        makeTurn('t1', today),
        makeTurn('t2', yesterday),
        makeTurn('t3', today),
      ],
      agentName: 'Agent',
    });

    const dividerIds = result.items
      .filter((item) => item.kind === 'divider')
      .map((item) => item.id);
    expect(dividerIds).toEqual([
      `divider:${dayjs(today).format('YYYY-MM-DD')}`,
      `divider:${dayjs(yesterday).format('YYYY-MM-DD')}`,
      `divider:${dayjs(today).format('YYYY-MM-DD')}:2`,
    ]);
    expect(new Set(dividerIds).size).toBe(dividerIds.length);
  });
});
