import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import CameraInputModal from './CameraInputModal';

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({
      styles,
    }),
  };
});

const createCameraAdapter = () => {
  const stop = jest.fn();
  const captureFrame = jest.fn(async () => ({
    blob: new Blob(['jpeg'], { type: 'image/jpeg' }),
    width: 640,
    height: 360,
    capturedAt: 1234,
    mimeType: 'image/jpeg',
  }));
  const stream = {
    getVideoTracks: () => [
      {
        label: 'Test Camera',
        getSettings: () => ({ width: 640, height: 360 }),
        stop,
      },
    ],
    getTracks: () => [{ stop }],
  } as unknown as MediaStream;

  return {
    adapter: {
      isSupported: () => true,
      startPreview: jest.fn(async () => ({
        stream,
        deviceLabel: 'Test Camera',
        stop,
        captureFrame,
      })),
    },
    captureFrame,
    stop,
  };
};

describe('CameraInputModal', () => {
  beforeEach(() => {
    Object.defineProperty(HTMLMediaElement.prototype, 'play', {
      configurable: true,
      value: jest.fn().mockResolvedValue(undefined),
    });
    if (typeof globalThis.URL.createObjectURL !== 'function') {
      (
        globalThis.URL as unknown as { createObjectURL: (blob: Blob) => string }
      ).createObjectURL = () => 'blob:camera-preview';
    }
    if (typeof globalThis.URL.revokeObjectURL !== 'function') {
      (
        globalThis.URL as unknown as { revokeObjectURL: (url: string) => void }
      ).revokeObjectURL = () => {};
    }
  });

  it('captures, uploads, and sends only a visual artifact reference', async () => {
    const camera = createCameraAdapter();
    const uploadArtifact = jest.fn(async () => ({
      artifactId: 'vision-artifact-1',
      workspaceId: 'default',
      mimeType: 'image/jpeg',
      width: 640,
      height: 360,
      capturedAt: 1234,
    }));
    const onSend = jest.fn();

    render(
      <CameraInputModal
        open
        workspaceId="default"
        initialPrompt="请看这张图。"
        cameraInputAdapter={camera.adapter}
        uploadArtifact={uploadArtifact}
        onCancel={jest.fn()}
        onSend={onSend}
      />,
    );

    const captureButton = await screen.findByRole('button', {
      name: /截取画面/,
    });
    await waitFor(() => {
      expect(camera.adapter.startPreview).toHaveBeenCalledTimes(1);
      expect((captureButton as HTMLButtonElement).disabled).toBe(false);
    });

    fireEvent.click(captureButton);

    await waitFor(() => {
      expect(uploadArtifact).toHaveBeenCalledWith(
        'default',
        expect.any(Blob),
        { width: 640, height: 360, capturedAt: 1234 },
        expect.any(AbortSignal),
      );
    });

    const sendButton = await screen.findByRole('button', { name: /发送图像/ });
    await waitFor(() => {
      expect((sendButton as HTMLButtonElement).disabled).toBe(false);
    });
    fireEvent.click(sendButton);

    await waitFor(() => {
      expect(onSend).toHaveBeenCalledWith('请看这张图。', {
        inputMode: 'camera',
        cameraSessionId: expect.stringMatching(/^camera-/),
        visionArtifactId: 'vision-artifact-1',
        mimeType: 'image/jpeg',
        width: '640',
        height: '360',
      });
    });
  });
});
