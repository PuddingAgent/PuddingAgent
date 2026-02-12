import {
  type AgentAvatarRuntimeState,
  reduceAgentAvatarRuntime,
  selectAvatarRenderState,
} from './agentAvatarRuntime';

describe('agentAvatarRuntime', () => {
  it('initializes a sprite avatar runtime from configured assets without starting fake activity', () => {
    const runtime = reduceAgentAvatarRuntime(undefined, {
      type: 'configure',
      agentId: 'default-agent',
      avatarId: 'neutral',
      runtimeKind: 'sprite',
      spriteSheetUrl: '/admin/assets/agent-sprites/neutral/spritesheet.png',
      now: 100,
    });

    expect(runtime).toMatchObject({
      agentId: 'default-agent',
      avatarId: 'neutral',
      runtimeKind: 'sprite',
      status: 'idle',
      expression: 'neutral',
      motion: 'idle',
      visible: true,
      motionEnabled: true,
      updatedAt: 100,
    });
  });

  it('maps real voice capture and playback projection states to listening and speaking', () => {
    const runtime: AgentAvatarRuntimeState = {
      agentId: 'default-agent',
      avatarId: 'neutral',
      runtimeKind: 'sprite',
      status: 'idle',
      expression: 'neutral',
      motion: 'idle',
      visible: true,
      motionEnabled: true,
      updatedAt: 200,
    };

    const listening = reduceAgentAvatarRuntime(runtime, {
      type: 'voice_capture_status',
      agentId: 'default-agent',
      status: 'recording',
      sessionId: 'voice-1',
      now: 210,
    });
    const speaking = reduceAgentAvatarRuntime(listening, {
      type: 'voice_playback_status',
      agentId: 'default-agent',
      status: 'playing',
      deliveryId: 'delivery-1',
      now: 220,
    });

    expect(listening.status).toBe('listening');
    expect(listening.motion).toBe('listen');
    expect(listening.activeVoiceSessionId).toBe('voice-1');
    expect(speaking.status).toBe('speaking');
    expect(speaking.motion).toBe('talk');
    expect(speaking.activeDeliveryId).toBe('delivery-1');
  });

  it('maps camera preview and visual reasoning to seeing and thinking without storing media bytes', () => {
    const runtime: AgentAvatarRuntimeState = {
      agentId: 'default-agent',
      avatarId: 'neutral',
      runtimeKind: 'sprite',
      status: 'idle',
      expression: 'neutral',
      motion: 'idle',
      visible: true,
      motionEnabled: true,
      updatedAt: 300,
    };

    const seeing = reduceAgentAvatarRuntime(runtime, {
      type: 'camera_capture_status',
      agentId: 'default-agent',
      status: 'previewing',
      sessionId: 'camera-1',
      artifactId: 'vision-frame-1',
      now: 310,
    });
    const thinking = reduceAgentAvatarRuntime(seeing, {
      type: 'visual_reasoning_status',
      agentId: 'default-agent',
      status: 'reasoning',
      sessionId: 'vision-1',
      now: 320,
    });

    expect(seeing.status).toBe('seeing');
    expect(seeing.motion).toBe('look');
    expect(seeing.activeCameraSessionId).toBe('camera-1');
    expect(seeing.latestVisionArtifactId).toBe('vision-frame-1');
    expect('frameBytes' in seeing).toBe(false);
    expect(thinking.status).toBe('thinking');
    expect(thinking.motion).toBe('think');
    expect(thinking.activeVisionSessionId).toBe('vision-1');
  });

  it('returns to idle when realtime voice, camera, and visual sessions complete', () => {
    const runtime: AgentAvatarRuntimeState = {
      agentId: 'default-agent',
      avatarId: 'neutral',
      runtimeKind: 'sprite',
      status: 'thinking',
      expression: 'thinking',
      motion: 'think',
      visible: true,
      motionEnabled: true,
      updatedAt: 300,
    };

    const voiceDone = reduceAgentAvatarRuntime(runtime, {
      type: 'voice_capture_status',
      agentId: 'default-agent',
      status: 'completed',
      now: 310,
    });
    const cameraDone = reduceAgentAvatarRuntime(voiceDone, {
      type: 'camera_capture_status',
      agentId: 'default-agent',
      status: 'done',
      now: 320,
    });
    const visualDone = reduceAgentAvatarRuntime(cameraDone, {
      type: 'visual_reasoning_status',
      agentId: 'default-agent',
      status: 'completed',
      now: 330,
    });

    expect(voiceDone.status).toBe('idle');
    expect(cameraDone.status).toBe('idle');
    expect(visualDone.status).toBe('idle');
    expect(visualDone.motion).toBe('idle');
  });

  it('maps assistant response projection states to thinking, speaking, and tool activity', () => {
    const runtime: AgentAvatarRuntimeState = {
      agentId: 'default-agent',
      avatarId: 'neutral',
      runtimeKind: 'sprite',
      status: 'idle',
      expression: 'neutral',
      motion: 'idle',
      visible: true,
      motionEnabled: true,
      updatedAt: 330,
    };

    const thinking = reduceAgentAvatarRuntime(runtime, {
      type: 'assistant_response_status',
      agentId: 'default-agent',
      status: 'thinking',
      now: 340,
    });
    const speaking = reduceAgentAvatarRuntime(thinking, {
      type: 'assistant_response_status',
      agentId: 'default-agent',
      status: 'streaming',
      now: 350,
    });
    const tool = reduceAgentAvatarRuntime(speaking, {
      type: 'assistant_response_status',
      agentId: 'default-agent',
      status: 'tool_executing',
      now: 360,
    });

    expect(thinking.status).toBe('thinking');
    expect(speaking.status).toBe('speaking');
    expect(tool.status).toBe('tool');
  });

  it('gives error and reduced motion policies precedence in render state', () => {
    const runtime = reduceAgentAvatarRuntime(undefined, {
      type: 'configure',
      agentId: 'default-agent',
      avatarId: 'neutral',
      runtimeKind: 'sprite',
      spriteSheetUrl: '/admin/assets/agent-sprites/neutral/spritesheet.png',
      reducedMotion: true,
      now: 400,
    });
    const failed = reduceAgentAvatarRuntime(runtime, {
      type: 'error',
      agentId: 'default-agent',
      message: 'tts playback failed',
      now: 410,
    });

    const renderState = selectAvatarRenderState(failed);

    expect(failed.status).toBe('error');
    expect(failed.expression).toBe('concerned');
    expect(failed.error).toBe('tts playback failed');
    expect(renderState).toMatchObject({
      runtimeKind: 'sprite',
      status: 'error',
      spriteSheetUrl: '/admin/assets/agent-sprites/neutral/spritesheet.png',
      spriteRow: 6,
      spriteFrameCount: 1,
      ariaLabel: 'Agent default-agent 状态：异常',
    });
  });

  it('can be hidden or put to sleep without losing configured avatar assets', () => {
    const runtime = reduceAgentAvatarRuntime(undefined, {
      type: 'configure',
      agentId: 'default-agent',
      avatarId: 'sleepy',
      runtimeKind: 'sprite',
      spriteSheetUrl: '/admin/assets/agent-sprites/sleepy/spritesheet.png',
      now: 500,
    });
    const hidden = reduceAgentAvatarRuntime(runtime, {
      type: 'visibility_changed',
      agentId: 'default-agent',
      visible: false,
      now: 510,
    });
    const sleeping = reduceAgentAvatarRuntime(hidden, {
      type: 'agent_availability_changed',
      agentId: 'default-agent',
      enabled: false,
      now: 520,
    });

    expect(hidden.visible).toBe(false);
    expect(sleeping.status).toBe('sleeping');
    expect(sleeping.spriteSheetUrl).toBe(
      '/admin/assets/agent-sprites/sleepy/spritesheet.png',
    );
  });
});
