import {
  Alert,
  Button,
  Card,
  Empty,
  Form,
  Input,
  List,
  Modal,
  message,
  Select,
  Space,
  Switch,
  Tag,
  Typography,
} from 'antd';
import React, { useMemo, useState } from 'react';
import {
  addHttpHookTrigger,
  buildHttpHookEndpoint,
  type HttpHookDraftValues,
  listHttpHookTriggers,
  removeHttpHookTrigger,
  setHttpHookEnabled,
} from './httpHookTriggers';
import type {
  OrchestrationCatalog,
  OrchestrationGraphDefinition,
} from './types';

const { Paragraph, Text } = Typography;

interface HttpHookPanelProps {
  definition: OrchestrationGraphDefinition;
  savedDefinition?: OrchestrationGraphDefinition;
  catalog?: OrchestrationCatalog;
  disabled?: boolean;
  onDefinitionChange: (definition: OrchestrationGraphDefinition) => void;
}

const HttpHookPanel: React.FC<HttpHookPanelProps> = ({
  definition,
  savedDefinition,
  catalog,
  disabled,
  onDefinitionChange,
}) => {
  const [form] = Form.useForm<HttpHookDraftValues>();
  const [modalOpen, setModalOpen] = useState(false);
  const hooks = listHttpHookTriggers(definition);
  const savedHookIds = useMemo(
    () =>
      new Set(
        savedDefinition
          ? listHttpHookTriggers(savedDefinition).map(
              (trigger) => trigger.triggerId,
            )
          : [],
      ),
    [savedDefinition],
  );

  const openCreate = () => {
    let suffix = hooks.length + 1;
    let triggerId = suffix === 1 ? 'http-hook' : `http-hook-${suffix}`;
    while (
      (definition.triggers ?? []).some(
        (trigger) => trigger.triggerId === triggerId,
      )
    ) {
      suffix += 1;
      triggerId = `http-hook-${suffix}`;
    }
    form.setFieldsValue({ triggerId, sourcePath: '$' });
    setModalOpen(true);
  };

  const addHook = async () => {
    const values = await form.validateFields();
    const result = addHttpHookTrigger(definition, catalog, values);
    if (result.error) {
      message.error(result.error);
      return;
    }
    onDefinitionChange(result.definition);
    setModalOpen(false);
    form.resetFields();
    message.success(`已添加 HTTP Hook ${values.triggerId.trim()}`);
  };

  return (
    <Card
      size="small"
      title={`HTTP Hooks（${hooks.length}）`}
      extra={
        <Button size="small" disabled={disabled} onClick={openCreate}>
          新增 Hook
        </Button>
      }
    >
      <Alert
        type="info"
        showIcon
        message="调试 Hook 固定调用一个已保存 Revision"
        description="接口要求 Admin Bearer Token 和 sourceEventId；相同事件重试返回同一个 Run，不会跟随 Graph Head。请求体上限 1 MiB。"
        style={{ marginBottom: 12 }}
      />
      {hooks.length === 0 ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="尚未定义 HTTP Hook"
        />
      ) : (
        <List
          size="small"
          dataSource={hooks}
          renderItem={(hook) => {
            const saved = savedDefinition && savedHookIds.has(hook.triggerId);
            const endpoint = saved
              ? buildHttpHookEndpoint(
                  savedDefinition.graphId,
                  savedDefinition.revisionId,
                  hook.triggerId,
                )
              : undefined;
            const mapping = hook.inputBindings?.[0];
            const samplePayload =
              mapping?.sourcePath === '$'
                ? { message: 'hello from HTTP Hook' }
                : { message: 'hello from HTTP Hook' };
            const sampleBody = JSON.stringify(
              { sourceEventId: 'debug-event-001', payload: samplePayload },
              null,
              2,
            );
            const curl = endpoint
              ? `curl -X POST "${window.location.origin}${endpoint}" -H "Authorization: Bearer <ADMIN_TOKEN>" -H "Content-Type: application/json" --data '${sampleBody.replace(/\n/g, '')}'`
              : undefined;
            return (
              <List.Item
                actions={[
                  <Switch
                    key="enabled"
                    size="small"
                    checked={hook.enabled !== false}
                    disabled={disabled}
                    checkedChildren="启用"
                    unCheckedChildren="停用"
                    onChange={(enabled) =>
                      onDefinitionChange(
                        setHttpHookEnabled(definition, hook.triggerId, enabled),
                      )
                    }
                  />,
                  <Button
                    key="delete"
                    type="link"
                    size="small"
                    danger
                    disabled={disabled}
                    onClick={() =>
                      Modal.confirm({
                        title: `删除 HTTP Hook ${hook.triggerId}？`,
                        content: '保存 Revision 前可通过放弃草稿恢复。',
                        okText: '确认删除',
                        okButtonProps: { danger: true },
                        cancelText: '取消',
                        onOk: () =>
                          onDefinitionChange(
                            removeHttpHookTrigger(definition, hook.triggerId),
                          ),
                      })
                    }
                  >
                    删除
                  </Button>,
                ]}
              >
                <List.Item.Meta
                  title={
                    <Space wrap>
                      <Text strong>{hook.triggerId}</Text>
                      <Tag color={hook.enabled === false ? 'default' : 'green'}>
                        {hook.enabled === false ? '已停用' : '已启用'}
                      </Tag>
                      {!saved ? (
                        <Tag color="orange">保存 Revision 后可调用</Tag>
                      ) : null}
                    </Space>
                  }
                  description={
                    <div>
                      {mapping ? (
                        <Paragraph style={{ marginBottom: 6 }}>
                          payload <Text code>{mapping.sourcePath ?? '$'}</Text>{' '}
                          → Graph Input{' '}
                          <Text code>{mapping.targetInputId}</Text>
                        </Paragraph>
                      ) : (
                        <Paragraph type="secondary" style={{ marginBottom: 6 }}>
                          无输入映射；调用只创建并激活 Run。
                        </Paragraph>
                      )}
                      {endpoint ? (
                        <>
                          <Text code copyable style={{ fontSize: 12 }}>
                            POST {endpoint}
                          </Text>
                          <Paragraph
                            copyable={{ text: sampleBody }}
                            style={{ margin: '8px 0 0' }}
                          >
                            复制示例请求体
                          </Paragraph>
                          {curl ? (
                            <Paragraph
                              copyable={{ text: curl }}
                              style={{ margin: '4px 0 0' }}
                            >
                              复制 cURL
                            </Paragraph>
                          ) : null}
                        </>
                      ) : null}
                    </div>
                  }
                />
              </List.Item>
            );
          }}
        />
      )}

      <Modal
        title="新增 HTTP Hook"
        open={modalOpen}
        okText="添加到草稿"
        cancelText="取消"
        onOk={() => void addHook()}
        onCancel={() => {
          setModalOpen(false);
          form.resetFields();
        }}
      >
        <Form form={form} layout="vertical">
          <Form.Item
            name="triggerId"
            label="Trigger ID"
            rules={[
              {
                required: true,
                whitespace: true,
                message: '请输入 Trigger ID',
              },
              {
                pattern: /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/,
                message: '仅允许字母、数字、点、下划线和连字符',
              },
            ]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item name="targetInputId" label="映射到 Graph Input（可选）">
            <Select
              allowClear
              placeholder="无输入映射"
              options={(definition.inputs ?? []).map((input) => ({
                value: input.inputId,
                label: `${input.inputId} · ${input.contract.dataType}`,
              }))}
            />
          </Form.Item>
          <Form.Item
            noStyle
            shouldUpdate={(previous, current) =>
              previous.targetInputId !== current.targetInputId
            }
          >
            {({ getFieldValue }) =>
              getFieldValue('targetInputId') ? (
                <Form.Item
                  name="sourcePath"
                  label="Payload 路径"
                  tooltip="受限 JSONPath：$、$.field、$.items[0]"
                  rules={[{ required: true, message: '请输入 Payload 路径' }]}
                >
                  <Input placeholder="$.message" />
                </Form.Item>
              ) : null
            }
          </Form.Item>
        </Form>
      </Modal>
    </Card>
  );
};

export default HttpHookPanel;
