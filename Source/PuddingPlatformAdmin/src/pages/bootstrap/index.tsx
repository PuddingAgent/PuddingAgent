import {
  CheckCircleOutlined,
  CloudServerOutlined,
  HomeOutlined,
  IdcardOutlined,
  LockOutlined,
  MailOutlined,
  SafetyCertificateOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { Helmet } from '@umijs/max';
import { Alert, App, Button, Form, Input, Radio, Space, Steps, Typography } from 'antd';
import { createStyles } from 'antd-style';
import React, { useEffect, useMemo, useState } from 'react';
import { Footer } from '@/components';
import Settings from '../../../config/defaultSettings';

type BootstrapFormValues = {
  userId: string;
  email: string;
  displayName?: string;
  password: string;
  confirmPassword: string;
  providerMode: 'custom' | 'skip';
  providerId?: string;
  providerName?: string;
  protocol?: string;
  baseUrl?: string;
  apiKey?: string;
  chatModelId?: string;
  memoryModelId?: string;
  workspaceName?: string;
  agentName?: string;
};

const useStyles = createStyles(() => ({
  container: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    overflow: 'auto',
    background: 'var(--warm-beige)',
    color: 'var(--text-primary)',
  },
  shell: {
    flex: 1,
    width: 'min(980px, calc(100vw - 32px))',
    margin: '0 auto',
    padding: '28px 0',
    display: 'flex',
    alignItems: 'center',
  },
  panel: {
    width: '100%',
    padding: '28px',
    background: 'color-mix(in srgb, var(--soft-white) 82%, transparent)',
    backdropFilter: 'blur(16px)',
    WebkitBackdropFilter: 'blur(16px)',
    border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    borderRadius: 8,
    boxShadow: '0 8px 32px rgba(0, 0, 0, 0.08)',
    color: 'var(--text-primary)',
    animation: 'pageEnterRuntime 200ms ease-out',
    '& .ant-steps-item-title, & .ant-form-item-label > label, & .ant-radio-wrapper': {
      color: 'color-mix(in srgb, var(--earth-brown) 86%, var(--text-primary)) !important',
    },
    '& .ant-steps-item-icon': {
      borderColor: 'color-mix(in srgb, var(--earth-brown) 18%, transparent) !important',
      background: 'color-mix(in srgb, var(--soft-white) 90%, transparent) !important',
    },
    '& .ant-steps-item-icon .ant-steps-icon': {
      color: 'color-mix(in srgb, var(--earth-brown) 70%, transparent) !important',
    },
    '& .ant-steps-item-active .ant-steps-item-icon': {
      borderColor: 'color-mix(in srgb, var(--accent-purple) 28%, transparent) !important',
      background: 'color-mix(in srgb, var(--accent-purple) 8%, var(--soft-white)) !important',
    },
    '& .ant-steps-item-active .ant-steps-icon, & .ant-steps-item-finish .ant-steps-icon': {
      color: 'var(--accent-purple) !important',
    },
    '& .ant-steps-item-finish .ant-steps-item-icon': {
      borderColor: 'color-mix(in srgb, var(--accent-purple) 20%, transparent) !important',
    },
    '& .ant-steps-item-tail::after': {
      background: 'color-mix(in srgb, var(--earth-brown) 10%, transparent) !important',
    },
    '& .ant-steps-item-description': { color: 'color-mix(in srgb, var(--earth-brown) 58%, transparent) !important' },
    '& .ant-input, & .ant-input-password, & .ant-input-affix-wrapper': {
      background: 'color-mix(in srgb, var(--soft-white) 92%, transparent)',
      borderColor: 'color-mix(in srgb, var(--earth-brown) 14%, transparent)',
      color: 'var(--text-primary)',
      borderRadius: 8,
      boxShadow: 'none',
      transition: 'border-color 180ms ease, box-shadow 180ms ease, background 180ms ease',
    },
    '& .ant-input:hover, & .ant-input-password:hover, & .ant-input-affix-wrapper:hover': {
      borderColor: 'color-mix(in srgb, var(--earth-brown) 26%, transparent)',
    },
    '& .ant-input:focus, & .ant-input-focused, & .ant-input-affix-wrapper-focused': {
      borderColor: 'color-mix(in srgb, var(--accent-purple) 46%, transparent) !important',
      boxShadow: '0 0 0 2px color-mix(in srgb, var(--accent-purple) 10%, transparent) !important',
      background: 'var(--soft-white)',
    },
    '& .ant-input::placeholder': { color: 'color-mix(in srgb, var(--earth-brown) 44%, transparent)' },
    '& .ant-input-affix-wrapper .ant-input': { background: 'transparent' },
    '& .ant-input-prefix, & .ant-input-password-icon': { color: 'color-mix(in srgb, var(--earth-brown) 52%, transparent)' },
    '& .ant-form-item': {
      marginBottom: 18,
    },
    '& .ant-form-item-label': {
      paddingBottom: 5,
    },
    '& .ant-alert': {
      borderRadius: 8,
      border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
      color: 'var(--text-primary)',
    },
    '& .ant-alert-info': {
      background: 'color-mix(in srgb, var(--accent-purple) 5%, var(--soft-white))',
      borderColor: 'color-mix(in srgb, var(--accent-purple) 14%, transparent)',
    },
    '& .ant-alert-success': {
      background: 'color-mix(in srgb, var(--desaturated-green) 10%, var(--soft-white))',
      borderColor: 'color-mix(in srgb, var(--desaturated-green) 24%, transparent)',
    },
    '& .ant-alert-warning': {
      background: 'color-mix(in srgb, #f97316 7%, var(--soft-white))',
      borderColor: 'color-mix(in srgb, #f97316 22%, transparent)',
    },
    '& .ant-btn': {
      borderRadius: 8,
      minHeight: 36,
      fontWeight: 500,
    },
    '& .ant-btn-default': {
      background: 'color-mix(in srgb, var(--soft-white) 86%, transparent)',
      borderColor: 'color-mix(in srgb, var(--earth-brown) 14%, transparent)',
      color: 'var(--earth-brown)',
    },
    '& .ant-btn-default:hover': {
      borderColor: 'color-mix(in srgb, var(--earth-brown) 28%, transparent) !important',
      color: 'var(--text-primary) !important',
      background: 'var(--soft-white) !important',
    },
    '& .ant-btn-primary': {
      background: 'var(--accent-purple)',
      borderColor: 'var(--accent-purple)',
      boxShadow: '0 8px 20px color-mix(in srgb, var(--accent-purple) 18%, transparent)',
    },
    '& .ant-btn-primary:hover': {
      background: 'color-mix(in srgb, var(--accent-purple) 84%, #ffffff) !important',
      borderColor: 'color-mix(in srgb, var(--accent-purple) 84%, #ffffff) !important',
    },
    '& .ant-radio-checked .ant-radio-inner': {
      borderColor: 'var(--accent-purple)',
      backgroundColor: 'var(--accent-purple)',
    },
  },
  header: {
    marginBottom: 22,
    paddingBottom: 16,
    borderBottom: '1px solid color-mix(in srgb, var(--earth-brown) 8%, transparent)',
  },
  title: {
    color: 'var(--text-primary) !important',
    marginBottom: '4px !important',
    fontWeight: '700 !important',
    letterSpacing: '0 !important',
  },
  subtitle: {
    color: 'color-mix(in srgb, var(--earth-brown) 70%, transparent)',
  },
  steps: {
    marginBottom: 26,
  },
  content: {
    minHeight: 300,
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '0 16px',
    '@media (max-width: 720px)': {
      gridTemplateColumns: '1fr',
    },
  },
  full: {
    gridColumn: '1 / -1',
  },
  actions: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: 12,
    marginTop: 24,
  },
  review: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: 12,
    '@media (max-width: 720px)': {
      gridTemplateColumns: '1fr',
    },
  },
  reviewItem: {
    border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    borderRadius: 8,
    padding: 14,
    background: 'color-mix(in srgb, var(--soft-white) 72%, transparent)',
  },
  reviewLabel: {
    color: 'color-mix(in srgb, var(--earth-brown) 68%, transparent)',
    fontSize: 12,
    marginBottom: 4,
  },
  reviewValue: {
    color: 'var(--text-primary)',
    wordBreak: 'break-word',
  },
}));

const Bootstrap: React.FC = () => {
  const { styles } = useStyles();
  const { message } = App.useApp();
  const [form] = Form.useForm<BootstrapFormValues>();
  const [step, setStep] = useState(0);
  const [submitting, setSubmitting] = useState(false);
  const [statusText, setStatusText] = useState('正在检查初始化状态...');
  const [password, setPassword] = useState('');
  const [formSnapshot, setFormSnapshot] = useState<Partial<BootstrapFormValues>>({});
  const providerMode = Form.useWatch('providerMode', form) || 'custom';
  const watchedValues = Form.useWatch([], form) || {};
  const values = { ...formSnapshot, ...watchedValues };

  const hasMinLength = password.length >= 8;
  const hasLower = /[a-z]/.test(password);
  const hasUpper = /[A-Z]/.test(password);
  const hasDigit = /\d/.test(password);
  const passwordReady = hasMinLength && hasLower && hasUpper && hasDigit;

  useEffect(() => {
    form.setFieldsValue({
      providerMode: 'custom',
      providerId: 'default-openai',
      providerName: 'Default OpenAI-Compatible',
      protocol: 'openai',
      baseUrl: 'https://api.openai.com/v1',
      chatModelId: 'gpt-4o-mini',
      memoryModelId: 'gpt-4o-mini',
      workspaceName: '默认工作空间',
      agentName: '默认助手',
    });

    fetch('/api/bootstrap/status')
      .then(async (res) => {
        if (res.status === 403) {
          window.location.href = '/admin/user/login';
          return;
        }
        const data = await res.json();
        setStatusText(data.needsSetup ? '等待完成首次初始化' : '系统已完成初始化');
        if (!data.needsSetup) {
          window.location.href = '/admin/user/login';
        }
      })
      .catch(() => setStatusText('无法读取初始化状态，请确认后端服务运行中'));
  }, [form]);

  const steps = useMemo(
    () => [
      { title: '管理员', icon: <IdcardOutlined /> },
      { title: '模型服务', icon: <CloudServerOutlined /> },
      { title: '默认空间', icon: <HomeOutlined /> },
      { title: '检查封存', icon: <CheckCircleOutlined /> },
    ],
    [],
  );

  const fieldsByStep: (keyof BootstrapFormValues)[][] = [
    ['userId', 'email', 'password', 'confirmPassword'],
    providerMode === 'skip'
      ? ['providerMode']
      : ['providerMode', 'providerId', 'providerName', 'protocol', 'baseUrl', 'chatModelId'],
    ['workspaceName', 'agentName'],
    [],
  ];

  const next = async () => {
    try {
      await form.validateFields(fieldsByStep[step]);
      setFormSnapshot(form.getFieldsValue(true));
      setStep((current) => Math.min(current + 1, steps.length - 1));
    } catch {
      // AntD already renders field-level validation messages.
    }
  };

  const previous = () => {
    setStep((current) => Math.max(current - 1, 0));
  };

  const submit = async () => {
    try {
      await form.validateFields();
    } catch {
      return;
    }
    const allValues = form.getFieldsValue(true);
    setSubmitting(true);
    try {
      const provider =
        allValues.providerMode === 'skip'
          ? { mode: 'skip' }
          : {
              mode: 'custom',
              providerId: allValues.providerId,
              name: allValues.providerName,
              protocol: allValues.protocol || 'openai',
              baseUrl: allValues.baseUrl,
              apiKey: allValues.apiKey,
              chatModelId: allValues.chatModelId,
              memoryModelId: allValues.memoryModelId,
            };

      const res = await fetch('/api/bootstrap/complete', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          admin: {
            userId: allValues.userId,
            email: allValues.email,
            displayName: allValues.displayName,
            password: allValues.password,
          },
          provider,
          defaults: {
            workspaceName: allValues.workspaceName,
            agentName: allValues.agentName,
          },
        }),
      });

      const data = await res.json();
      if (!res.ok || data.status !== 'ok') {
        message.error(data.message || '初始化失败，请查看 data/logs 中的日志');
        return;
      }

      if (data.token) {
        localStorage.setItem('pudding_token', data.token);
      }
      message.success('初始化完成，即将进入运行时');
      setTimeout(() => {
        window.location.href = '/admin/chat';
      }, 1000);
    } catch {
      message.error('网络错误，请检查后端服务是否启动');
    } finally {
      setSubmitting(false);
    }
  };

  const renderAdminStep = () => (
    <div className={styles.grid}>
      <Form.Item
        label="管理员账号"
        name="userId"
        rules={[
          { required: true, message: '请输入用户名' },
          { min: 3, message: '用户名至少3个字符' },
        ]}
      >
        <Input size="large" prefix={<IdcardOutlined />} placeholder="登录用户名，如 admin" />
      </Form.Item>
      <Form.Item
        label="邮箱"
        name="email"
        rules={[
          { required: true, message: '请输入邮箱' },
          { type: 'email', message: '请输入有效邮箱' },
        ]}
      >
        <Input size="large" prefix={<MailOutlined />} placeholder="邮箱地址" />
      </Form.Item>
      <Form.Item label="显示名称" name="displayName">
        <Input size="large" prefix={<UserOutlined />} placeholder="显示名称，可选" />
      </Form.Item>
      <Form.Item
        label="登录密码"
        name="password"
        rules={[
          { required: true, message: '请输入密码' },
          { pattern: /^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,}$/, message: '至少8位，含大小写字母和数字' },
        ]}
      >
        <Input.Password
          size="large"
          prefix={<LockOutlined />}
          placeholder="密码"
          onChange={(event) => setPassword(event.target.value)}
        />
      </Form.Item>
      <Form.Item
        label="确认密码"
        name="confirmPassword"
        dependencies={['password']}
        rules={[
          { required: true, message: '请再次输入密码' },
          ({ getFieldValue }) => ({
            validator(_, value) {
              if (!value || getFieldValue('password') === value) return Promise.resolve();
              return Promise.reject(new Error('两次输入的密码不一致'));
            },
          }),
        ]}
      >
        <Input.Password size="large" prefix={<LockOutlined />} placeholder="确认密码" />
      </Form.Item>
      <div className={styles.full}>
        <Alert
          type={passwordReady ? 'success' : 'info'}
          showIcon
          message={`密码要求：8位以上、包含大写字母、小写字母和数字。当前${passwordReady ? '已满足' : '未完全满足'}。`}
        />
      </div>
    </div>
  );

  const renderProviderStep = () => (
    <div className={styles.grid}>
      <Form.Item label="模型服务模式" name="providerMode" className={styles.full}>
        <Radio.Group
          options={[
            { label: '配置 OpenAI 兼容服务', value: 'custom' },
            { label: '暂时跳过，使用 fake/已有配置', value: 'skip' },
          ]}
        />
      </Form.Item>
      {providerMode !== 'skip' && (
        <>
          <Form.Item label="Provider ID" name="providerId" rules={[{ required: true, message: '请输入 Provider ID' }]}>
            <Input size="large" prefix={<CloudServerOutlined />} placeholder="Provider ID，如 openai" />
          </Form.Item>
          <Form.Item label="Provider 名称" name="providerName" rules={[{ required: true, message: '请输入 Provider 名称' }]}>
            <Input size="large" placeholder="显示名称" />
          </Form.Item>
          <Form.Item label="协议" name="protocol" rules={[{ required: true, message: '请选择协议' }]}>
            <Radio.Group options={[{ label: 'OpenAI', value: 'openai' }]} />
          </Form.Item>
          <Form.Item label="Base URL" name="baseUrl" rules={[{ required: true, message: '请输入 Base URL' }]}>
            <Input size="large" placeholder="https://api.openai.com/v1" />
          </Form.Item>
          <Form.Item label="API Key" name="apiKey">
            <Input.Password size="large" prefix={<SafetyCertificateOutlined />} placeholder="API Key，可稍后在 KeyVault 更新" />
          </Form.Item>
          <Form.Item label="默认聊天模型" name="chatModelId" rules={[{ required: true, message: '请输入默认聊天模型' }]}>
            <Input size="large" placeholder="默认聊天模型，如 gpt-4o-mini" />
          </Form.Item>
          <Form.Item label="记忆模型" name="memoryModelId">
            <Input size="large" placeholder="记忆/总结模型，可选" />
          </Form.Item>
        </>
      )}
      {providerMode === 'skip' && (
        <div className={styles.full}>
          <Alert type="warning" showIcon message="跳过后系统仍可初始化，但真实模型调用需要稍后在 LLM 资源池中配置。" />
        </div>
      )}
    </div>
  );

  const renderDefaultsStep = () => (
    <div className={styles.grid}>
      <Form.Item label="默认工作空间" name="workspaceName" rules={[{ required: true, message: '请输入默认空间名称' }]}>
        <Input size="large" prefix={<HomeOutlined />} placeholder="默认工作空间" />
      </Form.Item>
      <Form.Item label="默认助手" name="agentName">
        <Input size="large" prefix={<UserOutlined />} placeholder="默认助手名称" />
      </Form.Item>
      <div className={styles.full}>
        <Alert
          type="info"
          showIcon
          message="初始化会确保 default workspace 存在，并把首个管理员加入平台团队和默认空间。"
        />
      </div>
    </div>
  );

  const renderReviewStep = () => (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Alert type="success" showIcon message="准备封存初始化" description="提交后将创建首个管理员并关闭初始化入口。" />
      <div className={styles.review}>
        <div className={styles.reviewItem}>
          <div className={styles.reviewLabel}>管理员</div>
          <div className={styles.reviewValue}>{values.userId || '-'}</div>
        </div>
        <div className={styles.reviewItem}>
          <div className={styles.reviewLabel}>邮箱</div>
          <div className={styles.reviewValue}>{values.email || '-'}</div>
        </div>
        <div className={styles.reviewItem}>
          <div className={styles.reviewLabel}>模型服务</div>
          <div className={styles.reviewValue}>
            {providerMode === 'skip' ? '跳过' : `${values.providerName || '-'} / ${values.chatModelId || '-'}`}
          </div>
        </div>
        <div className={styles.reviewItem}>
          <div className={styles.reviewLabel}>默认空间</div>
          <div className={styles.reviewValue}>{values.workspaceName || '-'}</div>
        </div>
      </div>
    </Space>
  );

  const stepContent = [renderAdminStep, renderProviderStep, renderDefaultsStep, renderReviewStep][step]();

  return (
    <div className={styles.container}>
      <Helmet>
        <title>系统初始化 - {Settings.title}</title>
      </Helmet>
      <div className={styles.shell}>
        <div className={styles.panel}>
          <div className={styles.header}>
            <Typography.Title level={2} className={styles.title}>
              Initialize Pudding Runtime
            </Typography.Title>
            <Typography.Text className={styles.subtitle}>
              首次启动 — 完成运行时初始化
            </Typography.Text>
          </div>
          <Alert type="info" showIcon message={statusText} style={{ marginBottom: 20 }} />
          <Steps current={step} items={steps} className={styles.steps} />
          <Form form={form} layout="vertical" requiredMark={false}>
            <div className={styles.content}>{stepContent}</div>
          </Form>
          <div className={styles.actions}>
            <Button disabled={step === 0 || submitting} onClick={previous}>
              上一步
            </Button>
            {step < steps.length - 1 ? (
              <Button type="primary" onClick={next}>
                下一步
              </Button>
            ) : (
              <Button type="primary" loading={submitting} icon={<CheckCircleOutlined />} onClick={submit}>
                完成初始化
              </Button>
            )}
          </div>
        </div>
      </div>
      <Footer />
    </div>
  );
};

export default Bootstrap;
