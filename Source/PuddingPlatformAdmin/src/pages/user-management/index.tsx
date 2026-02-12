import {
  PageContainer,
  ProTable,
  ProForm,
  ProFormText,
  ProFormSelect,
  ProFormSwitch,
} from '@ant-design/pro-components';
import type { ProColumns, ActionType } from '@ant-design/pro-components';
import {
  Button,
  Drawer,
  Modal,
  Popconfirm,
  Space,
  Tag,
  Typography,
  Form,
  Select,
  Input,
  message,
} from 'antd';
import { PlusOutlined, EditOutlined, DeleteOutlined, KeyOutlined, SafetyOutlined } from '@ant-design/icons';
import React, { useRef, useState, useEffect } from 'react';
import {
  listUsers,
  createUser,
  updateUser,
  deleteUser,
  changeUserPassword,
  assignUserRoles,
  listRoles,
  type AppUserDto,
  type CreateUserRequest,
  type UpdateUserRequest,
  type AppRoleDto,
} from '@/services/platform/api';

const { Text } = Typography;

const USER_TYPE_OPTIONS = [
  { label: 'Admin（平台管理员）', value: 'Admin' },
  { label: 'SimpleUser（普通用户）', value: 'SimpleUser' },
];

export default function UserManagementPage() {
  const actionRef = useRef<ActionType>(null);
  const [roles, setRoles] = useState<AppRoleDto[]>([]);

  // 新建/编辑用户
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editingUser, setEditingUser] = useState<AppUserDto | null>(null);
  const [drawerForm] = Form.useForm();
  const [drawerLoading, setDrawerLoading] = useState(false);

  // 改密码
  const [pwdModalOpen, setPwdModalOpen] = useState(false);
  const [pwdUserId, setPwdUserId] = useState<string>('');
  const [pwdForm] = Form.useForm();

  // 分配角色
  const [roleModalOpen, setRoleModalOpen] = useState(false);
  const [roleUserId, setRoleUserId] = useState<string>('');
  const [selectedRoleIds, setSelectedRoleIds] = useState<string[]>([]);
  const [roleLoading, setRoleLoading] = useState(false);

  useEffect(() => {
    listRoles().then(setRoles).catch(() => {});
  }, []);

  const columns: ProColumns<AppUserDto>[] = [
    { title: 'UserId', dataIndex: 'userId', width: 140, copyable: true },
    { title: '用户名', dataIndex: 'username', width: 120 },
    { title: '邮箱', dataIndex: 'email', width: 200 },
    { title: '显示名', dataIndex: 'displayName', width: 120 },
    {
      title: '类型',
      dataIndex: 'userType',
      width: 120,
      render: (_, r) => (
        <Tag color={r.userType === 'Admin' ? 'red' : 'blue'}>
          {r.userType === 'Admin' ? 'Admin' : '普通用户'}
        </Tag>
      ),
    },
    {
      title: '角色',
      dataIndex: 'roleIds',
      width: 200,
      render: (_, r) =>
        r.roleIds.length === 0
          ? <Text type="secondary">无</Text>
          : r.roleIds.map((id) => <Tag key={id}>{id}</Tag>),
    },
    {
      title: '状态',
      dataIndex: 'isEnabled',
      width: 80,
      render: (_, r) => (
        <Tag color={r.isEnabled ? 'success' : 'default'}>{r.isEnabled ? '启用' : '禁用'}</Tag>
      ),
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      width: 170,
      render: (v) => new Date(v as string).toLocaleString('zh-CN'),
    },
    {
      title: '操作',
      key: 'action',
      width: 200,
      render: (_, r) => (
        <Space>
          <Button
            size="small"
            icon={<EditOutlined />}
            onClick={() => openEdit(r)}
          >
            编辑
          </Button>
          <Button
            size="small"
            icon={<KeyOutlined />}
            onClick={() => openPwd(r.userId)}
          />
          <Button
            size="small"
            icon={<SafetyOutlined />}
            onClick={() => openRoles(r)}
          />
          <Popconfirm
            title={`确认删除用户 "${r.username}"？`}
            onConfirm={() => handleDelete(r.userId)}
            okText="删除"
            cancelText="取消"
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  function openCreate() {
    setEditingUser(null);
    drawerForm.resetFields();
    setDrawerOpen(true);
  }

  function openEdit(user: AppUserDto) {
    setEditingUser(user);
    drawerForm.setFieldsValue({
      username: user.username,
      email: user.email,
      displayName: user.displayName,
      userType: user.userType,
      isEnabled: user.isEnabled,
    });
    setDrawerOpen(true);
  }

  function openPwd(userId: string) {
    setPwdUserId(userId);
    pwdForm.resetFields();
    setPwdModalOpen(true);
  }

  function openRoles(user: AppUserDto) {
    setRoleUserId(user.userId);
    setSelectedRoleIds(user.roleIds);
    setRoleModalOpen(true);
  }

  async function handleDrawerSave() {
    const values = await drawerForm.validateFields();
    setDrawerLoading(true);
    try {
      if (editingUser) {
        await updateUser(editingUser.userId, values as UpdateUserRequest);
        message.success('用户已更新');
      } else {
        await createUser(values as CreateUserRequest);
        message.success('用户已创建');
      }
      setDrawerOpen(false);
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    } finally {
      setDrawerLoading(false);
    }
  }

  async function handleDelete(userId: string) {
    try {
      await deleteUser(userId);
      message.success('用户已删除');
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '删除失败');
    }
  }

  async function handlePwdSave() {
    const { newPassword } = await pwdForm.validateFields();
    try {
      await changeUserPassword(pwdUserId, newPassword);
      message.success('密码已修改');
      setPwdModalOpen(false);
    } catch (e: any) {
      message.error(e?.message ?? '修改失败');
    }
  }

  async function handleRoleSave() {
    setRoleLoading(true);
    try {
      await assignUserRoles(roleUserId, selectedRoleIds);
      message.success('角色已更新');
      setRoleModalOpen(false);
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    } finally {
      setRoleLoading(false);
    }
  }

  return (
    <PageContainer title="用户管理" subTitle="管理平台用户账号与权限角色分配">
      <ProTable<AppUserDto>
        actionRef={actionRef}
        rowKey="userId"
        columns={columns}
        request={async () => {
          const data = await listUsers();
          return { data, success: true, total: data.length };
        }}
        toolBarRender={() => [
          <Button key="create" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            新建用户
          </Button>,
        ]}
        search={false}
        pagination={{ pageSize: 20 }}
      />

      {/* 新建/编辑 Drawer */}
      <Drawer
        title={editingUser ? `编辑用户：${editingUser.username}` : '新建用户'}
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        width={500}
        extra={
          <Space>
            <Button onClick={() => setDrawerOpen(false)}>取消</Button>
            <Button type="primary" loading={drawerLoading} onClick={handleDrawerSave}>
              保存
            </Button>
          </Space>
        }
      >
        <ProForm form={drawerForm} submitter={false} layout="vertical">
          {!editingUser && (
            <>
              <ProFormText
                name="userId"
                label="UserId（英文唯一标识）"
                rules={[{ required: true }, { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字和连字符' }]}
                placeholder="e.g. zhangsan"
              />
              <ProFormText.Password
                name="password"
                label="初始密码"
                rules={[{ required: true }, { min: 6, message: '至少 6 位' }]}
              />
            </>
          )}
          <ProFormText
            name="username"
            label="用户名"
            rules={[{ required: true }]}
          />
          <ProFormText
            name="email"
            label="邮箱"
            rules={[{ required: true }, { type: 'email' }]}
          />
          <ProFormText name="displayName" label="显示名（可选）" />
          <ProFormSelect
            name="userType"
            label="用户类型"
            options={USER_TYPE_OPTIONS}
            initialValue="SimpleUser"
            rules={[{ required: true }]}
          />
          {editingUser && (
            <ProFormSwitch name="isEnabled" label="启用" initialValue />
          )}
        </ProForm>
      </Drawer>

      {/* 修改密码 Modal */}
      <Modal
        title="修改密码"
        open={pwdModalOpen}
        onCancel={() => setPwdModalOpen(false)}
        onOk={handlePwdSave}
        okText="确认修改"
        destroyOnClose
      >
        <Form form={pwdForm} layout="vertical">
          <Form.Item
            name="newPassword"
            label="新密码"
            rules={[{ required: true }, { min: 6, message: '至少 6 位' }]}
          >
            <Input.Password placeholder="输入新密码" />
          </Form.Item>
          <Form.Item
            name="confirm"
            label="确认密码"
            dependencies={['newPassword']}
            rules={[
              { required: true },
              ({ getFieldValue }) => ({
                validator(_, value) {
                  if (!value || getFieldValue('newPassword') === value) return Promise.resolve();
                  return Promise.reject(new Error('两次密码不一致'));
                },
              }),
            ]}
          >
            <Input.Password placeholder="再次输入新密码" />
          </Form.Item>
        </Form>
      </Modal>

      {/* 分配角色 Modal */}
      <Modal
        title="分配角色"
        open={roleModalOpen}
        onCancel={() => setRoleModalOpen(false)}
        onOk={handleRoleSave}
        okText="保存"
        confirmLoading={roleLoading}
        destroyOnClose
      >
        <p style={{ marginBottom: 12 }}>请选择此用户的角色（支持多选）：</p>
        <Select
          mode="multiple"
          style={{ width: '100%' }}
          value={selectedRoleIds}
          onChange={setSelectedRoleIds}
          options={roles.map((r) => ({
            label: `${r.name}（${r.roleId}）`,
            value: r.roleId,
          }))}
          placeholder="选择角色"
        />
      </Modal>
    </PageContainer>
  );
}
