import { Tree } from 'antd';
import type { DataNode } from 'antd/es/tree';
import { FolderOutlined, FileTextOutlined, BookOutlined } from '@ant-design/icons';
import React, { useMemo } from 'react';
import type { MemoryLibraryTreeNodeDto } from '../types';

interface MemoryPageTreeProps {
  loading: boolean;
  data: MemoryLibraryTreeNodeDto[];
  selectedKey?: string;
  onSelect: (node: MemoryLibraryTreeNodeDto) => void;
}

/**
 * 将 TreeNode DTO 转换为 Ant Design Tree 的 DataNode 格式。
 * 通过 type 区分图标：library → BookOutlined, book_page → FileTextOutlined, 其他 → FolderOutlined。
 */
function toDataNodes(nodes: MemoryLibraryTreeNodeDto[]): DataNode[] {
  return nodes.map((n) => ({
    key: n.id,
    title: n.title,
    icon: n.type === 'book_page'
      ? React.createElement(FileTextOutlined)
      : n.type === 'library'
        ? React.createElement(BookOutlined)
        : React.createElement(FolderOutlined),
    children: n.children?.length ? toDataNodes(n.children) : undefined,
    // 存储原始数据以便 onSelect 回传
    _raw: n,
  }));
}

const MemoryPageTree: React.FC<MemoryPageTreeProps> = ({
  loading,
  data,
  selectedKey,
  onSelect,
}) => {
  const treeData = useMemo(() => toDataNodes(data), [data]);

  if (loading) {
    return <div className="tree-empty">加载中...</div>;
  }

  if (!data.length) {
    return <div className="tree-empty">暂无记忆树，请先选择 Library。</div>;
  }

  return (
    <Tree
      showIcon
      treeData={treeData}
      selectedKeys={selectedKey ? [selectedKey] : []}
      onSelect={(keys, info) => {
        const raw = (info.node as any)._raw as MemoryLibraryTreeNodeDto | undefined;
        if (raw) onSelect(raw);
      }}
    />
  );
};

export default MemoryPageTree;
