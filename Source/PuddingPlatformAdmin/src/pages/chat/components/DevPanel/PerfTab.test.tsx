import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { buildPerfDiagnosticSnapshot } from '@/utils/debug';
import PerfTab from './PerfTab';

jest.mock('../../styles', () => ({
  useChatStyles: () => ({
    styles: {
      devPanelSection: 'perf-section',
      devPerfDiagnosisItem: 'diagnosis-item',
    },
  }),
}));

describe('PerfTab', () => {
  it('uses nested styles and renders a real snapshot with stopped capture controls', () => {
    const snapshot = buildPerfDiagnosticSnapshot();
    snapshot.diagnosis = [
      {
        code: 'test',
        severity: 'warn',
        title: 'Slow paint',
        evidence: 'Paint took 900ms',
        nextStep: 'Inspect paint',
      },
    ];
    snapshot.top.workflowSteps = [
      {
        name: 'workflow.step',
        at: 1,
        payload: { workflow: 'history', step: 'load', durationMs: 900 },
      },
    ];
    const startCapture = jest.fn();
    const stopCapture = jest.fn();
    const props = {
      perfEvents: [],
      perfSummary: snapshot.summary,
      diagnosticSnapshot: snapshot,
      diagnosticsEnabled: true,
      diagnosticCopiedAt: null,
      captureState: { status: 'stopped' as const },
      updateDiagnosticsEnabled: jest.fn(),
      copyDiagnosticSnapshot: jest.fn(async () => {}),
      startCapture,
      stopCapture,
      downloadDiagnosticSnapshot: jest.fn(),
      clearPerf: jest.fn(),
      formatMetric: (value: number | null, suffix = '') =>
        value == null ? '-' : `${value}${suffix}`,
      getEventTone: () => 'blue',
      getNestedNumber: () => null,
    };
    const { container, rerender } = render(<PerfTab {...props} />);
    expect(container.firstElementChild?.className).toBe('perf-section');
    expect(
      screen
        .getByText('Slow paint')
        .closest('.diagnosis-item')
        ?.getAttribute('data-severity'),
    ).toBe('warn');
    fireEvent.click(screen.getByText('最慢流程步骤'));
    expect(screen.getByText('history.load')).toBeTruthy();
    expect(screen.getByText('900ms')).toBeTruthy();
    expect(
      (screen.getByRole('button', { name: /停止采集/ }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: /开始采集/ }));
    expect(startCapture).toHaveBeenCalledTimes(1);
    rerender(<PerfTab {...props} captureState={{ status: 'recording' }} />);
    expect(
      (screen.getByRole('button', { name: /开始采集/ }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: /停止采集/ }));
    expect(stopCapture).toHaveBeenCalledTimes(1);
  });
});
