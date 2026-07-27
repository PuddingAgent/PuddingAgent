import { Tabs, Descriptions, Tag, Empty, Spin, Typography } from 'antd';
import {
  InfoCircleOutlined,
  LinkOutlined,
  FileSearchOutlined,
} from '@ant-design/icons';
import React, { useMemo } from 'react';
import type { MemoryBookPageDto } from '../types';

const { Text } = Typography;

interface MemoryInspectorProps {
  loading: boolean;
  book?: MemoryBookPageDto;
  nodeTitle?: string;
  nodeType?: string;
  nodeId?: string;
  /** 来源引用列表（V1 由后端 API 提供，当前为空占位）。 */
  sources?: { sourceReferenceId?: string; targetType: string; targetId: string; label?: string }[];
  /** 指针/反向链接列表（V1 由后端 API 提供，当前为空占位）。 */
  pointers?: { pointerId: string; targetType: string; targetId: string; label?: string }[];
}

const MemoryInspector: React.FC<MemoryInspectorProps> = ({
  loading,
  book,
  nodeTitle,
  nodeType,
  nodeId,
  sources,
  pointers,
}) => {
  const uniqueSources = useMemo(
    () => Array.from(new Map(
      (sources ?? []).map((source) => [
        `${source.sourceReferenceId ?? ''}:${source.targetType}:${source.targetId}`,
        source,
      ]),
    ).values()),
    [sources],
  );
  const uniquePointers = useMemo(
    () => Array.from(new Map(
      (pointers ?? []).map((pointer) => [
        `${pointer.targetType}:${pointer.targetId}:${pointer.label ?? ''}`,
        pointer,
      ]),
    ).values()),
    [pointers],
  );

  if (loading) {
    return (
      <div className="memory-inspector-loading">
        <Spin />
      </div>
    );
  }

  const hasData = book || nodeId;

  return (
    <Tabs
      className="memory-inspector-tabs"
      size="small"
      items={[
        {
          key: 'info',
          label: <span><InfoCircleOutlined /> 信息</span>,
          children: !hasData ? (
            <Empty description="选择节点查看信息" />
          ) : (
            <Descriptions column={1} size="small">
              {book && (
                <>
                  <Descriptions.Item label="Book ID">
                    <Text code copyable={{ text: book.bookId }}>{book.bookId}</Text>
                  </Descriptions.Item>
                  <Descriptions.Item label="Library ID">
                    <Text code copyable={{ text: book.libraryId }}>{book.libraryId}</Text>
                  </Descriptions.Item>
                  <Descriptions.Item label="Workspace">
                    <Text code copyable={{ text: book.workspaceId }}>{book.workspaceId}</Text>
                  </Descriptions.Item>
                  <Descriptions.Item label="Status">
                    <Tag color={book.status === 'active' ? 'green' : 'default'}>{book.status}</Tag>
                  </Descriptions.Item>
                  <Descriptions.Item label="章节数">{book.chapters.length}</Descriptions.Item>
                </>
              )}
              {nodeId && !book && (
                <>
                  <Descriptions.Item label="Node ID">
                    <Text code copyable={{ text: nodeId }}>{nodeId}</Text>
                  </Descriptions.Item>
                  <Descriptions.Item label="Node Type">
                    <Tag>{nodeType}</Tag>
                  </Descriptions.Item>
                  <Descriptions.Item label="Title">{nodeTitle}</Descriptions.Item>
                </>
              )}
            </Descriptions>
          ),
        },
        {
          key: 'sources',
          label: <span><FileSearchOutlined /> 来源 {uniqueSources.length || ''}</span>,
          children: uniqueSources.length ? (
            <div className="memory-inspector-list">
              {uniqueSources.map((s) => (
                <div className="memory-inspector-card" key={s.sourceReferenceId ?? `${s.targetType}:${s.targetId}`}>
                  <div className="memory-inspector-card-topline">
                  <Tag>{s.targetType}</Tag>
                    <Text code copyable={{ text: s.targetId }}>{s.targetId}</Text>
                  </div>
                  {s.label && <Text type="secondary" className="memory-inspector-card-label">{s.label}</Text>}
                </div>
              ))}
            </div>
          ) : (
            <Empty description="暂无来源引用" image={Empty.PRESENTED_IMAGE_SIMPLE} />
          ),
        },
        {
          key: 'links',
          label: <span><LinkOutlined /> 链接 {uniquePointers.length || ''}</span>,
          children: uniquePointers.length ? (
            <div className="memory-inspector-list">
              {uniquePointers.map((p) => (
                <div className="memory-inspector-card" key={p.pointerId}>
                  <div className="memory-inspector-card-topline">
                  <Tag color="purple">{p.targetType}</Tag>
                    <Text code copyable={{ text: p.targetId }}>{p.targetId}</Text>
                  </div>
                  {p.label && <Text type="secondary" className="memory-inspector-card-label">{p.label}</Text>}
                </div>
              ))}
            </div>
          ) : (
            <Empty description="暂无指针引用" image={Empty.PRESENTED_IMAGE_SIMPLE} />
          ),
        },
      ]}
    />
  );
};

export default MemoryInspector;
