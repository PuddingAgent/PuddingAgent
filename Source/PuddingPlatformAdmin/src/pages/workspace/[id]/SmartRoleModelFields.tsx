import { ProFormSelect } from '@ant-design/pro-components';
import { Alert, Typography } from 'antd';
import React, { useEffect, useState } from 'react';
import {
  listLlmModels,
  listLlmProviders,
  type LlmModelDto,
  type LlmProviderDto,
} from '@/services/platform/api';

const { Text } = Typography;

const SMART_ROLE_FIELDS = [
  {
    name: 'explorerModel',
    label: 'Explorer 探索者',
    description: '文件、代码库和会话记录探索',
  },
  {
    name: 'researcherModel',
    label: 'Researcher 研究员',
    description: '多源深度研究与分析',
  },
  {
    name: 'plannerModel',
    label: 'Planner 规划者',
    description: '任务规划与步骤分解',
  },
  {
    name: 'reviewerModel',
    label: 'Reviewer 审查者',
    description: '代码和方案质量审查',
  },
  {
    name: 'developerModel',
    label: 'Developer 开发者',
    description: '代码实现与构建',
  },
  {
    name: 'deployerModel',
    label: 'Deployer 部署者',
    description: '部署与运维任务',
  },
  {
    name: 'testerModel',
    label: 'Tester 测试者',
    description: '测试执行与结果分析',
  },
] as const;

interface ModelOption {
  label: string;
  value: string;
}

function toModelOptions(
  provider: LlmProviderDto,
  models: LlmModelDto[],
): ModelOption[] {
  return models
    .filter((model) => !model.isDeprecated && !model.isEmbedding)
    .sort((left, right) => left.sortOrder - right.sortOrder || left.name.localeCompare(right.name))
    .map((model) => ({
      label: `${provider.name} / ${model.name} (${Math.round(model.maxContextTokens / 1024)}K)`,
      value: `${provider.providerId}/${model.modelId}`,
    }));
}

const SmartRoleModelFields: React.FC = () => {
  const [options, setOptions] = useState<ModelOption[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadWarning, setLoadWarning] = useState<string>();

  useEffect(() => {
    let active = true;

    void (async () => {
      setLoading(true);
      setLoadWarning(undefined);

      try {
        const providers = (await listLlmProviders())
          .filter((provider) => provider.isEnabled)
          .sort((left, right) => left.name.localeCompare(right.name));
        const modelResults = await Promise.allSettled(
          providers.map(async (provider) => ({
            provider,
            models: await listLlmModels(provider.providerId),
          })),
        );

        if (!active) return;

        const loadedOptions = modelResults.flatMap((result) =>
          result.status === 'fulfilled'
            ? toModelOptions(result.value.provider, result.value.models)
            : [],
        );
        setOptions(loadedOptions);

        const failedCount = modelResults.filter((result) => result.status === 'rejected').length;
        if (failedCount > 0) {
          setLoadWarning(`${failedCount} 个服务商的模型列表加载失败，当前仅显示已成功加载的模型。`);
        }
      } catch {
        if (active) {
          setOptions([]);
          setLoadWarning('LLM 服务商列表加载失败，已保存的配置仍会保留。');
        }
      } finally {
        if (active) setLoading(false);
      }
    })();

    return () => {
      active = false;
    };
  }, []);

  return (
    <>
      <Text strong style={{ display: 'block', marginTop: 12, marginBottom: 8 }}>
        Smart 子代理模型
      </Text>
      <Alert
        showIcon
        type="info"
        message="这些字段属于当前 Agent 实例"
        description="Smart 工具调用子代理时，从 Agent manifest 读取对应角色的 providerId/modelId；留空时由子代理默认模型策略处理。"
        style={{ marginBottom: 12 }}
      />
      {loadWarning && (
        <Alert
          showIcon
          type="warning"
          message={loadWarning}
          style={{ marginBottom: 12 }}
        />
      )}
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
          columnGap: 16,
        }}
      >
        {SMART_ROLE_FIELDS.map((role) => (
          <ProFormSelect
            key={role.name}
            name={role.name}
            label={role.label}
            options={options}
            placeholder="选择服务商 / 模型"
            extra={role.description}
            fieldProps={{
              allowClear: true,
              loading,
              showSearch: true,
              optionFilterProp: 'label',
            }}
          />
        ))}
      </div>
    </>
  );
};

export default SmartRoleModelFields;
