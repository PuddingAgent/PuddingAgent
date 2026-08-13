import { type NodeProps } from '@xyflow/react';
import { Empty, Tag, Typography } from 'antd';
import React from 'react';
import type { OrchestrationFlowNode } from './graphViewModel';

const { Text } = Typography;

/**
 * Read-only virtual `Graph Inputs` canvas node (doc 84 §9).
 *
 * It renders the graph-level input contract so the editor can see the graph's public
 * call contract at a glance. It deliberately exposes no <Handle> and is marked
 * draggable=false by the view model, so it can never be wired as an ordinary
 * component node; the real wiring lives in node graphInputBindings[].
 */
const GraphInputsNode: React.FC<NodeProps<OrchestrationFlowNode>> = ({
  data,
}) => {
  const inputs = data.inputs ?? [];
  return (
    <div style={{ padding: '9px 12px 10px', minWidth: 206 }}>
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          gap: 8,
          marginBottom: 7,
        }}
      >
        <Text strong ellipsis={{ tooltip: data.title }}>
          {data.title}
        </Text>
        <Tag bordered={false} color="purple" style={{ marginInlineEnd: 0 }}>
          {inputs.length}
        </Tag>
      </div>
      <Text type="secondary" style={{ fontSize: 10 }}>
        图级公共契约 · 只读虚拟节点
      </Text>
      {inputs.length === 0 ? (
        <div
          style={{
            marginTop: 9,
            paddingTop: 7,
            borderTop: '1px solid rgba(128,128,128,.22)',
          }}
        >
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="暂无图级输入"
          />
        </div>
      ) : (
        <div
          style={{
            display: 'grid',
            gap: 5,
            marginTop: 9,
            paddingTop: 7,
            borderTop: '1px solid rgba(128,128,128,.22)',
          }}
        >
          {inputs.map((input) => (
            <div key={input.inputId}>
              <Text style={{ fontSize: 11 }}>{input.inputId}</Text>
              <br />
              <Text type="secondary" style={{ fontSize: 10 }}>
                {input.contract.dataType} · {input.contract.cardinality}
                {input.defaultValue !== undefined &&
                input.defaultValue !== null
                  ? ' · 默认值'
                  : ''}
              </Text>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};

export default React.memo(GraphInputsNode);
