import React from 'react';
import { Col, Row } from 'antd';
import { ProFormSelect } from '@ant-design/pro-components';
import type { LlmProviderDto, LlmModelDto } from '@/services/platform/api';
import { useStyles } from '../styles';
import { getAgentTemplateSelectPopupProps } from '../selectPopup';

export interface ModelMemorySectionProps {
  id: string;
  providers: LlmProviderDto[];
  models: LlmModelDto[];
  memoryModels: LlmModelDto[];
  loadingModels: boolean;
  loadingMemoryModels: boolean;
  onProviderChange: (providerId: string) => void | Promise<void>;
  onMemoryProviderChange: (providerId: string) => void | Promise<void>;
  embeddingModels: LlmModelDto[];
  loadingEmbeddingModels: boolean;
  onEmbeddingProviderChange: (providerId: string) => void | Promise<void>;
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
  embeddingModels,
  loadingEmbeddingModels,
  onEmbeddingProviderChange,
}) => {
  const { styles } = useStyles();
  const providerOptions = providers
    .filter((p) => p.isEnabled)
    .map((p) => ({ label: p.name, value: p.providerId }));

  return (
    <section id={id} data-section-id={id} className={styles.section}>
      <div className={styles.sectionTitle}>默认模型策略</div>

      {/* 主模型：服务商 + 模型两列布局 */}
      <Row gutter={16}>
        <Col span={12}>
          <ProFormSelect
            name="preferredProviderId"
            label="默认服务商"
            options={providerOptions}
            placeholder="不选则使用平台默认"
            fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup, {
              onChange: onProviderChange,
              allowClear: true,
            })}
          />
        </Col>
        <Col span={12}>
          <ProFormSelect
            name="preferredModelId"
            label="默认模型"
            options={models.map((m) => ({
              label: `${m.name} (${(m.maxContextTokens / 1000).toFixed(0)}K)`,
              value: m.modelId,
            }))}
            placeholder="不选则使用服务商默认"
            fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup, {
              loading: loadingModels,
              allowClear: true,
            })}
          />
        </Col>
      </Row>

      {/* 潜意识模型：服务商 + 模型两列布局 */}
      <Row gutter={16}>
        <Col span={12}>
          <ProFormSelect
            name="memoryLlmProviderId"
            label="默认潜意识模型服务商"
            options={providerOptions}
            placeholder="不选则跟随主聊天模型"
            extra="端点、Key 和供应商参数在 LLM 资源池配置，这里只选择服务商。"
            fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup, {
              onChange: onMemoryProviderChange,
              allowClear: true,
            })}
          />
        </Col>
        <Col span={12}>
          <ProFormSelect
            name="memoryLlmModelId"
            label="默认潜意识模型"
            options={memoryModels.map((m) => ({
              label: `${m.name} (${m.modelId})`,
              value: m.modelId,
            }))}
            placeholder="不选则使用该服务商默认模型"
            extra="建议选择轻量模型，用于记忆深度探索。"
            fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup, {
              loading: loadingMemoryModels,
              allowClear: true,
            })}
          />
        </Col>
      </Row>

      <ProFormSelect
        name="memorySearchMode"
        label="默认记忆搜索模式"
        options={[
          { label: '关闭（仅关键词+标签检索）', value: 'off' },
          { label: '即时（关键词+标签+后台异步探索）', value: 'instant' },
          { label: '深度（同步探索，首次冷启动≤60s，上下文最精准）', value: 'deep' },
        ]}
        initialValue="deep"
        fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup)}
      />

      <ProFormSelect
        name="reasoningEffort"
        label="默认推理深度"
        options={[
          { label: '跟随模型默认', value: '' },
          { label: '低（快速响应）', value: 'low' },
          { label: '中（平衡）', value: 'medium' },
          { label: '高（深度思考）', value: 'high' },
        ]}
        fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup)}
      />

      {/* ── Embedding 模型选择 ── */}
      <div style={{ marginTop: 24, paddingTop: 16, borderTop: '1px dashed #d9d9d9' }}>
        <div style={{ fontWeight: 500, marginBottom: 8, color: 'rgba(0,0,0,0.85)' }}>
          Embedding 向量化模型
        </div>
        <Row gutter={16}>
          <Col span={12}>
            <ProFormSelect
              name="embeddingProviderId"
              label="Embedding 服务商"
              options={providerOptions}
              placeholder="不选则使用平台默认"
              extra="选择提供 Embedding API 的服务商。"
              fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup, {
                onChange: onEmbeddingProviderChange,
                allowClear: true,
              })}
            />
          </Col>
          <Col span={12}>
            <ProFormSelect
              name="embeddingModelId"
              label="Embedding 模型"
              options={embeddingModels.map((m) => ({
                label: `${m.name} (${m.modelId})`,
                value: m.modelId,
              }))}
              placeholder="不选则使用服务商默认"
              extra="选择向量化模型，建议使用 text-embedding-v4 或 text-embedding-3-small。"
              fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup, {
                loading: loadingEmbeddingModels,
                allowClear: true,
              })}
            />
          </Col>
        </Row>
      </div>
    </section>
  );
};

export default ModelMemorySection;
