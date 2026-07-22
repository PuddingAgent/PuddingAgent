import {
  CopyOutlined,
  DownloadOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  SyncOutlined,
} from '@ant-design/icons';
import { Button, Paragraph, Switch, Text } from 'antd';
import React from 'react';
import type { PuddingPerfEvent } from '@/utils/debug';
import { useChatStyles } from '../../styles';
import CountsWorkflowPanel from './DevPanel/CountsWorkflowPanel';
import PerfEventList from './DevPanel/PerfEventList';
import PerfMetricsGrid from './DevPanel/PerfMetricsGrid';

interface DiagnosisItem {
  code: string;
  title: string;
  severity: string;
  evidence: string;
}

interface PerfTabProps {
  perfEvents: PuddingPerfEvent[];
  perfSummary: Record<string, unknown>;
  diagnosticsEnabled: boolean;
  diagnosticSnapshot: {
    diagnosis: DiagnosisItem[];
    top: { workflowSteps: Array<{ step: string; count: number }> };
  };
  diagnosticCopiedAt: number | null;
  captureState: { status: 'idle' | 'recording' };
  updateDiagnosticsEnabled: (enabled: boolean) => void;
  copyDiagnosticSnapshot: () => Promise<void>;
  startCapture: () => void;
  stopCapture: () => void;
  downloadDiagnosticSnapshot: () => void;
  clearPerf: () => void;
  formatMetric: (v: unknown) => string;
  getEventTone: (evt: PuddingPerfEvent) => string;
  getNestedNumber: (obj: unknown, path: string) => number;
}

const PerfTab: React.FC<PerfTabProps> = ({
  perfEvents,
  perfSummary,
  diagnosticsEnabled,
  diagnosticSnapshot,
  diagnosticCopiedAt,
  captureState,
  updateDiagnosticsEnabled,
  copyDiagnosticSnapshot,
  startCapture,
  stopCapture,
  downloadDiagnosticSnapshot,
  clearPerf,
  formatMetric,
  getEventTone,
  getNestedNumber,
}) => {
  const styles = useChatStyles();

  return (
    <div className={styles.devPanelSection}>
      <div className={styles.devPerfToolbar}>
        <div
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 8,
            flexWrap: 'wrap',
          }}
        >
          <Text type="secondary" style={{ fontSize: 12 }}>
            前端输出性能 · 最近{' '}
            {formatMetric(getNestedNumber(perfSummary, 'totalEvents'))}{' '}
            条事件
          </Text>
          <Text style={{ fontSize: 12 }}>诊断模式</Text>
          <Switch
            size="small"
            aria-label="诊断模式"
            checked={diagnosticsEnabled}
            checkedChildren="开"
            unCheckedChildren="关"
            onChange={updateDiagnosticsEnabled}
          />
        </div>
        <div className={styles.devPerfToolbarActions}>
          <Button
            size="small"
            icon={<CopyOutlined />}
            disabled={!diagnosticsEnabled}
            onClick={() => {
              void copyDiagnosticSnapshot();
            }}
          >
            {diagnosticCopiedAt ? '已复制' : '复制诊断'}
          </Button>
          <Button
            size="small"
            icon={<PlayCircleOutlined />}
            disabled={
              !diagnosticsEnabled || captureState.status === 'recording'
            }
            onClick={startCapture}
          >
            开始采集
          </Button>
          <Button
            size="small"
            icon={<PauseCircleOutlined />}
            disabled={
              !diagnosticsEnabled || captureState.status !== 'recording'
            }
            onClick={stopCapture}
          >
            停止采集
          </Button>
          <Button
            size="small"
            icon={<DownloadOutlined />}
            disabled={!diagnosticsEnabled}
            onClick={downloadDiagnosticSnapshot}
          >
            下载快照
          </Button>
          <Button
            size="small"
            icon={<SyncOutlined />}
            onClick={clearPerf}
          >
            清空
          </Button>
        </div>
      </div>

      <div className={styles.devPerfDiagnosisList}>
        {diagnosticSnapshot.diagnosis.map((item) => (
          <div
            key={item.code}
            className={styles.devPerfDiagnosisItem}
            data-severity={item.severity}
          >
            <Text strong>{item.title}</Text>
            <Text type="secondary">{item.evidence}</Text>
          </div>
        ))}
      </div>

      <PerfMetricsGrid perfSummary={perfSummary} />

      <CountsWorkflowPanel
        perfSummary={perfSummary}
        topWorkflowSteps={diagnosticSnapshot.top.workflowSteps}
        formatMetric={formatMetric}
        getEventTone={getEventTone}
      />

      <PerfEventList
        perfEvents={perfEvents}
        getEventTone={getEventTone}
      />
      <Paragraph className={styles.devPanelHint}>
        复制诊断会包含摘要、瓶颈判断、间隔抖动、Top
        慢记录和最近原始事件。 Console 输出默认关闭；如需同时打印，设置
        localStorage.pudding_perf_console = &quot;1&quot;。 摘要 API:
        window.__PUDDING_PERF__.summary() / snapshot()
      </Paragraph>
    </div>
  );
};

export default React.memo(PerfTab);
