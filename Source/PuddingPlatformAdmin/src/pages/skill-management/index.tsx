import {
  PageContainer,
  ProTable,
  ProForm,
  ProFormText,
  ProFormTextArea,
  ProFormSwitch,
  ProFormDigit,
} from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import {
  Button,
  Card,
  Col,
  Drawer,
  Form,
  Popconfirm,
  Radio,
  Row,
  Space,
  Badge,
  Input,
  message,
  Typography,
  Upload,
  Tag,
  theme,
} from 'antd';
import { DeleteOutlined, EditOutlined, PlusOutlined, UploadOutlined, DownloadOutlined, FileZipOutlined, FileTextOutlined, AppstoreOutlined, TableOutlined } from '@ant-design/icons';
import React, { useRef, useState } from 'react';
import type { UploadFile } from 'antd';
import {
  listSkillPackages,
  createSkillPackage,
  updateSkillPackage,
  updateSkillPackageFile,
  deleteSkillPackage,
  getSkillPackageDownloadUrl,
  type SkillPackageDto,
  type UpdateSkillPackageRequest,
} from '@/services/platform/api';

const { Text } = Typography;

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

const SkillManagementPage: React.FC = () => {
  const { token } = theme.useToken();

  /** 根据文件扩展名映射图标组件 */
  const getFileIcon = (fileName: string): React.ReactNode => {
    const name = fileName.toLowerCase();
    if (name.endsWith('.zip')) return <FileZipOutlined style={{ fontSize: 20, color: token.colorWarning }} />;
    if (name.endsWith('.tar.gz') || name.endsWith('.tgz')) return <FileTextOutlined style={{ fontSize: 20, color: token.colorInfo }} />;
    return <FileTextOutlined style={{ fontSize: 20, color: token.colorBorder }} />;
  };

  /** Upload 文件列表项渲染：图标 + 文件名 + 文件大小 */
  const fileItemRender: Parameters<typeof Upload>[0]['itemRender'] = (originNode, file) => {
    const size = (file as any).size ?? (file.originFileObj as any)?.size;
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '4px 0' }}>
        {getFileIcon(file.name)}
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: 13, fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {file.name}
          </div>
          {size != null && (
            <Text type="secondary" style={{ fontSize: 11 }}>{formatBytes(size)}</Text>
          )}
        </div>
      </div>
    );
  };
  const tableRef = useRef<ActionType | undefined>(undefined);
  const [viewMode, setViewMode] = useState<'card' | 'table'>('card');

  // Create/Edit meta drawer
  const [formDrawer, setFormDrawer] = useState(false);
  const [editItem, setEditItem] = useState<SkillPackageDto | null>(null);
  const [form] = Form.useForm<UpdateSkillPackageRequest & { skillPackageId?: string; version?: string }>();

  // File upload state for create
  const [fileList, setFileList] = useState<UploadFile[]>([]);

  // Update file drawer
  const [fileDrawer, setFileDrawer] = useState(false);
  const [fileDrawerItem, setFileDrawerItem] = useState<SkillPackageDto | null>(null);
  const [updateFileList, setUpdateFileList] = useState<UploadFile[]>([]);
  const [updateVersion, setUpdateVersion] = useState('');

  const openCreate = () => {
    setEditItem(null);
    setFileList([]);
    form.resetFields();
    form.setFieldsValue({ isEnabled: true, sortOrder: 100, version: '1.0.0' });
    setFormDrawer(true);
  };

  const openEdit = (item: SkillPackageDto) => {
    setEditItem(item);
    form.setFieldsValue({
      name: item.name,
      description: item.description,
      isEnabled: item.isEnabled,
      sortOrder: item.sortOrder,
    });
    setFormDrawer(true);
  };

  const openUpdateFile = (item: SkillPackageDto) => {
    setFileDrawerItem(item);
    setUpdateFileList([]);
    setUpdateVersion(item.version);
    setFileDrawer(true);
  };

  const handleSave = async () => {
    const values = await form.validateFields();

    if (editItem) {
      // 只更新元数据
      const req: UpdateSkillPackageRequest = {
        name: values.name!,
        description: values.description,
        isEnabled: values.isEnabled ?? true,
        sortOrder: values.sortOrder ?? 100,
      };
      await updateSkillPackage(editItem.skillPackageId, req);
      message.success('SKILL 已更新');
    } else {
      // 新建：需要文件
      if (fileList.length === 0) {
        message.error('请上传 SKILL 包文件（.zip 或 .tar.gz）');
        return;
      }
      const fd = new FormData();
      fd.append('skillPackageId', values.skillPackageId ?? '');
      fd.append('name', values.name ?? '');
      if (values.description) fd.append('description', values.description);
      fd.append('version', values.version ?? '1.0.0');
      fd.append('file', fileList[0].originFileObj as File);
      await createSkillPackage(fd);
      message.success('SKILL 已创建');
    }

    setFormDrawer(false);
    tableRef.current?.reload();
  };

  const handleUpdateFile = async () => {
    if (!fileDrawerItem) return;
    if (updateFileList.length === 0) {
      message.error('请选择新的 SKILL 包文件');
      return;
    }
    const fd = new FormData();
    fd.append('version', updateVersion || fileDrawerItem.version);
    fd.append('file', updateFileList[0].originFileObj as File);
    await updateSkillPackageFile(fileDrawerItem.skillPackageId, fd);
    message.success('SKILL 文件已更新');
    setFileDrawer(false);
    tableRef.current?.reload();
  };

  const handleDelete = async (skillPackageId: string) => {
    await deleteSkillPackage(skillPackageId);
    message.success('SKILL 已删除');
    tableRef.current?.reload();
  };

  const handleDownload = async (skillPackageId: string) => {
    const { url } = await getSkillPackageDownloadUrl(skillPackageId);
    window.open(url, '_blank');
  };

  const columns: ProColumns<SkillPackageDto>[] = [
    {
      title: 'SKILL ID',
      dataIndex: 'skillPackageId',
      width: 180,
      copyable: true,
      render: (_, r) => <Text code>{r.skillPackageId}</Text>,
    },
    {
      title: '名称',
      dataIndex: 'name',
      width: 160,
    },
    {
      title: '版本',
      dataIndex: 'version',
      width: 90,
      render: (_, r) => <Tag color="blue">{r.version}</Tag>,
    },
    {
      title: '描述',
      ellipsis: true,
      render: (_, r) => <Text type="secondary">{r.description || '—'}</Text>,
    },
    {
      title: '文件',
      width: 140,
      render: (_, r) => (
        <Space direction="vertical" size={0}>
          <Text type="secondary" style={{ fontSize: 12 }}>{r.fileName}</Text>
          <Text type="secondary" style={{ fontSize: 11 }}>{formatBytes(r.fileSizeBytes)}</Text>
        </Space>
      ),
    },
    {
      title: '状态',
      width: 90,
      render: (_, r) =>
        r.isEnabled ? <Badge status="processing" text="启用" /> : <Badge status="default" text="停用" />,
    },
    {
      title: '操作',
      width: 140,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} title="编辑信息" />
          <Button size="small" icon={<UploadOutlined />} onClick={() => openUpdateFile(r)} title="更新文件" />
          <Button size="small" icon={<DownloadOutlined />} onClick={() => handleDownload(r.skillPackageId)} title="下载" />
          <Popconfirm title="确认删除该 SKILL 包？" onConfirm={() => handleDelete(r.skillPackageId)}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  const acceptTypes = '.zip,.tar.gz,.tgz';

  return (
    <PageContainer
      title="SKILL 管理"
      subTitle="上传并管理可供 Agent 模板选择的技能包（.zip / .tar.gz）"
    >
      {viewMode === 'card' ? (
        <ProTable<SkillPackageDto>
          actionRef={tableRef}
          rowKey="id"
          columns={[]}
          request={async () => {
            const data = await listSkillPackages();
            return { data, success: true };
          }}
          search={false}
          toolBarRender={() => [
            <Radio.Group
              key="viewToggle"
              value={viewMode}
              onChange={(e) => setViewMode(e.target.value)}
              optionType="button"
              buttonStyle="solid"
              size="small"
              style={{ marginRight: 8 }}
            >
              <Radio.Button value="card"><AppstoreOutlined /> 卡片</Radio.Button>
              <Radio.Button value="table"><TableOutlined /> 表格</Radio.Button>
            </Radio.Group>,
            <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
              上传 SKILL
            </Button>,
          ]}
          tableRender={(_, tableProps) => {
            const items = (tableProps?.dataSource ?? []) as SkillPackageDto[];
            return (
              <Row gutter={[16, 16]} style={{ padding: '0 0 16px' }}>
                {items.map((item) => (
                  <Col xs={24} sm={12} lg={8} xl={6} key={item.id}>
                    <Card
                      hoverable
                      size="small"
                      style={{
                        borderRadius: 14,
                        borderLeft: '4px solid #22d3ee',
                        background: 'rgba(250,250,247,0.78)',
                        backdropFilter: 'blur(8px)',
                        boxShadow: '0 2px 16px rgba(0,0,0,0.04)',
                      }}
                      styles={{ body: { padding: '16px' } }}
                    >
                      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 8 }}>
                        <Space>
                          {getFileIcon(item.fileName)}
                          <div>
                            <Text strong style={{ fontSize: 15, display: 'block' }}>{item.name}</Text>
                            <Text code style={{ fontSize: 11 }}>{item.skillPackageId}</Text>
                          </div>
                        </Space>
                        <Tag color="blue" style={{ fontSize: 11 }}>v{item.version}</Tag>
                      </div>

                      {item.description && (
                        <Text type="secondary" style={{ fontSize: 12, display: 'block', marginBottom: 8 }}>
                          {item.description.length > 50 ? `${item.description.slice(0, 50)}…` : item.description}
                        </Text>
                      )}

                      <div style={{ marginBottom: 8 }}>
                        <Space direction="vertical" size={0}>
                          <Text type="secondary" style={{ fontSize: 11 }}>{item.fileName}</Text>
                          <Text type="secondary" style={{ fontSize: 10 }}>{formatBytes(item.fileSizeBytes)}</Text>
                        </Space>
                      </div>

                      <div style={{ borderTop: '1px solid #f0f0f0', paddingTop: 10, display: 'flex', gap: 4 }}>
                        <Badge status={item.isEnabled ? 'processing' : 'default'} text={item.isEnabled ? '启用' : '停用'} />
                        <div style={{ flex: 1 }} />
                        <Button size="small" icon={<EditOutlined />} type="text" onClick={() => openEdit(item)} title="编辑信息" />
                        <Button size="small" icon={<DownloadOutlined />} type="text" onClick={() => handleDownload(item.skillPackageId)} title="下载" />
                        <Popconfirm title="确认删除该 SKILL 包？" onConfirm={() => handleDelete(item.skillPackageId)}>
                          <Button size="small" danger icon={<DeleteOutlined />} type="text" />
                        </Popconfirm>
                      </div>
                    </Card>
                  </Col>
                ))}
              </Row>
            );
          }}
        />
      ) : (
        <ProTable<SkillPackageDto>
          actionRef={tableRef}
          rowKey="id"
          columns={columns}
          request={async () => {
            const data = await listSkillPackages();
            return { data, success: true };
          }}
          search={false}
          toolBarRender={() => [
            <Radio.Group
              key="viewToggle"
              value={viewMode}
              onChange={(e) => setViewMode(e.target.value)}
              optionType="button"
              buttonStyle="solid"
              size="small"
              style={{ marginRight: 8 }}
            >
              <Radio.Button value="card"><AppstoreOutlined /> 卡片</Radio.Button>
              <Radio.Button value="table"><TableOutlined /> 表格</Radio.Button>
            </Radio.Group>,
            <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
              上传 SKILL
            </Button>,
          ]}
        />
      )}

      {/* Create / Edit meta Drawer */}
      <Drawer
        title={editItem ? '编辑 SKILL 信息' : '上传新 SKILL'}
        open={formDrawer}
        width={520}
        onClose={() => setFormDrawer(false)}
        extra={
          <Button type="primary" onClick={handleSave}>
            保存
          </Button>
        }
      >
        <ProForm form={form} submitter={false} layout="vertical">
          <ProFormText
            name="skillPackageId"
            label="SKILL ID"
            rules={[
              { required: !editItem },
              { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字、连字符' },
            ]}
            disabled={!!editItem}
            placeholder="如 my-python-utils"
          />
          <ProFormText
            name="name"
            label="名称"
            rules={[{ required: true }]}
            placeholder="如 Python 工具集"
          />
          <ProFormTextArea name="description" label="用途描述" rows={3} placeholder="简要说明该 SKILL 的功能，将注入到 Agent 提示词中" />
          {!editItem && (
            <ProFormText
              name="version"
              label="版本"
              rules={[{ required: true }]}
              placeholder="如 1.0.0"
            />
          )}
          <Space size="large">
            <ProFormDigit name="sortOrder" label="排序权重" min={0} />
            <ProFormSwitch name="isEnabled" label="启用" />
          </Space>

          {!editItem && (
            <Form.Item label="SKILL 包文件" required>
              <Upload
                accept={acceptTypes}
                maxCount={1}
                fileList={fileList}
                itemRender={fileItemRender}
                beforeUpload={(file) => {
                  const name = file.name.toLowerCase();
                  if (!name.endsWith('.zip') && !name.endsWith('.tar.gz') && !name.endsWith('.tgz')) {
                    message.error('仅支持 .zip 或 .tar.gz 格式');
                    return Upload.LIST_IGNORE;
                  }
                  setFileList([{ ...file, originFileObj: file } as UploadFile]);
                  return false; // prevent auto upload
                }}
                onRemove={() => setFileList([])}
              >
                <Button icon={<UploadOutlined />}>选择文件</Button>
              </Upload>
            </Form.Item>
          )}
        </ProForm>
      </Drawer>

      {/* Update File Drawer */}
      <Drawer
        title={`更新文件 — ${fileDrawerItem?.name ?? ''}`}
        open={fileDrawer}
        width={480}
        onClose={() => setFileDrawer(false)}
        extra={
          <Button type="primary" onClick={handleUpdateFile}>
            上传更新
          </Button>
        }
      >
        <div style={{ marginBottom: 16 }}>
          <Text>新版本号：</Text>
          <Input
            style={{
              marginLeft: 8,
              borderRadius: token.borderRadius,
              width: 140,
              display: 'inline-block',
              verticalAlign: 'middle',
            }}
            value={updateVersion}
            onChange={(e) => setUpdateVersion(e.target.value)}
            placeholder="如 1.1.0"
          />
        </div>
        <Upload
          accept={acceptTypes}
          maxCount={1}
          fileList={updateFileList}
          itemRender={fileItemRender}
          beforeUpload={(file) => {
            const name = file.name.toLowerCase();
            if (!name.endsWith('.zip') && !name.endsWith('.tar.gz') && !name.endsWith('.tgz')) {
              message.error('仅支持 .zip 或 .tar.gz 格式');
              return Upload.LIST_IGNORE;
            }
            setUpdateFileList([{ ...file, originFileObj: file } as UploadFile]);
            return false;
          }}
          onRemove={() => setUpdateFileList([])}
        >
          <Button icon={<UploadOutlined />}>选择新文件</Button>
        </Upload>
      </Drawer>
    </PageContainer>
  );
};

export default SkillManagementPage;
