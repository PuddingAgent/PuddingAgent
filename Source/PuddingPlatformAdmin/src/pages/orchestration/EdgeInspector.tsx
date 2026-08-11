import {
  Alert,
  Button,
  Descriptions,
  Empty,
  Form,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import { patchEdgeDraft, removeEdgeFromDraft } from './edgeEditor';
import type { OrchestrationGraphDefinition } from './types';

const { Text } = Typography;

interface EdgeInspectorProps {
  definition: OrchestrationGraphDefinition;
  edgeId?: string;
  disabled?: boolean;
  onDefinitionChange: (definition: OrchestrationGraphDefinition) => void;
  onDeleted: () => void;
}

const EdgeInspector: React.FC<EdgeInspectorProps> = ({
  definition,
  edgeId,
  disabled,
  onDefinitionChange,
  onDeleted,
}) => {
  const edge = definition.edges.find((item) => item.edgeId === edgeId);
  if (!edge) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="选择画布中的连线"
      />
    );
  }
  return (
    <>
      <Space wrap style={{ marginBottom: 10 }}>
        <Tag color={edge.kind === 'data' ? 'purple' : 'blue'}>{edge.kind}</Tag>
        <Text code>{edge.edgeId}</Text>
      </Space>
      <Descriptions size="small" column={1} bordered>
        <Descriptions.Item label="源节点">{edge.fromNodeId}</Descriptions.Item>
        <Descriptions.Item label="目标节点">{edge.toNodeId}</Descriptions.Item>
        <Descriptions.Item label="数据映射">
          {edge.bindings.length === 0
            ? '—'
            : edge.bindings
                .map(
                  (binding) =>
                    `${binding.sourcePortId}${binding.sourcePath} → ${binding.targetPortId} (${binding.aggregation})`,
                )
                .join('；')}
        </Descriptions.Item>
      </Descriptions>
      {edge.predicate ? (
        <Alert
          type="info"
          showIcon
          message={`受管谓词 ${edge.predicate.evaluatorId}@${edge.predicate.version}`}
          description="谓词由注册表治理；当前编辑器只读，不能输入任意脚本或表达式。"
          style={{ marginTop: 10 }}
        />
      ) : null}
      <Form layout="vertical" style={{ marginTop: 12 }}>
        <Form.Item label="触发条件" style={{ marginBottom: 10 }}>
          <Select
            value={edge.condition}
            disabled={disabled}
            options={[
              { value: 'onSuccess', label: '上游成功（onSuccess）' },
              { value: 'onCompletion', label: '上游完成（onCompletion）' },
              { value: 'always', label: '始终（always）' },
            ]}
            onChange={(condition) =>
              onDefinitionChange(
                patchEdgeDraft(definition, edge.edgeId, { condition }),
              )
            }
          />
        </Form.Item>
        <Button
          danger
          size="small"
          disabled={disabled}
          onClick={() => {
            onDefinitionChange(removeEdgeFromDraft(definition, edge.edgeId));
            onDeleted();
          }}
        >
          删除连线
        </Button>
      </Form>
    </>
  );
};

export default EdgeInspector;
