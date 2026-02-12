# Agent Workbench UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the current chat page into an Agent Workbench with a center timeline, bottom multimodal intent console, and right-side Agent presence rail that integrates browser microphone input, voice output, camera entry points, and sprite/static avatar state.

**Architecture:** Keep the current route and message flow compatible while separating product surfaces into focused UI units: `AgentPresenceRail` for live Agent state, `IntentConsole` for multimodal input, and the existing timeline for messages/events. Voice and camera state should flow through projection-like frontend view models first, then can be replaced by backend event projections when the message fabric and realtime providers mature.

**Tech Stack:** React 19, TypeScript, Ant Design, Jest, Testing Library, browser Web Speech/Web Speech Synthesis adapters, existing Pudding chat state and avatar runtime hooks.

---

## Design Inputs

- Product design: `Docs/superpowers/specs/2026-05-28-agent-workbench-interaction-design.md`
- UI blueprint: `Docs/superpowers/specs/2026-05-28-agent-workbench-ui-blueprint.md`
- Architecture ADR: `Docs/07架构/47ADR-046事件驱动多AgentOS交互体验架构ADR.md`
- Existing implementation anchor:
  - `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx`
  - `Source/PuddingPlatformAdmin/src/pages/chat/components/InputArea.tsx`
  - `Source/PuddingPlatformAdmin/src/pages/chat/components/VoiceConversationPanel.tsx`
  - `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentAvatarRuntimeView.tsx`
  - `Source/PuddingPlatformAdmin/src/pages/chat/components/CameraInputModal.tsx`
  - `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`

## Implementation Snapshot

Checked on 2026-05-28:

- `InputArea.tsx` already has `keyboard | voice` local mode state and renders `VoiceConversationPanel`.
- `VoiceConversationPanel.tsx` already captures browser speech into an editable draft, sends voice metadata, and can speak the latest assistant answer.
- `ChatMain.tsx` already reduces local voice/camera runtime events into `AgentAvatarRuntimeView`.
- `AgentAvatarRuntimeView.tsx` renders sprite/static avatar state, but it is currently an isolated compact aside rather than a full Agent presence rail.
- `CameraInputModal.tsx` already supports browser camera preview, snapshot upload, and visual request metadata.
- The page still reads structurally as `SessionSidebar + ChatMain + InputArea`; the workbench information architecture is not yet explicit.

## File Map

- Create: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/agentPresenceProjection.ts`
  - Converts `ChatStatus`, selected Agent, avatar render state, voice capture events, voice playback events, camera availability, and runtime activity counts into a UI view model for the right rail.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/agentPresenceProjection.test.ts`
  - Locks the state mapping for listening, transcribing, speaking, error, hidden avatar, and camera unavailable states.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentPresenceRail.tsx`
  - Renders the right-side Agent presence surface: avatar, primary state, voice capture card, voice output card, vision card, and current activity list.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentPresenceRail.test.tsx`
  - Verifies accessible status text, replay/stop controls, camera entry, and avatar hidden-state behavior.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx`
  - Owns input mode tabs and delegates keyboard mode to the existing text composer structure and voice mode to the voice panel.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.test.tsx`
  - Verifies keyboard mode, voice mode, camera disabled state, and voice transcript sending metadata at the workbench level.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/InputArea.tsx`
  - Reduce to compatibility wrapper or migrate contents into `IntentConsole`; keep exported prop shape stable during the first pass if downstream callers still import it.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/VoiceConversationPanel.tsx`
  - Align copy and state names with `VoiceIntentPanel` behavior from the blueprint; keep file name in the first pass to reduce churn unless all imports/tests are updated together.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx`
  - Compose center timeline + bottom `IntentConsole` + right `AgentPresenceRail`.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.test.tsx`
  - Replace compact avatar-only expectations with Agent presence rail expectations.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/types.ts`
  - Carries user message metadata and derives message block modality.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageRow.tsx`
  - Passes message block modality into the user bubble.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.tsx`
  - Renders voice and vision modality badges beside user message metadata.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.test.tsx`
  - Covers `inputMode=voice` badge rendering.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`
  - Add workbench shell, center, right rail, intent console, presence cards, and responsive rules.

---

## Task 1: Add Agent Presence Projection

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/agentPresenceProjection.ts`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/agentPresenceProjection.test.ts`

- [ ] **Step 1: Write the failing projection tests**

Create `Source/PuddingPlatformAdmin/src/pages/chat/hooks/agentPresenceProjection.test.ts` with:

```ts
import { buildAgentPresenceProjection } from './agentPresenceProjection';
import type { AgentAvatarRenderState } from './agentAvatarRuntime';

const avatar: AgentAvatarRenderState = {
  visible: true,
  runtimeKind: 'sprite',
  status: 'idle',
  ariaLabel: '小布 avatar idle',
  spriteSheetUrl: '/sprites/pudding.png',
  spriteRow: 0,
  spriteFrameCount: 1,
};

describe('buildAgentPresenceProjection', () => {
  it('promotes recording voice capture to the primary listening state', () => {
    const projection = buildAgentPresenceProjection({
      agentName: '小布',
      chatStatus: 'completed',
      avatar,
      voiceCapture: { status: 'recording', sessionId: 'voice-1' },
      voicePlayback: { status: 'completed', deliveryId: 'voice-out-1', canReplay: true },
      camera: { supported: true, enabled: true, active: false },
      subAgentsRunning: 0,
    });

    expect(projection.primaryState).toBe('listening');
    expect(projection.primaryLabel).toBe('正在听');
    expect(projection.voiceCapture.label).toBe('麦克风正在采集');
    expect(projection.avatarStatus).toBe('listening');
  });

  it('promotes playback to speaking unless voice capture is active', () => {
    const projection = buildAgentPresenceProjection({
      agentName: '小布',
      chatStatus: 'completed',
      avatar,
      voiceCapture: { status: 'completed', sessionId: 'voice-1' },
      voicePlayback: { status: 'playing', deliveryId: 'voice-out-1', canReplay: true },
      camera: { supported: true, enabled: true, active: false },
      subAgentsRunning: 0,
    });

    expect(projection.primaryState).toBe('speaking');
    expect(projection.primaryLabel).toBe('正在说话');
    expect(projection.voicePlayback.label).toBe('正在朗读最新回复');
    expect(projection.avatarStatus).toBe('speaking');
  });

  it('keeps avatar hidden without hiding the rail status', () => {
    const projection = buildAgentPresenceProjection({
      agentName: '小布',
      chatStatus: 'idle',
      avatar: { ...avatar, visible: false },
      voiceCapture: { status: 'idle' },
      voicePlayback: { status: 'unavailable', canReplay: false },
      camera: { supported: false, enabled: false, active: false },
      subAgentsRunning: 2,
    });

    expect(projection.avatarVisible).toBe(false);
    expect(projection.primaryLabel).toBe('待命');
    expect(projection.vision.label).toBe('当前浏览器不支持摄像头');
    expect(projection.activities).toContain('子 Agent：2 个');
  });
});
```

- [ ] **Step 2: Run projection tests and verify they fail**

Run from `Source/PuddingPlatformAdmin`:

```powershell
npm run jest -- src/pages/chat/hooks/agentPresenceProjection.test.ts --runInBand
```

Expected: FAIL because `agentPresenceProjection.ts` does not exist.

- [ ] **Step 3: Implement the projection**

Create `Source/PuddingPlatformAdmin/src/pages/chat/hooks/agentPresenceProjection.ts`:

```ts
import type { ChatStatus } from '../components/InputArea';
import type { AgentAvatarRenderState, AgentAvatarStatus } from './agentAvatarRuntime';

export type PresencePrimaryState =
  | 'idle'
  | 'listening'
  | 'transcribing'
  | 'thinking'
  | 'tool'
  | 'speaking'
  | 'seeing'
  | 'error';

export interface PresenceVoiceCapture {
  status: string;
  sessionId?: string;
  error?: string;
}

export interface PresenceVoicePlayback {
  status: string;
  deliveryId?: string;
  error?: string;
  canReplay: boolean;
}

export interface PresenceCameraState {
  supported: boolean;
  enabled: boolean;
  active: boolean;
}

export interface AgentPresenceProjectionInput {
  agentName: string;
  chatStatus: ChatStatus;
  avatar: AgentAvatarRenderState | null;
  voiceCapture: PresenceVoiceCapture;
  voicePlayback: PresenceVoicePlayback;
  camera: PresenceCameraState;
  subAgentsRunning: number;
}

export interface AgentPresenceProjection {
  agentName: string;
  primaryState: PresencePrimaryState;
  primaryLabel: string;
  primaryDetail: string;
  avatarVisible: boolean;
  avatarStatus: AgentAvatarStatus;
  avatar: AgentAvatarRenderState | null;
  voiceCapture: { label: string; detail: string; error?: string };
  voicePlayback: { label: string; detail: string; error?: string; canReplay: boolean };
  vision: { label: string; detail: string; active: boolean };
  activities: string[];
}

const primaryLabel: Record<PresencePrimaryState, string> = {
  idle: '待命',
  listening: '正在听',
  transcribing: '正在转写',
  thinking: '正在思考',
  tool: '正在使用工具',
  speaking: '正在说话',
  seeing: '正在看',
  error: '需要处理',
};

function mapChatStatus(status: ChatStatus): PresencePrimaryState {
  if (status === 'thinking' || status === 'streaming') return 'thinking';
  if (status === 'tool_executing') return 'tool';
  if (status === 'error') return 'error';
  return 'idle';
}

function mapAvatarStatus(state: PresencePrimaryState): AgentAvatarStatus {
  if (state === 'transcribing') return 'listening';
  if (state === 'tool') return 'tool';
  if (state === 'seeing') return 'seeing';
  return state as AgentAvatarStatus;
}

function voiceCaptureLabel(status: string): string {
  if (status === 'requesting_permission') return '等待麦克风权限';
  if (status === 'recording') return '麦克风正在采集';
  if (status === 'transcribing') return '正在转写语音';
  if (status === 'completed') return '转写等待确认';
  if (status === 'failed') return '麦克风不可用';
  return '麦克风关闭';
}

function voicePlaybackLabel(status: string): string {
  if (status === 'synthesizing') return '正在准备朗读';
  if (status === 'playing') return '正在朗读最新回复';
  if (status === 'failed') return '语音输出不可用';
  if (status === 'cancelled') return '朗读已停止';
  if (status === 'completed') return '可再次朗读';
  return '语音输出待命';
}

export function buildAgentPresenceProjection(input: AgentPresenceProjectionInput): AgentPresenceProjection {
  let state = mapChatStatus(input.chatStatus);

  if (input.voicePlayback.status === 'playing' || input.voicePlayback.status === 'synthesizing') {
    state = 'speaking';
  }
  if (input.camera.active) {
    state = 'seeing';
  }
  if (input.voiceCapture.status === 'recording') {
    state = 'listening';
  }
  if (input.voiceCapture.status === 'transcribing') {
    state = 'transcribing';
  }
  if (input.voiceCapture.status === 'failed' || input.voicePlayback.status === 'failed') {
    state = 'error';
  }

  const activities: string[] = [];
  if (input.chatStatus === 'thinking' || input.chatStatus === 'streaming') activities.push('正在整理上下文');
  if (input.chatStatus === 'tool_executing') activities.push('正在调用工具');
  if (input.subAgentsRunning > 0) activities.push(`子 Agent：${input.subAgentsRunning} 个`);
  if (activities.length === 0) activities.push('可以输入、说话或添加上下文');

  return {
    agentName: input.agentName,
    primaryState: state,
    primaryLabel: primaryLabel[state],
    primaryDetail: state === 'listening' ? '麦克风正在此浏览器中启用' : '当前会话状态会在这里同步',
    avatarVisible: Boolean(input.avatar?.visible),
    avatarStatus: mapAvatarStatus(state),
    avatar: input.avatar,
    voiceCapture: {
      label: voiceCaptureLabel(input.voiceCapture.status),
      detail: input.voiceCapture.sessionId ? `会话 ${input.voiceCapture.sessionId}` : '未开始语音会话',
      error: input.voiceCapture.error,
    },
    voicePlayback: {
      label: voicePlaybackLabel(input.voicePlayback.status),
      detail: input.voicePlayback.deliveryId ? `输出 ${input.voicePlayback.deliveryId}` : '最近回复可在这里朗读',
      error: input.voicePlayback.error,
      canReplay: input.voicePlayback.canReplay,
    },
    vision: input.camera.supported
      ? {
          label: input.camera.active ? '摄像头正在启用' : '摄像头关闭',
          detail: input.camera.enabled ? '可从意图控制台开启视觉输入' : '请选择工作空间后启用视觉输入',
          active: input.camera.active,
        }
      : {
          label: '当前浏览器不支持摄像头',
          detail: '视觉输入不会自动启用',
          active: false,
        },
    activities,
  };
}
```

- [ ] **Step 4: Run projection tests and verify they pass**

Run:

```powershell
npm run jest -- src/pages/chat/hooks/agentPresenceProjection.test.ts --runInBand
```

Expected: PASS.

---

## Task 2: Build Agent Presence Rail

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentPresenceRail.tsx`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentPresenceRail.test.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`

- [ ] **Step 1: Write the failing component tests**

Create `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentPresenceRail.test.tsx`:

```tsx
import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import AgentPresenceRail from './AgentPresenceRail';
import type { AgentPresenceProjection } from '../hooks/agentPresenceProjection';

const projection: AgentPresenceProjection = {
  agentName: '小布',
  primaryState: 'listening',
  primaryLabel: '正在听',
  primaryDetail: '麦克风正在此浏览器中启用',
  avatarVisible: false,
  avatarStatus: 'listening',
  avatar: null,
  voiceCapture: { label: '麦克风正在采集', detail: '会话 voice-1' },
  voicePlayback: { label: '可再次朗读', detail: '输出 voice-out-1', canReplay: true },
  vision: { label: '摄像头关闭', detail: '可从意图控制台开启视觉输入', active: false },
  activities: ['正在整理上下文'],
};

describe('AgentPresenceRail', () => {
  it('renders the agent state, voice, vision, and activity surfaces', () => {
    render(<AgentPresenceRail projection={projection} />);

    expect(screen.getByRole('complementary', { name: 'Agent 感知栏' })).toBeInTheDocument();
    expect(screen.getByText('小布')).toBeInTheDocument();
    expect(screen.getByText('正在听')).toBeInTheDocument();
    expect(screen.getByText('麦克风正在采集')).toBeInTheDocument();
    expect(screen.getByText('可再次朗读')).toBeInTheDocument();
    expect(screen.getByText('摄像头关闭')).toBeInTheDocument();
    expect(screen.getByText('正在整理上下文')).toBeInTheDocument();
  });

  it('exposes replay, stop, and camera actions when handlers are provided', () => {
    const replay = jest.fn();
    const stop = jest.fn();
    const camera = jest.fn();

    render(
      <AgentPresenceRail
        projection={projection}
        onReplayLatest={replay}
        onStopPlayback={stop}
        onOpenCamera={camera}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: '回放最近回复' }));
    fireEvent.click(screen.getByRole('button', { name: '停止朗读' }));
    fireEvent.click(screen.getByRole('button', { name: '开启视觉输入' }));

    expect(replay).toHaveBeenCalledTimes(1);
    expect(stop).toHaveBeenCalledTimes(1);
    expect(camera).toHaveBeenCalledTimes(1);
  });
});
```

- [ ] **Step 2: Run the component tests and verify they fail**

Run:

```powershell
npm run jest -- src/pages/chat/components/AgentPresenceRail.test.tsx --runInBand
```

Expected: FAIL because `AgentPresenceRail.tsx` does not exist.

- [ ] **Step 3: Implement `AgentPresenceRail`**

Create `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentPresenceRail.tsx`:

```tsx
import { AudioOutlined, CameraOutlined, CustomerServiceOutlined, StopOutlined } from '@ant-design/icons';
import { Button } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';
import type { AgentPresenceProjection } from '../hooks/agentPresenceProjection';
import AgentAvatarRuntimeView from './AgentAvatarRuntimeView';

interface AgentPresenceRailProps {
  projection: AgentPresenceProjection;
  onReplayLatest?: () => void;
  onStopPlayback?: () => void;
  onOpenCamera?: () => void;
  onAvatarVisibilityChange?: (visible: boolean) => void;
}

const AgentPresenceRail: React.FC<AgentPresenceRailProps> = ({
  projection,
  onReplayLatest,
  onStopPlayback,
  onOpenCamera,
  onAvatarVisibilityChange,
}) => {
  const { styles } = useChatStyles();

  return (
    <aside className={styles.agentPresenceRail} aria-label="Agent 感知栏">
      <section className={styles.presenceSection}>
        <div className={styles.presenceHeader}>
          <div>
            <div className={styles.presenceEyebrow}>Agent</div>
            <div className={styles.presenceAgentName}>{projection.agentName}</div>
          </div>
          <span className={styles.presenceStatePill} data-state={projection.primaryState}>
            {projection.primaryLabel}
          </span>
        </div>
        <div className={styles.presenceDetail}>{projection.primaryDetail}</div>
        {projection.avatarVisible && projection.avatar ? (
          <AgentAvatarRuntimeView
            renderState={{ ...projection.avatar, status: projection.avatarStatus }}
            agentName={projection.agentName}
            statusDetail={projection.primaryLabel}
            onVisibilityChange={onAvatarVisibilityChange}
          />
        ) : (
          <div className={styles.presenceAvatarPlaceholder}>虚拟形象已隐藏，状态仍会同步。</div>
        )}
      </section>

      <section className={styles.presenceSection}>
        <div className={styles.presenceSectionTitle}><AudioOutlined /> 听觉</div>
        <div className={styles.presenceMetricLabel}>{projection.voiceCapture.label}</div>
        <div className={styles.presenceMetricDetail}>{projection.voiceCapture.error || projection.voiceCapture.detail}</div>
      </section>

      <section className={styles.presenceSection}>
        <div className={styles.presenceSectionTitle}><CustomerServiceOutlined /> 声音</div>
        <div className={styles.presenceMetricLabel}>{projection.voicePlayback.label}</div>
        <div className={styles.presenceMetricDetail}>{projection.voicePlayback.error || projection.voicePlayback.detail}</div>
        <div className={styles.presenceActionRow}>
          <Button size="small" onClick={onReplayLatest} disabled={!projection.voicePlayback.canReplay} aria-label="回放最近回复">
            回放
          </Button>
          <Button size="small" icon={<StopOutlined />} onClick={onStopPlayback} aria-label="停止朗读">
            停止
          </Button>
        </div>
      </section>

      <section className={styles.presenceSection}>
        <div className={styles.presenceSectionTitle}><CameraOutlined /> 视觉</div>
        <div className={styles.presenceMetricLabel}>{projection.vision.label}</div>
        <div className={styles.presenceMetricDetail}>{projection.vision.detail}</div>
        <Button size="small" onClick={onOpenCamera} disabled={!onOpenCamera} aria-label="开启视觉输入">
          开启视觉输入
        </Button>
      </section>

      <section className={styles.presenceSection}>
        <div className={styles.presenceSectionTitle}>当前活动</div>
        <ul className={styles.runtimeActivityList}>
          {projection.activities.map((activity) => <li key={activity}>{activity}</li>)}
        </ul>
      </section>
    </aside>
  );
};

export default AgentPresenceRail;
```

- [ ] **Step 4: Add presence rail styles**

In `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`, add style keys inside `createStyles` return object:

```ts
agentPresenceRail: {
  width: 264,
  minWidth: 244,
  maxWidth: 280,
  borderLeft: '1px solid var(--pudding-chat-border)',
  background: 'color-mix(in srgb, var(--pudding-chat-surface) 86%, var(--pudding-chat-surface-muted))',
  padding: 12,
  display: 'flex',
  flexDirection: 'column' as const,
  gap: 10,
  overflowY: 'auto' as const,
  '@media (max-width: 1100px)': {
    width: '100%',
    maxWidth: 'none',
    minWidth: 0,
    borderLeft: 'none',
    borderTop: '1px solid var(--pudding-chat-border)',
  },
},
presenceSection: {
  border: '1px solid var(--pudding-chat-border)',
  borderRadius: 8,
  background: 'var(--pudding-chat-surface)',
  padding: 10,
},
presenceHeader: {
  display: 'flex',
  alignItems: 'flex-start',
  justifyContent: 'space-between',
  gap: 8,
},
presenceEyebrow: {
  fontSize: 11,
  color: 'var(--pudding-chat-text-subtle)',
},
presenceAgentName: {
  fontSize: 14,
  fontWeight: 600,
  color: 'var(--pudding-chat-text)',
},
presenceStatePill: {
  height: 24,
  padding: '0 8px',
  borderRadius: 6,
  border: '1px solid var(--pudding-chat-border)',
  fontSize: 12,
  lineHeight: '22px',
  color: 'var(--pudding-chat-text-muted)',
  background: 'var(--pudding-chat-surface-muted)',
},
presenceDetail: {
  marginTop: 6,
  fontSize: 12,
  color: 'var(--pudding-chat-text-muted)',
  lineHeight: 1.5,
},
presenceAvatarPlaceholder: {
  marginTop: 10,
  minHeight: 72,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  borderRadius: 7,
  background: 'color-mix(in srgb, var(--pudding-chat-surface-muted) 52%, transparent)',
  color: 'var(--pudding-chat-text-subtle)',
  fontSize: 12,
  textAlign: 'center' as const,
  padding: 10,
},
presenceSectionTitle: {
  display: 'flex',
  alignItems: 'center',
  gap: 6,
  fontSize: 12,
  fontWeight: 600,
  color: 'var(--pudding-chat-text)',
  marginBottom: 8,
},
presenceMetricLabel: {
  fontSize: 13,
  color: 'var(--pudding-chat-text)',
  lineHeight: 1.45,
},
presenceMetricDetail: {
  marginTop: 3,
  fontSize: 12,
  color: 'var(--pudding-chat-text-muted)',
  lineHeight: 1.45,
},
presenceActionRow: {
  marginTop: 8,
  display: 'flex',
  gap: 6,
  flexWrap: 'wrap' as const,
},
runtimeActivityList: {
  margin: 0,
  paddingLeft: 16,
  color: 'var(--pudding-chat-text-muted)',
  fontSize: 12,
  lineHeight: 1.6,
},
```

- [ ] **Step 5: Run rail tests and verify they pass**

Run:

```powershell
npm run jest -- src/pages/chat/components/AgentPresenceRail.test.tsx src/pages/chat/hooks/agentPresenceProjection.test.ts --runInBand
```

Expected: PASS.

---

## Task 3: Extract the Intent Console Boundary

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.test.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/InputArea.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`

- [ ] **Step 1: Write the failing workbench-level input tests**

Create `Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.test.tsx`:

```tsx
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import IntentConsole from './IntentConsole';
import type { BrowserVoiceInputAdapter } from '../hooks/browserVoiceInput';

const voiceAdapter: BrowserVoiceInputAdapter = {
  isSupported: () => true,
  start: async (callbacks) => {
    callbacks.onPermissionGranted?.();
    callbacks.onFinalTranscript?.('请总结今天的工作');
    return { stop: jest.fn() };
  },
};

const baseProps = {
  inputValue: '',
  onInputChange: jest.fn(),
  onKeyDown: jest.fn(),
  loading: false,
  onSend: jest.fn(),
  onStop: jest.fn(),
  onExport: jest.fn(),
  disabled: false,
  tLimit: 1000,
  tUsed: 100,
  tPct: 10,
  status: 'idle' as const,
};

describe('IntentConsole', () => {
  it('renders keyboard and voice as first-class modes', () => {
    render(<IntentConsole {...baseProps} voiceInputAdapter={voiceAdapter} />);

    expect(screen.getByRole('tab', { name: '键盘' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: '语音' })).toHaveAttribute('aria-selected', 'false');
    expect(screen.getByTestId('chat-input')).toBeInTheDocument();
  });

  it('sends voice transcript with voice metadata from the console boundary', async () => {
    const sendWithMetadata = jest.fn();
    render(
      <IntentConsole
        {...baseProps}
        voiceInputAdapter={voiceAdapter}
        onSendWithMetadata={sendWithMetadata}
      />,
    );

    fireEvent.click(screen.getByRole('tab', { name: '语音' }));
    fireEvent.click(screen.getByRole('button', { name: '开始语音会话' }));

    await waitFor(() => expect(screen.getByLabelText('语音转写草稿')).toHaveValue('请总结今天的工作'));
    fireEvent.click(screen.getByRole('button', { name: '发送语音内容' }));

    expect(sendWithMetadata).toHaveBeenCalledWith(
      '请总结今天的工作',
      expect.objectContaining({ inputMode: 'voice', asrProvider: 'browser' }),
    );
  });
});
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:

```powershell
npm run jest -- src/pages/chat/components/IntentConsole.test.tsx --runInBand
```

Expected: FAIL because `IntentConsole.tsx` does not exist.

- [ ] **Step 3: Create `IntentConsole` by moving the current `InputArea` implementation**

Copy the current `InputArea.tsx` implementation into `IntentConsole.tsx`, rename the component to `IntentConsole`, and export it as default. Keep the prop interface identical to `InputAreaProps` for the first pass.

At the top of `IntentConsole.tsx`, use:

```tsx
import { AudioOutlined, EditOutlined, PlusOutlined, SendOutlined, StopOutlined } from '@ant-design/icons';
import { Button, Input, Popover, Tooltip } from 'antd';
import React, { useCallback, useRef, useState } from 'react';
import { useChatStyles } from '../styles';
import CommandPalette, { COMMANDS, type Command } from './CommandPalette';
import ComposerActionMenu from './ComposerActionMenu';
import ComposerFeedbackStrip, { type FeedbackState } from './ComposerFeedbackStrip';
import ComposerStatusDetails, { type ComposerRuntimeSummary } from './ComposerStatusDetails';
import CameraInputModal from './CameraInputModal';
import VoiceConversationPanel from './VoiceConversationPanel';
```

Keep this exported type in `IntentConsole.tsx`:

```ts
export type ChatStatus = 'idle' | 'composing' | 'thinking' | 'tool_executing' | 'streaming' | 'completed' | 'error';
```

- [ ] **Step 4: Make `InputArea` a compatibility wrapper**

Replace `Source/PuddingPlatformAdmin/src/pages/chat/components/InputArea.tsx` with:

```tsx
import IntentConsole, { type ChatStatus } from './IntentConsole';

export type { ChatStatus };
export default IntentConsole;
```

This keeps existing imports stable while making the product boundary explicit.

- [ ] **Step 5: Rename the visible mode switch aria label**

In `IntentConsole.tsx`, ensure the mode switch says:

```tsx
<div className={styles.composerModeSwitch} role="tablist" aria-label="意图输入模式">
```

- [ ] **Step 6: Run input tests and verify they pass**

Run:

```powershell
npm run jest -- src/pages/chat/components/IntentConsole.test.tsx src/pages/chat/components/InputArea.test.tsx src/pages/chat/components/VoiceConversationPanel.test.tsx --runInBand
```

Expected: PASS.

---

## Task 4: Compose the Workbench Layout in ChatMain

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.test.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`

- [ ] **Step 1: Update tests to expect the workbench structure**

In `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.test.tsx`, add this test near the avatar runtime tests:

```tsx
it('renders the Agent Workbench with timeline, intent console, and presence rail', () => {
  renderChatMain();

  expect(screen.getByRole('main', { name: 'Agent 工作台' })).toBeInTheDocument();
  expect(screen.getByRole('region', { name: '会话时间线' })).toBeInTheDocument();
  expect(screen.getByRole('tablist', { name: '意图输入模式' })).toBeInTheDocument();
  expect(screen.getByRole('complementary', { name: 'Agent 感知栏' })).toBeInTheDocument();
});
```

Use the existing `renderChatMain()` helper in `ChatMain.test.tsx`.

- [ ] **Step 2: Run ChatMain tests and verify the new test fails**

Run:

```powershell
npm run jest -- src/pages/chat/components/ChatMain.test.tsx --runInBand
```

Expected: FAIL because the ARIA workbench regions do not exist yet.

- [ ] **Step 3: Import the new components and projection**

In `ChatMain.tsx`, replace:

```ts
import InputArea, { type ChatStatus } from './InputArea';
```

with:

```ts
import IntentConsole, { type ChatStatus } from './IntentConsole';
import AgentPresenceRail from './AgentPresenceRail';
import { buildAgentPresenceProjection } from '../hooks/agentPresenceProjection';
```

- [ ] **Step 4: Track latest local voice states for the rail**

In `ChatMain.tsx`, add state near `localInteractionRuntimeEvents`:

```ts
const [latestVoiceCapture, setLatestVoiceCapture] = useState<{ status: string; sessionId?: string; error?: string }>({ status: 'idle' });
const [latestVoicePlayback, setLatestVoicePlayback] = useState<{ status: string; deliveryId?: string; error?: string }>({ status: 'unavailable' });
```

Update `handleVoiceCaptureStatus`:

```ts
setLatestVoiceCapture({
  status: status.toLowerCase(),
  sessionId: detail?.sessionId,
  error: detail?.error,
});
```

Update `handleVoicePlaybackStatus`:

```ts
setLatestVoicePlayback({
  status: status.toLowerCase(),
  deliveryId: detail?.deliveryId,
  error: detail?.error,
});
```

Reset both in the existing `agentId` and `selectedSessionId` effects:

```ts
setLatestVoiceCapture({ status: 'idle' });
setLatestVoicePlayback({ status: 'unavailable' });
```

- [ ] **Step 5: Build the projection in ChatMain**

After `avatarRenderState`, add:

```ts
const presenceProjection = React.useMemo(() => buildAgentPresenceProjection({
  agentName,
  chatStatus,
  avatar: avatarRenderState,
  voiceCapture: latestVoiceCapture,
  voicePlayback: {
    ...latestVoicePlayback,
    canReplay: Boolean(latestAssistantText.trim()),
  },
  camera: {
    supported: typeof navigator !== 'undefined' && Boolean(navigator.mediaDevices?.getUserMedia),
    enabled: Boolean(workspaceId && !disabled && !loading),
    active: false,
  },
  subAgentsRunning: subAgentCount,
}), [agentName, avatarRenderState, chatStatus, disabled, latestAssistantText, latestVoiceCapture, latestVoicePlayback, loading, subAgentCount, workspaceId]);
```

- [ ] **Step 6: Replace the main return layout**

Inside `ChatMain.tsx`, keep `WorkspaceNavigationHeader`, `MessageList`, `DevPanel`, and `IntentConsole`, but wrap them like this:

```tsx
<main className={styles.workbenchShell} aria-label="Agent 工作台">
  <div className={styles.workbenchCenter}>
    <WorkspaceNavigationHeader ... />
    <section className={styles.timelineRegion} aria-label="会话时间线">
      <MessageList ... />
    </section>
    <IntentConsole ... />
  </div>
  <AgentPresenceRail
    projection={presenceProjection}
    onAvatarVisibilityChange={setAvatarVisible}
  />
</main>
```

Use the exact existing props that were previously passed to `InputArea` when rendering `IntentConsole`.

- [ ] **Step 7: Add workbench shell styles**

In `styles.ts`, add:

```ts
workbenchShell: {
  height: '100%',
  minHeight: 0,
  display: 'grid',
  gridTemplateColumns: 'minmax(0, 1fr) 264px',
  background: 'var(--pudding-chat-bg)',
  '@media (max-width: 1100px)': {
    gridTemplateColumns: '1fr',
    gridTemplateRows: 'minmax(0, 1fr) auto',
  },
},
workbenchCenter: {
  minWidth: 0,
  minHeight: 0,
  display: 'flex',
  flexDirection: 'column' as const,
},
timelineRegion: {
  minHeight: 0,
  flex: 1,
  display: 'flex',
  flexDirection: 'column' as const,
},
```

- [ ] **Step 8: Run ChatMain tests**

Run:

```powershell
npm run jest -- src/pages/chat/components/ChatMain.test.tsx src/pages/chat/components/AgentPresenceRail.test.tsx --runInBand
```

Expected: PASS.

---

## Task 5: Render Voice Modality in the Timeline

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/types.ts`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageRow.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.tsx`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.test.tsx`

- [ ] **Step 1: Add a failing voice metadata badge test**

Create `Source/PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import React from 'react';
import UserMessageBubble from './UserMessageBubble';

jest.mock('../styles', () => {
  const styles = new Proxy({}, { get: (_target, prop) => String(prop) });
  return {
    useChatStyles: () => ({
      styles,
      cx: (...names: Array<string | false | undefined>) => names.filter(Boolean).join(' '),
    }),
  };
});

describe('UserMessageBubble voice metadata', () => {
  it('marks user messages sent from voice input', () => {
    render(
      <UserMessageBubble
        content="请总结今天的工作"
        createdAt={1000}
        status="success"
        userName="我"
        modality="voice"
        formatTime={() => '10:24'}
      />,
    );

    expect(screen.getByText('Voice')).toBeInTheDocument();
    expect(screen.getByText('请总结今天的工作')).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run user bubble tests and verify the new test fails**

Run:

```powershell
npm run jest -- src/pages/chat/components/UserMessageBubble.test.tsx --runInBand
```

Expected: FAIL because `UserMessageBubble` does not accept `modality` yet.

- [ ] **Step 3: Carry message metadata into message blocks**

In `Source/PuddingPlatformAdmin/src/pages/chat/types.ts`, add `metadata` to `ChatTurn.userMessage`:

```ts
metadata?: Record<string, string>;
```

Add these fields to `ChatMessageBlock`:

```ts
metadata?: Record<string, string>;
modality?: 'text' | 'voice' | 'camera';
```

In the user block inside `buildMessageBlocks`, add:

```ts
metadata: turn.userMessage.metadata,
modality: turn.userMessage.metadata?.inputMode === 'voice'
  ? 'voice'
  : turn.userMessage.metadata?.inputMode === 'camera'
    ? 'camera'
    : 'text',
```

- [ ] **Step 4: Pass modality into the user message bubble**

In `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageRow.tsx`, add this prop to `UserMessageBubble`:

```tsx
modality={block.modality}
```

- [ ] **Step 5: Render the badge**

In `Source/PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.tsx`, update props:

```ts
modality?: 'text' | 'voice' | 'camera';
```

Destructure `modality`, then render in `styles.userMetaRow` before the user name:

```tsx
{modality === 'voice' ? <span className={styles.messageModalityBadge}>Voice</span> : null}
{modality === 'camera' ? <span className={styles.messageModalityBadge}>Vision</span> : null}
```

- [ ] **Step 6: Add badge style**

In `styles.ts`, add:

```ts
messageModalityBadge: {
  display: 'inline-flex',
  alignItems: 'center',
  height: 18,
  padding: '0 6px',
  borderRadius: 5,
  border: '1px solid var(--pudding-chat-border)',
  color: 'var(--pudding-chat-text-muted)',
  background: 'color-mix(in srgb, var(--pudding-chat-accent) 8%, transparent)',
  fontSize: 11,
  lineHeight: '16px',
},
```

- [ ] **Step 7: Run user bubble tests**

Run:

```powershell
npm run jest -- src/pages/chat/components/UserMessageBubble.test.tsx --runInBand
```

Expected: PASS.

---

## Task 6: Polish Voice Intent Copy and Accessibility

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/VoiceConversationPanel.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/VoiceConversationPanel.test.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`

- [ ] **Step 1: Add an accessibility test for polite status updates**

In `VoiceConversationPanel.test.tsx`, add:

```tsx
it('announces voice state changes without relying on color only', async () => {
  renderVoicePanel();

  fireEvent.click(screen.getByRole('button', { name: '开始语音会话' }));

  await waitFor(() => {
    expect(screen.getByRole('status')).toHaveTextContent(/正在听|正在转写|待确认/);
  });
});
```

Use the existing `ControlledVoicePanel` helper in `VoiceConversationPanel.test.tsx`.

- [ ] **Step 2: Run the voice panel test and verify it fails**

Run:

```powershell
npm run jest -- src/pages/chat/components/VoiceConversationPanel.test.tsx --runInBand
```

Expected: FAIL because the state text does not have `role="status"` yet.

- [ ] **Step 3: Add `role=status` and clearer copy**

In `VoiceConversationPanel.tsx`, change the state element:

```tsx
<span className={styles.voicePanelState} role="status" aria-live="polite">
  {stateLabel[panelState]}
</span>
```

Change the supported subtitle to:

```tsx
{voiceSupported ? 'Agent 可以通过当前浏览器听见你；转写会等待确认。' : '当前浏览器不支持语音输入。'}
```

Change the textarea placeholder to:

```tsx
placeholder="开始语音会话后说话，转写结果会在这里等待确认。"
```

- [ ] **Step 4: Run voice panel tests**

Run:

```powershell
npm run jest -- src/pages/chat/components/VoiceConversationPanel.test.tsx --runInBand
```

Expected: PASS.

---

## Task 7: Run Focused Verification and Visual QA

**Files:**
- No code file changes required unless a preceding test exposes a defect.

- [ ] **Step 1: Run focused Jest suite**

Run from `Source/PuddingPlatformAdmin`:

```powershell
npm run jest -- src/pages/chat/components/AgentPresenceRail.test.tsx src/pages/chat/hooks/agentPresenceProjection.test.ts src/pages/chat/components/IntentConsole.test.tsx src/pages/chat/components/InputArea.test.tsx src/pages/chat/components/VoiceConversationPanel.test.tsx src/pages/chat/components/ChatMain.test.tsx src/pages/chat/components/UserMessageBubble.test.tsx src/pages/chat/components/MessageItem.test.tsx src/pages/chat/components/AgentAvatarRuntimeView.test.tsx --runInBand
```

Expected: all listed suites PASS.

- [ ] **Step 2: Run TypeScript check**

Run:

```powershell
npm run tsc
```

Expected: no new TypeScript errors from the files touched in this plan. If pre-existing unrelated TypeScript errors appear, record the exact file paths and error codes in the handoff summary instead of hiding them.

- [ ] **Step 3: Start or reuse the frontend dev server**

If no dev server is running, run:

```powershell
npm run start:dev
```

Expected: frontend available through the existing local admin route.

- [ ] **Step 4: Browser smoke test**

Use the in-app Browser plugin against:

```text
http://localhost/admin/chat?workspaceId=default&agentId=default.global_general-assistant.401
```

Verify:

- The page exposes an Agent Workbench main region.
- The right Agent presence rail is visible on desktop width.
- Switching to Voice mode opens a full voice panel.
- Voice controls are not limited to a small microphone button.
- The Agent presence rail changes when voice recording/playback status changes.
- Camera appears as a visual input capability but does not dominate the first voice workflow.

- [ ] **Step 5: Responsive smoke test**

In the Browser plugin, check desktop and a narrow mobile viewport.

Expected:

- Desktop: center timeline, bottom intent console, right presence rail.
- Narrow viewport: no text overlap; right presence rail collapses or stacks below without hiding the intent console.

---

## Self-Review Checklist

- Spec coverage:
  - Text interaction remains supported through keyboard mode.
  - Voice input is a dedicated panel inside `IntentConsole`.
  - Voice output is represented in `AgentPresenceRail` and through latest answer replay.
  - Camera remains an explicit visual input capability without expanding first scope.
  - Sprite/static avatar is retained through `AgentAvatarRuntimeView` and moved into presence semantics.
  - Event-driven architecture is represented through frontend projection boundaries.
  - UI consistency is improved through explicit workbench layout and shared styles.

- Red-flag text scan:
  - Search this file for unfinished-work markers, vague future-work phrases, and copy-paste shortcuts.
  - Expected result: no matches except this instruction line.

- Type consistency:
  - `ChatStatus` remains exported from `InputArea.tsx` through compatibility wrapper.
  - `IntentConsole` owns the real implementation.
  - `AgentPresenceProjection` has a stable input/output shape for `ChatMain`.
  - Voice status strings are lower-case values already produced by `ChatMain` handlers.

## Execution Notes

- Do not start by renaming every existing component. First make the product surfaces explicit and tested.
- Preserve current route compatibility for `/admin/chat`.
- Keep camera as a visible capability, but do not couple camera streaming work to the first voice workbench delivery.
- Keep commits or review chunks task-sized. This work touches shared chat files and should be easy to bisect.
