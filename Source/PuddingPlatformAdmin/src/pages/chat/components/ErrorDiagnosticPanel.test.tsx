import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { getTurnErrorDiagnostic } from '@/services/platform/api';
import ErrorDiagnosticPanel from './ErrorDiagnosticPanel';

jest.mock('@/services/platform/api', () => ({ getTurnErrorDiagnostic: jest.fn() }));
const getDiagnostic = jest.mocked(getTurnErrorDiagnostic);
const copy = jest.fn().mockResolvedValue(undefined);

beforeEach(() => {
  jest.clearAllMocks();
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: copy } });
});

it('loads persisted diagnostics only on expansion and copies the backend report', async () => {
  getDiagnostic.mockResolvedValue({
    errorMessage: 'upload failed',
    diagnostic: { causeTitle: '模型服务连接中断', reportText: '上传请求体：1597297 bytes，Socket 10053', reportJson: '{"errorId":"llm-123"}' },
  });
  render(<ErrorDiagnosticPanel sessionId="session" turnId="turn" errorMessage="upload failed" />);
  expect(getDiagnostic).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('查看诊断信息'));
  expect(await screen.findByText(/1597297 bytes/)).toBeTruthy();
  expect(getDiagnostic).toHaveBeenCalledWith('session', 'turn');
  fireEvent.click(screen.getByText('复制诊断信息'));
  await screen.findByText('诊断信息已复制');
  expect(copy.mock.calls[0][0]).toContain('Socket 10053');
  expect(copy.mock.calls[0][0]).toContain('"errorId":"llm-123"');
});

it('keeps current facts visible when a stored report cannot be loaded and supports retry', async () => {
  getDiagnostic.mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce({
    diagnostic: { reportText: 'recovered report' },
  });
  render(<ErrorDiagnosticPanel sessionId="session" turnId="turn" errorMessage="original failure" />);
  fireEvent.click(screen.getByText('查看诊断信息'));
  await screen.findByText(/未能读取完整报告/);
  expect(screen.getByText(/original failure/)).toBeTruthy();
  fireEvent.click(screen.getByText('重新读取'));
  await screen.findByText('recovered report');
});

it('uses live diagnostic facts without refetching and reports clipboard failure honestly', async () => {
  copy.mockRejectedValueOnce(new Error('denied'));
  render(<ErrorDiagnosticPanel sessionId="session" turnId="turn" errorMessage="upload failed"
    event={{ reportText: 'live report', retryable: false }} />);
  fireEvent.click(screen.getByText('查看诊断信息'));
  expect(getDiagnostic).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('复制诊断信息'));
  await waitFor(() => expect(screen.getByText(/复制失败/)).toBeTruthy());
});
