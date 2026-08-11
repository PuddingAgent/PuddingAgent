import { Alert, AutoComplete, Form, Input, Spin, Typography } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import {
  listGlobalAgentTemplates,
  listLlmModels,
  listLlmProviders,
  listWorkspaceAgentTemplates,
} from '@/services/platform/api';
import type {
  OrchestrationExecutorBinding,
  OrchestrationNodeDefinition,
} from './types';

const { Text } = Typography;

export interface SubAgentNodeSettingsProps {
  node: OrchestrationNodeDefinition;
  workspaceId: string;
  disabled: boolean;
  onExecutorChange: (executor: OrchestrationExecutorBinding) => void;
}

interface SelectOption {
  value: string;
  label: string;
}

const SubAgentNodeSettings: React.FC<SubAgentNodeSettingsProps> = ({
  node,
  workspaceId,
  disabled,
  onExecutorChange,
}) => {
  const [routeOptions, setRouteOptions] = useState<SelectOption[]>([]);
  const [templateOptions, setTemplateOptions] = useState<SelectOption[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string>();
  const executor = node.executor ?? { kind: 'subAgent' as const };

  useEffect(() => {
    let active = true;
    setLoading(true);
    setLoadError(undefined);
    void (async () => {
      try {
        const [providers, workspaceTemplates, globalTemplates] =
          await Promise.all([
            listLlmProviders(),
            listWorkspaceAgentTemplates(workspaceId),
            listGlobalAgentTemplates(true),
          ]);
        const enabledProviders = providers.filter(
          (provider) => provider.isEnabled,
        );
        const modelsByProvider = await Promise.all(
          enabledProviders.map(async (provider) => ({
            provider,
            models: await listLlmModels(provider.providerId),
          })),
        );
        if (!active) return;
        setRouteOptions(
          modelsByProvider
            .flatMap(({ provider, models }) =>
              models
                .filter((model) => !model.isDeprecated && !model.isEmbedding)
                .map((model) => ({
                  value: `${provider.providerId}/${model.modelId}`,
                  label: `${provider.name} / ${model.name} · ${model.protocol}`,
                })),
            )
            .sort((left, right) => left.label.localeCompare(right.label)),
        );

        const seen = new Set<string>();
        setTemplateOptions(
          [...workspaceTemplates, ...globalTemplates]
            .filter(
              (template) =>
                template.isEnabled && !seen.has(template.templateId),
            )
            .map((template) => {
              seen.add(template.templateId);
              return {
                value: template.templateId,
                label: `${template.name} · ${template.role}`,
              };
            }),
        );
      } catch (error) {
        if (active)
          setLoadError(
            error instanceof Error ? error.message : '加载 Agent 配置失败',
          );
      } finally {
        if (active) setLoading(false);
      }
    })();
    return () => {
      active = false;
    };
  }, [workspaceId]);

  const updateExecutor = (changes: Partial<OrchestrationExecutorBinding>) => {
    onExecutorChange({
      kind: 'subAgent',
      role: executor.role ?? '',
      templateId: executor.templateId ?? '',
      routeKey: executor.routeKey ?? '',
      ...changes,
    });
  };

  const currentRouteOptions = useMemo(() => {
    if (
      !executor.routeKey ||
      routeOptions.some((item) => item.value === executor.routeKey)
    )
      return routeOptions;
    return [
      { value: executor.routeKey, label: `${executor.routeKey} · 当前值` },
      ...routeOptions,
    ];
  }, [executor.routeKey, routeOptions]);

  return (
    <div style={{ marginTop: 14 }}>
      <Text strong>Sub-agent 执行设置</Text>
      {loadError ? (
        <Alert
          type="warning"
          showIcon
          message="目录加载失败，仍可手动输入"
          description={loadError}
          style={{ marginTop: 8, marginBottom: 8 }}
        />
      ) : null}
      <Spin spinning={loading} size="small">
        <Form layout="vertical" style={{ marginTop: 8 }}>
          <Form.Item label="角色" required style={{ marginBottom: 8 }}>
            <Input
              value={executor.role}
              disabled={disabled}
              placeholder="例如 copy-planner / storyboard-director"
              onChange={(event) => updateExecutor({ role: event.target.value })}
            />
          </Form.Item>
          <Form.Item label="Agent 模板" required style={{ marginBottom: 8 }}>
            <AutoComplete
              value={executor.templateId}
              options={templateOptions}
              disabled={disabled}
              placeholder="选择或输入 Template ID"
              filterOption={(input, option) =>
                String(option?.label ?? option?.value ?? '')
                  .toLowerCase()
                  .includes(input.toLowerCase())
              }
              onChange={(templateId) => updateExecutor({ templateId })}
            />
          </Form.Item>
          <Form.Item
            label="精确模型路由"
            required
            extra="路由冻结为 provider/model；运行时不会回退到服务商默认模型。"
            style={{ marginBottom: 0 }}
          >
            <AutoComplete
              value={executor.routeKey}
              options={currentRouteOptions}
              disabled={disabled}
              placeholder="provider/model"
              filterOption={(input, option) =>
                String(option?.label ?? option?.value ?? '')
                  .toLowerCase()
                  .includes(input.toLowerCase())
              }
              onChange={(routeKey) => updateExecutor({ routeKey })}
            />
          </Form.Item>
        </Form>
      </Spin>
    </div>
  );
};

export default SubAgentNodeSettings;
