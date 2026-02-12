import type {
  SessionRecord,
  WorkspaceAgentDto,
  WorkspaceNotification,
} from '@/services/platform/api';
import type { ChatTurn, SubAgentCardMap } from '../types';
import { resolveWorkspaceAgentSpriteSheetUrl } from './workspaceStudioSprites';

export type WorkspaceStudioAgentState =
  | 'resting'
  | 'working'
  | 'playing'
  | 'sleeping';
export type WorkspaceStudioAreaId =
  | 'rest'
  | 'work'
  | 'meeting'
  | 'play'
  | 'sleep'
  | 'entry'
  | 'decor';
export type WorkspaceStudioObjectId =
  | 'workbench'
  | 'taskBoard'
  | 'statusBoard'
  | 'activityBoard'
  | 'mailbox'
  | 'door'
  | 'restArea'
  | 'meetingTable'
  | 'gameConsole'
  | 'sleepArea'
  | 'bookshelf'
  | 'plant';
export type WorkspaceStudioAgentCommand = 'chat' | 'focus';

export interface WorkspaceStudioSceneStatus {
  activeTaskCount: number;
  recentActivityCount: number;
  restingAgentCount: number;
  playingAgentCount: number;
  sleepingAgentCount: number;
}

export type WorkspaceStudioSceneEventKind = 'message' | 'handoff' | 'task';

export interface WorkspaceStudioSceneEvent {
  id: string;
  kind: WorkspaceStudioSceneEventKind;
  sourceAgentId: string;
  targetAgentId?: string;
  text: string;
  createdAt: number;
}

export interface WorkspaceStudioAgent {
  agentId: string;
  name: string;
  avatarUrl?: string;
  avatarEmoji?: string;
  spriteSheetUrl: string;
  spriteTextureKey: string;
  spriteRow: number;
  spriteFrameCount: number;
  selected: boolean;
  state: WorkspaceStudioAgentState;
  stateLabel: string;
  activity: string;
  canChat: boolean;
}

export interface WorkspaceStudioAgentActivity {
  state: WorkspaceStudioAgentState;
  activity: string;
}

export interface WorkspaceStudioAgentStateSummary {
  state: WorkspaceStudioAgentState;
  label: string;
  count: number;
}

export interface WorkspaceStudioAgentPosition {
  x: number;
  y: number;
}

export interface WorkspaceStudioRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface WorkspaceStudioAreaDefinition {
  areaId: WorkspaceStudioAreaId;
  label: string;
  bounds: WorkspaceStudioRect;
  agentSlots: WorkspaceStudioAgentPosition[];
  collisionRects: WorkspaceStudioRect[];
}

export interface WorkspaceStudioObjectDefinition {
  objectId: WorkspaceStudioObjectId;
  label: string;
  shortLabel: string;
  description: string;
  areaId: WorkspaceStudioAreaId;
  bounds: WorkspaceStudioRect;
  interactive: boolean;
  persistentLabel: boolean;
  activation:
    | 'task'
    | 'status'
    | 'recent'
    | 'resting'
    | 'playing'
    | 'sleeping'
    | 'collaboration'
    | 'none';
  primaryAction?:
    | 'workspace'
    | 'chat'
    | 'status'
    | 'activities'
    | 'messages'
    | 'agents'
    | 'templates'
    | 'global'
    | 'space';
  collisionRects: WorkspaceStudioRect[];
}

export interface BuildWorkspaceStudioAgentsInput {
  agents: WorkspaceAgentDto[];
  selectedAgentId?: string;
  turns: ChatTurn[];
  loading: boolean;
  subAgentCards: SubAgentCardMap;
  agentActivities?: Record<string, WorkspaceStudioAgentActivity>;
}

const ACTIVE_ASSISTANT_STATUSES = new Set([
  'thinking',
  'executing',
  'streaming',
]);
const SCENE_EVENT_TTL_MS = 12_000;

const stateLabels: Record<WorkspaceStudioAgentState, string> = {
  resting: '休息',
  working: '工作中',
  playing: '娱乐',
  sleeping: '睡觉',
};

const stateSpriteRows: Record<WorkspaceStudioAgentState, number> = {
  resting: 0,
  working: 7,
  playing: 3,
  sleeping: 6,
};

const stateSpriteFrameCounts: Record<WorkspaceStudioAgentState, number> = {
  resting: 6,
  working: 6,
  playing: 4,
  sleeping: 6,
};

const stateOrder: WorkspaceStudioAgentState[] = [
  'working',
  'resting',
  'playing',
  'sleeping',
];

export function getWorkspaceStudioSpriteTextureKey(
  spriteSheetUrl: string,
): string {
  return `workspace-agent-sprite:${spriteSheetUrl}`;
}

export const workspaceStudioAreas: WorkspaceStudioAreaDefinition[] = [
  {
    areaId: 'rest',
    label: '休息区',
    bounds: { x: 106, y: 452, width: 240, height: 126 },
    agentSlots: [
      { x: 17, y: 64 },
      { x: 24, y: 70 },
      { x: 11, y: 70 },
    ],
    collisionRects: [
      { x: 100, y: 486, width: 205, height: 30 },
      { x: 118, y: 462, width: 110, height: 42 },
      { x: 252, y: 510, width: 58, height: 58 },
    ],
  },
  {
    areaId: 'work',
    label: '工作区',
    bounds: { x: 430, y: 410, width: 230, height: 136 },
    agentSlots: [
      { x: 34, y: 59 },
      { x: 42, y: 62 },
      { x: 30, y: 68 },
    ],
    collisionRects: [
      { x: 450, y: 458, width: 128, height: 42 },
      { x: 560, y: 456, width: 54, height: 52 },
    ],
  },
  {
    areaId: 'meeting',
    label: '会议区',
    bounds: { x: 640, y: 590, width: 350, height: 90 },
    agentSlots: [
      { x: 48, y: 65 },
      { x: 55, y: 65 },
      { x: 51, y: 72 },
    ],
    collisionRects: [{ x: 720, y: 606, width: 146, height: 54 }],
  },
  {
    areaId: 'play',
    label: '娱乐区',
    bounds: { x: 1020, y: 430, width: 180, height: 134 },
    agentSlots: [
      { x: 68, y: 62 },
      { x: 74, y: 68 },
      { x: 62, y: 68 },
    ],
    collisionRects: [
      { x: 1024, y: 344, width: 126, height: 72 },
      { x: 1212, y: 462, width: 58, height: 92 },
    ],
  },
  {
    areaId: 'sleep',
    label: '睡眠区',
    bounds: { x: 1268, y: 438, width: 182, height: 124 },
    agentSlots: [
      { x: 72, y: 62 },
      { x: 68, y: 69 },
      { x: 76, y: 69 },
    ],
    collisionRects: [
      { x: 1300, y: 486, width: 74, height: 44 },
      { x: 1368, y: 438, width: 70, height: 96 },
    ],
  },
  {
    areaId: 'entry',
    label: '入口',
    bounds: { x: 680, y: 870, width: 300, height: 116 },
    agentSlots: [
      { x: 56, y: 86 },
      { x: 50, y: 86 },
      { x: 62, y: 86 },
    ],
    collisionRects: [
      { x: 696, y: 928, width: 180, height: 54 },
      { x: 910, y: 874, width: 60, height: 70 },
    ],
  },
  {
    areaId: 'decor',
    label: '装饰',
    bounds: { x: 72, y: 72, width: 1456, height: 170 },
    agentSlots: [],
    collisionRects: [],
  },
];

export const workspaceStudioObjects: WorkspaceStudioObjectDefinition[] = [
  {
    objectId: 'workbench',
    label: '工作台电脑',
    shortLabel: '电脑',
    description: '当前任务、对话与本地工作入口',
    areaId: 'work',
    bounds: { x: 450, y: 420, width: 170, height: 102 },
    interactive: true,
    persistentLabel: true,
    activation: 'task',
    primaryAction: 'workspace',
    collisionRects: [
      { x: 450, y: 458, width: 128, height: 42 },
      { x: 560, y: 456, width: 54, height: 52 },
    ],
  },
  {
    objectId: 'taskBoard',
    label: '任务板',
    shortLabel: '任务板',
    description: '展示正在运行的任务和最近的会话反馈',
    areaId: 'decor',
    bounds: { x: 1044, y: 154, width: 132, height: 64 },
    interactive: true,
    persistentLabel: true,
    activation: 'task',
    primaryAction: 'chat',
    collisionRects: [{ x: 1044, y: 154, width: 132, height: 64 }],
  },
  {
    objectId: 'statusBoard',
    label: '工作室状态板',
    shortLabel: '状态板',
    description: '墙上的 Agent 状态总览，点击后查看工作、休息与睡眠分布',
    areaId: 'decor',
    bounds: { x: 1216, y: 112, width: 242, height: 76 },
    interactive: true,
    persistentLabel: true,
    activation: 'status',
    primaryAction: 'status',
    collisionRects: [{ x: 1216, y: 112, width: 242, height: 76 }],
  },
  {
    objectId: 'activityBoard',
    label: '最近活动板',
    shortLabel: '活动板',
    description: '墙上的最近活动列表入口，点击后查看最新会话反馈',
    areaId: 'decor',
    bounds: { x: 1216, y: 198, width: 242, height: 64 },
    interactive: true,
    persistentLabel: true,
    activation: 'recent',
    primaryAction: 'activities',
    collisionRects: [{ x: 1216, y: 198, width: 242, height: 64 }],
  },
  {
    objectId: 'mailbox',
    label: '门口邮箱',
    shortLabel: '邮箱',
    description: 'Email、飞书与 Webhook 等外部消息入口',
    areaId: 'entry',
    bounds: { x: 884, y: 858, width: 94, height: 82 },
    interactive: true,
    persistentLabel: true,
    activation: 'recent',
    primaryAction: 'messages',
    collisionRects: [{ x: 910, y: 874, width: 60, height: 70 }],
  },
  {
    objectId: 'door',
    label: '工作室入口',
    shortLabel: '入口',
    description: '返回工作空间列表与切换入口',
    areaId: 'entry',
    bounds: { x: 696, y: 906, width: 180, height: 84 },
    interactive: true,
    persistentLabel: true,
    activation: 'none',
    primaryAction: 'space',
    collisionRects: [{ x: 696, y: 928, width: 180, height: 54 }],
  },
  {
    objectId: 'restArea',
    label: '休息区',
    shortLabel: '休息区',
    description: '休息、等待和冷却中的 Agent 会停留在这里',
    areaId: 'rest',
    bounds: { x: 108, y: 452, width: 234, height: 120 },
    interactive: true,
    persistentLabel: false,
    activation: 'resting',
    primaryAction: 'templates',
    collisionRects: [],
  },
  {
    objectId: 'meetingTable',
    label: '中部会议桌',
    shortLabel: '会议桌',
    description: '多 Agent 协作、交接和编队讨论的画面锚点',
    areaId: 'meeting',
    bounds: { x: 650, y: 596, width: 342, height: 88 },
    interactive: true,
    persistentLabel: false,
    activation: 'collaboration',
    primaryAction: 'chat',
    collisionRects: [{ x: 720, y: 606, width: 146, height: 54 }],
  },
  {
    objectId: 'gameConsole',
    label: '娱乐终端',
    shortLabel: '娱乐区',
    description: '娱乐、探索和低优先级创意任务的状态反馈点',
    areaId: 'play',
    bounds: { x: 1000, y: 430, width: 210, height: 132 },
    interactive: true,
    persistentLabel: false,
    activation: 'playing',
    primaryAction: 'agents',
    collisionRects: [
      { x: 1024, y: 344, width: 126, height: 72 },
      { x: 1212, y: 462, width: 58, height: 92 },
    ],
  },
  {
    objectId: 'sleepArea',
    label: '睡眠区',
    shortLabel: '睡眠区',
    description: '暂停、冻结或不可用的 Agent 会停留在这里',
    areaId: 'sleep',
    bounds: { x: 1280, y: 450, width: 190, height: 110 },
    interactive: true,
    persistentLabel: false,
    activation: 'sleeping',
    primaryAction: 'templates',
    collisionRects: [],
  },
  {
    objectId: 'bookshelf',
    label: '资料书架',
    shortLabel: '书架',
    description: '知识库、提示词和团队规范的视觉入口',
    areaId: 'rest',
    bounds: { x: 238, y: 502, width: 82, height: 80 },
    interactive: true,
    persistentLabel: false,
    activation: 'none',
    primaryAction: 'global',
    collisionRects: [{ x: 252, y: 510, width: 58, height: 58 }],
  },
  {
    objectId: 'plant',
    label: '绿植',
    shortLabel: '绿植',
    description: '工作室氛围对象，后续可承载健康度或心情反馈',
    areaId: 'play',
    bounds: { x: 850, y: 462, width: 80, height: 76 },
    interactive: true,
    persistentLabel: false,
    activation: 'recent',
    primaryAction: 'global',
    collisionRects: [{ x: 896, y: 524, width: 24, height: 12 }],
  },
];

const defaultPositions: WorkspaceStudioAgentPosition[] = [
  { x: 16, y: 58 },
  { x: 34, y: 44 },
  { x: 52, y: 58 },
  { x: 70, y: 43 },
  { x: 84, y: 59 },
  { x: 25, y: 74 },
  { x: 61, y: 74 },
  { x: 78, y: 73 },
];

const stateAreas: Record<WorkspaceStudioAgentState, WorkspaceStudioAreaId> = {
  resting: 'rest',
  working: 'work',
  playing: 'play',
  sleeping: 'sleep',
};

const workspaceStudioAreaMap = new Map(
  workspaceStudioAreas.map((area) => [area.areaId, area]),
);
const workspaceStudioObjectMap = new Map(
  workspaceStudioObjects.map((object) => [object.objectId, object]),
);

function getDisplayName(agent: WorkspaceAgentDto): string {
  return agent.displayName?.trim() || agent.name?.trim() || 'Agent';
}

function hasActiveSelectedTurn(turns: ChatTurn[], loading: boolean): boolean {
  if (!loading) return false;
  const latest = turns[turns.length - 1];
  if (!latest) return true;
  return (
    latest.assistant.isStreaming ||
    ACTIVE_ASSISTANT_STATUSES.has(latest.assistant.status)
  );
}

function countRunningSubAgents(cards: SubAgentCardMap): number {
  return Object.values(cards).filter(
    (card) => card.status === 'running' || card.status === 'spawning',
  ).length;
}

function getSessionAgentId(session: SessionRecord): string | undefined {
  return (
    session.agentInstanceId?.trim() ||
    session.agentTemplateId?.trim() ||
    undefined
  );
}

export function buildWorkspaceStudioSessionActivities(
  sessions: SessionRecord[],
): Record<string, WorkspaceStudioAgentActivity> {
  const activeSessions = sessions
    .filter((session) => session.status === 'Active')
    .map((session) => ({ session, agentId: getSessionAgentId(session) }))
    .filter((item): item is { session: SessionRecord; agentId: string } =>
      Boolean(item.agentId),
    )
    .sort(
      (a, b) =>
        new Date(b.session.lastActiveAt).getTime() -
        new Date(a.session.lastActiveAt).getTime(),
    );

  return activeSessions.reduce<Record<string, WorkspaceStudioAgentActivity>>(
    (result, item) => {
      if (result[item.agentId]) return result;
      result[item.agentId] = {
        state: 'working',
        activity: item.session.title?.trim() || '会话进行中',
      };
      return result;
    },
    {},
  );
}

export function shouldRefreshWorkspaceStudioSessionsForNotification(
  event: Pick<WorkspaceNotification, 'workspaceId'>,
  workspaceId?: string,
): boolean {
  return Boolean(workspaceId && event.workspaceId === workspaceId);
}

export function summarizeWorkspaceStudioAgents(
  agents: Pick<WorkspaceStudioAgent, 'state'>[],
): WorkspaceStudioAgentStateSummary[] {
  const counts = agents.reduce<Record<WorkspaceStudioAgentState, number>>(
    (result, agent) => {
      result[agent.state] += 1;
      return result;
    },
    {
      resting: 0,
      working: 0,
      playing: 0,
      sleeping: 0,
    },
  );

  return stateOrder
    .map((state) => ({
      state,
      label: stateLabels[state],
      count: counts[state],
    }))
    .filter((summary) => summary.count > 0);
}

export function getWorkspaceStudioAreaDefinition(
  areaId: WorkspaceStudioAreaId,
): WorkspaceStudioAreaDefinition | undefined {
  return workspaceStudioAreaMap.get(areaId);
}

export function getWorkspaceStudioObjectDefinition(
  objectId: WorkspaceStudioObjectId,
): WorkspaceStudioObjectDefinition | undefined {
  return workspaceStudioObjectMap.get(objectId);
}

export function isWorkspaceStudioObjectActive(
  objectId: WorkspaceStudioObjectId,
  status?: WorkspaceStudioSceneStatus,
  events?: WorkspaceStudioSceneEvent[],
): boolean {
  const object = getWorkspaceStudioObjectDefinition(objectId);
  if (!object || !status) return false;
  switch (object.activation) {
    case 'task':
      return status.activeTaskCount > 0;
    case 'status':
      return (
        status.restingAgentCount +
          status.playingAgentCount +
          status.sleepingAgentCount +
          status.activeTaskCount >
        0
      );
    case 'recent':
      return status.recentActivityCount > 0;
    case 'resting':
      return status.restingAgentCount > 0;
    case 'playing':
      return status.playingAgentCount > 0;
    case 'sleeping':
      return status.sleepingAgentCount > 0;
    case 'collaboration':
      return (
        status.activeTaskCount > 1 ||
        Boolean(getWorkspaceStudioActiveEvent(events))
      );
    case 'none':
    default:
      return false;
  }
}

export function getWorkspaceStudioObjectStatusLabel(
  objectId: WorkspaceStudioObjectId,
  status?: WorkspaceStudioSceneStatus,
): string {
  const object = getWorkspaceStudioObjectDefinition(objectId);
  if (!object) return '';
  if (!status) return object.shortLabel;
  switch (objectId) {
    case 'statusBoard':
      return `${status.restingAgentCount + status.playingAgentCount + status.sleepingAgentCount + status.activeTaskCount} 个 Agent`;
    case 'activityBoard':
      return status.recentActivityCount > 0
        ? `${status.recentActivityCount} 条活动`
        : object.shortLabel;
    case 'taskBoard':
      return status.activeTaskCount > 0
        ? `${status.activeTaskCount} 个任务`
        : object.shortLabel;
    case 'mailbox':
      return status.recentActivityCount > 0
        ? `${status.recentActivityCount} 条活动`
        : object.shortLabel;
    case 'restArea':
      return status.restingAgentCount > 0
        ? `休息 ${status.restingAgentCount}`
        : object.shortLabel;
    case 'gameConsole':
      return status.playingAgentCount > 0
        ? `娱乐 ${status.playingAgentCount}`
        : object.shortLabel;
    case 'sleepArea':
      return status.sleepingAgentCount > 0
        ? `睡眠 ${status.sleepingAgentCount}`
        : object.shortLabel;
    case 'meetingTable':
      return status.activeTaskCount > 1 ? '协作中' : object.shortLabel;
    default:
      return object.shortLabel;
  }
}

export function getWorkspaceStudioAgentPosition(
  index: number,
  total: number,
  state?: WorkspaceStudioAgentState,
): WorkspaceStudioAgentPosition {
  const statePool = state
    ? getWorkspaceStudioAreaDefinition(stateAreas[state])?.agentSlots
    : undefined;
  if (statePool?.length) {
    return statePool[index % statePool.length];
  }

  if (total <= defaultPositions.length) {
    return defaultPositions[index % defaultPositions.length];
  }

  const columns = Math.min(6, Math.max(3, Math.ceil(Math.sqrt(total))));
  const rows = Math.ceil(total / columns);
  const row = Math.floor(index / columns);
  const col = index % columns;
  const itemsInRow = row === rows - 1 ? total - row * columns : columns;
  const x = itemsInRow <= 1 ? 50 : 14 + (72 * col) / (itemsInRow - 1);
  const yStep = rows <= 1 ? 0 : 26 / (rows - 1);
  const y = Math.min(80, 52 + row * yStep);

  return {
    x: Math.round(x * 10) / 10,
    y: Math.round(y * 10) / 10,
  };
}

export function getWorkspaceStudioActiveEvent(
  events: WorkspaceStudioSceneEvent[] | undefined,
  now = Date.now(),
): WorkspaceStudioSceneEvent | undefined {
  return events
    ?.filter((event) => now - event.createdAt <= SCENE_EVENT_TTL_MS)
    .sort((a, b) => b.createdAt - a.createdAt)[0];
}

export function getWorkspaceStudioInteractionPosition(
  source: WorkspaceStudioAgentPosition,
  target: WorkspaceStudioAgentPosition,
): WorkspaceStudioAgentPosition {
  const xOffset = source.x <= target.x ? -6 : 6;
  return {
    x: Math.max(10, Math.min(90, target.x + xOffset)),
    y: Math.max(48, Math.min(78, target.y + 3)),
  };
}

function readString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined;
}

export function normalizeWorkspaceStudioSceneEvent(
  event: WorkspaceNotification,
): WorkspaceStudioSceneEvent | undefined {
  const data = event.data ?? {};
  const sourceAgentId =
    readString(data.sourceAgentId) ||
    readString(data.fromAgentId) ||
    event.agentId;
  const targetAgentId =
    readString(data.targetAgentId) || readString(data.toAgentId);
  if (!sourceAgentId || !targetAgentId || sourceAgentId === targetAgentId)
    return undefined;

  const eventType = event.type.toLowerCase();
  const kind: WorkspaceStudioSceneEventKind = eventType.includes('handoff')
    ? 'handoff'
    : eventType.includes('task')
      ? 'task'
      : 'message';
  const text =
    readString(data.message) ||
    readString(data.summary) ||
    (kind === 'handoff'
      ? '交接任务'
      : kind === 'task'
        ? '协作任务'
        : '发送消息');
  const timestamp = new Date(event.timestamp).getTime();

  return {
    id: `${event.sessionId}:${event.type}:${sourceAgentId}:${targetAgentId}:${event.timestamp}`,
    kind,
    sourceAgentId,
    targetAgentId,
    text,
    createdAt: Number.isNaN(timestamp) ? Date.now() : timestamp,
  };
}

export function buildWorkspaceStudioAgents(
  input: BuildWorkspaceStudioAgentsInput,
): WorkspaceStudioAgent[] {
  const runningSubAgentCount = countRunningSubAgents(input.subAgentCards);
  const selectedHasActiveTurn = hasActiveSelectedTurn(
    input.turns,
    input.loading,
  );

  return input.agents.map((agent) => {
    const selected = agent.agentId === input.selectedAgentId;
    const canChat = agent.isEnabled && !agent.isFrozen;
    const agentActivity = input.agentActivities?.[agent.agentId];
    let state: WorkspaceStudioAgentState = 'resting';
    let activity = '等待任务';

    if (agent.isFrozen || !agent.isEnabled) {
      state = 'sleeping';
      activity = agent.isFrozen ? '已暂停' : '未启用';
    } else if (selected && selectedHasActiveTurn) {
      state = 'working';
      activity = '正在处理当前对话';
    } else if (selected && runningSubAgentCount > 0) {
      state = 'working';
      activity = `${runningSubAgentCount} 个子代理运行中`;
    } else if (agentActivity) {
      state = agentActivity.state;
      activity = agentActivity.activity;
    }

    const spriteSheetUrl = resolveWorkspaceAgentSpriteSheetUrl(agent.avatarId);

    return {
      agentId: agent.agentId,
      name: getDisplayName(agent),
      avatarUrl: agent.avatarUrl,
      avatarEmoji: agent.avatarEmoji,
      spriteSheetUrl,
      spriteTextureKey: getWorkspaceStudioSpriteTextureKey(spriteSheetUrl),
      spriteRow: stateSpriteRows[state],
      spriteFrameCount: stateSpriteFrameCounts[state],
      selected,
      state,
      stateLabel: stateLabels[state],
      activity,
      canChat,
    };
  });
}
