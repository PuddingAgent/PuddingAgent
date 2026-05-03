import {
  ApiOutlined,
  CloudServerOutlined,
  LockOutlined,
  ReloadOutlined,
  UnlockOutlined,
} from '@ant-design/icons';
import { PageContainer, ProDescriptions, ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';
import {
  App,
  Badge,
  Button,
  Descriptions,
  Drawer,
  Form,
  Input,
  Modal,
  Popconfirm,
  Space,
  Statistic,
  Table,
  Tag,
  Tooltip,
  Typography,
  theme,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import {
  freezeRuntimeNode,
  getRuntimeNodeCapabilities,
  listRuntimeNodes,
  unfreezeRuntimeNode,
  type NativeCapabilityDescriptor,
  type RuntimeNodeInfo,
  type RuntimeNodeStatus,
} from '@/services/platform/api';

const { Text } = Typography;

// ─── 状态配置 ─────────────────────────────────────────────────
const statusConfig: Record<
  RuntimeNodeStatus,
  { badge: 'success' | 'processing' | 'error' | 'warning' | 'default'; label: string }
> = {
  Online: { badge: 'success', label: '在线' },
  Offline: { badge: 'error', label: '离线' },
  Degraded: { badge: 'warning', label: '降级' },
};

const categoryLabels: Record<string, string> = {
  QueryState: '查询状态',
  RunTest: '运行测试',
  ReadResult: '读取结果',
  ExecuteCommand: '执行命令',
  Custom: '自定义',
};
const categoryColors: Record<string, string> = {
  QueryState: 'blue',
  RunTest: 'green',
  ReadResult: 'cyan',
  ExecuteCommand: 'volcano',
  Custom: 'default',
};

// ─── 工具函数 ─────────────────────────────────────────────────
/** 从 endpoint URL 中提取 host（IP 或 域名）。 */
function extractHost(endpoint: string): string {
  try {
    return new URL(endpoint).hostname;
  } catch {
    return endpoint;
  }
}

/** 将 ISO 时间转为相对描述（X 秒前 / X 分钟前 / X 小时前）。 */
function relativeTime(isoTime: string): string {
  const diffMs = Date.now() - new Date(isoTime).getTime();
  const seconds = Math.floor(diffMs / 1000);
  if (seconds < 60) return `${seconds} 秒前`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} 分钟前`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} 小时前`;
  return `${Math.floor(hours / 24)} 天前`;
}

// ─── 主组件 ──────────────────────────────────────────────────
export default function RuntimeManagementPage() {
  const { message } = App.useApp();
  const { token } = theme.useToken();
  const [nodes, setNodes] = useState<RuntimeNodeInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const pollingRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // 详情 Drawer
  const [detailOpen, setDetailOpen] = useState(false);
  const [detailNode, setDetailNode] = useState<RuntimeNodeInfo | null>(null);
  const [caps, setCaps] = useState<NativeCapabilityDescriptor[]>([]);
  const [capsLoading, setCapsLoading] = useState(false);

  // 冻结 Modal
  const [freezeOpen, setFreezeOpen] = useState(false);
  const [freezeNodeId, setFreezeNodeId] = useState('');
  const [freezeReason, setFreezeReason] = useState('');
  const [freezeLoading, setFreezeLoading] = useState(false);

  // ── 数据加载 ──────────────────────────────────────────────
  const fetchNodes = async () => {
    setLoading(true);
    try {
      const data = await listRuntimeNodes();
      setNodes(data);
    } catch {
      // silent — 节点可能暂时不可达
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void fetchNodes();
    // 每 10 秒自动刷新
    pollingRef.current = setInterval(() => void fetchNodes(), 10_000);
    return () => {
      if (pollingRef.current) clearInterval(pollingRef.current);
    };
  }, []);

  // ── 打开详情 ──────────────────────────────────────────────
  const openDetail = async (node: RuntimeNodeInfo) => {
    setDetailNode(node);
    setDetailOpen(true);
    if (node.embeddedMode) {
      setCapsLoading(true);
      try {
        const data = await getRuntimeNodeCapabilities(node.nodeId);
        setCaps(data);
      } catch {
        setCaps(node.nativeCapabilities ?? []);
      } finally {
        setCapsLoading(false);
      }
    } else {
      setCaps([]);
    }
  };

  // ── 冻结操作 ──────────────────────────────────────────────
  const openFreezeModal = (nodeId: string) => {
    setFreezeNodeId(nodeId);
    setFreezeReason('');
    setFreezeOpen(true);
  };

  const handleFreeze = async () => {
    if (!freezeReason.trim()) {
      message.warning('请填写冻结原因');
      return;
    }
    setFreezeLoading(true);
    try {
      await freezeRuntimeNode(freezeNodeId, freezeReason.trim());
      message.success(`节点 ${freezeNodeId} 已冻结`);
      setFreezeOpen(false);
      void fetchNodes();
    } catch {
      message.error('冻结操作失败');
    } finally {
      setFreezeLoading(false);
    }
  };

  const handleUnfreeze = async (nodeId: string) => {
    try {
      await unfreezeRuntimeNode(nodeId);
      message.success(`节点 ${nodeId} 已解冻`);
      void fetchNodes();
    } catch {
      message.error('解冻操作失败');
    }
  };

  // ── 统计数据 ─────────────────────────────────────────────
  const onlineCount = nodes.filter((n) => n.status === 'Online').length;
  const offlineCount = nodes.filter((n) => n.status === 'Offline').length;
  const frozenCount = nodes.filter((n) => n.isFrozen).length;
  const totalSessions = nodes.reduce((sum, n) => sum + n.activeSessionCount, 0);

  const statCardStyle = {
    background: token.colorBgContainer,
    borderRadius: token.borderRadiusLG,
    padding: '16px 24px',
    boxShadow: token.boxShadowSecondary,
    border: `1px solid ${token.colorBorderSecondary}`,
  };

  // ── 表格列 ────────────────────────────────────────────────
  const columns: ProColumns<RuntimeNodeInfo>[] = [
    {
      title: 'Node ID',
      dataIndex: 'nodeId',
      width: 140,
      render: (_, record) => (
        <Text code copyable style={{ fontSize: 12 }}>
          {record.nodeId}
        </Text>
      ),
    },
    {
      title: '端点 / IP',
      dataIndex: 'endpoint',
      width: 200,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Text style={{ fontSize: 12 }}>{record.endpoint}</Text>
          <Text type="secondary" style={{ fontSize: 11 }}>
            {extractHost(record.endpoint)}
          </Text>
        </Space>
      ),
    },
    {
      title: '连接状态',
      dataIndex: 'status',
      width: 100,
      render: (_, record) => {
        const cfg = statusConfig[record.status] ?? statusConfig.Offline;
        return (
          <Space direction="vertical" size={2}>
            <Badge status={cfg.badge} text={cfg.label} />
            {record.isFrozen && (
              <Tag icon={<LockOutlined />} color="orange" style={{ margin: 0, fontSize: 11 }}>
                已冻结
              </Tag>
            )}
          </Space>
        );
      },
    },
    {
      title: '活跃会话',
      dataIndex: 'activeSessionCount',
      width: 90,
      align: 'center',
      render: (_, record) => (
        <Tag color={record.activeSessionCount > 0 ? 'blue' : 'default'}>
          {record.activeSessionCount}
        </Tag>
      ),
    },
    {
      title: '节点模式',
      dataIndex: 'embeddedMode',
      width: 100,
      render: (_, record) =>
        record.embeddedMode ? (
          <Tag color="purple">嵌入式</Tag>
        ) : (
          <Tag color="geekblue">标准</Tag>
        ),
    },
    {
      title: '宿主类型',
      dataIndex: 'hostType',
      width: 130,
      render: (_, record) =>
        record.hostType ? (
          <Text style={{ fontSize: 12 }}>{record.hostType}</Text>
        ) : (
          <Text type="secondary">—</Text>
        ),
    },
    {
      title: '最近心跳',
      dataIndex: 'lastHeartbeat',
      width: 120,
      render: (_, record) => (
        <Tooltip title={new Date(record.lastHeartbeat).toLocaleString('zh-CN')}>
          <Text type={record.status === 'Offline' ? 'danger' : 'secondary'} style={{ fontSize: 12 }}>
            {relativeTime(record.lastHeartbeat)}
          </Text>
        </Tooltip>
      ),
    },
    {
      title: '操作',
      valueType: 'option',
      width: 160,
      render: (_, record) => [
        <a key="detail" onClick={() => openDetail(record)}>
          <ApiOutlined /> 详情
        </a>,
        record.isFrozen ? (
          <Popconfirm
            key="unfreeze"
            title="确认解冻此节点？"
            okText="解冻"
            cancelText="取消"
            onConfirm={() => handleUnfreeze(record.nodeId)}
          >
            <a style={{ color: token.colorSuccess }}>
              <UnlockOutlined /> 解冻
            </a>
          </Popconfirm>
        ) : (
          <a
            key="freeze"
            style={{ color: token.colorWarning }}
            onClick={() => openFreezeModal(record.nodeId)}
          >
            <LockOutlined /> 冻结
          </a>
        ),
      ],
    },
  ];

  // ── 能力表格列 ────────────────────────────────────────────
  const capColumns = [
    { title: '能力 ID', dataIndex: 'capabilityId', width: 180, render: (v: string) => <Text code>{v}</Text> },
    { title: '名称', dataIndex: 'name', width: 140 },
    {
      title: '分类',
      dataIndex: 'category',
      width: 110,
      render: (v: string) => (
        <Tag color={categoryColors[v] ?? 'default'}>{categoryLabels[v] ?? v}</Tag>
      ),
    },
    {
      title: '需审批',
      dataIndex: 'requiresApproval',
      width: 80,
      align: 'center' as const,
      render: (v: boolean) =>
        v ? <Tag color="red">是</Tag> : <Tag color="green">否</Tag>,
    },
    { title: '说明', dataIndex: 'description', ellipsis: true },
  ];

  return (
    <App>
      <PageContainer
        header={{
          title: 'Runtime 节点管理',
          subTitle: '查看并管理已注册到 PuddingController 的 Runtime 运行节点',
        }}
        extra={[
          <Button
            key="refresh"
            icon={<ReloadOutlined />}
            loading={loading}
            onClick={() => void fetchNodes()}
          >
            刷新
          </Button>,
        ]}
      >
        {/* 顶部统计 */}
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(4, 1fr)',
            gap: 16,
            marginBottom: 16,
          }}
        >
          <div
            style={statCardStyle}
          >
            <Statistic title="总节点数" value={nodes.length} prefix={<CloudServerOutlined />} />
          </div>
          <div
            style={statCardStyle}
          >
            <Statistic title="在线节点" value={onlineCount} valueStyle={{ color: token.colorSuccess }} />
          </div>
          <div
            style={statCardStyle}
          >
            <Statistic
              title="离线节点"
              value={offlineCount}
              valueStyle={{ color: offlineCount > 0 ? token.colorError : undefined }}
            />
          </div>
          <div
            style={statCardStyle}
          >
            <Statistic title="活跃会话总数" value={totalSessions} />
          </div>
        </div>

        {/* 节点表格 */}
        <ProTable<RuntimeNodeInfo>
          rowKey="nodeId"
          columns={columns}
          dataSource={nodes}
          loading={loading}
          search={false}
          pagination={false}
          options={{ reload: () => void fetchNodes(), density: false, fullScreen: false }}
          toolbar={{
            title: (
              <Space>
                <span>节点列表</span>
                {frozenCount > 0 && (
                  <Tag color="orange" icon={<LockOutlined />}>
                    {frozenCount} 个已冻结
                  </Tag>
                )}
              </Space>
            ),
          }}
        />
      </PageContainer>

      {/* 节点详情 Drawer */}
      <Drawer
        title={
          <Space>
            <CloudServerOutlined />
            节点详情
            {detailNode && (
              <Text code style={{ fontSize: 12 }}>
                {detailNode.nodeId}
              </Text>
            )}
          </Space>
        }
        open={detailOpen}
        width={640}
        onClose={() => setDetailOpen(false)}
      >
        {detailNode && (
          <>
            <Descriptions bordered column={1} size="small" style={{ marginBottom: 24 }}>
              <Descriptions.Item label="Node ID">
                <Text code copyable>{detailNode.nodeId}</Text>
              </Descriptions.Item>
              <Descriptions.Item label="端点">
                <Text>{detailNode.endpoint}</Text>
              </Descriptions.Item>
              <Descriptions.Item label="IP / 主机名">
                <Text>{extractHost(detailNode.endpoint)}</Text>
              </Descriptions.Item>
              <Descriptions.Item label="连接状态">
                <Badge
                  status={statusConfig[detailNode.status]?.badge ?? 'default'}
                  text={statusConfig[detailNode.status]?.label}
                />
              </Descriptions.Item>
              <Descriptions.Item label="冻结状态">
                {detailNode.isFrozen ? (
                  <Tag color="orange" icon={<LockOutlined />}>已冻结</Tag>
                ) : (
                  <Tag color="success">正常</Tag>
                )}
              </Descriptions.Item>
              <Descriptions.Item label="节点模式">
                {detailNode.embeddedMode ? (
                  <Tag color="purple">嵌入式宿主节点</Tag>
                ) : (
                  <Tag color="geekblue">标准 Runtime</Tag>
                )}
              </Descriptions.Item>
              {detailNode.hostType && (
                <Descriptions.Item label="宿主类型">
                  <Text>{detailNode.hostType}</Text>
                </Descriptions.Item>
              )}
              <Descriptions.Item label="活跃会话">
                <Tag color="blue">{detailNode.activeSessionCount}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="最近心跳">
                <Tooltip title={new Date(detailNode.lastHeartbeat).toLocaleString('zh-CN')}>
                  <Text>{relativeTime(detailNode.lastHeartbeat)}</Text>
                </Tooltip>
              </Descriptions.Item>
            </Descriptions>

            {/* 嵌入式节点原生能力列表 */}
            {detailNode.embeddedMode && (
              <>
                <Typography.Title level={5} style={{ marginTop: 0 }}>
                  原生能力列表
                </Typography.Title>
                <Table
                  rowKey="capabilityId"
                  columns={capColumns}
                  dataSource={caps}
                  loading={capsLoading}
                  size="small"
                  pagination={false}
                  locale={{ emptyText: '该节点暂无原生能力' }}
                />
              </>
            )}
          </>
        )}
      </Drawer>

      {/* 冻结原因 Modal */}
      <Modal
        title={
          <Space>
            <LockOutlined style={{ color: token.colorWarning }} />
            冻结节点
          </Space>
        }
        open={freezeOpen}
        okText="确认冻结"
        okButtonProps={{ danger: true, loading: freezeLoading }}
        cancelText="取消"
        onOk={handleFreeze}
        onCancel={() => setFreezeOpen(false)}
      >
        <div style={{ marginBottom: 8 }}>
          冻结后，该嵌入式节点将拒绝所有原生能力调用。
        </div>
        <Input.TextArea
          rows={3}
          placeholder="请填写冻结原因（必填）"
          value={freezeReason}
          onChange={(e) => setFreezeReason(e.target.value)}
        />
      </Modal>
    </App>
  );
}
