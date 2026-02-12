import type { WorkspaceAgentDto } from '@/services/platform/api';
import type { ChatTurn, SubAgentCardMap } from '../types';
import {
  buildWorkspaceStudioAgents,
  buildWorkspaceStudioSessionActivities,
  getWorkspaceStudioActiveEvent,
  getWorkspaceStudioAgentPosition,
  getWorkspaceStudioInteractionPosition,
  getWorkspaceStudioObjectDefinition,
  getWorkspaceStudioObjectStatusLabel,
  getWorkspaceStudioSpriteTextureKey,
  isWorkspaceStudioObjectActive,
  normalizeWorkspaceStudioSceneEvent,
  shouldRefreshWorkspaceStudioSessionsForNotification,
  summarizeWorkspaceStudioAgents,
  workspaceStudioObjects,
} from './workspaceStudio';
import { generatedWorkspaceAgentAvatarIds } from './workspaceStudioSprites';

const agent = (
  overrides: Partial<WorkspaceAgentDto> &
    Pick<WorkspaceAgentDto, 'agentId' | 'name'>,
): WorkspaceAgentDto => ({
  displayName: overrides.name,
  isEnabled: true,
  isFrozen: false,
  createdAt: '2026-05-25T00:00:00Z',
  updatedAt: '2026-05-25T00:00:00Z',
  ...overrides,
});

const turn = (
  status: ChatTurn['assistant']['status'],
  isStreaming = false,
): ChatTurn => ({
  turnId: 'turn-1',
  userMessage: {
    id: 'user-1',
    text: '请开始',
    timestamp: 1,
    status: 'success',
  },
  assistant: {
    id: 'assistant-1',
    status,
    timelineItems: [],
    answerMarkdown: '',
    isStreaming,
    renderMode: 'structured',
  },
});

describe('buildWorkspaceStudioAgents', () => {
  it('keeps the workspace model multi-agent while the current chat targets one agent', () => {
    const items = buildWorkspaceStudioAgents({
      agents: [
        agent({
          agentId: 'planner',
          name: '规划 Agent',
          avatarUrl: '/assets/planner.png',
        }),
        agent({
          agentId: 'coder',
          name: '编码 Agent',
          avatarUrl: '/assets/coder.png',
        }),
        agent({ agentId: 'sleeper', name: '休眠 Agent', isFrozen: true }),
      ],
      selectedAgentId: 'coder',
      turns: [turn('streaming', true)],
      loading: true,
      subAgentCards: {},
    });

    expect(items).toHaveLength(3);
    expect(items.map((item) => item.agentId)).toEqual([
      'planner',
      'coder',
      'sleeper',
    ]);
    expect(items.find((item) => item.agentId === 'coder')).toMatchObject({
      state: 'working',
      spriteSheetUrl: '/admin/assets/pets/pudding/spritesheet.png',
      spriteTextureKey: getWorkspaceStudioSpriteTextureKey(
        '/admin/assets/pets/pudding/spritesheet.png',
      ),
      spriteRow: 7,
      spriteFrameCount: 6,
      selected: true,
      stateLabel: '工作中',
      avatarUrl: '/assets/coder.png',
    });
    expect(items.find((item) => item.agentId === 'planner')).toMatchObject({
      state: 'resting',
      spriteRow: 0,
      spriteFrameCount: 6,
      selected: false,
      stateLabel: '休息',
    });
    expect(items.find((item) => item.agentId === 'sleeper')).toMatchObject({
      state: 'sleeping',
      spriteRow: 6,
      spriteFrameCount: 6,
      stateLabel: '睡觉',
    });
  });

  it('uses avatar-specific sprites when a generated workspace sprite exists', () => {
    const items = buildWorkspaceStudioAgents({
      agents: [
        agent({ agentId: 'support', name: '支持 Agent', avatarId: 'smile' }),
        agent({ agentId: 'night', name: '夜间 Agent', avatarId: 'sleepy' }),
        agent({ agentId: 'analyst', name: '分析 Agent', avatarId: 'thinking' }),
        agent({ agentId: 'alert', name: '警戒 Agent', avatarId: 'angry' }),
        agent({ agentId: 'cheer', name: '鼓励 Agent', avatarId: 'amber' }),
        agent({ agentId: 'ops', name: '运维 Agent', avatarId: 'mint' }),
        agent({ agentId: 'engineer', name: '工程 Agent', avatarId: 'silver' }),
        agent({ agentId: 'generic', name: '通用 Agent', avatarId: 'missing' }),
      ],
      selectedAgentId: 'support',
      turns: [],
      loading: false,
      subAgentCards: {},
    });

    expect(
      items.find((item) => item.agentId === 'support')?.spriteSheetUrl,
    ).toBe('/admin/assets/agent-sprites/smile/spritesheet.png');
    expect(items.find((item) => item.agentId === 'night')?.spriteSheetUrl).toBe(
      '/admin/assets/agent-sprites/sleepy/spritesheet.png',
    );
    expect(
      items.find((item) => item.agentId === 'analyst')?.spriteSheetUrl,
    ).toBe('/admin/assets/agent-sprites/thinking/spritesheet.png');
    expect(items.find((item) => item.agentId === 'alert')?.spriteSheetUrl).toBe(
      '/admin/assets/agent-sprites/angry/spritesheet.png',
    );
    expect(items.find((item) => item.agentId === 'cheer')?.spriteSheetUrl).toBe(
      '/admin/assets/agent-sprites/amber/spritesheet.png',
    );
    expect(items.find((item) => item.agentId === 'ops')?.spriteSheetUrl).toBe(
      '/admin/assets/agent-sprites/mint/spritesheet.png',
    );
    expect(
      items.find((item) => item.agentId === 'engineer')?.spriteSheetUrl,
    ).toBe('/admin/assets/agent-sprites/silver/spritesheet.png');
    expect(
      items.find((item) => item.agentId === 'generic')?.spriteSheetUrl,
    ).toBe('/admin/assets/pets/pudding/spritesheet.png');
  });

  it('shares one texture key for agents using the same spritesheet', () => {
    const items = buildWorkspaceStudioAgents({
      agents: [
        agent({ agentId: 'default-agent', name: '默认助手' }),
        agent({ agentId: 'consultant', name: '咨询专家' }),
      ],
      selectedAgentId: 'default-agent',
      turns: [],
      loading: false,
      subAgentCards: {},
    });

    expect(items[0].spriteSheetUrl).toBe(
      '/admin/assets/pets/pudding/spritesheet.png',
    );
    expect(items[1].spriteSheetUrl).toBe(
      '/admin/assets/pets/pudding/spritesheet.png',
    );
    expect(items[0].spriteTextureKey).toBe(items[1].spriteTextureKey);
  });

  it('keeps generated workspace sprite avatars discoverable from the manifest', () => {
    expect(generatedWorkspaceAgentAvatarIds).toEqual([
      'amber',
      'angry',
      'mint',
      'silver',
      'sleepy',
      'smile',
      'thinking',
    ]);
  });

  it('uses running sub-agent cards as a workspace-level busy signal without hiding other agents', () => {
    const subAgentCards: SubAgentCardMap = {
      'turn-1': {
        turnId: 'turn-1',
        subSessionId: 'session-sub-1',
        taskSummary: '整理上下文',
        status: 'running',
        spawnedAt: 1,
      },
    };

    const items = buildWorkspaceStudioAgents({
      agents: [
        agent({ agentId: 'lead', name: '主控 Agent' }),
        agent({ agentId: 'reviewer', name: '审查 Agent' }),
      ],
      selectedAgentId: 'lead',
      turns: [turn('success')],
      loading: false,
      subAgentCards,
    });

    expect(items).toHaveLength(2);
    expect(items.find((item) => item.agentId === 'lead')).toMatchObject({
      state: 'working',
      activity: '1 个子代理运行中',
    });
    expect(items.find((item) => item.agentId === 'reviewer')).toMatchObject({
      state: 'resting',
    });
  });

  it('maps active workspace sessions to matching agent activity', () => {
    const activities = buildWorkspaceStudioSessionActivities([
      {
        sessionId: 'session-1',
        workspaceId: 'default',
        agentTemplateId: 'global:general-assistant',
        channelId: 'chat',
        ownerUserId: 'user-1',
        sessionType: 'ServiceSession',
        status: 'Active',
        agentInstanceId: 'reviewer',
        title: '审查 PR',
        createdAt: '2026-05-25T00:00:00Z',
        lastActiveAt: '2026-05-25T00:02:00Z',
      },
      {
        sessionId: 'session-2',
        workspaceId: 'default',
        agentTemplateId: 'planner',
        channelId: 'chat',
        ownerUserId: 'user-1',
        sessionType: 'ServiceSession',
        status: 'Completed',
        title: '已完成会话',
        createdAt: '2026-05-25T00:00:00Z',
        lastActiveAt: '2026-05-25T00:03:00Z',
      },
    ]);

    expect(activities).toEqual({
      reviewer: {
        state: 'working',
        activity: '审查 PR',
      },
    });

    const items = buildWorkspaceStudioAgents({
      agents: [
        agent({ agentId: 'planner', name: '规划 Agent' }),
        agent({ agentId: 'reviewer', name: '审查 Agent' }),
      ],
      selectedAgentId: 'planner',
      turns: [],
      loading: false,
      subAgentCards: {},
      agentActivities: activities,
    });

    expect(items.find((item) => item.agentId === 'reviewer')).toMatchObject({
      state: 'working',
      activity: '审查 PR',
    });
    expect(items.find((item) => item.agentId === 'planner')).toMatchObject({
      state: 'resting',
    });
  });

  it('refreshes studio sessions only for matching workspace notifications', () => {
    expect(
      shouldRefreshWorkspaceStudioSessionsForNotification(
        { workspaceId: 'default' },
        'default',
      ),
    ).toBe(true);
    expect(
      shouldRefreshWorkspaceStudioSessionsForNotification(
        { workspaceId: 'other' },
        'default',
      ),
    ).toBe(false);
    expect(
      shouldRefreshWorkspaceStudioSessionsForNotification(
        { workspaceId: 'default' },
        undefined,
      ),
    ).toBe(false);
  });

  it('summarizes agent state counts in work-first order', () => {
    const items = buildWorkspaceStudioAgents({
      agents: [
        agent({ agentId: 'working', name: '工作 Agent' }),
        agent({ agentId: 'resting', name: '休息 Agent' }),
        agent({ agentId: 'sleeping', name: '睡觉 Agent', isEnabled: false }),
      ],
      selectedAgentId: 'working',
      turns: [turn('streaming', true)],
      loading: true,
      subAgentCards: {},
    });

    expect(summarizeWorkspaceStudioAgents(items)).toEqual([
      { state: 'working', label: '工作中', count: 1 },
      { state: 'resting', label: '休息', count: 1 },
      { state: 'sleeping', label: '睡觉', count: 1 },
    ]);
  });

  it('generates non-overlapping floor positions for larger agent groups', () => {
    const positions = Array.from({ length: 10 }, (_, index) =>
      getWorkspaceStudioAgentPosition(index, 10),
    );

    expect(
      new Set(positions.map((position) => `${position.x},${position.y}`)).size,
    ).toBe(10);
    expect(
      positions.every((position) => position.x >= 14 && position.x <= 86),
    ).toBe(true);
    expect(
      positions.every((position) => position.y >= 52 && position.y <= 80),
    ).toBe(true);
  });

  it('places agents near state-specific room zones when their state is known', () => {
    expect(getWorkspaceStudioAgentPosition(0, 4, 'working')).toEqual({
      x: 34,
      y: 59,
    });
    expect(getWorkspaceStudioAgentPosition(0, 4, 'resting')).toEqual({
      x: 17,
      y: 64,
    });
    expect(getWorkspaceStudioAgentPosition(0, 4, 'playing')).toEqual({
      x: 68,
      y: 62,
    });
    expect(getWorkspaceStudioAgentPosition(0, 4, 'sleeping')).toEqual({
      x: 72,
      y: 62,
    });
  });

  it('keeps studio object definitions unique and interaction-ready', () => {
    expect(
      new Set(workspaceStudioObjects.map((object) => object.objectId)).size,
    ).toBe(workspaceStudioObjects.length);
    expect(getWorkspaceStudioObjectDefinition('meetingTable')).toMatchObject({
      label: '中部会议桌',
      areaId: 'meeting',
      interactive: true,
      activation: 'collaboration',
    });
    expect(getWorkspaceStudioObjectDefinition('gameConsole')).toMatchObject({
      label: '娱乐终端',
      areaId: 'play',
      interactive: true,
      activation: 'playing',
    });
    expect(getWorkspaceStudioObjectDefinition('statusBoard')).toMatchObject({
      label: '工作室状态板',
      areaId: 'decor',
      interactive: true,
      activation: 'status',
    });
    expect(getWorkspaceStudioObjectDefinition('activityBoard')).toMatchObject({
      label: '最近活动板',
      areaId: 'decor',
      interactive: true,
      activation: 'recent',
    });
    expect(
      workspaceStudioObjects.every(
        (object) => object.bounds.width > 0 && object.bounds.height > 0,
      ),
    ).toBe(true);
  });

  it('derives object activity feedback from scene state', () => {
    const status = {
      activeTaskCount: 2,
      recentActivityCount: 1,
      restingAgentCount: 1,
      playingAgentCount: 1,
      sleepingAgentCount: 0,
    };

    expect(isWorkspaceStudioObjectActive('workbench', status)).toBe(true);
    expect(isWorkspaceStudioObjectActive('meetingTable', status)).toBe(true);
    expect(isWorkspaceStudioObjectActive('gameConsole', status)).toBe(true);
    expect(isWorkspaceStudioObjectActive('statusBoard', status)).toBe(true);
    expect(isWorkspaceStudioObjectActive('activityBoard', status)).toBe(true);
    expect(isWorkspaceStudioObjectActive('sleepArea', status)).toBe(false);
    expect(getWorkspaceStudioObjectStatusLabel('statusBoard', status)).toBe(
      '4 个 Agent',
    );
    expect(getWorkspaceStudioObjectStatusLabel('activityBoard', status)).toBe(
      '1 条活动',
    );
    expect(getWorkspaceStudioObjectStatusLabel('taskBoard', status)).toBe(
      '2 个任务',
    );
    expect(getWorkspaceStudioObjectStatusLabel('mailbox', status)).toBe(
      '1 条活动',
    );
    expect(getWorkspaceStudioObjectStatusLabel('meetingTable', status)).toBe(
      '协作中',
    );
  });

  it('keeps later agents inside their state-specific room zones', () => {
    expect(getWorkspaceStudioAgentPosition(5, 8, 'working')).toEqual({
      x: 30,
      y: 68,
    });
    expect(getWorkspaceStudioAgentPosition(6, 8, 'sleeping')).toEqual({
      x: 72,
      y: 62,
    });
  });

  it('moves an interacting agent close to the target agent without leaving the room floor', () => {
    expect(
      getWorkspaceStudioInteractionPosition({ x: 17, y: 60 }, { x: 72, y: 58 }),
    ).toEqual({ x: 66, y: 61 });
    expect(
      getWorkspaceStudioInteractionPosition({ x: 88, y: 70 }, { x: 12, y: 77 }),
    ).toEqual({ x: 18, y: 78 });
  });

  it('normalizes workspace notifications into temporary scene events', () => {
    const event = normalizeWorkspaceStudioSceneEvent({
      type: 'agent.message.sent',
      workspaceId: 'default',
      sessionId: 'session-1',
      agentId: 'planner',
      timestamp: '2026-05-26T00:00:00Z',
      data: {
        targetAgentId: 'coder',
        message: '请审查这一段',
      },
    });

    expect(event).toMatchObject({
      kind: 'message',
      sourceAgentId: 'planner',
      targetAgentId: 'coder',
      text: '请审查这一段',
      createdAt: new Date('2026-05-26T00:00:00Z').getTime(),
    });
  });

  it('selects the latest non-expired scene event', () => {
    const now = 10_000;
    expect(
      getWorkspaceStudioActiveEvent(
        [
          {
            id: 'old',
            kind: 'message',
            sourceAgentId: 'a',
            targetAgentId: 'b',
            text: 'old',
            createdAt: now - 20_000,
          },
          {
            id: 'new',
            kind: 'message',
            sourceAgentId: 'b',
            targetAgentId: 'a',
            text: 'new',
            createdAt: now - 200,
          },
        ],
        now,
      )?.id,
    ).toBe('new');
  });
});
