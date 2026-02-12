import { Typography, Tag, Spin, Empty, Divider, Space, Popconfirm, Button } from 'antd';
import { BookOutlined, FileTextOutlined, DeleteOutlined } from '@ant-design/icons';
import React from 'react';
import type { MemoryBookPageDto } from '../types';

const { Title, Text, Paragraph } = Typography;

interface MemoryPageEditorProps {
  loading: boolean;
  book?: MemoryBookPageDto;
  /** 当选中非 Book 节点时，显示 TreeNode 信息。 */
  nodeTitle?: string;
  nodeSummary?: string;
  nodeType?: string;
  /** 归档章节回调。 */
  onArchiveChapter?: (chapterId: string) => void;
}

const MemoryPageEditor: React.FC<MemoryPageEditorProps> = ({
  loading,
  book,
  nodeTitle,
  nodeSummary,
  nodeType,
  onArchiveChapter,
}) => {
  if (loading) {
    return (
      <div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}>
        <Spin />
      </div>
    );
  }

  // Book page view
  if (book) {
    return (
      <div>
        <Title level={3} style={{ marginBottom: 4 }}>{book.title}</Title>
        <div style={{ marginBottom: 16 }}>
          <Tag color="blue">{book.status}</Tag>
          <Text type="secondary" style={{ marginLeft: 8 }}>
            {book.chapters.length} 个章节
          </Text>
        </div>
        {book.summary && (
          <Paragraph type="secondary" style={{ marginBottom: 24 }}>
            {book.summary}
          </Paragraph>
        )}
        <Divider />
        {book.chapters.length === 0 ? (
          <Empty description="暂无章节" />
        ) : (
          book.chapters.map((ch) => (
            <div key={ch.chapterId} style={{ marginBottom: 24 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
                <FileTextOutlined />
                <Title level={5} style={{ margin: 0 }}>{ch.title}</Title>
              </div>
              <Paragraph
                ellipsis={{ rows: 4, expandable: true, symbol: '展开' }}
                style={{ whiteSpace: 'pre-wrap', color: '#555' }}
              >
                {ch.content}
              </Paragraph>
              <Space size="small" style={{ fontSize: 12, color: '#999' }}>
                <Tag>{ch.contentType}</Tag>
                <span>重要性: {ch.importance.toFixed(2)}</span>
              </Space>
              {onArchiveChapter && (
                <div style={{ marginTop: 4 }}>
                  <Popconfirm title="归档此章节？" onConfirm={() => onArchiveChapter(ch.chapterId)}>
                    <Button size="small" type="text" danger icon={<DeleteOutlined />}>
                      归档
                    </Button>
                  </Popconfirm>
                </div>
              )}
            </div>
          ))
        )}
      </div>
    );
  }

  // TreeNode info view (non-Book)
  if (nodeTitle) {
    return (
      <div>
        <Title level={3} style={{ marginBottom: 4 }}>
          <BookOutlined style={{ marginRight: 8 }} />
          {nodeTitle}
        </Title>
        {nodeType && (
          <Tag style={{ marginBottom: 16 }}>{nodeType}</Tag>
        )}
        {nodeSummary ? (
          <Paragraph type="secondary">{nodeSummary}</Paragraph>
        ) : (
          <Empty description="此节点为目录页，请选择子节点或挂载的 Book。" />
        )}
      </div>
    );
  }

  return (
    <div className="editor-empty">
      请从左侧记忆树中选择一个节点
    </div>
  );
};

export default MemoryPageEditor;
