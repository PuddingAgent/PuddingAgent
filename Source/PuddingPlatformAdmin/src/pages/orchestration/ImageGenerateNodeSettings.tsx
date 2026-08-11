import { Form, Input, Select, Space, Switch, Typography } from 'antd';
import React from 'react';
import type { OrchestrationNodeDefinition } from './types';

const { Text } = Typography;

export interface ImageGenerateNodeSettingsProps {
  node: OrchestrationNodeDefinition;
  disabled: boolean;
  onConfigurationChange: (configuration: Record<string, unknown>) => void;
}

const ImageGenerateNodeSettings: React.FC<ImageGenerateNodeSettingsProps> = ({
  node,
  disabled,
  onConfigurationChange,
}) => {
  const patch = (key: string, value: unknown) =>
    onConfigurationChange({ ...node.configuration, [key]: value });
  return (
    <div style={{ marginTop: 14 }}>
      <Text strong>图片生成设置</Text>
      <Form layout="vertical" style={{ marginTop: 8 }}>
        <Space size={8} style={{ width: '100%' }} align="start">
          <Form.Item label="模式" style={{ flex: 1, marginBottom: 8 }}>
            <Select
              value={
                (node.configuration.mode as string | undefined) ?? 'default'
              }
              disabled={disabled}
              onChange={(value) => patch('mode', value)}
              options={[
                { value: 'default', label: '默认' },
                { value: 'precision', label: '精准编辑' },
                { value: 'sequence', label: '序列图' },
              ]}
            />
          </Form.Item>
          <Form.Item label="尺寸" style={{ flex: 1, marginBottom: 8 }}>
            <Select
              value={(node.configuration.size as string | undefined) ?? '2K'}
              disabled={disabled}
              onChange={(value) => patch('size', value)}
              options={['1K', '2K', '4K'].map((value) => ({
                value,
                label: value,
              }))}
            />
          </Form.Item>
        </Space>
        <Form.Item label="Provider（可选）" style={{ marginBottom: 8 }}>
          <Input
            allowClear
            value={(node.configuration.providerId as string | undefined) ?? ''}
            disabled={disabled}
            placeholder="留空使用图片服务默认路由"
            onChange={(event) => patch('providerId', event.target.value)}
          />
        </Form.Item>
        <Form.Item label="Model（可选）" style={{ marginBottom: 8 }}>
          <Input
            allowClear
            value={(node.configuration.modelId as string | undefined) ?? ''}
            disabled={disabled}
            placeholder="留空使用图片服务默认模型"
            onChange={(event) => patch('modelId', event.target.value)}
          />
        </Form.Item>
        <Form.Item label="水印" style={{ marginBottom: 0 }}>
          <Switch
            checked={
              (node.configuration.watermark as boolean | undefined) ?? true
            }
            disabled={disabled}
            onChange={(value) => patch('watermark', value)}
          />
        </Form.Item>
      </Form>
    </div>
  );
};

export default ImageGenerateNodeSettings;
