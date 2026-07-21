import React, { useEffect, useState } from 'react';
import {
  PlayCircleOutlined,
} from '@ant-design/icons';
import {
  Button,
  Select,
  Tag,
  Typography,
} from 'antd';
import type {
  BenchmarkCaseSummaryDto,
} from '@/services/platform/api';
import {
  getBenchmarkCase,
  listBenchmarkCases,
  prepareBenchmarkCase,
} from '@/services/platform/api';
import { useChatStyles } from '../../styles';

interface BenchmarkTabProps {
  workspaceId?: string;
  resolvedSessionId: string | null;
  inspectorOpen: boolean;
  onRunBenchmarkPrompt?: (
    prompt: string,
    metadata: Record<string, string>,
  ) => Promise<void> | void;
}

const BenchmarkTab: React.FC<BenchmarkTabProps> = ({
  workspaceId,
  resolvedSessionId,
  inspectorOpen,
  onRunBenchmarkPrompt,
}) => {
  const { styles } = useChatStyles();
  const [benchmarkCases, setBenchmarkCases] = useState<
    BenchmarkCaseSummaryDto[]
  >([]);
  const [selectedBenchmarkCaseId, setSelectedBenchmarkCaseId] = useState<
    string | undefined
  >();
  const [loadingBenchmarkCases, setLoadingBenchmarkCases] = useState(false);
  const [sendingBenchmarkCase, setSendingBenchmarkCase] = useState(false);
  const [benchmarkError, setBenchmarkError] = useState<string | null>(null);

  useEffect(() => {
    if (!inspectorOpen) return;
    let alive = true;
    setLoadingBenchmarkCases(true);
    setBenchmarkError(null);

    const loadBenchmarkCases = async () => {
      try {
        const result = await listBenchmarkCases();
        if (!alive) return;
        const sorted = [...(result || [])].sort(
          (a, b) => a.sortOrder - b.sortOrder || a.title.localeCompare(b.title),
        );
        setBenchmarkCases(sorted);
        setSelectedBenchmarkCaseId((current) =>
          current && sorted.some((item) => item.id === current)
            ? current
            : sorted[0]?.id,
        );
      } catch {
        if (alive) {
          setBenchmarkCases([]);
          setSelectedBenchmarkCaseId(undefined);
          setBenchmarkError('试题列表加载失败');
        }
      } finally {
        if (alive) setLoadingBenchmarkCases(false);
      }
    };

    void loadBenchmarkCases();
    return () => {
      alive = false;
    };
  }, [inspectorOpen]);

  const sendSelectedBenchmarkCase = async () => {
    if (!selectedBenchmarkCaseId || !onRunBenchmarkPrompt) return;
    setSendingBenchmarkCase(true);
    setBenchmarkError(null);
    try {
      const prepared = workspaceId
        ? await prepareBenchmarkCase(
            selectedBenchmarkCaseId,
            workspaceId,
            resolvedSessionId,
          )
        : null;
      const detail = await getBenchmarkCase(selectedBenchmarkCaseId);
      await onRunBenchmarkPrompt(detail.prompt, {
        source: 'benchmark_launcher',
        benchmarkCaseId: detail.id,
        benchmarkTitle: detail.title,
        benchmarkRunId: prepared?.runId ?? '',
        benchmarkSeedId: prepared?.seed.seedId ?? '',
        benchmarkSeedFiles: String(prepared?.seed.files.length ?? 0),
      });
    } catch {
      setBenchmarkError('发送试题失败');
    } finally {
      setSendingBenchmarkCase(false);
    }
  };

  return (
    <div className={styles.devPanelSection}>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        从服务端配置拉取试题；发送时只提交题面文本。
      </Typography.Text>
      <Select
        size="small"
        loading={loadingBenchmarkCases}
        value={selectedBenchmarkCaseId}
        placeholder="选择试题"
        aria-label="选择试题"
        onChange={setSelectedBenchmarkCaseId}
        options={benchmarkCases.map((item) => ({
          value: item.id,
          label: item.title,
        }))}
        style={{ width: '100%' }}
        disabled={
          loadingBenchmarkCases || benchmarkCases.length === 0
        }
      />
      {selectedBenchmarkCaseId && (
        <div className={styles.devPerfDiagnosisList}>
          {benchmarkCases
            .filter((item) => item.id === selectedBenchmarkCaseId)
            .map((item) => (
              <div
                key={item.id}
                className={styles.devPerfDiagnosisItem}
                data-severity="info"
              >
                <Typography.Text strong>{item.title}</Typography.Text>
                <Typography.Text type="secondary">
                  {item.category}
                  {' · '}
                  {item.difficulty}
                  {item.estimatedRounds
                    ? ` · ${item.estimatedRounds} 轮`
                    : ''}
                </Typography.Text>
                <div className={styles.devPerfCounts}>
                  {item.coverage.map((tag) => (
                    <Tag key={tag} color="blue">
                      {tag}
                    </Tag>
                  ))}
                  {item.seedId && <Tag color="purple">seed</Tag>}
                </div>
              </div>
            ))}
        </div>
      )}
      {benchmarkError && (
        <Typography.Text type="danger" style={{ fontSize: 12 }}>
          {benchmarkError}
        </Typography.Text>
      )}
      <Button
        size="small"
        type="primary"
        icon={<PlayCircleOutlined />}
        loading={sendingBenchmarkCase}
        disabled={
          !selectedBenchmarkCaseId ||
          !onRunBenchmarkPrompt ||
          loadingBenchmarkCases
        }
        onClick={() => {
          void sendSelectedBenchmarkCase();
        }}
      >
        发送题面
      </Button>
    </div>
  );
};

export default BenchmarkTab;
