import { Tree } from 'antd';
import type { DataNode } from 'antd/es/tree';
import {
  FolderOutlined,
  FileTextOutlined,
  BookOutlined,
  AlignLeftOutlined,
} from '@ant-design/icons';
import React, { useMemo } from 'react';
import type { MemoryBookPageDto, MemoryLibraryTreeNodeDto } from '../types';

interface MemoryPageTreeProps {
  loading: boolean;
  data: MemoryLibraryTreeNodeDto[];
  selectedKey?: string;
  book?: MemoryBookPageDto;
  selectedChapterId?: string;
  onSelect: (node: MemoryLibraryTreeNodeDto) => void;
  onSelectChapter?: (chapterId: string) => void;
  emptyDescription?: string;
}

/**
 * 将 TreeNode DTO 转换为 Ant Design Tree 的 DataNode 格式。
 * 通过 type 区分图标：library → BookOutlined, book_page → FileTextOutlined, 其他 → FolderOutlined。
 */
function toDataNodes(nodes: MemoryLibraryTreeNodeDto[], book?: MemoryBookPageDto): DataNode[] {
  return nodes.map((node) => {
    const nestedNodes = node.children?.length ? toDataNodes(node.children, book) : [];
    const chapterNodes = book && node.bookId === book.bookId
      ? book.chapters.map((chapter, index) => ({
          key: `chapter:${chapter.chapterId}`,
          title: (
            <span className="memory-tree-chapter-title" title={chapter.title}>
              <span>{String(index + 1).padStart(2, '0')}</span>
              <span>{chapter.title}</span>
            </span>
          ),
          icon: React.createElement(AlignLeftOutlined),
          isLeaf: true,
          _chapterId: chapter.chapterId,
        }))
      : [];

    return {
      key: node.id,
      title: (
        <span
          className={`memory-tree-node-title memory-tree-node-title--${node.type}`}
          title={node.title}
        >
          {node.title}
        </span>
      ),
      icon: node.type === 'book_page'
        ? React.createElement(FileTextOutlined)
        : node.type === 'library'
          ? React.createElement(BookOutlined)
          : React.createElement(FolderOutlined),
      children: [...nestedNodes, ...chapterNodes].length ? [...nestedNodes, ...chapterNodes] : undefined,
      _raw: node,
    };
  });
}

const MemoryPageTree: React.FC<MemoryPageTreeProps> = ({
  loading,
  data,
  selectedKey,
  book,
  selectedChapterId,
  onSelect,
  onSelectChapter,
  emptyDescription,
}) => {
  const treeData = useMemo(() => toDataNodes(data, book), [data, book]);
  const activeKey = selectedChapterId ? `chapter:${selectedChapterId}` : selectedKey;

  if (loading) {
    return <div className="tree-empty">加载中...</div>;
  }

  if (!data.length) {
    return <div className="tree-empty">{emptyDescription ?? '暂无记忆树，请先选择 Library。'}</div>;
  }

  return (
    <Tree
      key={book?.bookId ?? 'memory-page-tree'}
      className="memory-page-tree"
      blockNode
      showIcon
      showLine={{ showLeafIcon: false }}
      virtual={false}
      defaultExpandAll
      treeData={treeData}
      selectedKeys={activeKey ? [activeKey] : []}
      onSelect={(_keys, info) => {
        const treeNode = info.node as any;
        const chapterId = treeNode._chapterId as string | undefined;
        if (chapterId) {
          onSelectChapter?.(chapterId);
          return;
        }
        const raw = treeNode._raw as MemoryLibraryTreeNodeDto | undefined;
        if (raw) onSelect(raw);
      }}
    />
  );
};

export default MemoryPageTree;
