import { ReloadOutlined } from '@ant-design/icons';
import {
  Alert,
  Button,
  Descriptions,
  Drawer,
  Empty,
  Progress,
  Space,
  Spin,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import {
  getSessionBenchmarkDiagnostics,
  type SessionBenchmarkReportDto,
} from '@/services/platform/api';

const { Text } = Typography;

interface SessionBenchmarkDrawerProps {
  sessionId?: string | null;
  open: boolean;
  onClose: () => void;
}

const severityColor: Record<string, string> = {
  high: 'red',
  medium: 'orange',
  low: 'blue',
};

const categoryLabel: Record<string, string> = {
  approval_mismatch: '审批不匹配',
  approval_denied: '审批拒绝',
  implicit_approval_coverage: '隐式审批覆盖率',
  runtime_failure: '运行失败',
  environment_failure: '环境问题',
  platform_log_noise: '日志噪声',
};

const SessionBenchmarkDrawer: React.FC<SessionBenchmarkDrawerProps> = ({
  sessionId,
  open,
  onClose,
}) => {
  const [report, setReport] = React.useState<SessionBenchmarkReportDto | null>(
    null,
  );
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const loadReport = React.useCallback(async () => {
    if (!sessionId) return;
    setLoading(true);
    setError(null);
    try {
      setReport(await getSessionBenchmarkDiagnostics(sessionId));
    } catch (err) {
      const message = err instanceof Error ? err.message : '诊断报告加载失败';
      setError(message);
    } finally {
      setLoading(false);
    }
  }, [sessionId]);

  React.useEffect(() => {
    if (open) void loadReport();
  }, [open, loadReport]);

  const toolCallCount = sumCounts(report?.counts.toolCalls);
  const toolResultCount = sumCounts(report?.counts.toolResults);
  const approvalDeniedCount =
    (report?.counts.approvalEvents.TicketMismatch ?? 0) +
    (report?.counts.approvalEvents.ImplicitDenied ?? 0) +
    (report?.counts.approvalEvents.TicketNeedHuman ?? 0);

  return (
    <Drawer
      title="Hermes 基准诊断"
      width={560}
      open={open}
      onClose={onClose}
      extra={
        <Button
          size="small"
          icon={<ReloadOutlined />}
          onClick={() => void loadReport()}
          disabled={!sessionId || loading}
        >
          刷新
        </Button>
      }
    >
      {!sessionId && <Empty description="当前会话尚未建立" />}
      {sessionId && loading && !report && (
        <div style={{ padding: '32px 0', textAlign: 'center' }}>
          <Spin />
        </div>
      )}
      {error && (
        <Alert
          type="error"
          message={error}
          showIcon
          style={{ marginBottom: 12 }}
        />
      )}
      {report && (
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <section>
            <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
              <Progress
                type="circle"
                percent={report.scores.overall}
                size={88}
                format={() => report.scores.grade}
              />
              <Descriptions size="small" column={1}>
                <Descriptions.Item label="Session">
                  {shortId(report.sessionId)}
                </Descriptions.Item>
                <Descriptions.Item label="总分">
                  {report.scores.overall}
                </Descriptions.Item>
                <Descriptions.Item label="Tokens">
                  {report.usage.totalTokens ?? '-'}
                </Descriptions.Item>
              </Descriptions>
            </div>
          </section>

          <section>
            <Text strong>执行计数</Text>
            <Descriptions size="small" column={2} style={{ marginTop: 8 }}>
              <Descriptions.Item label="工具调用">
                {toolCallCount}
              </Descriptions.Item>
              <Descriptions.Item label="工具结果">
                {toolResultCount}
              </Descriptions.Item>
              <Descriptions.Item label="失败结果">
                {report.counts.failedToolResults}
              </Descriptions.Item>
              <Descriptions.Item label="审批拒绝">
                {approvalDeniedCount}
              </Descriptions.Item>
              <Descriptions.Item label="审批工单">
                {report.counts.tickets}
              </Descriptions.Item>
              <Descriptions.Item label="白名单命中">
                {report.counts.approvalEvents.AllowlistHit ?? 0}
              </Descriptions.Item>
            </Descriptions>
          </section>

          <section>
            <Text strong>隐式审批</Text>
            <Descriptions size="small" column={2} style={{ marginTop: 8 }}>
              <Descriptions.Item label="覆盖率">
                {formatPercent(report.approvalStats.implicitCoveragePercent)}
              </Descriptions.Item>
              <Descriptions.Item label="批准样本">
                {report.approvalStats.implicitApprovals}/
                {report.approvalStats.approvalDecisionAttempts}
              </Descriptions.Item>
              <Descriptions.Item label="隐式批准">
                {report.approvalStats.implicitApproved}
              </Descriptions.Item>
              <Descriptions.Item label="隐式拒绝">
                {report.approvalStats.implicitDenied}
              </Descriptions.Item>
              <Descriptions.Item label="显式工单">
                {report.approvalStats.explicitTickets}
              </Descriptions.Item>
              <Descriptions.Item label="工单命中">
                {report.approvalStats.ticketApprovals}
              </Descriptions.Item>
              <Descriptions.Item label="平均耗时">
                {formatMs(report.approvalStats.implicitLatencyAvgMs)}
              </Descriptions.Item>
              <Descriptions.Item label="P95 耗时">
                {formatMs(report.approvalStats.implicitLatencyP95Ms)}
              </Descriptions.Item>
            </Descriptions>
          </section>

          <section>
            <Text strong>工具输出与耗时</Text>
            <Space
              direction="vertical"
              size={8}
              style={{ width: '100%', marginTop: 8 }}
            >
              {report.toolOutputStats.length === 0 && (
                <Text type="secondary">暂无工具输出统计</Text>
              )}
              {report.toolOutputStats.map((item) => (
                <div
                  key={item.toolName}
                  style={{
                    border: '1px solid var(--ant-color-border-secondary)',
                    borderRadius: 6,
                    padding: 10,
                  }}
                >
                  <Space wrap>
                    <Tag>{item.toolName}</Tag>
                    <Text type="secondary">{item.resultCount} 次结果</Text>
                  </Space>
                  <Descriptions
                    size="small"
                    column={2}
                    style={{ marginTop: 8 }}
                  >
                    <Descriptions.Item label="返回总量">
                      {formatLinesAndChars(
                        item.totalTextLineTotal,
                        item.totalTextCharTotal,
                      )}
                    </Descriptions.Item>
                    <Descriptions.Item label="平均返回">
                      {formatNumber(item.avgTotalTextCharCount)} 字
                    </Descriptions.Item>
                    <Descriptions.Item label="stdout">
                      {formatLinesAndChars(
                        item.outputLineTotal,
                        item.outputCharTotal,
                      )}
                    </Descriptions.Item>
                    <Descriptions.Item label="stderr/error">
                      {formatLinesAndChars(
                        item.errorLineTotal,
                        item.errorCharTotal,
                      )}
                    </Descriptions.Item>
                    <Descriptions.Item label="平均耗时">
                      {formatMs(item.durationAvgMs)}
                    </Descriptions.Item>
                    <Descriptions.Item label="P95 耗时">
                      {formatMs(item.durationP95Ms)}
                    </Descriptions.Item>
                    <Descriptions.Item label="最大耗时">
                      {formatMs(item.durationMaxMs)}
                    </Descriptions.Item>
                    <Descriptions.Item label="最大返回">
                      {formatNumber(item.maxTotalTextCharCount)} 字
                    </Descriptions.Item>
                  </Descriptions>
                </div>
              ))}
            </Space>
          </section>

          <section>
            <Text strong>摩擦点</Text>
            <Space
              direction="vertical"
              size={8}
              style={{ width: '100%', marginTop: 8 }}
            >
              {report.frictionPoints.length === 0 && (
                <Text type="secondary">未发现明显摩擦点</Text>
              )}
              {report.frictionPoints.map((point) => (
                <div
                  key={`${point.category}:${point.evidence}`}
                  style={{
                    border: '1px solid var(--ant-color-border-secondary)',
                    borderRadius: 6,
                    padding: 10,
                  }}
                >
                  <Space wrap>
                    <Tag color={severityColor[point.severity] ?? 'default'}>
                      {point.severity}
                    </Tag>
                    <Text strong>
                      {categoryLabel[point.category] ?? point.category}
                    </Text>
                  </Space>
                  <div style={{ marginTop: 6 }}>
                    <Text type="secondary">{point.evidence}</Text>
                  </div>
                  <div style={{ marginTop: 6 }}>{point.recommendation}</div>
                </div>
              ))}
            </Space>
          </section>

          <section>
            <Text strong>失败结果</Text>
            <Space
              direction="vertical"
              size={8}
              style={{ width: '100%', marginTop: 8 }}
            >
              {report.failures.length === 0 && (
                <Text type="secondary">没有失败的工具结果</Text>
              )}
              {report.failures.map((failure) => (
                <div
                  key={`${failure.seq}:${failure.name}`}
                  style={{
                    border: '1px solid var(--ant-color-border-secondary)',
                    borderRadius: 6,
                    padding: 10,
                  }}
                >
                  <Space wrap>
                    <Tag>{failure.name}</Tag>
                    <Tag color="red">
                      {categoryLabel[failure.category] ?? failure.category}
                    </Tag>
                    <Text type="secondary">seq {failure.seq}</Text>
                    <Text type="secondary">
                      {formatLinesAndChars(
                        failure.totalTextLineCount,
                        failure.totalTextCharCount,
                      )}
                    </Text>
                    <Text type="secondary">{formatMs(failure.durationMs)}</Text>
                  </Space>
                  {failure.pairedCommand && (
                    <pre style={preStyle}>{failure.pairedCommand}</pre>
                  )}
                  {(failure.error || failure.output) && (
                    <pre style={preStyle}>
                      {failure.error || failure.output}
                    </pre>
                  )}
                </div>
              ))}
            </Space>
          </section>

          <section>
            <Text strong>审批链路</Text>
            <Space
              direction="vertical"
              size={6}
              style={{ width: '100%', marginTop: 8 }}
            >
              {report.approvalTimeline.slice(0, 12).map((event, index) => (
                <div key={`${event.eventType}:${event.ticketId ?? index}`}>
                  <Tag>{event.eventType}</Tag>
                  {event.toolId && <Text>{event.toolId}</Text>}
                  {event.reason && (
                    <Text type="secondary"> - {event.reason}</Text>
                  )}
                </div>
              ))}
            </Space>
          </section>
        </Space>
      )}
    </Drawer>
  );
};

const preStyle: React.CSSProperties = {
  margin: '8px 0 0',
  padding: 8,
  borderRadius: 6,
  background: 'var(--ant-color-fill-tertiary)',
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
  fontSize: 12,
  lineHeight: 1.5,
};

function sumCounts(counts?: Record<string, number>): number {
  if (!counts) return 0;
  return Object.values(counts).reduce((sum, value) => sum + value, 0);
}

function shortId(value: string): string {
  if (value.length <= 16) return value;
  return `${value.slice(0, 8)}...${value.slice(-6)}`;
}

function formatPercent(value?: number): string {
  return typeof value === 'number' ? `${value}%` : '-';
}

function formatMs(value?: number): string {
  if (typeof value !== 'number') return '-';
  return value >= 1000 ? `${(value / 1000).toFixed(2)}s` : `${value}ms`;
}

function formatLinesAndChars(lines?: number, chars?: number): string {
  return `${formatNumber(lines ?? 0)} 行 / ${formatNumber(chars ?? 0)} 字`;
}

function formatNumber(value: number): string {
  return Number.isFinite(value) ? value.toLocaleString() : '-';
}

export default React.memo(SessionBenchmarkDrawer);
