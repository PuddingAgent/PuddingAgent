import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageProcessSummary from './MessageProcessSummary';

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
      cx: (...values: Array<string | false | undefined>) =>
        values.filter(Boolean).join(' '),
    }),
  };
});

describe('MessageProcessSummary', () => {
  it('shows sanitized raw thinking text when the process details are expanded', () => {
    render(
      <MessageProcessSummary
        status="success"
        items={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: 'The user asked for raw reasoning undefinedundefined',
            timestamp: 1,
            collapsed: true,
          },
        ]}
      />,
    );

    fireEvent.click(screen.getByText('查看过程'));

    expect(screen.getByText('The user asked for raw reasoning')).toBeTruthy();
    expect(screen.queryByText(/undefined/)).toBeNull();
  });

  it('renders thinking and tool events in chronological process order', () => {
    render(
      <MessageProcessSummary
        status="success"
        items={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: '先理解问题。',
            timestamp: 1,
            collapsed: true,
          },
          {
            id: 'tool-1',
            type: 'tool_call',
            name: 'web_search',
            status: 'tool_call',
            message: '调用工具: web_search',
            timestamp: 2,
            collapsed: true,
          },
          {
            id: 'tool-2',
            type: 'tool_result',
            name: 'web_search',
            status: 'success',
            message: '搜索到 3 个结果',
            exitCode: 0,
            timestamp: 3,
            collapsed: true,
          },
          {
            id: 'thinking-2',
            type: 'thinking',
            text: '根据搜索结果继续分析。',
            timestamp: 4,
            collapsed: true,
          },
        ]}
      />,
    );

    expect(screen.getByText(/已思考 2 轮/)).toBeTruthy();
    expect(screen.getByText(/调用 1 个工具/)).toBeTruthy();
    fireEvent.click(screen.getByText('查看过程'));

    expect(screen.getByText('思维消息 1')).toBeTruthy();
    expect(screen.getByText('工具调用 2')).toBeTruthy();
    expect(screen.getByText('工具结果 3')).toBeTruthy();
    expect(screen.getByText('思维消息 4')).toBeTruthy();
    expect(screen.getAllByText('web_search').length).toBeGreaterThan(0);
  });

  it('summarizes sub-agent events without exposing them only as generic process items', () => {
    render(
      <MessageProcessSummary
        status="executing"
        items={[
          {
            id: 'subagent-1',
            type: 'subagent_spawned',
            name: 'code-search',
            status: 'running',
            arguments: '检查工具返回是否存在截断',
            message: '检查工具返回是否存在截断',
            timestamp: 1,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(screen.getByText(/派生 1 个子代理/)).toBeTruthy();
    fireEvent.click(screen.getByText('查看过程'));

    expect(screen.getByText('子代理调用 1')).toBeTruthy();
    expect(screen.getByText('code-search')).toBeTruthy();
    expect(screen.getByText(/参数：检查工具返回是否存在截断/)).toBeTruthy();
  });

  it('shows trace chips and split tool detail blocks for audit review', () => {
    render(
      <MessageProcessSummary
        status="success"
        items={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: '需要检查文件。',
            timestamp: 1000,
            collapsed: true,
          },
          {
            id: 'tool-1',
            type: 'tool_call',
            name: 'file_search',
            status: 'tool_call',
            arguments: '{"pattern":"*.md"}',
            message: '调用工具: file_search',
            timestamp: 1500,
            collapsed: true,
          },
          {
            id: 'tool-2',
            type: 'tool_result',
            name: 'file_search',
            status: 'success',
            output: 'README.md',
            exitCode: 0,
            timestamp: 2600,
            collapsed: true,
          },
        ]}
      />,
    );

    fireEvent.click(screen.getByText('查看过程'));

    expect(screen.getByText('事件')).toBeTruthy();
    expect(screen.getByText('思维')).toBeTruthy();
    expect(screen.getByText('工具')).toBeTruthy();
    expect(screen.getByText('耗时')).toBeTruthy();

    fireEvent.click(screen.getAllByText('查看工具详情')[0]);

    expect(screen.getByText('参数')).toBeTruthy();
    expect(screen.getByText('消息')).toBeTruthy();
    expect(document.body.textContent).toContain('{"pattern":"*.md"}');
    expect(document.body.textContent).toContain('调用工具: file_search');
  });

  it('shows tokenized historical thinking fragments as process stream items', () => {
    render(
      <MessageProcessSummary
        status="success"
        items={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: 'The',
            timestamp: 1,
            collapsed: true,
          },
          {
            id: 'thinking-2',
            type: 'thinking',
            text: ' user',
            timestamp: 1,
            collapsed: true,
          },
          {
            id: 'thinking-3',
            type: 'thinking',
            text: ' is',
            timestamp: 1,
            collapsed: true,
          },
          {
            id: 'thinking-4',
            type: 'thinking',
            text: ' pointing',
            timestamp: 1,
            collapsed: true,
          },
          {
            id: 'thinking-5',
            type: 'thinking',
            text: ' out',
            timestamp: 1,
            collapsed: true,
          },
        ]}
      />,
    );

    fireEvent.click(screen.getByText('查看过程'));

    expect(screen.getByText('思维消息 1')).toBeTruthy();
    expect(screen.queryByText('思维消息 5')).toBeNull();
    expect(screen.getByText('The user is pointing out')).toBeTruthy();
  });

  it('keeps collapsed summary compact without printing raw thinking text', () => {
    render(
      <MessageProcessSummary
        status="streaming"
        items={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: '这是一段很长的思维链原文，折叠摘要中不应该直接打印出来。',
            timestamp: 1,
            collapsed: true,
          },
        ]}
      />,
    );

    expect(screen.getByText(/已思考 1 轮/)).toBeTruthy();
    expect(screen.getByText('查看过程')).toBeTruthy();
    expect(screen.queryByText(/这是一段很长的思维链原文/)).toBeNull();
  });

  it('shows the complete thinking text when the process is expanded', () => {
    const longThinking = `第一段说明。\n${'需要保留但不应该默认铺满屏幕。'.repeat(30)}`;
    render(
      <MessageProcessSummary
        status="success"
        items={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: longThinking,
            timestamp: 1,
            collapsed: true,
          },
        ]}
      />,
    );

    fireEvent.click(screen.getByText('查看过程'));

    expect(screen.getByText(/第一段说明/)).toBeTruthy();
    expect(screen.queryByText('查看完整思考')).toBeNull();
    expect(screen.queryByText('收起完整思考')).toBeNull();
    expect(document.body.textContent).toContain(
      '需要保留但不应该默认铺满屏幕。'.repeat(30),
    );
  });

  it('classifies approval failures in the collapsed summary', () => {
    render(
      <MessageProcessSummary
        status="error"
        items={[
          {
            id: 'tool-call-1',
            type: 'tool_call',
            name: 'shell',
            status: 'tool_call',
            timestamp: 1,
            collapsed: true,
          },
          {
            id: 'tool-result-1',
            type: 'tool_result',
            name: 'shell',
            status: 'failed',
            message:
              "High-risk tool runtime approval required. Approved ticket 'tap_1' exists, but it does not match the actual arguments.",
            exitCode: 403,
            timestamp: 2,
            collapsed: true,
          },
          {
            id: 'tool-result-2',
            type: 'tool_result',
            name: 'request_tool_approval',
            status: 'failed',
            message: 'requested_scope must be one of: once, session, timed.',
            exitCode: 1,
            timestamp: 3,
            collapsed: true,
          },
        ]}
      />,
    );

    expect(screen.getByText(/2 个失败/)).toBeTruthy();
    expect(screen.getByText(/审批不匹配 1/)).toBeTruthy();
    expect(screen.getByText(/审批范围错误 1/)).toBeTruthy();
  });

  it('opens diagnostics from the collapsed summary without expanding process details', () => {
    const onOpenDiagnostics = jest.fn();
    render(
      <MessageProcessSummary
        status="success"
        onOpenDiagnostics={onOpenDiagnostics}
        items={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: 'Hidden reasoning',
            timestamp: 1,
            collapsed: true,
          },
        ]}
      />,
    );

    const processLink = screen.getByText('查看过程');
    const diagnosticsButton = screen.getByRole('button', { name: '诊断报告' });

    expect(diagnosticsButton.className).toBe(processLink.className);
    expect(diagnosticsButton.querySelector('svg')).toBeNull();

    fireEvent.click(diagnosticsButton);

    expect(onOpenDiagnostics).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Hidden reasoning')).toBeNull();
  });
});
