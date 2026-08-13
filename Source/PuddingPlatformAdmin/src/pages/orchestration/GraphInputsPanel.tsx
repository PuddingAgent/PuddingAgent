import {
  Button,
  Card,
  Checkbox,
  Empty,
  Form,
  Input,
  Modal,
  message,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React, { useState } from 'react';
import {
  addGraphInput,
  formatGraphInputDefaultValue,
  listInputReferences,
  parseGraphInputDefaultValue,
  removeGraphInput,
  updateGraphInput,
} from './graphInputs';
import type {
  OrchestrationDataContract,
  OrchestrationGraphDefinition,
  OrchestrationGraphInput,
} from './types';

const { Text } = Typography;

interface GraphInputFormValues {
  inputId: string;
  dataType: string;
  mediaTypes: string;
  cardinality: OrchestrationDataContract['cardinality'];
  deliveries: OrchestrationDataContract['deliveries'];
  requiredAtActivation: boolean;
  /** ValueEnvelope JSON text; empty string means no default. */
  defaultValue: string;
}

interface GraphInputsPanelProps {
  definition: OrchestrationGraphDefinition;
  disabled?: boolean;
  onDefinitionChange: (definition: OrchestrationGraphDefinition) => void;
}

function toFormValues(input?: OrchestrationGraphInput): GraphInputFormValues {
  return input
    ? {
        inputId: input.inputId,
        dataType: input.contract.dataType,
        mediaTypes: input.contract.mediaTypes.join(', '),
        cardinality: input.contract.cardinality,
        deliveries: input.contract.deliveries,
        requiredAtActivation: input.requiredAtActivation !== false,
        defaultValue: formatGraphInputDefaultValue(input),
      }
    : {
        inputId: '',
        dataType: 'pudding.content',
        mediaTypes: 'text/plain',
        cardinality: 'one',
        deliveries: ['inline', 'artifact'],
        requiredAtActivation: true,
        defaultValue: '',
      };
}

function toGraphInput(values: GraphInputFormValues): OrchestrationGraphInput {
  const parsedDefault = parseGraphInputDefaultValue(values.defaultValue);
  return {
    inputId: values.inputId.trim(),
    // The form validator guarantees parseGraphInputDefaultValue succeeds; the
    // error branch is defensive and never yields a definition payload.
    ...(parsedDefault.error ? {} : { defaultValue: parsedDefault.value }),
    requiredAtActivation: values.requiredAtActivation,
    contract: {
      dataType: values.dataType.trim(),
      mediaTypes: values.mediaTypes
        .split(',')
        .map((item) => item.trim())
        .filter(Boolean),
      cardinality: values.cardinality,
      deliveries: values.deliveries,
    },
  };
}

const GraphInputsPanel: React.FC<GraphInputsPanelProps> = ({
  definition,
  disabled,
  onDefinitionChange,
}) => {
  const [form] = Form.useForm<GraphInputFormValues>();
  const [open, setOpen] = useState(false);
  const [editingInputId, setEditingInputId] = useState<string>();

  const openEditor = (input?: OrchestrationGraphInput) => {
    setEditingInputId(input?.inputId);
    form.setFieldsValue(toFormValues(input));
    setOpen(true);
  };

  const submit = async () => {
    const values = await form.validateFields();
    const input = toGraphInput(values);
    const next = editingInputId
      ? updateGraphInput(definition, editingInputId, {
          contract: input.contract,
          requiredAtActivation: input.requiredAtActivation,
          ...(input.defaultValue !== undefined
            ? { defaultValue: input.defaultValue }
            : { defaultValue: undefined }),
        })
      : addGraphInput(definition, input);
    if (next === definition) {
      message.warning(
        editingInputId
          ? 'Graph Input 不存在或没有变化'
          : `Graph Input ${input.inputId} 已存在`,
      );
      return;
    }
    onDefinitionChange(next);
    setOpen(false);
  };

  const confirmRemove = (input: OrchestrationGraphInput) => {
    const references = listInputReferences(definition, input.inputId);
    Modal.confirm({
      title: `删除 Graph Input ${input.inputId}？`,
      content:
        references.length > 0
          ? `将同步清理 ${references.length} 个节点端口绑定：${references.map((item) => `${item.nodeId}.${item.targetPortId}`).join('、')}`
          : '当前没有节点引用；保存 Revision 前仍可放弃草稿恢复。',
      okText: '删除并清理引用',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () =>
        onDefinitionChange(
          removeGraphInput(definition, input.inputId).definition,
        ),
    });
  };

  return (
    <>
      <Card
        size="small"
        title={`Graph Inputs（${definition.inputs?.length ?? 0}）`}
        extra={
          <Button size="small" disabled={disabled} onClick={() => openEditor()}>
            新增输入
          </Button>
        }
        style={{ marginBottom: 16 }}
      >
        {(definition.inputs?.length ?? 0) === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="暂无图级输入"
          />
        ) : (
          <Space direction="vertical" size={10} style={{ width: '100%' }}>
            {definition.inputs?.map((input) => {
              const references = listInputReferences(definition, input.inputId);
              return (
                <div
                  key={input.inputId}
                  style={{
                    borderBottom: '1px solid rgba(128,128,128,.18)',
                    paddingBottom: 9,
                  }}
                >
                  <Space wrap size={4}>
                    <Text strong>{input.inputId}</Text>
                    <Tag>{input.contract.dataType}</Tag>
                    <Tag
                      color={
                        input.contract.cardinality === 'many'
                          ? 'purple'
                          : 'default'
                      }
                    >
                      {input.contract.cardinality}
                    </Tag>
                    {input.requiredAtActivation !== false ? (
                      <Tag color="blue">激活必填</Tag>
                    ) : null}
                    {input.defaultValue !== undefined &&
                    input.defaultValue !== null ? (
                      <Tag color="green">默认值</Tag>
                    ) : null}
                  </Space>
                  <div>
                    <Text type="secondary" style={{ fontSize: 12 }}>
                      MIME {input.contract.mediaTypes.join(', ') || '*'} · 引用{' '}
                      {references.length}
                    </Text>
                  </div>
                  <Space size={4} style={{ marginTop: 5 }}>
                    <Button
                      size="small"
                      type="link"
                      disabled={disabled}
                      onClick={() => openEditor(input)}
                    >
                      编辑
                    </Button>
                    <Button
                      size="small"
                      type="link"
                      danger
                      disabled={disabled}
                      onClick={() => confirmRemove(input)}
                    >
                      删除
                    </Button>
                  </Space>
                </div>
              );
            })}
          </Space>
        )}
      </Card>

      <Modal
        title={
          editingInputId
            ? `编辑 Graph Input ${editingInputId}`
            : '新增 Graph Input'
        }
        open={open}
        okText={editingInputId ? '应用修改' : '添加输入'}
        cancelText="取消"
        onOk={() => void submit()}
        onCancel={() => setOpen(false)}
      >
        <Form form={form} layout="vertical" initialValues={toFormValues()}>
          <Form.Item
            name="inputId"
            label="Input ID"
            rules={[
              { required: true, whitespace: true, message: '请输入 Input ID' },
              {
                pattern: /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/,
                message: '仅允许字母、数字、点、下划线和连字符',
              },
            ]}
          >
            <Input disabled={Boolean(editingInputId)} />
          </Form.Item>
          <Form.Item
            name="dataType"
            label="Data Type"
            rules={[{ required: true, whitespace: true }]}
          >
            <Input placeholder="pudding.content" />
          </Form.Item>
          <Form.Item name="mediaTypes" label="MIME（逗号分隔）">
            <Input placeholder="text/plain, image/png" />
          </Form.Item>
          <Space align="start" style={{ width: '100%' }}>
            <Form.Item
              name="cardinality"
              label="Cardinality"
              rules={[{ required: true }]}
            >
              <Select
                style={{ width: 130 }}
                options={[{ value: 'one' }, { value: 'many' }]}
              />
            </Form.Item>
            <Form.Item
              name="deliveries"
              label="Delivery"
              rules={[{ required: true, type: 'array', min: 1 }]}
            >
              <Select
                mode="multiple"
                style={{ minWidth: 260 }}
                options={['inline', 'artifact', 'stream', 'event'].map(
                  (value) => ({ value }),
                )}
              />
            </Form.Item>
          </Space>
          <Form.Item name="requiredAtActivation" valuePropName="checked">
            <Checkbox>激活 Run 时必须提供</Checkbox>
          </Form.Item>
          <Form.Item
            name="defaultValue"
            label="Default Value（ValueEnvelope JSON，可选）"
            tooltip="留空表示无默认值；填写的 JSON 将作为 defaultValue 保存，服务端编译器会按 Contract 校验（dataType / mediaTypes / cardinality / delivery）。"
            rules={[
              {
                validator: async (_, value: string | undefined) => {
                  const parsed = parseGraphInputDefaultValue(value ?? '');
                  if (parsed.error) {
                    throw new Error(`不是合法 JSON：${parsed.error}`);
                  }
                },
              },
            ]}
          >
            <Input.TextArea
              rows={3}
              placeholder={'{\n  "dataType": "pudding.content",\n  "contentType": "text/plain",\n  "inlineValue": "..."\n}'}
            />
          </Form.Item>
        </Form>
      </Modal>
    </>
  );
};

export default GraphInputsPanel;
