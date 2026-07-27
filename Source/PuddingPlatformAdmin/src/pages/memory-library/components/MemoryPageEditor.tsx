import {
  Typography,
  Tag,
  Spin,
  Empty,
  Space,
  Popconfirm,
  Button,
  Input,
  InputNumber,
} from 'antd';
import {
  BookOutlined,
  FileTextOutlined,
  DeleteOutlined,
  EditOutlined,
  PlusOutlined,
  SaveOutlined,
  CloseOutlined,
} from '@ant-design/icons';
import React, { useEffect, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { MemoryBookPageDto, MemoryChapterSectionDto } from '../types';

const { Title, Text } = Typography;

interface MemoryPageEditorProps {
  loading: boolean;
  book?: MemoryBookPageDto;
  /** 当选中非 Book 节点时，显示 TreeNode 信息。 */
  nodeTitle?: string;
  nodeSummary?: string;
  nodeType?: string;
  selectedChapterId?: string;
  onCreateChapter?: () => void;
  onUpdateChapter?: (
    chapterId: string,
    values: { title: string; content: string; importance: number },
  ) => Promise<boolean>;
  onArchiveChapter?: (chapterId: string) => void;
  saving?: boolean;
}

const MemoryPageEditor: React.FC<MemoryPageEditorProps> = ({
  loading,
  book,
  nodeTitle,
  nodeSummary,
  nodeType,
  selectedChapterId,
  onCreateChapter,
  onUpdateChapter,
  onArchiveChapter,
  saving,
}) => {
  const [editingChapterId, setEditingChapterId] = useState<string>();
  const [draftTitle, setDraftTitle] = useState('');
  const [draftContent, setDraftContent] = useState('');
  const [draftImportance, setDraftImportance] = useState(0.5);

  useEffect(() => {
    if (editingChapterId && editingChapterId !== selectedChapterId) {
      setEditingChapterId(undefined);
    }
  }, [editingChapterId, selectedChapterId]);

  const beginEditing = (chapter: MemoryChapterSectionDto) => {
    setDraftTitle(chapter.title);
    setDraftContent(chapter.content);
    setDraftImportance(chapter.importance);
    setEditingChapterId(chapter.chapterId);
  };

  const saveEditing = async () => {
    if (!editingChapterId || !onUpdateChapter || !draftTitle.trim() || !draftContent.trim()) return;
    const saved = await onUpdateChapter(editingChapterId, {
      title: draftTitle.trim(),
      content: draftContent,
      importance: draftImportance,
    });
    if (saved) setEditingChapterId(undefined);
  };

  if (loading) {
    return (
      <div className="memory-editor-loading">
        <Spin />
      </div>
    );
  }

  if (book) {
    const selectedChapter = book.chapters.find((chapter) => chapter.chapterId === selectedChapterId)
      ?? book.chapters[0];
    const selectedChapterIndex = selectedChapter
      ? book.chapters.findIndex((chapter) => chapter.chapterId === selectedChapter.chapterId)
      : -1;
    const isEditing = selectedChapter?.chapterId === editingChapterId;

    return (
      <article className="memory-book-page">
        <header className="memory-book-header">
          <div className="memory-book-eyebrow">
            <BookOutlined />
            <span>记忆 Book</span>
          </div>
          <Title level={2}>{book.title}</Title>
          <Space size={8} wrap className="memory-book-meta">
            <Tag color={book.status === 'active' ? 'green' : 'default'}>{book.status}</Tag>
            <Text type="secondary">{book.chapters.length} 个章节</Text>
          </Space>
          {book.summary && (
            <div className="memory-book-summary memory-book-summary-markdown">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{book.summary}</ReactMarkdown>
            </div>
          )}
        </header>

        <main className="memory-chapter-reader memory-chapter-reader--standalone">
            {selectedChapter ? (
              <>
                <header className="memory-current-chapter-header">
                  <div className="memory-current-chapter-copy">
                    <div className="memory-book-eyebrow">
                      <FileTextOutlined />
                      <span>章节 {String(selectedChapterIndex + 1).padStart(2, '0')}</span>
                    </div>
                    <Title level={2}>{selectedChapter.title}</Title>
                    <Space size={6} wrap className="memory-current-chapter-meta">
                      <Tag bordered={false}>{selectedChapter.contentType}</Tag>
                      <Text type="secondary">重要性 {selectedChapter.importance.toFixed(2)}</Text>
                    </Space>
                  </div>
                  <div className="memory-current-chapter-actions">
                    {onUpdateChapter && !isEditing && (
                      <Button
                        size="small"
                        icon={<EditOutlined />}
                        onClick={() => beginEditing(selectedChapter)}
                      >
                        编辑
                      </Button>
                    )}
                    {onArchiveChapter && (
                      <Popconfirm
                        title="归档此章节？"
                        onConfirm={() => onArchiveChapter(selectedChapter.chapterId)}
                      >
                        <Button
                          aria-label={`归档章节 ${selectedChapter.title}`}
                          title="归档章节"
                          size="small"
                          danger
                          icon={<DeleteOutlined />}
                        />
                      </Popconfirm>
                    )}
                  </div>
                </header>
                {isEditing ? (
                  <div className="memory-chapter-inline-editor">
                    <Input
                      className="memory-chapter-title-input"
                      value={draftTitle}
                      onChange={(event) => setDraftTitle(event.target.value)}
                      placeholder="章节标题"
                    />
                    <Input.TextArea
                      className="memory-chapter-content-input"
                      value={draftContent}
                      onChange={(event) => setDraftContent(event.target.value)}
                      autoSize={{ minRows: 14, maxRows: 32 }}
                      placeholder="使用 Markdown 编写章节内容"
                    />
                    <div className="memory-chapter-inline-editor-footer">
                      <label htmlFor="memory-chapter-importance">
                        <Text type="secondary">重要性</Text>
                        <InputNumber
                          id="memory-chapter-importance"
                          min={0}
                          max={1}
                          step={0.1}
                          value={draftImportance}
                          onChange={(value) => setDraftImportance(value ?? 0.5)}
                        />
                      </label>
                      <Space size={8}>
                        <Button
                          icon={<CloseOutlined />}
                          onClick={() => setEditingChapterId(undefined)}
                          disabled={saving}
                        >
                          取消
                        </Button>
                        <Button
                          type="primary"
                          icon={<SaveOutlined />}
                          onClick={saveEditing}
                          loading={saving}
                          disabled={!draftTitle.trim() || !draftContent.trim()}
                        >
                          保存
                        </Button>
                      </Space>
                    </div>
                  </div>
                ) : (
                  <div className="memory-chapter-markdown memory-chapter-document">
                    <ReactMarkdown remarkPlugins={[remarkGfm]}>{selectedChapter.content}</ReactMarkdown>
                  </div>
                )}
              </>
            ) : (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="此 Book 暂无章节"
              >
                {onCreateChapter && (
                  <Button type="primary" icon={<PlusOutlined />} onClick={onCreateChapter}>
                    新建第一章
                  </Button>
                )}
              </Empty>
            )}
        </main>
      </article>
    );
  }

  if (nodeTitle) {
    return (
      <article className="memory-node-page">
        <div className="memory-book-eyebrow">
          <BookOutlined />
          <span>目录页面</span>
        </div>
        <Title level={2}>{nodeTitle}</Title>
        {nodeType && (
          <Tag className="memory-node-type">{nodeType}</Tag>
        )}
        {nodeSummary ? (
          <div className="memory-book-summary memory-book-summary-markdown">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>{nodeSummary}</ReactMarkdown>
          </div>
        ) : (
          <Empty description="此节点为目录页，请选择子节点或挂载的 Book。" />
        )}
      </article>
    );
  }

  return (
    <div className="editor-empty">
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="从左侧记忆树选择一个 Page 或 Book"
      />
    </div>
  );
};

export default MemoryPageEditor;
