import { PageContainer } from '@ant-design/pro-components';
import { Select, Input, Button, Alert, Space, Typography } from 'antd';
import { ReloadOutlined, SearchOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import {
  listWorkspaces,
  listMemoryLibraries,
  getMemoryLibraryTree,
  getMemoryBookPage,
  searchMemoryLibrary,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import type {
  MemoryLibraryTreeNodeDto,
  LibraryRecord,
  MemoryBookPageDto,
  MemorySearchResultDto,
} from './types';
import MemoryPageTree from './components/MemoryPageTree';
import MemoryPageEditor from './components/MemoryPageEditor';
import MemoryInspector from './components/MemoryInspector';
import MemorySearchResults from './components/MemorySearchResults';
import './styles.less';

const { Text } = Typography;

const MemoryLibraryPage: React.FC = () => {
  // ── State ──────────────────────────────────────────────────────
  const [workspaces, setWorkspaces] = useState<WorkspaceWithPermDto[]>([]);
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<string>();
  const [libraries, setLibraries] = useState<LibraryRecord[]>([]);
  const [selectedLibraryId, setSelectedLibraryId] = useState<string>();
  const [treeData, setTreeData] = useState<MemoryLibraryTreeNodeDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedNode, setSelectedNode] = useState<MemoryLibraryTreeNodeDto | null>(null);
  const [bookPage, setBookPage] = useState<MemoryBookPageDto | null>(null);
  const [bookLoading, setBookLoading] = useState(false);

  // Search state
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<MemorySearchResultDto[]>([]);
  const [searchVisible, setSearchVisible] = useState(false);

  // ── Load workspaces on mount ───────────────────────────────────
  useEffect(() => {
    listWorkspaces()
      .then((ws) => {
        setWorkspaces(ws);
        if (ws.length > 0) {
          const defaultWs = ws.find((w) => w.workspaceId === 'default') ?? ws[0];
          setSelectedWorkspaceId(defaultWs.workspaceId);
        }
      })
      .catch(() => setError('无法加载工作区列表'));
  }, []);

  // ── Load libraries when workspace changes ──────────────────────
  useEffect(() => {
    if (!selectedWorkspaceId) return;
    setLoading(true);
    setError(null);
    listMemoryLibraries(selectedWorkspaceId)
      .then((libs: LibraryRecord[]) => {
        setLibraries(libs);
        if (libs.length > 0) {
          setSelectedLibraryId(libs[0].libraryId);
        } else {
          setSelectedLibraryId(undefined);
          setTreeData([]);
          setSelectedNode(null);
        }
      })
      .catch(() => setError('无法加载图书馆列表'))
      .finally(() => setLoading(false));
  }, [selectedWorkspaceId]);

  // ── Load tree when library changes ─────────────────────────────
  useEffect(() => {
    if (!selectedWorkspaceId || !selectedLibraryId) return;
    setLoading(true);
    setError(null);
    getMemoryLibraryTree(selectedWorkspaceId, selectedLibraryId)
      .then((nodes: MemoryLibraryTreeNodeDto[]) => {
        setTreeData(nodes);
        setSelectedNode(null);
        setBookPage(null);
      })
      .catch(() => setError('无法加载记忆树'))
      .finally(() => setLoading(false));
  }, [selectedWorkspaceId, selectedLibraryId]);

  // ── Handlers ──────────────────────────────────────────────────
  const handleWorkspaceChange = useCallback((value: string) => {
    setSelectedWorkspaceId(value);
    setSelectedLibraryId(undefined);
    setSelectedNode(null);
    setBookPage(null);
    setTreeData([]);
    setSearchVisible(false);
    setSearchResults([]);
    setSearchQuery('');
  }, []);

  const handleLibraryChange = useCallback((value: string) => {
    setSelectedLibraryId(value);
    setSelectedNode(null);
    setBookPage(null);
    setTreeData([]);
  }, []);

  const handleRefresh = useCallback(() => {
    if (selectedWorkspaceId && selectedLibraryId) {
      setLoading(true);
      getMemoryLibraryTree(selectedWorkspaceId, selectedLibraryId)
        .then((nodes) => {
          setTreeData(nodes);
          setSelectedNode(null);
          setBookPage(null);
        })
        .catch(() => setError('刷新失败'))
        .finally(() => setLoading(false));
    }
  }, [selectedWorkspaceId, selectedLibraryId]);

  const handleTreeSelect = useCallback((node: MemoryLibraryTreeNodeDto) => {
    setSelectedNode(node);
    setSearchVisible(false);

    // If node has a mounted Book, load it
    if (node.bookId && selectedWorkspaceId) {
      setBookLoading(true);
      getMemoryBookPage(selectedWorkspaceId, node.bookId)
        .then(setBookPage)
        .catch(() => setError('无法加载 Book 页'))
        .finally(() => setBookLoading(false));
    } else {
      setBookPage(null);
      setBookLoading(false);
    }
  }, [selectedWorkspaceId]);

  const handleSearch = useCallback((value: string) => {
    setSearchQuery(value);
    if (!value.trim()) {
      setSearchVisible(false);
      setSearchResults([]);
      return;
    }
    if (!selectedWorkspaceId) return;
    setSearchVisible(true);
    searchMemoryLibrary(selectedWorkspaceId, value, 20)
      .then(setSearchResults)
      .catch(() => setError('搜索失败'));
  }, [selectedWorkspaceId]);

  const handleSearchResultSelect = useCallback((bookId: string, _chapterId: string) => {
    if (!selectedWorkspaceId) return;
    setSearchVisible(false);
    setBookLoading(true);
    getMemoryBookPage(selectedWorkspaceId, bookId)
      .then((page) => {
        setBookPage(page);
        setSelectedNode(null); // clear tree selection when viewing search result
      })
      .catch(() => setError('无法加载搜索结果'))
      .finally(() => setBookLoading(false));
  }, [selectedWorkspaceId]);

  // ── Render ────────────────────────────────────────────────────
  const workspaceOptions = workspaces.map((w) => ({
    label: w.name,
    value: w.workspaceId,
  }));

  return (
    <PageContainer>
      <div className="memory-library-page">
        {/* ── Toolbar ──────────────────────────────────────── */}
        <div className="memory-library-toolbar">
          <Select
            className="workspace-select"
            placeholder="选择工作区"
            options={workspaceOptions}
            value={selectedWorkspaceId}
            onChange={handleWorkspaceChange}
          />
          {libraries.length > 1 && (
            <Select
              style={{ minWidth: 160 }}
              placeholder="选择图书馆"
              value={selectedLibraryId}
              onChange={handleLibraryChange}
              options={libraries.map((l) => ({ label: l.name, value: l.libraryId }))}
            />
          )}
          <Input.Search
            className="search-input"
            placeholder="搜索当前工作区记忆..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            onSearch={handleSearch}
            allowClear
            enterButton={<SearchOutlined />}
          />
          <Button icon={<ReloadOutlined />} onClick={handleRefresh} disabled={!selectedLibraryId}>
            刷新
          </Button>
          <Space style={{ marginLeft: 'auto' }}>
            <Text type="secondary">
              {selectedNode ? `已选中: ${selectedNode.title}` : bookPage ? `浏览: ${bookPage.title}` : '未选中节点'}
            </Text>
          </Space>
        </div>

        {/* ── Content: Tree | Editor | Inspector ────────────── */}
        <div className="memory-library-content">
          {/* Left: Page Tree */}
          <div className="memory-page-tree-panel">
            {error ? (
              <Alert message={error} type="error" showIcon closable onClose={() => setError(null)} style={{ marginBottom: 12 }} />
            ) : null}
            {searchVisible ? (
              <MemorySearchResults results={searchResults} onSelect={handleSearchResultSelect} />
            ) : (
              <MemoryPageTree
                loading={loading}
                data={treeData}
                selectedKey={selectedNode?.id}
                onSelect={handleTreeSelect}
              />
            )}
          </div>

          {/* Center: Page Editor */}
          <div className="memory-page-editor-panel">
            <MemoryPageEditor
              loading={bookLoading}
              book={bookPage ?? undefined}
              nodeTitle={selectedNode?.title}
              nodeSummary={selectedNode?.summary}
              nodeType={selectedNode?.type}
            />
          </div>

          {/* Right: Inspector */}
          <div className="memory-inspector-panel">
            <MemoryInspector
              loading={bookLoading}
              book={bookPage ?? undefined}
              nodeTitle={selectedNode?.title}
              nodeType={selectedNode?.type}
              nodeId={selectedNode?.id ?? bookPage?.bookId}
            />
          </div>
        </div>
      </div>
    </PageContainer>
  );
};

export default MemoryLibraryPage;
