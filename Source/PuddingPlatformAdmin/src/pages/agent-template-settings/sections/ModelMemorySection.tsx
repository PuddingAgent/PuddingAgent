import React from 'react';
import { Col, Row } from 'antd';
import { ProFormSelect } from '@ant-design/pro-components';
import type { LlmProviderDto, LlmModelDto } from '@/services/platform/api';
import { useStyles } from '../styles';

export interface ModelMemorySectionProps {
  id: string;
  providers: LlmProviderDto[];
  models: LlmModelDto[];
  memoryModels: LlmModelDto[];
  loadingModels: boolean;
  loadingMemoryModels: boolean;
  onProviderChange: (providerId: string) => void | Promise<void>;
  onMemoryProviderChange: (providerId: string) => void | Promise<void>;
}

const ModelMemorySection: React.FC<ModelMemorySectionProps> = ({
  id,
  providers,
  models,
  memoryModels,
  loadingModels,
  loadingMemoryModels,
  onProviderChange,
  onMemoryProviderChange,
}) => {
  const { styles } = useStyles();
  const providerOptions = providers
    .filter((p) => p.isEnabled)
    .map((p) => ({ label: p.name, value: p.providerId }));

  return (
    <section id={id} data-section-id={id} className={styles.section}>
      <div className={styles.sectionTitle}>模型与记忆</div>

      {/* 主模型：服务商 + 模型两列布局 */}
      <Row gutter={16}>
        <Col span={12}>
          <ProFormSelect
            name="preferredProviderId"
            label="首选服务商"
            options={providerOptions}
            placeholder="不选则使用平台默认"
            fieldProps={{ onChange: onProviderChange, allowClear: true }}
          />
        </Col>
        <Col span={12}>
          <ProFormSelect
            name="preferredModelId"
            label="首选模型"
            options={models.map((m) => ({
              label: `${m.name} (${(m.maxContextTokens / 1000).toFixed(0)}K)`,
              value: m.modelId,
            }))}
            placeholder="不选则使用服务商默认"
            fieldProps={{ loading: loadingModels, allowClear: true }}
          />
        </Col>
      </Row>

      {/* 潜意识模型：服务商 + 模型两列布局 */}
      <Row gutter={16}>
        <Col span={12}>
          <ProFormSelect
            name="memoryLlmProviderId"
            label="潜意识模型服务商"
            options={providerOptions}
            placeholder="不选则跟随主聊天模型"
            extra="端点、Key 和供应商参数在 LLM 资源池配置，这里只选择服务商。"
            fieldProps={{ onChange: onMemoryProviderChange, allowClear: true }}
          />
        </Col>
        <Col span={12}>
          <ProFormSelect
            name="memoryLlmModelId"
            label="潜意识模型"
            options={memoryModels.map((m) => ({
              label: `${m.name} (${m.modelId})`,
              value: m.modelId,
            }))}
            placeholder="不选则使用该服务商默认模型"
            extra="建议选择轻量模型，用于记忆深度探索。"
            fieldProps={{ loading: loadingMemoryModels, allowClear: true }}
          />
        </Col>
      </Row>

      <ProFormSelect
        name="memorySearchMode"
        label="记忆搜索模式"
        options={[
          { label: '关闭（仅关键词+标签检索）', value: 'off' },
          { label: '即时（关键词+标签+后台异步探索）', value: 'instant' },
          { label: '深度（同步探索，首次冷启动≤60s，上下文最精准）', value: 'deep' },
        ]}
        initialValue="deep"
      />

      <ProFormSelect
        name="reasoningEffort"
        label="推理深度"
        options={[
          { label: '跟随模型默认', value: '' },
          { label: '低（快速响应）', value: 'low' },
          { label: '中（平衡）', value: 'medium' },
          { label: '高（深度思考）', value: 'high' },
        ]}
      />
    </section>
  );
};

export default ModelMemorySection;
