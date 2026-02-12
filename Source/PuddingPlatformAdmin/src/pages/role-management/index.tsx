import {
  PageContainer,
  ProTable,
  ProForm,
  ProFormText,
  ProFormTextArea,
} from '@ant-design/pro-components';
import type { ProColumns, ActionType } from '@ant-design/pro-components';
import {
  Button,
  Drawer,
  Popconfirm,
  Space,
  Tag,
  Typography,
  Checkbox,
  Form,
  Divider,
  message,
} from 'antd';
import { PlusOutlined, EditOutlined, DeleteOutlined } from '@ant-design/icons';
import React, { useRef, useState } from 'react';
import {
  listRoles,
  createRole,
  updateRole,
  deleteRole,
  type AppRoleDto,
  type UpsertRoleRequest,
} from '@/services/platform/api';

const { Text } = Typography;

// 全量权限定义（后端 AppRoleEntity 中 PermissionsJson 的合法值）
const PERMISSION_GROUPS = [
  {
    group: 'Workspace',
    items: [
      { label: '查看 Workspace', value: 'workspace:read' },
      { label: '使用 Workspace（写入）', value: 'workspace:write' },
      { label: '管理 Workspace', value: 'workspace:manage' },
    ],
  },
  {
    group: 'Team',
    items: [
      { label: '查看团队', value: 'team:read' },
      { label: '管理团队', value: 'team:manage' },
    ],
  },
  {
    group: 'User',
    items: [
      { label: '查看用户', value: 'user:read' },
      { label: '管理用户', value: 'user:manage' },
    ],
  },
  {
    group: 'Agent',
    items: [
      { label: '运行 Agent', value: 'agent:run' },
      { label: '管理 Agent', value: 'agent:manage' },
    ],
  },
  {
    group: 'Template',
    items: [
      { label: '查看模板', value: 'template:read' },
      { label: '管理模板', value: 'template:manage' },
    ],
  },
  {
    group: 'LLM',
    items: [
      { label: '查看 LLM 资源', value: 'llm:read' },
      { label: '管理 LLM 资源', value: 'llm:manage' },
    ],
  },
];

export default function RoleManagementPage() {
  const actionRef = useRef<ActionType>(null);

  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editingRole, setEditingRole] = useState<AppRoleDto | null>(null);
  const [drawerForm] = Form.useForm();
  const [selectedPerms, setSelectedPerms] = useState<string[]>([]);
  const [drawerLoading, setDrawerLoading] = useState(false);

  const columns: ProColumns<AppRoleDto>[] = [
    { title: 'RoleId', dataIndex: 'roleId', width: 160, copyable: true },
    { title: '名称', dataIndex: 'name', width: 160 },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '权限',
      dataIndex: 'permissions',
      render: (_, r) =>
        r.permissions.length === 0
          ? <Text type="secondary">无</Text>
          : r.permissions.map((p) => <Tag key={p} style={{ marginBottom: 2 }}>{p}</Tag>),
    },
    {
      title: '系统内置',
      dataIndex: 'isSystemRole',
      width: 90,
      render: (_, r) => r.isSystemRole ? <Tag color="orange">内置</Tag> : <Tag>自定义</Tag>,
    },
    {
      title: '操作',
      key: 'action',
      width: 130,
      render: (_, r) => (
        <Space>
          <Button
            size="small"
            icon={<EditOutlined />}
            disabled={r.isSystemRole}
            onClick={() => openEdit(r)}
          >
            编辑
          </Button>
          <Popconfirm
            title={`确认删除角色 "${r.name}"？`}
            disabled={r.isSystemRole}
            onConfirm={() => handleDelete(r.roleId)}
            okText="删除"
            cancelText="取消"
          >
            <Button
              size="small"
              danger
              icon={<DeleteOutlined />}
              disabled={r.isSystemRole}
            />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  function openCreate() {
    setEditingRole(null);
    drawerForm.resetFields();
    setSelectedPerms([]);
    setDrawerOpen(true);
  }

  function openEdit(role: AppRoleDto) {
    setEditingRole(role);
    drawerForm.setFieldsValue({ roleId: role.roleId, name: role.name, description: role.description });
    setSelectedPerms(role.permissions);
    setDrawerOpen(true);
  }

  async function handleSave() {
    const values = await drawerForm.validateFields();
    setDrawerLoading(true);
    try {
      const req: UpsertRoleRequest = {
        roleId: values.roleId,
        name: values.name,
        description: values.description,
        permissions: selectedPerms,
      };
      if (editingRole) {
        await updateRole(editingRole.roleId, req);
        message.success('角色已更新');
      } else {
        await createRole(req);
        message.success('角色已创建');
      }
      setDrawerOpen(false);
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    } finally {
      setDrawerLoading(false);
    }
  }

  async function handleDelete(roleId: string) {
    try {
      await deleteRole(roleId);
      message.success('角色已删除');
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '删除失败');
    }
  }

  return (
    <PageContainer title="权限角色" subTitle="管理平台角色与权限配置">
      <ProTable<AppRoleDto>
        actionRef={actionRef}
        rowKey="roleId"
        columns={columns}
        request={async () => {
          const data = await listRoles();
          return { data, success: true, total: data.length };
        }}
        toolBarRender={() => [
          <Button key="create" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            新建角色
          </Button>,
        ]}
        search={false}
        pagination={{ pageSize: 20 }}
      />

      <Drawer
        title={editingRole ? `编辑角色：${editingRole.name}` : '新建角色'}
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        width={520}
        extra={
          <Space>
            <Button onClick={() => setDrawerOpen(false)}>取消</Button>
            <Button type="primary" loading={drawerLoading} onClick={handleSave}>
              保存
            </Button>
          </Space>
        }
      >
        <ProForm form={drawerForm} submitter={false} layout="vertical">
          {!editingRole && (
            <ProFormText
              name="roleId"
              label="RoleId（英文唯一标识）"
              rules={[{ required: true }, { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字和连字符' }]}
              placeholder="e.g. workspace-viewer"
            />
          )}
          <ProFormText name="name" label="角色名称" rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={2} />
        </ProForm>

        <Divider orientation="left">权限配置</Divider>
        {PERMISSION_GROUPS.map((g) => (
          <div key={g.group} style={{ marginBottom: 16 }}>
            <Text strong style={{ fontSize: 13 }}>{g.group}</Text>
            <div style={{ marginTop: 6, paddingLeft: 4 }}>
              {g.items.map((item) => (
                <Checkbox
                  key={item.value}
                  checked={selectedPerms.includes(item.value)}
                  onChange={(e) => {
                    setSelectedPerms((prev) =>
                      e.target.checked
                        ? [...prev, item.value]
                        : prev.filter((p) => p !== item.value),
                    );
                  }}
                  style={{ display: 'block', marginBottom: 4 }}
                >
                  {item.label} <Text type="secondary" style={{ fontSize: 11 }}>({item.value})</Text>
                </Checkbox>
              ))}
            </div>
          </div>
        ))}
      </Drawer>
    </PageContainer>
  );
}
