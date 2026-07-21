import React from 'react';
import {
  CodeOutlined,
  DatabaseOutlined,
  SettingOutlined,
} from '@ant-design/icons';
import {
  Empty,
  Progress,
  Spin,
  Tag,
  Typography,
} from 'antd';
import dayjs from 'dayjs';
import type { ContextSnapshot } from './types';
import { useChatStyles } from '../../styles';

const { Text, Paragraph } = Typography;

interface ContextTabProps {
  loadingContext: boolean;
  contextData: ContextSnapshot | null;
}

const SpaceLine: React.FC<{ label: string; value: string }> = ({
  label,
  value,
}) => {
  const { styles } = useChatStyles();
  return (
    <div className={styles.devLine}>
      <Text type="secondary">{label}</Text>
      <Text>{value}</Text>
    </div>
  );
};

const ContextTab: React.FC<ContextTabProps> = ({
  loadingContext,
  contextData,
}) => {
  const { styles } = useChatStyles();

  if (loadingContext) {
    return (
      <div className={styles.devPanelLoading}>
        <Spin size="small" />
      </div>
    );
  }

  if (!contextData) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="暂无上下文诊断"
      />
    );
  }

  return (
    <div className={styles.devPanelSection}>
      <SpaceLine
        label="组装时间"
        value={
          contextData.assembledAt
            ? dayjs(contextData.assembledAt).format(
                'YYYY-MM-DD HH:mm:ss',
              )
            : '-'
        }
      />
      <div style={{ marginBottom: 8 }}>
        <Text type="secondary" style={{ fontSize: 12 }}>
          上下文预算
        </Text>
        <Progress
          percent={Math.min(
            ((contextData.totalTokens || 0) / 200) * 100,
            100,
          )}
          size="small"
          strokeColor={
            (contextData.totalTokens || 0) > 160
              ? 'var(--warning-signal, #F97316)'
              : 'var(--memory-glow, #A78BFA)'
          }
          format={() => `${contextData.totalTokens || 0} tokens`}
        />
      </div>
      {contextData.message && (
        <Paragraph className={styles.devPanelHint}>
          {contextData.message}
        </Paragraph>
      )}
      {/* 上下文分层摘要，不展示完整 JSON dump */}
      <div
        style={{ display: 'flex', flexDirection: 'column', gap: 4 }}
      >
        {(contextData.layers || []).map((layer) => (
          <div
            key={layer.layerName}
            style={{
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'center',
              padding: '4px 8px',
              background: 'var(--ant-colorFillQuaternary)',
              borderRadius: 6,
              fontSize: 12,
            }}
          >
            <span
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 4,
              }}
            >
              {layer.layerName.toLowerCase().includes('system') ? (
                <SettingOutlined style={{ fontSize: 11 }} />
              ) : layer.layerName
                  .toLowerCase()
                  .includes('memory') ? (
                <DatabaseOutlined style={{ fontSize: 11 }} />
              ) : (
                <CodeOutlined style={{ fontSize: 11 }} />
              )}
              {layer.layerName}
            </span>
            <Tag>{layer.tokenCount} tk</Tag>
          </div>
        ))}
      </div>
    </div>
  );
};

export default ContextTab;
