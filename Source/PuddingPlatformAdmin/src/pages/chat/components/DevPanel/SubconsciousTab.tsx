import React from 'react';
import {
  BulbOutlined,
  CheckCircleOutlined,
  ExclamationCircleOutlined,
  LoadingOutlined,
} from '@ant-design/icons';
import {
  Collapse,
  Empty,
  Spin,
  Tag,
  Timeline,
  Typography,
} from 'antd';
import dayjs from 'dayjs';
import type { SubconsciousResult } from './types';
import { useChatStyles } from '../../styles';

const { Text, Paragraph } = Typography;

interface SubconsciousTabProps {
  subconsciousData: SubconsciousResult | null;
  loadingSubconscious: boolean;
}

const SubconsciousTab: React.FC<SubconsciousTabProps> = ({
  subconsciousData,
  loadingSubconscious,
}) => {
  const { styles } = useChatStyles();

  if (loadingSubconscious) {
    return (
      <div className={styles.devPanelLoading}>
        <Spin size="small" />
      </div>
    );
  }

  if (!subconsciousData) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="暂无思考数据"
      />
    );
  }

  return (
    <div className={styles.devPanelSection}>
      <div className={styles.subconsciousTimeline}>
        <Timeline
          items={[
            {
              color: subconsciousData.job?.startedAt
                ? 'blue'
                : 'gray',
              dot: subconsciousData.job?.startedAt ? (
                <LoadingOutlined />
              ) : (
                <ExclamationCircleOutlined />
              ),
              children: (
                <div>
                  <Text strong>开始处理</Text>
                  <br />
                  <Text
                    type="secondary"
                    className={styles.devEventTime}
                  >
                    {subconsciousData.job?.startedAt
                      ? dayjs(
                          subconsciousData.job.startedAt,
                        ).format('HH:mm:ss')
                      : '-'}
                  </Text>
                </div>
              ),
            },
            {
              color:
                subconsciousData.job?.factsExtracted != null
                  ? 'blue'
                  : 'gray',
              dot:
                subconsciousData.job?.factsExtracted != null ? (
                  <BulbOutlined />
                ) : undefined,
              children: (
                <div>
                  <Text strong>LLM 分析中</Text>
                  <br />
                  <Text type="secondary">
                    提取了{' '}
                    {subconsciousData.job?.factsExtracted ?? 0}{' '}
                    条事实， 合并{' '}
                    {subconsciousData.job?.factsMerged ?? 0} 条
                  </Text>
                </div>
              ),
            },
            {
              color:
                subconsciousData.job?.status === 'completed'
                  ? 'green'
                  : subconsciousData.job?.status === 'failed'
                    ? 'red'
                    : 'gray',
              dot:
                subconsciousData.job?.status === 'completed' ? (
                  <CheckCircleOutlined />
                ) : subconsciousData.job?.status === 'failed' ? (
                  <ExclamationCircleOutlined />
                ) : undefined,
              children: (
                <div>
                  <Text strong>
                    {subconsciousData.job?.status === 'completed'
                      ? '处理完成'
                      : subconsciousData.job?.status === 'failed'
                        ? '处理失败'
                        : '等待中...'}
                  </Text>
                  <br />
                  <Text type="secondary">
                    耗时 {subconsciousData.job?.elapsedMs ?? 0}ms
                    {subconsciousData.job?.llmModelId
                      ? ` · ${subconsciousData.job.llmModelId}`
                      : ''}
                  </Text>
                  {subconsciousData.job?.errorMessage && (
                    <Paragraph className={styles.devErrorText}>
                      {subconsciousData.job.errorMessage}
                    </Paragraph>
                  )}
                </div>
              ),
            },
          ]}
        />
      </div>
      <Collapse
        size="small"
        ghost
        items={[
          {
            key: 'facts',
            label: `抽取事实 (${subconsciousData.facts.length})`,
            children: (
              <div className={styles.devList}>
                {subconsciousData.facts.map((f) => (
                  <div
                    key={f.factId}
                    className={styles.devListItem}
                  >
                    <Tag>{f.category}</Tag>
                    <Text>{f.statement}</Text>
                  </div>
                ))}
              </div>
            ),
          },
          {
            key: 'prefs',
            label: `偏好 (${subconsciousData.preferences.length})`,
            children: (
              <div className={styles.devList}>
                {subconsciousData.preferences.map((p) => (
                  <div
                    key={p.preferenceId}
                    className={styles.devListItem}
                  >
                    <Tag>{p.category}</Tag>
                    <Text>
                      {p.key} = {p.value}
                    </Text>
                  </div>
                ))}
              </div>
            ),
          },
        ]}
      />
    </div>
  );
};

export default SubconsciousTab;
