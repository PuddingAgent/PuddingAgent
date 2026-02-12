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
import { Alert, App, Button, Form, Input, Radio, Space, Typography } from 'antd';
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
    background: 'var(--pudding-chat-bg)',
    color: 'var(--pudding-chat-text)',
  },
  shell: {
    flex: 1,
    width: 'min(1180px, calc(100vw - 48px))',
    margin: '0 auto',
    padding: '24px 0',
    display: 'flex',
    alignItems: 'center',
    '@media (max-width: 860px)': {
      width: 'min(100%, calc(100vw - 16px))',
      padding: '8px 0',
      alignItems: 'flex-start',
    },
  },
  panel: {
    position: 'relative' as const,
    width: '100%',
    minHeight: 'min(780px, calc(100vh - 48px))',
    display: 'grid',
    gridTemplateColumns: '280px minmax(0, 1fr)',
    overflow: 'hidden',
    background: 'var(--pudding-chat-surface)',
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 8,
    boxShadow: '0 12px 36px rgba(40, 32, 24, 0.10)',
    color: 'var(--pudding-chat-text)',
    animation: 'pageEnterRuntime 200ms ease-out',
    '@media (max-width: 860px)': {
      gridTemplateColumns: '1fr',
      minHeight: 'auto',
    },
    '& .ant-form-item-label > label, & .ant-radio-wrapper': {
      color: 'var(--pudding-chat-text-muted) !important',
    },
    '& .ant-input, & .ant-input-password, & .ant-input-affix-wrapper': {
      minHeight: 40,
      background: 'var(--pudding-chat-surface)',
      borderColor: 'var(--pudding-chat-border)',
      color: 'var(--pudding-chat-text)',
      borderRadius: 8,
      boxShadow: 'none',
      transition: 'border-color 180ms ease, box-shadow 180ms ease, background 180ms ease',
    },
    '& .ant-input:hover, & .ant-input-password:hover, & .ant-input-affix-wrapper:hover': {
      borderColor: 'var(--pudding-chat-border-strong)',
    },
    '& .ant-input:focus, & .ant-input-focused, & .ant-input-affix-wrapper-focused': {
      borderColor: 'color-mix(in srgb, var(--pudding-chat-accent) 44%, var(--pudding-chat-border)) !important',
      boxShadow: '0 0 0 2px var(--pudding-chat-accent-soft) !important',
      background: 'var(--pudding-chat-surface)',
    },
    '& .ant-input::placeholder': { color: 'var(--pudding-chat-text-subtle)' },
    '& .ant-input-affix-wrapper .ant-input': { background: 'transparent' },
    '& .ant-input-prefix, & .ant-input-password-icon': { color: 'var(--pudding-chat-text-subtle)' },
    '& .ant-form-item': {
      marginBottom: 18,
    },
    '& .ant-form-item-label': {
      paddingBottom: 5,
    },
    '& .ant-alert': {
      borderRadius: 8,
      border: '1px solid var(--pudding-chat-border)',
      color: 'var(--pudding-chat-text)',
    },
    '& .ant-alert-info': {
      background: 'color-mix(in srgb, var(--pudding-chat-accent) 5%, var(--pudding-chat-surface))',
      borderColor: 'color-mix(in srgb, var(--pudding-chat-accent) 16%, var(--pudding-chat-border))',
    },
    '& .ant-alert-success': {
      background: 'color-mix(in srgb, var(--pudding-chat-success) 10%, var(--pudding-chat-surface))',
      borderColor: 'color-mix(in srgb, var(--pudding-chat-success) 24%, var(--pudding-chat-border))',
    },
    '& .ant-alert-warning': {
      background: 'color-mix(in srgb, #f97316 7%, var(--pudding-chat-surface))',
      borderColor: 'color-mix(in srgb, #f97316 22%, var(--pudding-chat-border))',
    },
    '& .ant-btn': {
      borderRadius: 8,
      minHeight: 36,
      fontWeight: 500,
    },
    '& .ant-btn-default': {
      background: 'var(--pudding-chat-surface)',
      borderColor: 'var(--pudding-chat-border)',
      color: 'var(--pudding-chat-text-muted)',
    },
    '& .ant-btn-default:hover': {
      borderColor: 'var(--pudding-chat-border-strong) !important',
      color: 'var(--pudding-chat-text) !important',
      background: 'var(--pudding-chat-surface-muted) !important',
    },
    '& .ant-btn-primary': {
      background: 'var(--pudding-chat-accent)',
      borderColor: 'var(--pudding-chat-accent)',
      boxShadow: 'none',
    },
    '& .ant-btn-primary:hover': {
      background: 'color-mix(in srgb, var(--pudding-chat-accent) 86%, #ffffff) !important',
      borderColor: 'color-mix(in srgb, var(--pudding-chat-accent) 86%, #ffffff) !important',
    },
    '& .ant-radio-checked .ant-radio-inner': {
      borderColor: 'var(--pudding-chat-accent)',
      backgroundColor: 'var(--pudding-chat-accent)',
    },
  },
  sidePanel: {
    display: 'flex',
    flexDirection: 'column',
    gap: 20,
    padding: '32px 24px',
    background: 'var(--pudding-chat-sidebar-bg)',
    borderRight: '1px solid var(--pudding-chat-border)',
    backdropFilter: 'blur(12px)',
    '@media (max-width: 860px)': {
      borderRight: 0,
      borderBottom: '1px solid var(--pudding-chat-border)',
      padding: 24,
    },
  },
  brandBlock: {
    display: 'flex',
    alignItems: 'center',
    gap: 12,
    minWidth: 0,
    padding: '2px 0 0',
  },
  brandImage: {
    width: 58,
    height: 58,
    flex: '0 0 58px',
    borderRadius: 16,
    objectFit: 'cover' as const,
    background: 'var(--pudding-chat-surface)',
    border: '1px solid var(--pudding-chat-border)',
    boxShadow: 'none',
  },
  brandText: {
    minWidth: 0,
  },
  brandKicker: {
    display: 'block',
    marginBottom: 4,
    color: 'var(--pudding-chat-text-subtle)',
    fontSize: 11,
    fontWeight: 700,
    letterSpacing: 0,
    textTransform: 'uppercase' as const,
  },
  title: {
    color: 'var(--pudding-chat-text) !important',
    margin: '0 !important',
    fontSize: '23px !important',
    fontWeight: '700 !important',
    letterSpacing: '0 !important',
    lineHeight: '1.16 !important',
  },
  subtitle: {
    color: 'var(--pudding-chat-text-muted)',
    lineHeight: 1.55,
  },
  stepRail: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 4,
    margin: 0,
    padding: 0,
    listStyle: 'none',
    '@media (max-width: 860px)': {
      display: 'grid',
      gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    },
    '@media (max-width: 560px)': {
      gridTemplateColumns: '1fr',
    },
  },
  stepItem: {
    display: 'block',
    padding: '8px 0 8px 12px',
    borderLeft: '2px solid transparent',
    color: 'var(--pudding-chat-text-muted)',
    transition: 'background 160ms ease, border-color 160ms ease, color 160ms ease',
    '&[data-state="active"]': {
      color: 'var(--pudding-chat-text)',
      borderLeftColor: 'var(--pudding-chat-accent)',
    },
    '&[data-state="done"]': {
      color: 'var(--pudding-chat-success)',
    },
  },
  stepTitle: {
    display: 'block',
    fontSize: 13,
    fontWeight: 700,
    marginBottom: 2,
    color: 'inherit',
  },
  stepDescription: {
    display: 'block',
    fontSize: 12,
    lineHeight: 1.45,
    color: 'color-mix(in srgb, currentColor 72%, transparent)',
  },
  mainPanel: {
    display: 'flex',
    flexDirection: 'column' as const,
    minWidth: 0,
    padding: '32px 38px 26px',
    background: 'var(--pudding-chat-surface)',
    '@media (max-width: 860px)': {
      padding: 24,
    },
  },
  mainHeader: {
    display: 'flex',
    gap: 20,
    marginBottom: 24,
    paddingBottom: 18,
    borderBottom: '1px solid var(--pudding-chat-border)',
    '@media (max-width: 720px)': {
      flexDirection: 'column' as const,
    },
  },
  stepEyebrow: {
    display: 'block',
    marginBottom: 6,
    color: 'var(--pudding-chat-accent)',
    fontSize: 12,
    fontWeight: 700,
  },
  mainTitle: {
    margin: '0 0 6px !important',
    color: 'var(--pudding-chat-text) !important',
    letterSpacing: '0 !important',
  },
  content: {
    minHeight: 460,
    '@media (max-width: 860px)': {
      minHeight: 'auto',
    },
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '2px 18px',
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
    marginTop: 'auto',
    paddingTop: 22,
    borderTop: '1px solid var(--pudding-chat-border)',
  },
  passwordChecklist: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: 8,
    gridColumn: '1 / -1',
    '@media (max-width: 720px)': {
      gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    },
  },
  passwordRule: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    padding: '8px 10px',
    borderRadius: 8,
    border: '1px solid var(--pudding-chat-border)',
    color: 'var(--pudding-chat-text-muted)',
    fontSize: 12,
    background: 'var(--pudding-chat-surface)',
    '&[data-ready="true"]': {
      color: 'var(--pudding-chat-success)',
      borderColor: 'color-mix(in srgb, var(--pudding-chat-success) 28%, var(--pudding-chat-border))',
      background: 'color-mix(in srgb, var(--pudding-chat-success) 8%, var(--pudding-chat-surface))',
    },
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
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 8,
    padding: 14,
    background: 'var(--pudding-chat-surface)',
  },
  reviewLabel: {
    color: 'var(--pudding-chat-text-subtle)',
    fontSize: 12,
    marginBottom: 4,
  },
  reviewValue: {
    color: 'var(--pudding-chat-text)',
    wordBreak: 'break-word',
    fontWeight: 600,
  },
  impactList: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: 10,
    '@media (max-width: 720px)': {
      gridTemplateColumns: '1fr',
    },
  },
  impactItem: {
    display: 'flex',
    gap: 8,
    alignItems: 'flex-start',
    padding: 12,
    borderRadius: 8,
    background: 'var(--pudding-chat-surface)',
    border: '1px solid var(--pudding-chat-border)',
    color: 'var(--pudding-chat-text-muted)',
  },
}));

const Bootstrap: React.FC = () => {
  const { styles } = useStyles();
  const { message } = App.useApp();
  const [form] = Form.useForm<BootstrapFormValues>();
  const [step, setStep] = useState(0);
  const [submitting, setSubmitting] = useState(false);
  const [password, setPassword] = useState('');
  const [formSnapshot, setFormSnapshot] = useState<Partial<BootstrapFormValues>>({});
  const providerMode = Form.useWatch('providerMode', form) || 'custom';
  const watchedValues = Form.useWatch([], form) || {};
  const values = { ...formSnapshot, ...watchedValues };

  const hasMinLength = password.length >= 8;
  const hasLower = /[a-z]/.test(password);
  const hasUpper = /[A-Z]/.test(password);
  const hasDigit = /\d/.test(password);

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
        if (!data.needsSetup) {
          window.location.href = '/admin/user/login';
        }
      })
      .catch(() => message.warning('无法读取初始化状态，请确认后端服务运行中'));
  }, [form, message]);

  const steps = useMemo(
    () => [
      {
        title: '管理员',
        description: '创建首个系统管理员账号。',
      },
      {
        title: '模型服务',
        description: '配置默认模型供应商或稍后处理。',
      },
      {
        title: '默认空间',
        description: '准备初始 workspace 和助手。',
      },
      {
        title: '检查保存',
        description: '确认配置并关闭初始化入口。',
      },
    ],
    [],
  );
  const currentStep = steps[step];

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
      <div className={styles.full}>
        <Alert
          type="info"
          showIcon
          message="开发环境快速初始化"
          description={
            <span>
              默认密码仅用于本地开发环境快速初始化：
              <Typography.Text code>Admin@123456</Typography.Text>
              。生产环境请设置独立强密码。
            </span>
          }
        />
      </div>
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
      <div className={styles.passwordChecklist}>
        {[
          ['8 位以上', hasMinLength],
          ['包含大写字母', hasUpper],
          ['包含小写字母', hasLower],
          ['包含数字', hasDigit],
        ].map(([label, ready]) => (
          <div key={String(label)} className={styles.passwordRule} data-ready={String(ready)}>
            <CheckCircleOutlined />
            <span>{label}</span>
          </div>
        ))}
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
      <Alert type="success" showIcon message="准备保存初始化配置" description="提交后将写入首个管理员、默认空间和模型配置，并关闭初始化入口。" />
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
      <div className={styles.impactList}>
        {[
          '创建首个系统管理员账号',
          providerMode === 'skip' ? '跳过模型服务，稍后在 LLM 资源池配置' : '写入默认 OpenAI 兼容模型服务',
          '确保 default workspace 存在',
          '初始化完成后关闭首次启动入口',
        ].map((item) => (
          <div key={item} className={styles.impactItem}>
            <CheckCircleOutlined style={{ color: 'var(--pudding-chat-success)', marginTop: 2 }} />
            <span>{item}</span>
          </div>
        ))}
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
          <aside className={styles.sidePanel}>
            <div className={styles.brandBlock}>
              <img className={styles.brandImage} src="/admin/assets/images/me.png" alt="Pudding Runtime" />
              <div className={styles.brandText}>
                <Typography.Text className={styles.brandKicker}>Pudding Runtime</Typography.Text>
                <Typography.Title level={3} className={styles.title}>
                  系统初始化
                </Typography.Title>
              </div>
            </div>

            <ol className={styles.stepRail}>
              {steps.map((item, index) => {
                const state = index === step ? 'active' : index < step ? 'done' : 'pending';
                return (
                  <li key={item.title} className={styles.stepItem} data-state={state}>
                    <Typography.Text className={styles.stepTitle}>{item.title}</Typography.Text>
                    <Typography.Text className={styles.stepDescription}>{item.description}</Typography.Text>
                  </li>
                );
              })}
            </ol>

          </aside>

          <main className={styles.mainPanel}>
            <div className={styles.mainHeader}>
              <div>
                <Typography.Text className={styles.stepEyebrow}>Step {step + 1} / {steps.length}</Typography.Text>
                <Typography.Title level={3} className={styles.mainTitle}>
                  {currentStep.title}
                </Typography.Title>
                <Typography.Text className={styles.subtitle}>{currentStep.description}</Typography.Text>
              </div>
            </div>

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
          </main>
        </div>
      </div>
      <Footer />
    </div>
  );
};

export default Bootstrap;
