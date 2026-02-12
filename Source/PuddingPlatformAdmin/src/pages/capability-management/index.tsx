import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import { ApiOutlined, AppstoreOutlined, ReloadOutlined, TableOutlined, UploadOutlined } from '@ant-design/icons';
import {
  Alert,
  Badge,
  Button,
  Card,
  Col,
  Empty,
  Radio,
  Row,
  Space,
  Spin,
  Statistic,
  Tabs,
  Tag,
  Typography,
  Upload,
  message,
} from 'antd';
import type { UploadFile } from 'antd';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  listCapabilities,
  listPluginDiagnostics,
  listPlugins,
  reloadPlugin,
  reloadPluginCatalog,
  uploadPluginPackage,
  type CapabilityDto,
  type PluginCatalogItemDto,
  type PluginDiagnosticEventDto,
  type PluginToolItemDto,
} from '@/services/platform/api';
import {
  buildPluginCatalogSummary,
  getPluginDiagnosticStatusBadge,
  getPluginStatusBadge,
  getPluginToolStatusBadge,
} from './pluginCatalogViewModel';

const { Text } = Typography;

type ViewMode = 'card' | 'table';
type ActiveTab = 'tools' | 'plugins';

const getCategoryColor = (toolName: string): string => {
  const lower = toolName.toLowerCase();
  if (lower.includes('memory') || lower.includes('memo') || lower.includes('recall')) return '#7c3aed';
  if (lower.includes('query') || lower.includes('search') || lower.includes('find') || lower.includes('list')) return '#3b82f6';
  if (lower.includes('execute') || lower.includes('run') || lower.includes('shell') || lower.includes('terminal')) return '#f97316';
  if (lower.includes('security') || lower.includes('auth') || lower.includes('vault') || lower.includes('key')) return '#ef4444';
  if (lower.includes('local') || lower.includes('file') || lower.includes('read') || lower.includes('write') || lower.includes('patch')) return '#22c55e';
  return '#22d3ee';
};

const getCategoryLabel = (toolName: string): string => {
  const lower = toolName.toLowerCase();
  if (lower.includes('memory') || lower.includes('memo') || lower.includes('recall')) return 'Memory';
  if (lower.includes('query') || lower.includes('search') || lower.includes('find') || lower.includes('list')) return 'Query';
  if (lower.includes('execute') || lower.includes('run') || lower.includes('shell') || lower.includes('terminal')) return 'Execute';
  if (lower.includes('security') || lower.includes('auth') || lower.includes('vault') || lower.includes('key')) return 'Security';
  if (lower.includes('local') || lower.includes('file') || lower.includes('read') || lower.includes('write') || lower.includes('patch')) return 'Local';
  return 'Tool';
};

const renderPermissionTags = (item: CapabilityDto) => (
  <Space size={4} wrap>
    {item.requiresShellExecution && <Tag color="volcano">Shell</Tag>}
    {item.requiresFileWrite && <Tag color="blue">FileWrite</Tag>}
    {item.requiresNetworkAccess && <Tag color="purple">Network</Tag>}
    {!item.requiresShellExecution && !item.requiresFileWrite && !item.requiresNetworkAccess && (
      <Text type="secondary">低风险</Text>
    )}
  </Space>
);

const renderSourceTags = (item: CapabilityDto) => (
  <Space size={4} wrap>
    <Tag color={item.sourceKind === 'Plugin' ? 'geekblue' : 'default'}>
      {item.sourceKind || 'BuiltIn'}
    </Tag>
    {item.sourceId && <Tag>{item.sourceId}</Tag>}
    <Tag color={item.runtimeStatus === 'Available' ? 'success' : 'warning'}>
      {item.runtimeStatus || 'Available'}
    </Tag>
  </Space>
);

const renderPluginStatus = (status?: string) => {
  const badge = getPluginStatusBadge(status);
  return <Badge status={badge.status} text={badge.label} />;
};

const renderPluginToolStatus = (status?: string) => {
  const badge = getPluginToolStatusBadge(status);
  return <Badge status={badge.status} text={badge.label} />;
};

const renderPluginDiagnosticStatus = (status?: string) => {
  const badge = getPluginDiagnosticStatusBadge(status);
  return <Badge status={badge.status} text={badge.label} />;
};

const formatDiagnosticTime = (value: string) => {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
};

const renderPluginTool = (tool: PluginToolItemDto) => (
  <div
    key={tool.toolId}
    style={{
      border: '1px solid #f0f0f0',
      borderRadius: 8,
      padding: 12,
      background: '#fff',
    }}
  >
    <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, marginBottom: 6 }}>
      <Space size={6} wrap>
        <Text strong>{tool.name}</Text>
        <Text code>{tool.toolId}</Text>
      </Space>
      {renderPluginToolStatus(tool.runtimeStatus)}
    </div>
    {tool.description && (
      <Text type="secondary" style={{ display: 'block', fontSize: 12, marginBottom: 8 }}>
        {tool.description}
      </Text>
    )}
    <Space size={4} wrap>
      <Tag>{tool.category}</Tag>
      <Tag>{tool.permissionLevel}</Tag>
      <Tag>{tool.safety}</Tag>
      <Tag color={tool.isEnabledByDefault ? 'green' : 'default'}>
        {tool.isEnabledByDefault ? '默认启用' : '默认关闭'}
      </Tag>
    </Space>
  </div>
);

const renderPluginDiagnosticEvent = (event: PluginDiagnosticEventDto) => {
  const detailEntries = Object.entries(event.details || {}).slice(0, 4);
  return (
    <div
      key={event.eventId}
      style={{
        border: '1px solid #f0f0f0',
        borderRadius: 8,
        padding: 12,
        background: '#fff',
      }}
    >
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, marginBottom: 6 }}>
        <Space size={6} wrap>
          <Text code>{event.eventType}</Text>
          {event.pluginId && <Tag>{event.pluginId}</Tag>}
          {event.pluginVersion && <Tag>v{event.pluginVersion}</Tag>}
          {typeof event.durationMs === 'number' && <Tag>{event.durationMs} ms</Tag>}
        </Space>
        {renderPluginDiagnosticStatus(event.status)}
      </div>
      <Text type="secondary" style={{ display: 'block', fontSize: 12, marginBottom: 6 }}>
        {formatDiagnosticTime(event.occurredAtUtc)}
      </Text>
      {event.message && (
        <Text style={{ display: 'block', fontSize: 13, marginBottom: detailEntries.length ? 8 : 0 }}>
          {event.message}
        </Text>
      )}
      {detailEntries.length > 0 && (
        <Space size={4} wrap>
          {detailEntries.map(([key, value]) => (
            <Tag key={key}>
              {key}: {value}
            </Tag>
          ))}
        </Space>
      )}
    </div>
  );
};

const ToolManagementPage: React.FC = () => {
  const tableRef = useRef<ActionType | undefined>(undefined);
  const [activeTab, setActiveTab] = useState<ActiveTab>('tools');
  const [viewMode, setViewMode] = useState<ViewMode>('card');
  const [tools, setTools] = useState<CapabilityDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | undefined>();
  const [plugins, setPlugins] = useState<PluginCatalogItemDto[]>([]);
  const [pluginLoading, setPluginLoading] = useState(false);
  const [pluginLoadError, setPluginLoadError] = useState<string | undefined>();
  const [reloadingPluginId, setReloadingPluginId] = useState<string | undefined>();
  const [pluginPackageFiles, setPluginPackageFiles] = useState<UploadFile[]>([]);
  const [uploadingPluginPackage, setUploadingPluginPackage] = useState(false);
  const [pluginDiagnostics, setPluginDiagnostics] = useState<PluginDiagnosticEventDto[]>([]);
  const [pluginDiagnosticsLoading, setPluginDiagnosticsLoading] = useState(false);
  const [pluginDiagnosticsError, setPluginDiagnosticsError] = useState<string | undefined>();

  const reloadTools = useCallback(async () => {
    setLoading(true);
    setLoadError(undefined);
    try {
      setTools(await listCapabilities());
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : '工具注册表加载失败');
    } finally {
      setLoading(false);
    }
  }, []);

  const reloadPlugins = useCallback(async () => {
    setPluginLoading(true);
    setPluginLoadError(undefined);
    try {
      setPlugins(await listPlugins());
    } catch (error) {
      setPluginLoadError(error instanceof Error ? error.message : '插件目录加载失败');
    } finally {
      setPluginLoading(false);
    }
  }, []);

  const reloadPluginDiagnostics = useCallback(async () => {
    setPluginDiagnosticsLoading(true);
    setPluginDiagnosticsError(undefined);
    try {
      setPluginDiagnostics(await listPluginDiagnostics({ limit: 50 }));
    } catch (error) {
      setPluginDiagnosticsError(error instanceof Error ? error.message : '插件诊断加载失败');
    } finally {
      setPluginDiagnosticsLoading(false);
    }
  }, []);

  const handleReloadPlugin = useCallback(async (pluginId: string) => {
    setReloadingPluginId(pluginId);
    try {
      const result = await reloadPlugin(pluginId);
      if (result.requiresRestart) {
        message.warning(result.message);
      } else {
        message.success(result.message);
      }
      await reloadPlugins();
      await reloadTools();
      await reloadPluginDiagnostics();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '插件刷新失败');
    } finally {
      setReloadingPluginId(undefined);
    }
  }, [reloadPluginDiagnostics, reloadPlugins, reloadTools]);

  const handleReloadPluginCatalog = useCallback(async () => {
    setPluginLoading(true);
    setPluginLoadError(undefined);
    try {
      const result = await reloadPluginCatalog();
      message.success(result.message);
      await reloadPlugins();
      await reloadTools();
      await reloadPluginDiagnostics();
    } catch (error) {
      setPluginLoadError(error instanceof Error ? error.message : '插件目录刷新失败');
    } finally {
      setPluginLoading(false);
    }
  }, [reloadPluginDiagnostics, reloadPlugins, reloadTools]);

  const handleUploadPluginPackage = useCallback(async () => {
    const uploadFile = pluginPackageFiles[0];
    const file = uploadFile?.originFileObj as File | undefined;
    if (!file) {
      message.error('请选择插件 ZIP 包');
      return;
    }

    setUploadingPluginPackage(true);
    try {
      const formData = new FormData();
      formData.append('file', file);
      const result = await uploadPluginPackage(formData);
      if (result.requiresRestart) {
        message.warning(result.message);
      } else {
        message.success(result.message);
      }
      setPluginPackageFiles([]);
      await reloadPlugins();
      await reloadTools();
      await reloadPluginDiagnostics();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '插件包上传失败');
    } finally {
      setUploadingPluginPackage(false);
    }
  }, [pluginPackageFiles, reloadPluginDiagnostics, reloadPlugins, reloadTools]);

  useEffect(() => {
    reloadTools();
    reloadPlugins();
    reloadPluginDiagnostics();
  }, [reloadPluginDiagnostics, reloadPlugins, reloadTools]);

  const pluginSummary = useMemo(() => buildPluginCatalogSummary(plugins), [plugins]);

  const columns: ProColumns<CapabilityDto>[] = [
    {
      title: '工具 ID',
      dataIndex: 'toolName',
      width: 180,
      copyable: true,
      render: (_, r) => <Text code>{r.toolName}</Text>,
    },
    {
      title: '能力 ID',
      dataIndex: 'capabilityId',
      width: 180,
      copyable: true,
    },
    {
      title: '名称',
      dataIndex: 'name',
      width: 180,
    },
    {
      title: '权限需求',
      width: 220,
      render: (_, r) => renderPermissionTags(r),
    },
    {
      title: '来源',
      width: 240,
      render: (_, r) => renderSourceTags(r),
    },
    {
      title: '描述',
      ellipsis: true,
      render: (_, r) => <Text type="secondary">{r.description || '—'}</Text>,
    },
    {
      title: '注册状态',
      width: 100,
      render: (_, r) => (
        <Badge
          status={r.runtimeStatus === 'Available' ? 'success' : 'warning'}
          text={r.runtimeStatus === 'Available' ? '已注册' : r.runtimeStatus}
        />
      ),
    },
  ];

  const toolbar = [
    <Button key="reload" size="small" icon={<ReloadOutlined />} onClick={reloadTools}>
      刷新
    </Button>,
    <Radio.Group
      key="viewToggle"
      value={viewMode}
      onChange={(e) => setViewMode(e.target.value)}
      optionType="button"
      buttonStyle="solid"
      size="small"
    >
      <Radio.Button value="card">
        <AppstoreOutlined /> 卡片
      </Radio.Button>
      <Radio.Button value="table">
        <TableOutlined /> 表格
      </Radio.Button>
    </Radio.Group>,
  ];

  const pluginToolbar = [
    <Upload
      key="pluginUpload"
      accept=".zip"
      maxCount={1}
      fileList={pluginPackageFiles}
      beforeUpload={(file) => {
        if (!file.name.toLowerCase().endsWith('.zip')) {
          message.error('插件包必须是 .zip 文件');
          return Upload.LIST_IGNORE;
        }
        setPluginPackageFiles([{ ...file, originFileObj: file } as UploadFile]);
        return false;
      }}
      onRemove={() => setPluginPackageFiles([])}
    >
      <Button size="small" icon={<UploadOutlined />}>选择 ZIP</Button>
    </Upload>,
    <Button
      key="uploadPluginPackage"
      size="small"
      type="primary"
      icon={<UploadOutlined />}
      loading={uploadingPluginPackage}
      disabled={pluginPackageFiles.length === 0}
      onClick={handleUploadPluginPackage}
    >
      上传并校验
    </Button>,
    <Button key="reloadPlugins" size="small" icon={<ReloadOutlined />} onClick={handleReloadPluginCatalog}>
      刷新插件目录
    </Button>,
    <Button key="reloadPluginDiagnostics" size="small" icon={<ReloadOutlined />} onClick={reloadPluginDiagnostics}>
      刷新诊断
    </Button>,
  ];

  const toolRegistryContent = (
    <>
      {loadError && (
        <Alert
          type="error"
          showIcon
          message="工具注册表加载失败"
          description={loadError}
          style={{ marginBottom: 16 }}
        />
      )}
      {viewMode === 'card' ? (
        <div>
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              marginBottom: 16,
              gap: 12,
            }}
          >
            <Text strong>工具注册表</Text>
            <Space>{toolbar}</Space>
          </div>
          <Spin spinning={loading}>
            {tools.length === 0 && !loading ? (
              <Empty description="暂无注册工具" />
            ) : (
              <Row gutter={[16, 16]}>
                {tools.map((item) => {
                  const catColor = getCategoryColor(item.toolName);
                  const catLabel = getCategoryLabel(item.toolName);
                  return (
                    <Col xs={24} sm={12} lg={8} xl={6} key={item.toolName}>
                      <Card
                        hoverable
                        size="small"
                        style={{
                          borderRadius: 8,
                          borderLeft: `4px solid ${catColor}`,
                          background: 'rgba(250,250,247,0.78)',
                          boxShadow: '0 2px 16px rgba(0,0,0,0.04)',
                        }}
                        styles={{ body: { padding: '16px' } }}
                      >
                        <div
                          style={{
                            display: 'flex',
                            justifyContent: 'space-between',
                            alignItems: 'flex-start',
                            marginBottom: 8,
                            gap: 8,
                          }}
                        >
                          <Space align="start">
                            <ApiOutlined style={{ color: catColor, fontSize: 18, marginTop: 2 }} />
                            <div>
                              <Text strong style={{ fontSize: 15, display: 'block' }}>
                                {item.name}
                              </Text>
                              <Text code style={{ fontSize: 11 }}>
                                {item.toolName}
                              </Text>
                            </div>
                          </Space>
                          <Tag color={catColor} style={{ fontSize: 11, marginInlineEnd: 0 }}>
                            {catLabel}
                          </Tag>
                        </div>

                        {item.description && (
                          <Text type="secondary" style={{ fontSize: 12, display: 'block', marginBottom: 8 }}>
                            {item.description.length > 56 ? `${item.description.slice(0, 56)}…` : item.description}
                          </Text>
                        )}

                        <Space size={4} wrap style={{ marginBottom: 8 }}>
                          {renderPermissionTags(item)}
                        </Space>

                        <div style={{ marginBottom: 8 }}>
                          {renderSourceTags(item)}
                        </div>

                        <div style={{ borderTop: '1px solid #f0f0f0', paddingTop: 10 }}>
                          <Badge
                            status={item.runtimeStatus === 'Available' ? 'success' : 'warning'}
                            text={item.runtimeStatus === 'Available' ? '已注册' : item.runtimeStatus}
                          />
                        </div>
                      </Card>
                    </Col>
                  );
                })}
              </Row>
            )}
          </Spin>
        </div>
      ) : (
        <ProTable<CapabilityDto>
          actionRef={tableRef}
          rowKey="toolName"
          columns={columns}
          dataSource={tools}
          loading={loading}
          search={false}
          toolBarRender={() => toolbar}
        />
      )}
    </>
  );

  const pluginCatalogContent = (
    <div>
      {pluginLoadError && (
        <Alert
          type="error"
          showIcon
          message="插件目录加载失败"
          description={pluginLoadError}
          style={{ marginBottom: 16 }}
        />
      )}
      <Alert
        type="info"
        showIcon
        message="Phase 1 只读取插件描述文件"
        description="上传 ZIP 后，后端会先校验 plugin.json、包结构和解压路径，再安装到插件目录；manifest-only 工具会立即进入运行时目录。当前阶段不会动态加载 DLL，真正执行需要后续 Phase 2 的隔离加载器和权限校验。"
        style={{ marginBottom: 16 }}
      />
      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        <Col xs={12} md={6}>
          <Card size="small">
            <Statistic title="插件包" value={pluginSummary.totalPlugins} />
          </Card>
        </Col>
        <Col xs={12} md={6}>
          <Card size="small">
            <Statistic title="工具声明" value={pluginSummary.totalTools} />
          </Card>
        </Col>
        <Col xs={12} md={6}>
          <Card size="small">
            <Statistic title="ManifestOnly" value={pluginSummary.manifestOnlyPlugins} />
          </Card>
        </Col>
        <Col xs={12} md={6}>
          <Card size="small">
            <Statistic title="无效插件" value={pluginSummary.invalidPlugins} />
          </Card>
        </Col>
      </Row>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          marginBottom: 16,
          gap: 12,
        }}
      >
        <Text strong>插件包目录</Text>
        <Space>{pluginToolbar}</Space>
      </div>
      <Spin spinning={pluginLoading}>
        {plugins.length === 0 && !pluginLoading ? (
          <Empty description="暂无插件包。可上传包含 plugin.json 的 ZIP 包，或将插件目录放入 D:\\data\\plugins 后重启后端。" />
        ) : (
          <Row gutter={[16, 16]}>
            {plugins.map((plugin) => (
              <Col xs={24} lg={12} key={plugin.pluginId}>
                <Card
                  size="small"
                  style={{
                    borderRadius: 8,
                    borderLeft: plugin.status === 'ManifestInvalid' ? '4px solid #ef4444' : '4px solid #6366f1',
                    background: 'rgba(250,250,247,0.78)',
                    boxShadow: '0 2px 16px rgba(0,0,0,0.04)',
                  }}
                  title={
                    <Space size={8} wrap>
                      <Text strong>{plugin.name}</Text>
                      <Text code>{plugin.pluginId}</Text>
                    </Space>
                  }
                  extra={
                    <Button
                      size="small"
                      icon={<ReloadOutlined />}
                      loading={reloadingPluginId === plugin.pluginId}
                      onClick={() => handleReloadPlugin(plugin.pluginId)}
                    >
                      Reload
                    </Button>
                  }
                >
                  <Space size={8} wrap style={{ marginBottom: 12 }}>
                    <Tag>v{plugin.version}</Tag>
                    {renderPluginStatus(plugin.status)}
                    <Tag>{plugin.toolCount} tools</Tag>
                  </Space>
                  {plugin.statusReason && (
                    <Alert
                      type={plugin.status === 'ManifestInvalid' ? 'error' : 'warning'}
                      showIcon
                      message={plugin.statusReason}
                      style={{ marginBottom: 12 }}
                    />
                  )}
                  {plugin.tools.length === 0 ? (
                    <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="没有声明工具" />
                  ) : (
                    <Space direction="vertical" size={8} style={{ width: '100%' }}>
                      {plugin.tools.map(renderPluginTool)}
                    </Space>
                  )}
                </Card>
              </Col>
            ))}
          </Row>
        )}
      </Spin>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          marginTop: 20,
          marginBottom: 12,
          gap: 12,
        }}
      >
        <Text strong>最近插件诊断</Text>
        <Button size="small" icon={<ReloadOutlined />} onClick={reloadPluginDiagnostics}>
          刷新
        </Button>
      </div>
      {pluginDiagnosticsError && (
        <Alert
          type="error"
          showIcon
          message="插件诊断加载失败"
          description={pluginDiagnosticsError}
          style={{ marginBottom: 12 }}
        />
      )}
      <Spin spinning={pluginDiagnosticsLoading}>
        {pluginDiagnostics.length === 0 && !pluginDiagnosticsLoading ? (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无插件诊断事件" />
        ) : (
          <Space direction="vertical" size={8} style={{ width: '100%' }}>
            {pluginDiagnostics.map(renderPluginDiagnosticEvent)}
          </Space>
        )}
      </Spin>
    </div>
  );

  return (
    <PageContainer
      title="工具管理"
      subTitle="运行时工具注册表与插件包 manifest 状态"
    >
      <Tabs
        activeKey={activeTab}
        onChange={(key) => setActiveTab(key as ActiveTab)}
        items={[
          {
            key: 'tools',
            label: '工具注册表',
            children: toolRegistryContent,
          },
          {
            key: 'plugins',
            label: '插件包',
            children: pluginCatalogContent,
          },
        ]}
      />
    </PageContainer>
  );
};

export default ToolManagementPage;
