import {
  type CameraCaptureSession,
  reduceCameraCaptureSession,
  toCameraMessageDraft,
} from './cameraCaptureSession';

describe('cameraCaptureSession', () => {
  it('tracks explicit camera permission before preview starts', () => {
    const requested = reduceCameraCaptureSession(undefined, {
      type: 'permission_requested',
      sessionId: 'camera-1',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      now: 100,
    });

    expect(requested).toMatchObject({
      sessionId: 'camera-1',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'requesting_permission',
      permission: 'prompt',
      updatedAt: 100,
    });

    const granted = reduceCameraCaptureSession(requested, {
      type: 'permission_granted',
      sessionId: 'camera-1',
      deviceLabel: 'FaceTime Camera',
      now: 110,
    });

    expect(granted.status).toBe('ready');
    expect(granted.permission).toBe('granted');
    expect(granted.deviceLabel).toBe('FaceTime Camera');
  });

  it('starts local preview with visible resolution without retaining raw frame bytes', () => {
    const ready: CameraCaptureSession = {
      sessionId: 'camera-2',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'ready',
      permission: 'granted',
      framesCaptured: 0,
      updatedAt: 200,
    };

    const previewing = reduceCameraCaptureSession(ready, {
      type: 'preview_started',
      sessionId: 'camera-2',
      width: 1280,
      height: 720,
      now: 210,
    });

    expect(previewing.status).toBe('previewing');
    expect(previewing.width).toBe(1280);
    expect(previewing.height).toBe(720);
    expect('frameBytes' in previewing).toBe(false);
  });

  it('captures a snapshot artifact and waits for user confirmation before sending', () => {
    const previewing: CameraCaptureSession = {
      sessionId: 'camera-3',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'previewing',
      permission: 'granted',
      width: 1280,
      height: 720,
      framesCaptured: 0,
      updatedAt: 300,
    };

    const capturing = reduceCameraCaptureSession(previewing, {
      type: 'snapshot_requested',
      sessionId: 'camera-3',
      now: 310,
    });
    const captured = reduceCameraCaptureSession(capturing, {
      type: 'snapshot_captured',
      sessionId: 'camera-3',
      artifactId: 'vision-frame-1',
      mimeType: 'image/jpeg',
      width: 1280,
      height: 720,
      capturedAt: 320,
      now: 330,
    });

    expect(captured.status).toBe('awaiting_confirmation');
    expect(captured.latestArtifactId).toBe('vision-frame-1');
    expect(captured.mimeType).toBe('image/jpeg');
    expect(captured.framesCaptured).toBe(1);

    const sending = reduceCameraCaptureSession(captured, {
      type: 'confirm_send',
      sessionId: 'camera-3',
      now: 340,
    });
    expect(sending.status).toBe('sending');
  });

  it('tracks periodic sampling frequency and pause state explicitly', () => {
    const previewing: CameraCaptureSession = {
      sessionId: 'camera-4',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'previewing',
      permission: 'granted',
      framesCaptured: 0,
      updatedAt: 400,
    };

    const sampling = reduceCameraCaptureSession(previewing, {
      type: 'start_sampling',
      sessionId: 'camera-4',
      intervalMs: 5000,
      now: 410,
    });
    const sampled = reduceCameraCaptureSession(sampling, {
      type: 'sample_captured',
      sessionId: 'camera-4',
      artifactId: 'vision-frame-2',
      mimeType: 'image/png',
      capturedAt: 420,
      now: 430,
    });
    const paused = reduceCameraCaptureSession(sampled, {
      type: 'pause_sampling',
      sessionId: 'camera-4',
      now: 440,
    });

    expect(sampled.status).toBe('sampling');
    expect(sampled.samplingIntervalMs).toBe(5000);
    expect(sampled.framesCaptured).toBe(1);
    expect(paused.status).toBe('paused');
  });

  it('builds a camera-originated message draft only from a captured artifact', () => {
    const session: CameraCaptureSession = {
      sessionId: 'camera-5',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'awaiting_confirmation',
      permission: 'granted',
      framesCaptured: 1,
      latestArtifactId: 'vision-frame-3',
      mimeType: 'image/jpeg',
      width: 1920,
      height: 1080,
      prompt: '请分析这张截图。',
      updatedAt: 500,
    };

    const draft = toCameraMessageDraft(session);

    expect(draft).toEqual({
      roomId: 'room-default',
      content: '请分析这张截图。',
      metadata: {
        inputMode: 'camera',
        cameraSessionId: 'camera-5',
        visionArtifactId: 'vision-frame-3',
        mimeType: 'image/jpeg',
        width: '1920',
        height: '1080',
      },
    });
  });

  it('handles denied permission, stop, cancellation, and camera failures explicitly', () => {
    const requested = reduceCameraCaptureSession(undefined, {
      type: 'permission_requested',
      sessionId: 'camera-6',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      now: 600,
    });
    const denied = reduceCameraCaptureSession(requested, {
      type: 'permission_denied',
      sessionId: 'camera-6',
      error: 'camera permission denied',
      now: 610,
    });
    expect(denied.status).toBe('failed');
    expect(denied.permission).toBe('denied');

    const stopped = reduceCameraCaptureSession(denied, {
      type: 'stop',
      sessionId: 'camera-6',
      now: 620,
    });
    expect(stopped.status).toBe('completed');

    const cancelled = reduceCameraCaptureSession(stopped, {
      type: 'cancel',
      sessionId: 'camera-6',
      now: 630,
    });
    expect(cancelled.status).toBe('cancelled');

    const failed = reduceCameraCaptureSession(cancelled, {
      type: 'fail',
      sessionId: 'camera-6',
      error: 'camera stream ended',
      now: 640,
    });
    expect(failed.status).toBe('failed');
    expect(failed.error).toBe('camera stream ended');
  });
});
