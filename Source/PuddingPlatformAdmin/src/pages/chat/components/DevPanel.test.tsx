import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import {
  getBenchmarkCase,
  listBenchmarkCases,
  prepareBenchmarkCase,
} from '@/services/platform/api';
import { clearPerfEvents, recordPerfEvent } from '@/utils/debug';
import DevPanel from './DevPanel';

jest.mock('@umijs/max', () => ({
  request: jest.fn().mockRejectedValue(new Error('not available in unit test')),
}));

jest.mock('@/services/platform/api', () => ({
  getBenchmarkCase: jest.fn(),
  listBenchmarkCases: jest.fn(),
  prepareBenchmarkCase: jest.fn(),
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

describe('DevPanel performance diagnostics', () => {
  beforeEach(() => {
    clearPerfEvents();
    localStorage.setItem('pudding_perf', '1');
    Object.assign(navigator, {
      clipboard: {
        writeText: jest.fn().mockResolvedValue(undefined),
      },
    });
    URL.createObjectURL = jest.fn(() => 'blob:diagnostic');
    URL.revokeObjectURL = jest.fn();
    (listBenchmarkCases as jest.Mock).mockResolvedValue([]);
    (getBenchmarkCase as jest.Mock).mockRejectedValue(
      new Error('no case selected'),
    );
    (prepareBenchmarkCase as jest.Mock).mockResolvedValue({
      runId: 'brun_default',
      caseId: 'case',
      workspaceId: 'default',
      seed: { seedId: null, files: [] },
    });
  });

  afterEach(() => {
    localStorage.removeItem('pudding_perf');
  });

  it('keeps diagnostics disabled until the user enables the mode', async () => {
    localStorage.removeItem('pudding_perf');

    render(
      <DevPanel
        workspaceId="default"
        sessionId="session-toggle"
        rawEvents={[]}
      />,
    );

    expect(localStorage.getItem('pudding_perf')).toBeNull();
    expect(
      (await screen.findByRole('switch', { name: /诊断模式/ })).getAttribute(
        'aria-checked',
      ),
    ).toBe('false');
    expect(
      (screen.getByRole('button', { name: /开始采集/ }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);

    fireEvent.click(screen.getByRole('switch', { name: /诊断模式/ }));

    expect(localStorage.getItem('pudding_perf')).toBe('1');
    expect(
      (screen.getByRole('button', { name: /开始采集/ }) as HTMLButtonElement)
        .disabled,
    ).toBe(false);
  });

  it('copies a frontend performance diagnostic package for external analysis', async () => {
    recordPerfEvent('chat.output.paint', {
      domTextChars: 80,
      commitToPaintMs: 17,
    });
    recordPerfEvent('browser.longtask', { durationMs: 90, startTime: 42 });

    render(
      <DevPanel
        workspaceId="default"
        sessionId="session-copy"
        rawEvents={[
          {
            id: 'raw-1',
            timestamp: 1,
            event: 'delta',
            payload: '{"delta":"hi"}',
          },
        ]}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: /复制诊断/ }));

    await waitFor(() => {
      expect(navigator.clipboard.writeText).toHaveBeenCalledTimes(1);
    });
    const copied = (navigator.clipboard.writeText as jest.Mock).mock
      .calls[0][0] as string;
    expect(copied).toContain('"sessionId": "session-copy"');
    expect(copied).toContain('"chat.output.paint"');
    expect(copied).toContain('"raw"');
  });

  it('collects an interaction capture and downloads a snapshot without touching chat flow', async () => {
    const clickSpy = jest.fn();
    const appendSpy = jest.spyOn(document.body, 'appendChild');
    const removeSpy = jest
      .spyOn(HTMLElement.prototype, 'remove')
      .mockImplementation(jest.fn());
    const createElementSpy = jest
      .spyOn(document, 'createElement')
      .mockImplementation((tagName: string) => {
        const element = document.createElementNS(
          'http://www.w3.org/1999/xhtml',
          tagName,
        ) as HTMLElement;
        if (tagName.toLowerCase() === 'a') {
          Object.defineProperty(element, 'click', { value: clickSpy });
        }
        return element;
      });

    render(
      <DevPanel
        workspaceId="default"
        sessionId="session-download"
        rawEvents={[
          {
            id: 'raw-1',
            timestamp: 1,
            event: 'delta',
            payload: '{"delta":"hi"}',
          },
        ]}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: /开始采集/ }));
    recordPerfEvent('chat.output.paint', {
      domTextChars: 120,
      commitToPaintMs: 12,
    });
    fireEvent.click(await screen.findByRole('button', { name: /停止采集/ }));
    fireEvent.click(await screen.findByRole('button', { name: /下载快照/ }));

    expect(URL.createObjectURL).toHaveBeenCalledTimes(1);
    expect(clickSpy).toHaveBeenCalledTimes(1);
    expect(appendSpy).toHaveBeenCalled();

    const anchor = appendSpy.mock.calls.find(
      ([node]) => node instanceof HTMLAnchorElement,
    )?.[0] as HTMLAnchorElement;
    expect(anchor.download).toMatch(/pudding-perf-session-download-/);
    expect(anchor.href).toBe('blob:diagnostic');

    createElementSpy.mockRestore();
    removeSpy.mockRestore();
    appendSpy.mockRestore();
  });

  it('loads benchmark cases from the server and sends only the case prompt', async () => {
    const prompt =
      '请创建一个 Markdown 摘要脚本，用途是扫描当前目录中的 .md 文件并生成 summary.md；功能是列出文件名、标题、字数估算和前两行内容，并运行一次验证。';
    const runBenchmarkPrompt = jest.fn();
    (listBenchmarkCases as jest.Mock).mockResolvedValue([
      {
        id: 'workspace-markdown-summary',
        title: 'Markdown 摘要清单',
        category: 'file-write',
        coverage: ['file', 'execution'],
        difficulty: 'hard',
        estimatedRounds: '12-18',
        seedId: 'sample-docs',
        capabilityTargets: ['file', 'execution'],
        sortOrder: 10,
      },
    ]);
    (getBenchmarkCase as jest.Mock).mockResolvedValue({
      id: 'workspace-markdown-summary',
      title: 'Markdown 摘要清单',
      prompt,
    });

    render(
      <DevPanel
        workspaceId="default"
        sessionId="session-case"
        rawEvents={[]}
        onRunBenchmarkPrompt={runBenchmarkPrompt}
      />,
    );

    fireEvent.click(screen.getByText(/Cases/));

    await waitFor(() => {
      expect(screen.getAllByText('Markdown 摘要清单').length).toBeGreaterThan(
        0,
      );
    });
    expect(screen.queryByText(prompt)).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /发送题面/ }));

    await waitFor(() => {
      expect(prepareBenchmarkCase).toHaveBeenCalledWith(
        'workspace-markdown-summary',
        'default',
        'session-case',
      );
      expect(runBenchmarkPrompt).toHaveBeenCalledWith(prompt, {
        source: 'benchmark_launcher',
        benchmarkCaseId: 'workspace-markdown-summary',
        benchmarkTitle: 'Markdown 摘要清单',
        benchmarkRunId: 'brun_default',
        benchmarkSeedId: '',
        benchmarkSeedFiles: '0',
      });
    });
  });
});
