import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { uploadVisionArtifact } from '@/services/platform/api';
import IntentConsole from './IntentConsole';

jest.mock('@/services/platform/api', () => ({
  getCacheDiagnostics: jest.fn(),
  getContextHealth: jest.fn(),
  uploadVisionArtifact: jest.fn(),
}));

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

jest.mock('./CommandPalette', () => ({
  __esModule: true,
  COMMANDS: [],
  filterCommands: () => [],
  default: () => null,
}));

jest.mock('./ComposerActionMenu', () => () => null);
jest.mock(
  './ComposerFeedbackStrip',
  () =>
    ({ state }: { state: { subAgentsRunning: number } }) => (
      <div data-testid="feedback-strip">子任务 {state.subAgentsRunning}</div>
    ),
);
jest.mock(
  './ComposerStatusDetails',
  () =>
    ({ summary }: { summary: { subAgentsRunning: number } }) => (
      <div data-testid="status-details">运行中 {summary.subAgentsRunning}</div>
    ),
);

const voiceAdapter = {
  isSupported: () => true,
  start: jest.fn(async (callbacks: any) => {
    callbacks.onPermissionGranted?.('Built-in Microphone');
    callbacks.onFinalTranscript?.('请总结今天的工作');
    return { stop: jest.fn() };
  }),
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
  beforeEach(() => {
    jest.clearAllMocks();
    (URL.createObjectURL as jest.Mock).mockImplementation(
      (file: File) => `blob:${file.name}`,
    );
    URL.revokeObjectURL = jest.fn();
    global.Image = class {
      naturalWidth = 64;
      naturalHeight = 64;
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      set src(_value: string) {
        this.onload?.();
      }
    } as unknown as typeof Image;
  });

  afterEach(() => {
    window.history.pushState({}, '', '/');
  });

  it('renders a capsule keyboard composer with voice entry', () => {
    render(<IntentConsole {...baseProps} voiceInputAdapter={voiceAdapter} />);

    expect(screen.getByTestId('chat-input')).toBeTruthy();
    expect(screen.getByRole('button', { name: '开始语音输入' })).toBeTruthy();
  });

  it('exposes a URL-gated browser test greeting filler', () => {
    const onInputChange = jest.fn();
    window.history.pushState(
      {},
      '',
      '/admin/chat?workspaceId=default&uiTest=1',
    );

    render(
      <IntentConsole
        {...baseProps}
        onInputChange={onInputChange}
        voiceInputAdapter={voiceAdapter}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: '填入测试问候' }));

    expect(onInputChange).toHaveBeenCalledWith('你好');
  });

  it('sends voice transcript with voice metadata from the console boundary', async () => {
    const sendWithMetadata = jest.fn();

    function ControlledIntentConsole() {
      const [value, setValue] = React.useState('');
      return (
        <IntentConsole
          {...baseProps}
          inputValue={value}
          onInputChange={setValue}
          voiceInputAdapter={voiceAdapter}
          onSendWithMetadata={sendWithMetadata}
        />
      );
    }

    render(<ControlledIntentConsole />);

    fireEvent.click(screen.getByRole('button', { name: '开始语音输入' }));
    fireEvent.click(screen.getByRole('button', { name: '开始语音会话' }));

    await waitFor(() => {
      expect(screen.getByDisplayValue('请总结今天的工作')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: '发送语音内容' }));

    await waitFor(() => {
      expect(sendWithMetadata).toHaveBeenCalledWith(
        '请总结今天的工作',
        expect.objectContaining({ inputMode: 'voice', asrProvider: 'browser' }),
      );
    });
  });

  it('renders backend queued interactions as read-only snapshots with steering control', async () => {
    const updateQueued = jest.fn();
    const steerQueued = jest.fn(async () => {});

    render(
      <IntentConsole
        {...baseProps}
        loading
        status="streaming"
        interactionQueue={[
          {
            id: 'queue-1',
            text: '请先检查最新日志',
            createdAt: Date.now(),
            status: 'queued',
            source: 'backend_message_queue',
          },
        ]}
        onUpdateQueuedInteraction={updateQueued}
        onSteerQueuedInteraction={steerQueued}
      />,
    );

    expect(screen.getByTestId('interaction-queue')).toBeTruthy();
    expect(
      screen.getByText('后端消息队列快照，调度由 Agent 服务管理'),
    ).toBeTruthy();
    const queueMessage = screen.getByLabelText('队列消息');
    expect(queueMessage.getAttribute('aria-readonly')).toBe('true');
    expect(queueMessage.tagName).toBe('DIV');
    fireEvent.click(screen.getByRole('button', { name: '引导 Agent' }));

    expect(updateQueued).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(steerQueued).toHaveBeenCalledWith('queue-1');
    });
  });

  it('P1#10: renders retrying via queue dropdown — real retry warning + summary, busy-wait waiting without error', () => {
    render(
      <IntentConsole
        {...baseProps}
        loading
        status="streaming"
        interactionQueue={[
          {
            id: 'retry-1',
            text: '请重试该任务',
            createdAt: Date.now(),
            status: 'retrying',
            source: 'backend_message_queue',
            error: '{"message":"执行超时，正在重试"}',
            metadata: { attemptCount: '2' },
          },
          {
            id: 'busy-1',
            text: '等 Agent 空闲',
            createdAt: Date.now(),
            status: 'retrying',
            source: 'backend_message_queue',
            error:
              '{"executionState":"Busy","message":"Agent 正在处理其他请求"}',
            metadata: { attemptCount: '1' },
            waitReason: 'busy-wait',
          },
        ]}
      />,
    );

    // retrying ×2 归入排队：排队 2 · 执行 0 · 终态 0
    expect(screen.getByText('排队 2 · 执行 0 · 终态 0')).toBeTruthy();
    // 真实失败重试：警示标签 + 尝试次数 + 摘要错误
    expect(screen.getByText('重试中 · 第 2 次')).toBeTruthy();
    expect(screen.getByText('执行超时，正在重试')).toBeTruthy();
    // busy-wait：等待标签，且不渲染任何错误
    expect(screen.getByText('排队中 · 等待 Agent 空闲')).toBeTruthy();
    expect(screen.queryByText('Agent 正在处理其他请求')).toBeNull();
    // 不再渲染红色原文 JSON
    expect(screen.queryByText(/executionState/)).toBeNull();
  });

  it('renders injected steering state with round and latency diagnostics', () => {
    render(
      <IntentConsole
        {...baseProps}
        loading
        status="streaming"
        interactionQueue={[
          {
            id: 'queue-1',
            text: '请优先检查注入状态',
            createdAt: Date.now() - 5000,
            status: 'steering_injected',
            steeringId: 'steering-1',
            submittedAt: 1000,
            injectedAt: 3250,
            injectedRound: 4,
            injectionLatencyMs: 2250,
          },
        ]}
      />,
    );

    expect(screen.getByText('已注入 · 第 4 轮')).toBeTruthy();
    expect(screen.getByText('提交后 2.3s 注入，稍后自动收起')).toBeTruthy();
    expect(
      screen.getByLabelText('队列消息').getAttribute('aria-readonly'),
    ).toBe('true');
  });

  it('stages multiple images, allows removal and sends remaining images with edited text', async () => {
    const sendWithMetadata = jest.fn(async () => {});
    const upload = uploadVisionArtifact as jest.Mock;
    upload
      .mockResolvedValueOnce({
        artifactId: 'vision-first',
        mimeType: 'image/png',
        capturedAt: 1,
      })
      .mockResolvedValueOnce({
        artifactId: 'vision-second',
        mimeType: 'image/png',
        capturedAt: 2,
      });

    function ControlledIntentConsole() {
      const [value, setValue] = React.useState('');
      return (
        <IntentConsole
          {...baseProps}
          workspaceId="default"
          inputValue={value}
          onInputChange={setValue}
          onSendWithMetadata={sendWithMetadata}
        />
      );
    }

    render(<ControlledIntentConsole />);
    const first = new File(['first'], 'first.png', { type: 'image/png' });
    const second = new File(['second'], 'second.png', { type: 'image/png' });
    fireEvent.change(screen.getByTestId('image-file-input'), {
      target: { files: [first, second] },
    });

    expect(screen.getAllByTestId('image-preview-item')).toHaveLength(2);
    fireEvent.click(screen.getByRole('button', { name: '移除图片 first.png' }));
    expect(screen.getAllByTestId('image-preview-item')).toHaveLength(1);
    fireEvent.change(screen.getByTestId('image-file-input'), {
      target: { files: [first] },
    });
    fireEvent.change(screen.getByTestId('chat-input'), {
      target: { value: '比较这两张图' },
    });
    fireEvent.click(screen.getByRole('button', { name: '发送' }));

    await waitFor(() => expect(sendWithMetadata).toHaveBeenCalledTimes(1));
    expect(upload).toHaveBeenCalledTimes(2);
    // ADR-077：图片以 typed content parts 提交；metadata 只保留投影事实。
    expect(sendWithMetadata).toHaveBeenCalledWith(
      '比较这两张图',
      expect.objectContaining({
        inputMode: 'image',
        imageCount: '2',
      }),
      [
        { type: 'image', artifactId: 'vision-first', detail: 'original' },
        { type: 'image', artifactId: 'vision-second', detail: 'original' },
      ],
    );
    expect(screen.queryByTestId('image-preview-list')).toBeNull();
  });

  it('converts BMP images to PNG before uploading', async () => {
    const sendWithMetadata = jest.fn(async () => {});
    const upload = uploadVisionArtifact as jest.Mock;
    upload.mockResolvedValue({
      artifactId: 'vision-bmp-converted',
      mimeType: 'image/png',
      capturedAt: 1,
    });
    const drawImage = jest.fn();
    const originalCreateElement = document.createElement.bind(document);
    const createElement = jest
      .spyOn(document, 'createElement')
      .mockImplementation((tagName: string, options?: ElementCreationOptions) => {
        if (tagName.toLowerCase() !== 'canvas') {
          return originalCreateElement(tagName, options);
        }
        return {
          width: 0,
          height: 0,
          getContext: () => ({ drawImage }),
          toBlob: (callback: BlobCallback) =>
            callback(new Blob(['png'], { type: 'image/png' })),
        } as unknown as HTMLCanvasElement;
      });

    try {
      render(
        <IntentConsole
          {...baseProps}
          workspaceId="default"
          onSendWithMetadata={sendWithMetadata}
        />,
      );
      const bmp = new File(['bitmap'], 'clipboard.bmp', {
        type: 'image/bmp',
      });
      fireEvent.change(screen.getByTestId('image-file-input'), {
        target: { files: [bmp] },
      });
      fireEvent.click(screen.getByRole('button', { name: '发送' }));

      await waitFor(() => expect(upload).toHaveBeenCalledTimes(1));
      const uploadedFile = upload.mock.calls[0][1] as File;
      expect(uploadedFile.name).toBe('clipboard.png');
      expect(uploadedFile.type).toBe('image/png');
      expect(drawImage).toHaveBeenCalledTimes(1);
      expect(sendWithMetadata).toHaveBeenCalledTimes(1);
    } finally {
      createElement.mockRestore();
    }
  });
});
