import { PageContainer } from '@ant-design/pro-components';
import { Select, Input, Button, Spin, Alert, Space, Typography } from 'antd';
import { ReloadOutlined, PlusOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import {
  listWorkspaces,
  listMemoryLibraries,
  getMemoryLibraryTree,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import type { MemoryLibraryTreeNodeDto, LibraryRecord } from './types';
import MemoryPageTree from './components/MemoryPageTree';
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
      })
      .catch(() => setError('无法加载记忆树'))
      .finally(() => setLoading(false));
  }, [selectedWorkspaceId, selectedLibraryId]);

  // ── Handlers ──────────────────────────────────────────────────
  const handleWorkspaceChange = useCallback((value: string) => {
    setSelectedWorkspaceId(value);
    setSelectedLibraryId(undefined);
    setSelectedNode(null);
    setTreeData([]);
  }, []);

  const handleLibraryChange = useCallback((value: string) => {
    setSelectedLibraryId(value);
    setSelectedNode(null);
    setTreeData([]);
  }, []);

  const handleRefresh = useCallback(() => {
    if (selectedWorkspaceId && selectedLibraryId) {
      setLoading(true);
      getMemoryLibraryTree(selectedWorkspaceId, selectedLibraryId)
        .then(setTreeData)
        .catch(() => setError('刷新失败'))
        .finally(() => setLoading(false));
    }
  }, [selectedWorkspaceId, selectedLibraryId]);

  const handleTreeSelect = useCallback((node: MemoryLibraryTreeNodeDto) => {
    setSelectedNode(node);
  }, []);

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
          <Button icon={<ReloadOutlined />} onClick={handleRefresh} disabled={!selectedLibraryId}>
            刷新
          </Button>
          <Space style={{ marginLeft: 'auto' }}>
            <Text type="secondary">
              {selectedNode ? `已选中: ${selectedNode.title}` : '未选中节点'}
            </Text>
          </Space>
        </div>

        {/* ── Content: Tree | Editor | Inspector ────────────── */}
        <div className="memory-library-content">
          {/* Left: Page Tree */}
          <div className="memory-page-tree-panel">
            {error ? (
              <Alert message={error} type="error" showIcon style={{ marginBottom: 12 }} />
            ) : null}
            <MemoryPageTree
              loading={loading}
              data={treeData}
              selectedKey={selectedNode?.id}
              onSelect={handleTreeSelect}
            />
          </div>

          {/* Center: Page Editor */}
          <div className="memory-page-editor-panel">
            {selectedNode ? (
              <div>
                <Typography.Title level={3}>{selectedNode.title}</Typography.Title>
                {selectedNode.summary && <Text type="secondary">{selectedNode.summary}</Text>}
              </div>
            ) : (
              <div className="editor-empty">
                请从左侧记忆树中选择一个节点
              </div>
            )}
          </div>

          {/* Right: Inspector */}
          <div className="memory-inspector-panel">
            <div className="inspector-empty">
              选择节点后查看属性和来源
            </div>
          </div>
        </div>
      </div>
    </PageContainer>
  );
};

export default MemoryLibraryPage;
