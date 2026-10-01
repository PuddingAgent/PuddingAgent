import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { getIndexStatus } from '@/pages/index-status/api';
import {
  getCacheDiagnostics,
  getContextHealth,
  getSubconsciousDebugState,
  listLlmProviders,
  uploadVisionArtifact,
} from '@/services/platform/api';
import IntentConsole from './IntentConsole';

jest.mock('@/services/platform/api', () => ({
  getCacheDiagnostics: jest.fn(),
  getContextHealth: jest.fn(),
  getSubconsciousDebugState: jest.fn(),
  listLlmProviders: jest.fn(),
  uploadVisionArtifact: jest.fn(),
}));

// 索引态复用 index-status 的只读端点：只替身网络函数，保留纯函数（deriveIndexHealth 等）。
jest.mock('@/pages/index-status/api', () => ({
  ...jest.requireActual('@/pages/index-status/api'),
  getIndexStatus: jest.fn(),
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

// ContextUsageRing 是工具栏上的圆环控件：生产里 ComposerStatusDetails 是作为 `runtimeDetails`
// 传给它的，**只有气泡打开时才进 DOM**。本文件的用例考察运行摘要字段的透传，
// 与圆环外壳无关 ⇒ 替身直接渲染 runtimeDetails（与 InputArea.test.tsx 同一约定）。
jest.mock('./ContextUsageRing', () => {
  const ReactLib = require('react');
  const Stub = ({ runtimeDetails }: { runtimeDetails?: unknown }) =>
    ReactLib.createElement(
      'div',
      { 'data-testid': 'context-usage-ring' },
      runtimeDetails,
    );
  return { __esModule: true, default: Stub, ContextUsageRing: Stub };
});

jest.mock('./ComposerActionMenu', () => () => null);
// 运行摘要替身：既保留原有「运行中 N」渲染，也把本次任务新增的字段逐个暴露成可断言节点
// （缓存口径/样本数、四个服务态、timing/usage 透传）。
jest.mock(
  './ComposerStatusDetails',
  () =>
    ({ summary }: { summary: Record<string, any> }) => (
      <div data-testid="status-details">
        <span data-testid="summary-sub-agents">
          运行中 {summary.subAgentsRunning}
        </span>
        <span data-testid="summary-cache-hit-rate">
          {String(summary.cacheHitRate)}
        </span>
        <span data-testid="summary-cache-scope">
          {String(summary.cacheHitRateScope)}
        </span>
        <span data-testid="summary-cache-samples">
          {String(summary.cacheHitRateSampleCount)}
        </span>
        <span data-testid="summary-context-service">
          {String(summary.contextService)}
        </span>
        <span data-testid="summary-index">{String(summary.index)}</span>
        <span data-testid="summary-background-memory">
          {String(summary.backgroundMemory)}
        </span>
        <span data-testid="summary-model-service">
          {String(summary.modelService)}
        </span>
        <span data-testid="summary-completed-ms">
          {String(summary.turnTimings?.completedMs)}
        </span>
        <span data-testid="summary-prompt-tokens">
          {String(summary.usage?.promptTokens)}
        </span>
        <span data-testid="summary-completion-tokens">
          {String(summary.usage?.completionTokens)}
        </span>
      </div>
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
      screen.getByText('仅显示未认领消息；认领后转入会话轨迹 · ⚡ 可插嘴当前 Agent'),
    ).toBeTruthy();
    const queueMessage = screen.getByLabelText('队列消息');
    expect(queueMessage.getAttribute('aria-readonly')).toBe('true');
    expect(queueMessage.tagName).toBe('DIV');

    // 展开队列面板后动作按钮才进入可访问树（aria-hidden=!open）
    fireEvent.click(screen.getByTestId('message-queue-trigger'));
    // 后端队列项不能在前端转换为插嘴（避免重复执行），⚡ 按钮保持禁用
    const steerButton = screen.getByRole('button', {
      name: '引导 Agent',
    }) as HTMLButtonElement;
    expect(steerButton.disabled).toBe(true);
    expect(updateQueued).not.toHaveBeenCalled();
    expect(steerQueued).not.toHaveBeenCalled();
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

    // retrying ×2 归入排队：2 待认领 · 0 引导中 · 0 已结束
    expect(screen.getByText('2 待认领 · 0 引导中 · 0 已结束')).toBeTruthy();
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

  it('uploads BMP originals unchanged and lets server preprocessing convert (74ae4e0)', async () => {
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
      // 契约（随模块 74ae4e0 引入）：**保留原文件**，BMP 属 provider 安全类型 ⇒ 本地**不得**改写
      // 文件名/类型，也**不得**走 canvas 转换路径；模型可用副本由**服务端预处理**生成。
      // 依据：visionArtifactImage.ts 的文档注释 + 其单测 visionArtifactImage.test.ts
      //（it.each 'uploads %s unchanged for server preprocessing'，含 image/bmp，当前为绿）。
      expect(uploadedFile.name).toBe('clipboard.bmp');
      expect(uploadedFile.type).toBe('image/bmp');
      expect(drawImage).not.toHaveBeenCalled();
      expect(sendWithMetadata).toHaveBeenCalledTimes(1);
    } finally {
      createElement.mockRestore();
    }
  });

  // ── P0-A 第一批：鼠标排队 / 独立停止 / 补充当前任务 ──

  it('running composer sends via the same submit chain as Enter instead of hijacking stop', () => {
    const onSend = jest.fn();
    const onStop = jest.fn();
    render(
      <IntentConsole
        {...baseProps}
        loading
        status="streaming"
        inputValue="排队第二条消息"
        onSend={onSend}
        onStop={onStop}
      />,
    );

    const queueButton = screen.getByRole('button', { name: '加入队列' });
    fireEvent.click(queueButton);

    expect(onSend).toHaveBeenCalledTimes(1);
    expect(onStop).not.toHaveBeenCalled();
  });

  it('renders a dedicated stop button while running and hides it when idle', () => {
    const onStop = jest.fn();
    const { rerender } = render(
      <IntentConsole
        {...baseProps}
        loading
        status="streaming"
        onStop={onStop}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: '停止当前执行' }));
    expect(onStop).toHaveBeenCalledTimes(1);

    rerender(
      <IntentConsole
        {...baseProps}
        loading={false}
        status="completed"
        onStop={onStop}
      />,
    );
    expect(screen.queryByRole('button', { name: '停止当前执行' })).toBeNull();
    expect(screen.getByRole('button', { name: '发送' })).toBeTruthy();
  });

  it('offers a steer menu while running with a text draft, sharing the Ctrl/Cmd+Enter chain', async () => {
    const onSteerCurrent = jest.fn(async () => true);

    function ControlledComposer() {
      const [value, setValue] = React.useState('请顺带检查错误日志');
      return (
        <IntentConsole
          {...baseProps}
          loading
          status="tool_executing"
          inputValue={value}
          onInputChange={setValue}
          onSteerCurrent={onSteerCurrent}
        />
      );
    }

    render(<ControlledComposer />);

    fireEvent.click(screen.getByRole('button', { name: '补充给当前任务' }));
    // 菜单项可访问名含图标 aria-label（"thunderbolt …"），用正则匹配文本部分
    fireEvent.click(
      await screen.findByRole('menuitem', { name: /补充给当前任务/ }),
    );

    await waitFor(() => {
      expect(onSteerCurrent).toHaveBeenCalledWith('请顺带检查错误日志');
    });
  });

  // ── 诊断报告 §5–§6：真实数字 / 三态纪律 ──

  /** 五个诊断请求的替身基线：默认「全部失败」，各用例按需覆盖。 */
  const stubDiagnosticsRequests = (overrides: {
    contextHealth?: unknown;
    cacheDiagnostics?: unknown;
    indexStatus?: unknown;
    subconscious?: unknown;
    providers?: unknown;
  }) => {
    (getContextHealth as jest.Mock).mockReset();
    (getCacheDiagnostics as jest.Mock).mockReset();
    (getIndexStatus as jest.Mock).mockReset();
    (getSubconsciousDebugState as jest.Mock).mockReset();
    (listLlmProviders as jest.Mock).mockReset();

    const settle = (
      mocked: jest.Mock,
      value: unknown,
      failureReason: unknown,
    ) => {
      if (value !== undefined) mocked.mockResolvedValue(value);
      else mocked.mockRejectedValue(failureReason);
    };

    settle(getContextHealth as jest.Mock, overrides.contextHealth, {
      response: { status: 409 },
      data: { code: 'context_window_unresolved' },
    });
    settle(
      getCacheDiagnostics as jest.Mock,
      overrides.cacheDiagnostics,
      new Error('cache offline'),
    );
    settle(getIndexStatus as jest.Mock, overrides.indexStatus, {
      response: { status: 401 },
    });
    settle(getSubconsciousDebugState as jest.Mock, overrides.subconscious, {
      response: { status: 404 },
    });
    settle(
      listLlmProviders as jest.Mock,
      overrides.providers,
      new Error('providers offline'),
    );
  };

  it('converts the 0-1 cache ratio without guessing its unit, and labels the window', async () => {
    stubDiagnosticsRequests({
      cacheDiagnostics: {
        sessionId: 'session-1',
        analyzedEventCount: 42,
        averageCacheHitRate: 0.76,
      },
    });

    render(<IntentConsole {...baseProps} sessionId="session-1" />);

    await waitFor(() =>
      expect(screen.getByTestId('summary-cache-hit-rate').textContent).toBe(
        '76',
      ),
    );
    // 窗口口径必须自带出处与样本条数，不允许静默覆盖会话值。
    expect(screen.getByTestId('summary-cache-scope').textContent).toBe(
      'windowed',
    );
    expect(screen.getByTestId('summary-cache-samples').textContent).toBe('42');
  });

  it('scales the ratio unconditionally (100% stays 100, never 1)', async () => {
    stubDiagnosticsRequests({
      cacheDiagnostics: {
        sessionId: 'session-1',
        analyzedEventCount: 7,
        averageCacheHitRate: 1,
      },
    });

    render(<IntentConsole {...baseProps} sessionId="session-1" />);

    await waitFor(() =>
      expect(screen.getByTestId('summary-cache-hit-rate').textContent).toBe(
        '100',
      ),
    );
  });

  it('falls back to the session-wide scope when the windowed report has no rate', async () => {
    stubDiagnosticsRequests({
      cacheDiagnostics: {
        sessionId: 'session-1',
        analyzedEventCount: 0,
        averageCacheHitRate: null,
      },
    });

    render(
      <IntentConsole {...baseProps} sessionId="session-1" cacheHitRate={31} />,
    );

    await waitFor(() =>
      expect(screen.getByTestId('summary-cache-hit-rate').textContent).toBe(
        '31',
      ),
    );
    expect(screen.getByTestId('summary-cache-scope').textContent).toBe(
      'session',
    );
  });

  it('renders unknown — never available — for service probes that are unavailable', async () => {
    stubDiagnosticsRequests({});

    render(<IntentConsole {...baseProps} sessionId="session-1" />);

    await waitFor(() =>
      expect(screen.getByTestId('summary-index').textContent).toBe('unknown'),
    );
    expect(screen.getByTestId('summary-background-memory').textContent).toBe(
      'unknown',
    );
    expect(screen.getByTestId('summary-context-service').textContent).toBe(
      'unknown',
    );
    // 模型服务没有健康端点：请求失败只能是 error（而不是 available）。
    expect(screen.getByTestId('summary-model-service').textContent).toBe(
      'error',
    );
    expect(screen.getByTestId('summary-index').textContent).not.toBe(
      'disabled',
    );
  });

  it('maps real service data honestly (off index / running memory / usable provider)', async () => {
    stubDiagnosticsRequests({
      contextHealth: { sessionId: 'session-1', state: 'Healthy' },
      indexStatus: { generatedAtUtc: '2026-10-01T05:00:00Z', fullText: { enabled: false } },
      subconscious: { state: 'running' },
      providers: [{ isEnabled: true, hasApiKey: true }],
    });

    render(<IntentConsole {...baseProps} sessionId="session-1" />);

    await waitFor(() =>
      expect(screen.getByTestId('summary-index').textContent).toBe('disabled'),
    );
    expect(screen.getByTestId('summary-background-memory').textContent).toBe(
      'running',
    );
    expect(screen.getByTestId('summary-context-service').textContent).toBe(
      'available',
    );
    expect(screen.getByTestId('summary-model-service').textContent).toBe(
      'available',
    );
  });

  it('forwards this-turn timings and usage to the status details', () => {
    render(
      <IntentConsole
        {...baseProps}
        turnTimings={{ completedMs: 1234, modelMs: null }}
        latestUsage={{ promptTokens: 10, completionTokens: 5 }}
      />,
    );

    expect(screen.getByTestId('summary-completed-ms').textContent).toBe('1234');
    expect(screen.getByTestId('summary-prompt-tokens').textContent).toBe('10');
    expect(screen.getByTestId('summary-completion-tokens').textContent).toBe(
      '5',
    );
  });
});
