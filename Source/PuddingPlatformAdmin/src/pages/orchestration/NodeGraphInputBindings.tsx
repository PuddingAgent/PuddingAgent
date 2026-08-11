import { Alert, Empty, Form, Select, Space, Tag, Typography } from 'antd';
import React from 'react';
import { areDataContractsCompatible } from './edgeEditor';
import { setNodeGraphInputBindings } from './graphInputs';
import type {
  OrchestrationGraphDefinition,
  OrchestrationNodeDefinition,
  OrchestrationRegisteredComponent,
} from './types';

const { Text } = Typography;

interface NodeGraphInputBindingsProps {
  definition: OrchestrationGraphDefinition;
  node: OrchestrationNodeDefinition;
  component?: OrchestrationRegisteredComponent;
  disabled?: boolean;
  onDefinitionChange: (definition: OrchestrationGraphDefinition) => void;
}

const NodeGraphInputBindings: React.FC<NodeGraphInputBindingsProps> = ({
  definition,
  node,
  component,
  disabled,
  onDefinitionChange,
}) => {
  if (!component) {
    return (
      <Alert
        type="warning"
        showIcon
        message="Catalog 中找不到该组件，无法编辑端口绑定"
      />
    );
  }
  if (component.descriptor.inputPorts.length === 0) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="组件没有数据输入端口"
      />
    );
  }

  return (
    <Form layout="vertical">
      {component.descriptor.inputPorts.map((port) => {
        const values = (node.graphInputBindings ?? [])
          .filter(
            (binding) =>
              binding.targetPortId.toLowerCase() === port.portId.toLowerCase(),
          )
          .map((binding) => binding.inputId);
        const compatibleInputs = (definition.inputs ?? []).filter((input) =>
          areDataContractsCompatible(input.contract, port.contract),
        );
        const occupiedByDataEdge = definition.edges.some(
          (edge) =>
            edge.kind === 'data' &&
            edge.toNodeId.toLowerCase() === node.nodeId.toLowerCase() &&
            edge.bindings.some(
              (binding) =>
                binding.targetPortId.toLowerCase() ===
                port.portId.toLowerCase(),
            ),
        );
        const selectValue =
          port.contract.cardinality === 'many' ? values : values[0];
        return (
          <Form.Item
            key={port.portId}
            label={
              <Space size={4} wrap>
                <Text>{port.displayName || port.portId}</Text>
                <Tag>{port.contract.dataType}</Tag>
                <Tag>{port.contract.cardinality}</Tag>
                {port.required ? <Tag color="blue">required</Tag> : null}
              </Space>
            }
            extra={
              occupiedByDataEdge
                ? '该端口已有 data edge；单值端口不能再绑定 Graph Input。'
                : undefined
            }
            style={{ marginBottom: 10 }}
          >
            <Select
              allowClear
              mode={
                port.contract.cardinality === 'many' ? 'multiple' : undefined
              }
              value={selectValue}
              disabled={
                disabled ||
                (occupiedByDataEdge && port.contract.cardinality === 'one')
              }
              placeholder={
                compatibleInputs.length > 0
                  ? '选择兼容的 Graph Input'
                  : '没有兼容的 Graph Input'
              }
              options={compatibleInputs.map((input) => ({
                value: input.inputId,
                label: `${input.inputId} · ${input.contract.mediaTypes.join(', ') || '*'}`,
              }))}
              onChange={(value) => {
                const inputIds = Array.isArray(value)
                  ? value
                  : value
                    ? [value]
                    : [];
                onDefinitionChange(
                  setNodeGraphInputBindings(
                    definition,
                    node.nodeId,
                    port.portId,
                    inputIds,
                  ),
                );
              }}
            />
          </Form.Item>
        );
      })}
    </Form>
  );
};

export default NodeGraphInputBindings;
