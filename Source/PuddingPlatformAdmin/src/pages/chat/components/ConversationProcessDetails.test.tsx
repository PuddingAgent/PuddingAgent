import * as React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import ConversationProcessDetails from './ConversationProcessDetails';
import { getConversationProcess } from '../client/conversationOpenApi';

jest.mock('../client/conversationOpenApi', () => ({ getConversationProcess: jest.fn() }));
const getProcess = getConversationProcess as jest.Mock;
const page = (items: unknown[] = [], more = false) => ({ items, hasMoreBefore: more,
  nextBeforeSequence: more ? 40 : null, scannedBackThroughSequence: 40, omittedNonDisplayable: 0, truncation: 'None' });
beforeEach(() => getProcess.mockReset());

it('loads only on expansion, follows empty-page cursors, and displays real tool arguments and output', async () => {
  getProcess.mockResolvedValueOnce(page([], true)).mockResolvedValueOnce(page([
    { sequence: 20, eventId: 'call', toolCallId: 'call-1', type: 'tool.call.requested', name: 'shell', arguments: 'Get-Date' },
    { sequence: 21, eventId: 'result', toolCallId: 'call-1', type: 'tool.call.completed', name: 'shell', output: '20:11:55', exitCode: 0 },
  ]));
  render(<ConversationProcessDetails conversationId="slack-thread" turnId="turn-1" />);
  expect(getProcess).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: '执行过程' }));
  await screen.findByRole('button', { name: '加载更早过程' });
  expect(screen.queryByText('本回合没有可展示的过程记录。')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '加载更早过程' }));
  await screen.findByText('输出：20:11:55');
  fireEvent.click(screen.getByText('参数与返回结果'));
  expect(screen.getAllByText('shell')).toHaveLength(1);
  expect(screen.getByText('完成')).toBeTruthy();
  expect(screen.getByText('参数：Get-Date')).toBeTruthy();
  expect(getProcess.mock.calls[1][0]).toBe('slack-thread');
  expect(getProcess.mock.calls[1][1]).toMatchObject({ turnId: 'turn-1', beforeSequence: 40, limit: 100 });
  expect(getProcess.mock.calls[1][1].throughSequence).toBeUndefined();
  fireEvent.click(screen.getByText('原始记录'));
  expect(screen.getByText('#20 · shell')).toBeTruthy();
});

it('reports a failed lookup instead of claiming there were no tool calls, then permits retry', async () => {
  getProcess.mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce(page());
  render(<ConversationProcessDetails conversationId="slack-thread" turnId="turn-1" />);
  fireEvent.click(screen.getByRole('button', { name: '执行过程' }));
  await screen.findByRole('alert');
  expect(screen.queryByText('本回合没有可展示的过程记录。')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: '刷新过程' }));
  await screen.findByText('本回合没有可展示的过程记录。');
  expect(screen.queryByRole('alert')).toBeNull();
});

it('aborts and ignores a late response when switching channel threads', async () => {
  let resolveOld!: (value: unknown) => void;
  getProcess.mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve; })).mockResolvedValueOnce(page());
  const { rerender } = render(<ConversationProcessDetails conversationId="old" turnId="turn-old" />);
  fireEvent.click(screen.getByRole('button', { name: '执行过程' }));
  const signal = getProcess.mock.calls[0][1].signal;
  rerender(<ConversationProcessDetails conversationId="new" turnId="turn-new" />);
  expect(signal.aborted).toBe(true);
  resolveOld(page([{ sequence: 1, eventId: 'old', type: 'tool.call.completed', output: 'must-not-leak' }]));
  await waitFor(() => expect(getProcess).toHaveBeenCalledTimes(2));
  await screen.findByText('本回合没有可展示的过程记录。');
  expect(screen.queryByText(/must-not-leak/)).toBeNull();
});
