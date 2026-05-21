import { PageContainer } from '@ant-design/pro-components';
import { Select, Input, Button, Alert, Space, Typography, Modal, Form, Popconfirm, message } from 'antd';
import {
  ReloadOutlined,
  SearchOutlined,
  PlusOutlined,
  EditOutlined,
  DeleteOutlined,
} from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import {
  listWorkspaces,
  listWorkspaceAgents,
  listAgentMemoryLibraries,
  ensureAgentDefaultMemoryLibrary,
  getAgentMemoryLibraryTree,
  getAgentMemoryBookPage,
  searchAgentMemoryLibrary,
  createAgentMemoryTreeNode,
  createAgentMemoryBook,
  updateAgentMemoryBook,
  createAgentMemoryChapter,
  archiveAgentMemoryBook,
  archiveAgentMemoryChapter,
  listAgentMemorySources,
  listAgentMemoryPointers,
  type WorkspaceWithPermDto,
  type WorkspaceAgentDto,
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
  const [agents, setAgents] = useState<WorkspaceAgentDto[]>([]);
  const [selectedAgentId, setSelectedAgentId] = useState<string>();
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

  // Edit state
  const [newPageModalOpen, setNewPageModalOpen] = useState(false);
  const [newBookModalOpen, setNewBookModalOpen] = useState(false);
  const [editBookModalOpen, setEditBookModalOpen] = useState(false);
  const [newChapterModalOpen, setNewChapterModalOpen] = useState(false);
  const [editLoading, setEditLoading] = useState(false);
  const [newPageForm] = Form.useForm();
  const [newBookForm] = Form.useForm();
  const [editBookForm] = Form.useForm();
  const [newChapterForm] = Form.useForm();

  // Sources & pointers for Inspector
  const [sources, setSources] = useState<any[]>([]);
  const [pointers, setPointers] = useState<{ outgoing: any[]; backlinks: any[] }>({ outgoing: [], backlinks: [] });

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

  // ── Load agents when workspace changes ─────────────────────────
  useEffect(() => {
    if (!selectedWorkspaceId) return;
    setLoading(true);
    setError(null);
    listWorkspaceAgents(selectedWorkspaceId)
      .then((items) => {
        const enabledAgents = items.filter((a) => a.isEnabled && !a.isFrozen);
        setAgents(enabledAgents);
        setSelectedAgentId(enabledAgents[0]?.agentId);
      })
      .catch(() => setError('无法加载 Agent 列表'))
      .finally(() => setLoading(false));
  }, [selectedWorkspaceId]);

  // ── Load libraries when workspace/agent changes ────────────────
  useEffect(() => {
    if (!selectedWorkspaceId || !selectedAgentId) return;
    setLoading(true);
    setError(null);
    listAgentMemoryLibraries(selectedWorkspaceId, selectedAgentId)
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
  }, [selectedWorkspaceId, selectedAgentId]);

  // ── Load tree when library changes ─────────────────────────────
  useEffect(() => {
    if (!selectedWorkspaceId || !selectedAgentId || !selectedLibraryId) return;
    setLoading(true);
    setError(null);
    getAgentMemoryLibraryTree(selectedWorkspaceId, selectedAgentId, selectedLibraryId)
      .then((nodes: MemoryLibraryTreeNodeDto[]) => {
        setTreeData(nodes);
        setSelectedNode(null);
        setBookPage(null);
      })
      .catch(() => setError('无法加载记忆树'))
      .finally(() => setLoading(false));
  }, [selectedWorkspaceId, selectedAgentId, selectedLibraryId]);

  // ── Handlers ──────────────────────────────────────────────────
  const handleWorkspaceChange = useCallback((value: string) => {
    setSelectedWorkspaceId(value);
    setSelectedAgentId(undefined);
    setAgents([]);
    setLibraries([]);
    setSelectedLibraryId(undefined);
    setSelectedNode(null);
    setBookPage(null);
    setTreeData([]);
    setSearchVisible(false);
    setSearchResults([]);
    setSearchQuery('');
  }, []);

  const handleAgentChange = useCallback((value: string) => {
    setSelectedAgentId(value);
    setLibraries([]);
    setSelectedLibraryId(undefined);
    setSelectedNode(null);
    setBookPage(null);
    setTreeData([]);
    setSources([]);
    setPointers({ outgoing: [], backlinks: [] });
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
    if (selectedWorkspaceId && selectedAgentId && selectedLibraryId) {
      setLoading(true);
      getAgentMemoryLibraryTree(selectedWorkspaceId, selectedAgentId, selectedLibraryId)
        .then((nodes) => {
          setTreeData(nodes);
          setSelectedNode(null);
          setBookPage(null);
        })
        .catch(() => setError('刷新失败'))
        .finally(() => setLoading(false));
    }
  }, [selectedWorkspaceId, selectedAgentId, selectedLibraryId]);

  const handleEnsureDefaultLibrary = useCallback(async () => {
    if (!selectedWorkspaceId || !selectedAgentId) return;
    setEditLoading(true);
    try {
      const library = await ensureAgentDefaultMemoryLibrary(selectedWorkspaceId, selectedAgentId);
      setLibraries([library]);
      setSelectedLibraryId(library.libraryId);
      message.success('已创建默认记忆图书馆');
    } catch {
      message.error('创建默认图书馆失败');
    } finally {
      setEditLoading(false);
    }
  }, [selectedWorkspaceId, selectedAgentId]);

  const handleTreeSelect = useCallback((node: MemoryLibraryTreeNodeDto) => {
    setSelectedNode(node);
    setSearchVisible(false);
    setSources([]);
    setPointers({ outgoing: [], backlinks: [] });

    // If node has a mounted Book, load it
    if (node.bookId && selectedWorkspaceId && selectedAgentId) {
      setBookLoading(true);
      getAgentMemoryBookPage(selectedWorkspaceId, selectedAgentId, node.bookId)
        .then(setBookPage)
        .catch(() => setError('无法加载 Book 页'))
        .finally(() => setBookLoading(false));

      // 加载来源引用
      listAgentMemorySources(selectedWorkspaceId, selectedAgentId, 'book', node.bookId)
        .then(setSources)
        .catch(() => {});
      // 加载指针
      listAgentMemoryPointers(selectedWorkspaceId, selectedAgentId, 'book', node.bookId)
        .then(setPointers)
        .catch(() => {});
    } else {
      setBookPage(null);
      setBookLoading(false);
      if (selectedWorkspaceId && selectedAgentId) {
        // 加载 TreeNode 的来源和指针
        listAgentMemorySources(selectedWorkspaceId, selectedAgentId, 'tree_node', node.id)
          .then(setSources)
          .catch(() => {});
        listAgentMemoryPointers(selectedWorkspaceId, selectedAgentId, 'tree_node', node.id)
          .then(setPointers)
          .catch(() => {});
      }
    }
  }, [selectedWorkspaceId, selectedAgentId]);

  const handleSearch = useCallback((value: string) => {
    setSearchQuery(value);
    if (!value.trim()) {
      setSearchVisible(false);
      setSearchResults([]);
      return;
    }
    if (!selectedWorkspaceId || !selectedAgentId) return;
    setSearchVisible(true);
    searchAgentMemoryLibrary(selectedWorkspaceId, selectedAgentId, value, 20)
      .then(setSearchResults)
      .catch(() => setError('搜索失败'));
  }, [selectedWorkspaceId, selectedAgentId]);

  const handleSearchResultSelect = useCallback((bookId: string, _chapterId: string) => {
    if (!selectedWorkspaceId || !selectedAgentId) return;
    setSearchVisible(false);
    setBookLoading(true);
    getAgentMemoryBookPage(selectedWorkspaceId, selectedAgentId, bookId)
      .then((page) => {
        setBookPage(page);
        setSelectedNode(null); // clear tree selection when viewing search result
      })
      .catch(() => setError('无法加载搜索结果'))
      .finally(() => setBookLoading(false));
  }, [selectedWorkspaceId, selectedAgentId]);

  // ── Edit handlers ─────────────────────────────────────────────
  const handleCreatePage = useCallback(async (values: any) => {
    if (!selectedWorkspaceId || !selectedAgentId || !selectedLibraryId) return;
    setEditLoading(true);
    try {
      await createAgentMemoryTreeNode(selectedWorkspaceId, selectedAgentId, {
        libraryId: selectedLibraryId,
        parentNodeId: selectedNode?.id,
        name: values.name,
        summary: values.summary,
        nodeType: values.nodeType || 'category',
      });
      message.success('页面已创建');
      setNewPageModalOpen(false);
      newPageForm.resetFields();
      handleRefresh();
    } catch {
      message.error('创建失败');
    } finally {
      setEditLoading(false);
    }
  }, [selectedWorkspaceId, selectedAgentId, selectedLibraryId, selectedNode?.id, newPageForm, handleRefresh]);

  const handleCreateBook = useCallback(async (values: any) => {
    if (!selectedWorkspaceId || !selectedAgentId || !selectedLibraryId) return;
    setEditLoading(true);
    try {
      const result = await createAgentMemoryBook(selectedWorkspaceId, selectedAgentId, {
        libraryId: selectedLibraryId,
        nodeId: selectedNode?.id,
        title: values.title,
        summary: values.summary,
      });
      message.success('Book 已创建');
      setNewBookModalOpen(false);
      newBookForm.resetFields();
      if (result?.bookId && selectedWorkspaceId) {
        getAgentMemoryBookPage(selectedWorkspaceId, selectedAgentId, result.bookId).then(setBookPage).catch(() => {});
      }
    } catch {
      message.error('创建失败');
    } finally {
      setEditLoading(false);
    }
  }, [selectedWorkspaceId, selectedAgentId, selectedLibraryId, selectedNode?.id, newBookForm]);

  const handleEditBook = useCallback(async (values: any) => {
    if (!bookPage || !selectedWorkspaceId || !selectedAgentId) return;
    setEditLoading(true);
    try {
      const result = await updateAgentMemoryBook(selectedWorkspaceId, selectedAgentId, bookPage.bookId, { title: values.title, summary: values.summary });
      setBookPage(result);
      setEditBookModalOpen(false);
      message.success('已更新');
    } catch {
      message.error('更新失败');
    } finally {
      setEditLoading(false);
    }
  }, [bookPage, selectedWorkspaceId, selectedAgentId]);

  const handleCreateChapter = useCallback(async (values: any) => {
    if (!bookPage || !selectedWorkspaceId || !selectedAgentId) return;
    setEditLoading(true);
    try {
      await createAgentMemoryChapter(selectedWorkspaceId, selectedAgentId, {
        bookId: bookPage.bookId,
        title: values.title,
        content: values.content,
        importance: values.importance ?? 0.5,
      });
      message.success('章节已添加');
      setNewChapterModalOpen(false);
      newChapterForm.resetFields();
      if (selectedWorkspaceId) {
        getAgentMemoryBookPage(selectedWorkspaceId, selectedAgentId, bookPage.bookId).then(setBookPage).catch(() => {});
      }
    } catch {
      message.error('添加失败');
    } finally {
      setEditLoading(false);
    }
  }, [bookPage, selectedWorkspaceId, selectedAgentId, newChapterForm]);

  const handleArchiveBook = useCallback(async () => {
    if (!bookPage || !selectedWorkspaceId || !selectedAgentId) return;
    try {
      await archiveAgentMemoryBook(selectedWorkspaceId, selectedAgentId, bookPage.bookId);
      message.success('Book 已归档');
      setBookPage(null);
      handleRefresh();
    } catch {
      message.error('归档失败');
    }
  }, [bookPage, selectedWorkspaceId, selectedAgentId, handleRefresh]);

  const handleArchiveChapter = useCallback(async (chapterId: string) => {
    if (!selectedWorkspaceId || !selectedAgentId) return;
    try {
      await archiveAgentMemoryChapter(selectedWorkspaceId, selectedAgentId, chapterId);
      message.success('章节已归档');
      if (bookPage) {
        getAgentMemoryBookPage(selectedWorkspaceId, selectedAgentId, bookPage.bookId).then(setBookPage).catch(() => {});
      }
    } catch {
      message.error('归档失败');
    }
  }, [bookPage, selectedWorkspaceId, selectedAgentId]);

  // ── Render ────────────────────────────────────────────────────
  const workspaceOptions = workspaces.map((w) => ({
    label: w.name,
    value: w.workspaceId,
  }));

  const agentOptions = agents.map((a) => ({
    label: a.displayName || a.name,
    value: a.agentId,
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
          <Select
            style={{ minWidth: 180 }}
            placeholder="选择 Agent"
            options={agentOptions}
            value={selectedAgentId}
            onChange={handleAgentChange}
            disabled={!selectedWorkspaceId || agents.length === 0}
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
            placeholder="搜索当前 Agent 的记忆..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            onSearch={handleSearch}
            allowClear
            enterButton={<SearchOutlined />}
            disabled={!selectedWorkspaceId || !selectedAgentId}
          />
          <Button icon={<ReloadOutlined />} onClick={handleRefresh} disabled={!selectedLibraryId}>
            刷新
          </Button>
          {selectedWorkspaceId && selectedAgentId && libraries.length === 0 && (
            <Button onClick={handleEnsureDefaultLibrary} loading={editLoading}>
              创建默认图书馆
            </Button>
          )}
          <Button icon={<PlusOutlined />} onClick={() => setNewPageModalOpen(true)} disabled={!selectedLibraryId}>
            新建 Page
          </Button>
          <Button icon={<PlusOutlined />} onClick={() => setNewBookModalOpen(true)} disabled={!selectedLibraryId}>
            新建 Book
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
            {bookPage && (
              <div style={{ marginBottom: 16, display: 'flex', gap: 8 }}>
                <Button size="small" icon={<EditOutlined />} onClick={() => {
                  editBookForm.setFieldsValue({ title: bookPage.title, summary: bookPage.summary });
                  setEditBookModalOpen(true);
                }}>编辑信息</Button>
                <Button size="small" icon={<PlusOutlined />} onClick={() => setNewChapterModalOpen(true)}>添加章节</Button>
                <Popconfirm title="归档后将不可见，确认归档？" onConfirm={handleArchiveBook}>
                  <Button size="small" danger icon={<DeleteOutlined />}>归档 Book</Button>
                </Popconfirm>
              </div>
            )}
            <MemoryPageEditor
              loading={bookLoading}
              book={bookPage ?? undefined}
              nodeTitle={selectedNode?.title}
              nodeSummary={selectedNode?.summary}
              nodeType={selectedNode?.type}
              onArchiveChapter={handleArchiveChapter}
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
              sources={sources}
              pointers={pointers.outgoing.concat(pointers.backlinks).map((p: any) => ({
                pointerId: p.pointerId,
                targetType: p.targetType,
                targetId: p.targetId,
                label: p.targetLabel,
              }))}
            />
          </div>
        </div>
      </div>

      {/* ── Modals ──────────────────────────────────────────── */}
      <Modal
        title="新建 Page"
        open={newPageModalOpen}
        onCancel={() => setNewPageModalOpen(false)}
        onOk={() => newPageForm.submit()}
        confirmLoading={editLoading}
      >
        <Form form={newPageForm} layout="vertical" onFinish={handleCreatePage}>
          <Form.Item name="name" label="名称" rules={[{ required: true }]}>
            <Input placeholder="页面名称" />
          </Form.Item>
          <Form.Item name="summary" label="摘要">
            <Input.TextArea rows={3} placeholder="页面摘要" />
          </Form.Item>
          <Form.Item name="nodeType" label="类型" initialValue="category">
            <Select options={[
              { label: '分类', value: 'category' },
              { label: '主题', value: 'topic' },
              { label: '系统', value: 'system' },
              { label: '书架', value: 'shelf' },
            ]} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title="新建 Book"
        open={newBookModalOpen}
        onCancel={() => setNewBookModalOpen(false)}
        onOk={() => newBookForm.submit()}
        confirmLoading={editLoading}
      >
        <Form form={newBookForm} layout="vertical" onFinish={handleCreateBook}>
          <Form.Item name="title" label="标题" rules={[{ required: true }]}>
            <Input placeholder="Book 标题" />
          </Form.Item>
          <Form.Item name="summary" label="摘要">
            <Input.TextArea rows={3} placeholder="Book 摘要" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title="编辑 Book 信息"
        open={editBookModalOpen}
        onCancel={() => setEditBookModalOpen(false)}
        onOk={() => editBookForm.submit()}
        confirmLoading={editLoading}
      >
        <Form form={editBookForm} layout="vertical" onFinish={handleEditBook}>
          <Form.Item name="title" label="标题" rules={[{ required: true }]}>
            <Input placeholder="Book 标题" />
          </Form.Item>
          <Form.Item name="summary" label="摘要">
            <Input.TextArea rows={3} placeholder="Book 摘要" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title="添加章节"
        open={newChapterModalOpen}
        onCancel={() => setNewChapterModalOpen(false)}
        onOk={() => newChapterForm.submit()}
        confirmLoading={editLoading}
      >
        <Form form={newChapterForm} layout="vertical" onFinish={handleCreateChapter}>
          <Form.Item name="title" label="章节标题" rules={[{ required: true }]}>
            <Input placeholder="章节标题" />
          </Form.Item>
          <Form.Item name="content" label="内容" rules={[{ required: true }]}>
            <Input.TextArea rows={6} placeholder="章节内容" />
          </Form.Item>
          <Form.Item name="importance" label="重要性 (0-1)" initialValue={0.5}>
            <Input type="number" min={0} max={1} step={0.1} />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  );
};

export default MemoryLibraryPage;
