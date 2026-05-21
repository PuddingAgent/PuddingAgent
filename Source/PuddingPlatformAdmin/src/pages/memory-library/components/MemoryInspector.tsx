import { Tabs, Descriptions, Tag, Empty, Spin, Typography } from 'antd';
import {
  InfoCircleOutlined,
  LinkOutlined,
  FileSearchOutlined,
} from '@ant-design/icons';
import React from 'react';
import type { MemoryBookPageDto } from '../types';

const { Text } = Typography;

interface MemoryInspectorProps {
  loading: boolean;
  book?: MemoryBookPageDto;
  nodeTitle?: string;
  nodeType?: string;
  nodeId?: string;
  /** 来源引用列表（V1 由后端 API 提供，当前为空占位）。 */
  sources?: { targetType: string; targetId: string; label?: string }[];
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
  if (loading) {
    return (
      <div style={{ display: 'flex', justifyContent: 'center', padding: 24 }}>
        <Spin />
      </div>
    );
  }

  const hasData = book || nodeId;

  return (
    <Tabs
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
                    <Text code style={{ fontSize: 11 }}>{book.bookId}</Text>
                  </Descriptions.Item>
                  <Descriptions.Item label="Library ID">
                    <Text code style={{ fontSize: 11 }}>{book.libraryId}</Text>
                  </Descriptions.Item>
                  <Descriptions.Item label="Workspace">
                    <Text code style={{ fontSize: 11 }}>{book.workspaceId}</Text>
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
                    <Text code style={{ fontSize: 11 }}>{nodeId}</Text>
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
          label: <span><FileSearchOutlined /> 来源</span>,
          children: sources?.length ? (
            <div>
              {sources.map((s, i) => (
                <div key={i} style={{ marginBottom: 8 }}>
                  <Tag>{s.targetType}</Tag>
                  <Text code style={{ fontSize: 11 }}>{s.targetId}</Text>
                  {s.label && <div><Text type="secondary">{s.label}</Text></div>}
                </div>
              ))}
            </div>
          ) : (
            <Empty description="暂无来源引用" image={Empty.PRESENTED_IMAGE_SIMPLE} />
          ),
        },
        {
          key: 'links',
          label: <span><LinkOutlined /> 链接</span>,
          children: pointers?.length ? (
            <div>
              {pointers.map((p) => (
                <div key={p.pointerId} style={{ marginBottom: 8 }}>
                  <Tag color="purple">{p.targetType}</Tag>
                  <Text code style={{ fontSize: 11 }}>{p.targetId}</Text>
                  {p.label && <div><Text type="secondary">{p.label}</Text></div>}
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
