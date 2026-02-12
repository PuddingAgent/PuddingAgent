export interface BrowserCameraFrame {
  blob: Blob;
  width: number;
  height: number;
  capturedAt: number;
  mimeType: string;
}

export interface BrowserCameraInputHandle {
  stream: MediaStream;
  deviceLabel?: string;
  stop: () => void;
  captureFrame: (video: HTMLVideoElement) => Promise<BrowserCameraFrame>;
}

export interface BrowserCameraInputAdapter {
  isSupported: () => boolean;
  startPreview: () => Promise<BrowserCameraInputHandle>;
}

const DEFAULT_MIME_TYPE = 'image/jpeg';
const DEFAULT_QUALITY = 0.86;

function stopStream(stream: MediaStream): void {
  for (const track of stream.getTracks()) {
    track.stop();
  }
}

function canvasToBlob(
  canvas: HTMLCanvasElement,
  mimeType: string,
  quality: number,
): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => {
        if (!blob) {
          reject(new Error('无法从摄像头画面生成图片'));
          return;
        }
        resolve(blob);
      },
      mimeType,
      quality,
    );
  });
}

export const defaultBrowserCameraInputAdapter: BrowserCameraInputAdapter = {
  isSupported: () => Boolean(globalThis.navigator?.mediaDevices?.getUserMedia),

  async startPreview() {
    if (!globalThis.navigator?.mediaDevices?.getUserMedia) {
      throw new Error('当前浏览器不支持摄像头输入');
    }

    const stream = await globalThis.navigator.mediaDevices.getUserMedia({
      video: {
        width: { ideal: 1280 },
        height: { ideal: 720 },
        facingMode: { ideal: 'environment' },
      },
      audio: false,
    });
    const track = stream.getVideoTracks()[0];

    return {
      stream,
      deviceLabel: track?.label,
      stop: () => stopStream(stream),
      captureFrame: async (video) => {
        const settings = track?.getSettings?.() ?? {};
        const width = video.videoWidth || settings.width || 1280;
        const height = video.videoHeight || settings.height || 720;

        if (!width || !height) {
          throw new Error('摄像头画面尚未准备好');
        }

        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext('2d');
        if (!ctx) {
          throw new Error('浏览器无法创建图像画布');
        }

        ctx.drawImage(video, 0, 0, width, height);
        const blob = await canvasToBlob(
          canvas,
          DEFAULT_MIME_TYPE,
          DEFAULT_QUALITY,
        );
        return {
          blob,
          width,
          height,
          capturedAt: Date.now(),
          mimeType: DEFAULT_MIME_TYPE,
        };
      },
    };
  },
};
