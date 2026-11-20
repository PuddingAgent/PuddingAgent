// ─── 子代理委派配置（需求 3「配置界面」）────────────────────────────
//
// 四个开关决定「谁能创建子代理、能同时跑几个、能不能自己选模型、选不了时用哪个」。
//
// 两个必须让用户看见的事实：
// ① 改动**不需要重启 Core** —— system.json 以 reloadOnChange:true 接入配置链，
//    而 SubAgentTool 每次派发时实时读配置；保存后下一次派发即生效。
// ② 每个值都要标明「来自配置文件」还是「代码默认」——两者长得一样的话，
//    用户无法判断自己到底改过没有。
//
// 保存后一律用 PUT 的响应体更新本地状态，不在保存后立刻 GET 回读：
// 配置重载是异步的，回读可能拿到旧快照，会把「已保存」误报成「没生效」。

import { PageContainer } from '@ant-design/pro-components';
import {
  Alert,
  Button,
  Card,
  Descriptions,
  Form,
  Input,
  InputNumber,
  Space,
  Spin,
  Switch,
  Tag,
  Typography,
  message,
} from 'antd';
import React, { useCallback, useEffect, useState } from 'react';
import {
  getSubAgentDelegation,
  updateSubAgentDelegation,
  type SubAgentDelegationSnapshotDto,
} from '@/services/platform/api';

const { Paragraph, Text } = Typography;

type FormValues = {
  maxDepth: number;
  maxConcurrentSubAgents: number;
  allowModelSelection: boolean;
  defaultModelRoute?: string;
};

const toFormValues = (
  snapshot: SubAgentDelegationSnapshotDto,
): FormValues => ({
  maxDepth: snapshot.maxDepth,
  maxConcurrentSubAgents: snapshot.maxConcurrentSubAgents,
  allowModelSelection: snapshot.allowModelSelection,
  defaultModelRoute: snapshot.defaultModelRoute ?? '',
});

/** 来源标记：区分「文件里写了」与「取的是代码默认」。 */
const SourceTag: React.FC<{ fromConfigFile: boolean }> = ({ fromConfigFile }) =>
  fromConfigFile ? (
    <Tag color="blue" data-testid="source-config-file">
      来自配置文件
    </Tag>
  ) : (
    <Tag data-testid="source-code-default">代码默认</Tag>
  );

const SubAgentTeamSettingsPage: React.FC = () => {
  const [form] = Form.useForm<FormValues>();
  const [snapshot, setSnapshot] = useState<SubAgentDelegationSnapshotDto | null>(
    null,
  );
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [allowModelSelection, setAllowModelSelection] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const data = await getSubAgentDelegation();
      setSnapshot(data);
      setAllowModelSelection(data.allowModelSelection);
      form.setFieldsValue(toFormValues(data));
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : '加载配置失败');
      setSnapshot(null);
    } finally {
      setLoading(false);
    }
  }, [form]);

  useEffect(() => {
    void load();
  }, [load]);

  const onSave = async (values: FormValues) => {
    setSaving(true);
    try {
      const data = await updateSubAgentDelegation({
        maxDepth: values.maxDepth,
        maxConcurrentSubAgents: values.maxConcurrentSubAgents,
        allowModelSelection: values.allowModelSelection,
        // 空串 = 清除该键；后端不会把空串写进文件。
        defaultModelRoute: values.defaultModelRoute ?? '',
      });
      setSnapshot(data);
      setAllowModelSelection(data.allowModelSelection);
      form.setFieldsValue(toFormValues(data));
      message.success('已保存，下一次派发即生效（不需要重启）');
    } catch (e) {
      // 400 的 detail 是多行逐条错误，原样展示比「保存失败」有用。
      message.error(e instanceof Error ? e.message : '保存失败');
    } finally {
      setSaving(false);
    }
  };

  return (
    <PageContainer
      header={{ title: '子代理委派' }}
      subTitle="谁能创建子代理、能同时跑几个、能不能自己选模型"
    >
      {loading ? (
        <Spin />
      ) : loadError ? (
        <Alert
          type="error"
          showIcon
          message="加载失败"
          description={loadError}
          action={
            <Button size="small" onClick={() => void load()}>
              重试
            </Button>
          }
        />
      ) : (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Alert
            type="info"
            showIcon
            message="保存后不需要重启"
            description="下一次派发子代理就使用新值。配置文件 system.json 由配置框架自动重载。"
          />

          {snapshot && snapshot.warnings.length > 0 && (
            <Alert
              type="warning"
              showIcon
              data-testid="subagent-delegation-warnings"
              message="当前组合有副作用"
              description={
                <ul style={{ margin: 0, paddingInlineStart: 18 }}>
                  {snapshot.warnings.map((w) => (
                    <li key={w}>{w}</li>
                  ))}
                </ul>
              }
            />
          )}

          <Form<FormValues>
            form={form}
            layout="vertical"
            onFinish={(values) => void onSave(values)}
            onValuesChange={(changed) => {
              if (typeof changed.allowModelSelection === 'boolean') {
                setAllowModelSelection(changed.allowModelSelection);
              }
            }}
          >
            <Card
              title="委派深度"
              extra={
                snapshot && (
                  <SourceTag fromConfigFile={snapshot.maxDepthFromConfigFile} />
                )
              }
              style={{ marginBottom: 16 }}
            >
              <Form.Item
                label="最大递归深度"
                name="maxDepth"
                rules={[
                  { required: true, message: '请填写最大递归深度' },
                  {
                    type: 'number',
                    min: 0,
                    max: 8,
                    message: '取值范围 0–8',
                  },
                ]}
                extra="1 = 仅主 Agent 可创建子代理；0 = 不限制（允许子代理再建子代理）。"
              >
                <InputNumber
                  min={0}
                  max={8}
                  style={{ width: 200 }}
                  data-testid="subagent-delegation-max-depth"
                />
              </Form.Item>
            </Card>

            <Card
              title="并发上限"
              extra={
                snapshot && (
                  <SourceTag
                    fromConfigFile={snapshot.maxConcurrentSubAgentsFromConfigFile}
                  />
                )
              }
              style={{ marginBottom: 16 }}
            >
              <Form.Item
                label="同时存活的子智能体数量上限"
                name="maxConcurrentSubAgents"
                rules={[
                  { required: true, message: '请填写并发上限' },
                  {
                    type: 'number',
                    min: 0,
                    max: 128,
                    message: '取值范围 0–128',
                  },
                ]}
                extra="统计同一主 Agent 下所有递归层级的存活子代理，主 Agent 不计入；达到上限时新的启动请求会被拒绝。0 = 不限制。"
              >
                <InputNumber
                  min={0}
                  max={128}
                  style={{ width: 200 }}
                  data-testid="subagent-delegation-max-concurrent"
                />
              </Form.Item>
            </Card>

            <Card
              title="模型选择"
              extra={
                snapshot && (
                  <SourceTag
                    fromConfigFile={snapshot.allowModelSelectionFromConfigFile}
                  />
                )
              }
              style={{ marginBottom: 16 }}
            >
              <Form.Item
                label="允许 Agent 为子代理选择模型"
                name="allowModelSelection"
                valuePropName="checked"
                extra="关闭后，子代理使用下方默认路由；Agent 传入的模型参数被忽略（已选模型会保留在配置里）。"
              >
                <Switch data-testid="subagent-delegation-allow-model-selection" />
              </Form.Item>

              <Form.Item
                label="默认模型路由"
                name="defaultModelRoute"
                rules={[
                  {
                    validator: (_, value?: string) => {
                      if (allowModelSelection) return Promise.resolve();
                      if (!value?.trim()) {
                        return Promise.reject(
                          new Error(
                            '关闭模型选择时必须填写默认路由，否则每次派发都会失败',
                          ),
                        );
                      }
                      return Promise.resolve();
                    },
                  },
                  {
                    pattern: /^[^/\s]+\/[^/\s]+$/,
                    message:
                      '必须是 providerId/modelId 形式，如 fastrouter/gpt-6.2-sol',
                  },
                ]}
                extra={
                  allowModelSelection
                    ? '当前未启用（仅在选择被关闭时使用）。'
                    : '所有子代理都会使用这个路由。'
                }
              >
                <Input
                  placeholder="fastrouter/gpt-6.2-sol"
                  allowClear
                  style={{ maxWidth: 360 }}
                  data-testid="subagent-delegation-default-model-route"
                />
              </Form.Item>
            </Card>

            <Space>
              <Button
                type="primary"
                htmlType="submit"
                loading={saving}
                data-testid="subagent-delegation-save"
              >
                保存
              </Button>
              <Button onClick={() => void load()} disabled={saving}>
                重新读取
              </Button>
            </Space>
          </Form>

          {snapshot && (
            <Descriptions
              title="生效来源"
              size="small"
              column={1}
              style={{ maxWidth: 900 }}
            >
              <Descriptions.Item label="配置文件">
                <Text code copyable={{ text: snapshot.configFile }}>
                  {snapshot.configFile}
                </Text>
              </Descriptions.Item>
              <Descriptions.Item label="默认路由来源">
                <SourceTag
                  fromConfigFile={snapshot.defaultModelRouteFromConfigFile}
                />
              </Descriptions.Item>
            </Descriptions>
          )}

          <Paragraph type="secondary" style={{ marginBottom: 0 }}>
            取值来自运行中的配置链，不是启动快照 —— 这里显示的就是下一次派发会用的值。
          </Paragraph>
        </Space>
      )}
    </PageContainer>
  );
};

export default SubAgentTeamSettingsPage;
