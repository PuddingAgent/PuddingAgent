import { Handle, type NodeProps, Position } from '@xyflow/react';
import { Tag, Typography } from 'antd';
import React from 'react';
import { ComponentNodeOutput } from './componentUiRegistry';
import {
  CONTROL_INPUT_HANDLE,
  CONTROL_OUTPUT_HANDLE,
  dataInputHandle,
  dataOutputHandle,
} from './edgeEditor';
import type { OrchestrationFlowNode } from './graphViewModel';
import type { OrchestrationPortDefinition } from './types';

const { Text } = Typography;

function PortLabel({
  port,
  align,
}: {
  port: OrchestrationPortDefinition;
  align: 'left' | 'right';
}) {
  return (
    <div style={{ textAlign: align, lineHeight: 1.25, minHeight: 28 }}>
      <Text style={{ fontSize: 11 }}>{port.displayName || port.portId}</Text>
      <br />
      <Text type="secondary" style={{ fontSize: 10 }}>
        {port.contract.dataType} · {port.contract.cardinality}
      </Text>
    </div>
  );
}

const OrchestrationComponentNode: React.FC<
  NodeProps<OrchestrationFlowNode>
> = ({ data }) => (
  <div style={{ padding: '9px 12px 10px', minWidth: 206 }}>
    <Handle
      id={CONTROL_INPUT_HANDLE}
      type="target"
      position={Position.Left}
      style={{ top: 18, width: 9, height: 9, background: '#1677ff' }}
    />
    <Handle
      id={CONTROL_OUTPUT_HANDLE}
      type="source"
      position={Position.Right}
      style={{ top: 18, width: 9, height: 9, background: '#1677ff' }}
    />
    <div
      style={{
        display: 'flex',
        justifyContent: 'space-between',
        gap: 8,
        marginBottom: 7,
      }}
    >
      <Text strong ellipsis={{ tooltip: data.title }} style={{ maxWidth: 150 }}>
        {data.title}
      </Text>
      <Tag bordered={false} style={{ marginInlineEnd: 0, fontSize: 10 }}>
        {data.status}
      </Tag>
    </div>
    <Text type="secondary" style={{ fontSize: 10 }}>
      {data.kind}
    </Text>
    {data.inputPorts.length > 0 || data.outputPorts.length > 0 ? (
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: '1fr 1fr',
          gap: '5px 14px',
          marginTop: 9,
          paddingTop: 7,
          borderTop: '1px solid rgba(128,128,128,.22)',
        }}
      >
        <div style={{ display: 'grid', gap: 5 }}>
          {data.inputPorts.map((port) => (
            <div key={port.portId} style={{ position: 'relative' }}>
              <Handle
                id={dataInputHandle(port.portId)}
                type="target"
                position={Position.Left}
                style={{
                  top: 14,
                  left: -12,
                  width: 8,
                  height: 8,
                  background: '#722ed1',
                }}
              />
              <PortLabel port={port} align="left" />
            </div>
          ))}
        </div>
        <div style={{ display: 'grid', gap: 5 }}>
          {data.outputPorts.map((port) => (
            <div key={port.portId} style={{ position: 'relative' }}>
              <Handle
                id={dataOutputHandle(port.portId)}
                type="source"
                position={Position.Right}
                style={{
                  top: 14,
                  right: -12,
                  width: 8,
                  height: 8,
                  background: '#722ed1',
                }}
              />
              <PortLabel port={port} align="right" />
            </div>
          ))}
        </div>
      </div>
    ) : null}
    <ComponentNodeOutput data={data} />
  </div>
);

export default React.memo(OrchestrationComponentNode);
