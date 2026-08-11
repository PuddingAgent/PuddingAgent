import {
  Alert,
  Form,
  Input,
  InputNumber,
  Modal,
  Switch,
  Typography,
} from 'antd';
import React, { useEffect } from 'react';
import { buildManualRunInputs } from './manualRun';
import type {
  OrchestrationGraphDefinition,
  OrchestrationValueEnvelope,
} from './types';

const { Text } = Typography;

export interface ManualRunModalProps {
  open: boolean;
  definition?: OrchestrationGraphDefinition;
  loading: boolean;
  onCancel: () => void;
  onSubmit: (
    inputs: Record<string, OrchestrationValueEnvelope>,
  ) => Promise<void>;
}

const ManualRunModal: React.FC<ManualRunModalProps> = ({
  open,
  definition,
  loading,
  onCancel,
  onSubmit,
}) => {
  const [form] = Form.useForm<Record<string, unknown>>();

  useEffect(() => {
    if (open) form.resetFields();
  }, [form, open]);

  return (
    <Modal
      title="运行编排"
      open={open}
      okText="运行"
      cancelText="取消"
      confirmLoading={loading}
      onCancel={onCancel}
      onOk={() => form.submit()}
      destroyOnClose
    >
      {definition ? (
        <>
          <Alert
            type="info"
            showIcon
            message={`将运行 ${definition.revisionId}`}
            description="运行固定到当前已保存 Revision；未保存草稿不会进入本次执行。"
            style={{ marginBottom: 16 }}
          />
          <Form
            form={form}
            layout="vertical"
            onFinish={async (values) => {
              try {
                await onSubmit(buildManualRunInputs(definition, values));
              } catch (error) {
                form.setFields([
                  {
                    name: (definition.inputs ?? [])[0]?.inputId,
                    errors: [
                      error instanceof Error ? error.message : String(error),
                    ],
                  },
                ]);
              }
            }}
          >
            {(definition.inputs ?? []).map((input) => {
              const required = input.requiredAtActivation !== false;
              const label = (
                <span>
                  {input.inputId}{' '}
                  <Text type="secondary">{input.contract.dataType}</Text>
                </span>
              );
              if (input.contract.dataType === 'pudding.boolean') {
                return (
                  <Form.Item
                    key={input.inputId}
                    name={input.inputId}
                    label={label}
                    valuePropName="checked"
                    rules={[{ required }]}
                  >
                    <Switch />
                  </Form.Item>
                );
              }
              if (input.contract.dataType === 'pudding.number') {
                return (
                  <Form.Item
                    key={input.inputId}
                    name={input.inputId}
                    label={label}
                    rules={[{ required, message: `请输入 ${input.inputId}` }]}
                  >
                    <InputNumber style={{ width: '100%' }} />
                  </Form.Item>
                );
              }
              return (
                <Form.Item
                  key={input.inputId}
                  name={input.inputId}
                  label={label}
                  rules={[
                    {
                      required,
                      whitespace: true,
                      message: `请输入 ${input.inputId}`,
                    },
                  ]}
                >
                  <Input.TextArea
                    rows={input.contract.dataType === 'pudding.json' ? 6 : 4}
                    placeholder={
                      input.inputId === 'prompt'
                        ? '描述要生成的图片，例如：雨夜霓虹街道中的未来城市，电影感广角'
                        : input.contract.dataType === 'pudding.json'
                          ? '{ }'
                          : undefined
                    }
                  />
                </Form.Item>
              );
            })}
            {(definition.inputs ?? []).length === 0 ? (
              <Text type="secondary">
                这个 Revision 没有 Graph Inputs，将直接运行。
              </Text>
            ) : null}
          </Form>
        </>
      ) : null}
    </Modal>
  );
};

export default ManualRunModal;
